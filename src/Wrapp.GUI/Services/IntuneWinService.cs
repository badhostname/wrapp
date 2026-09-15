using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Pure C# service for .intunewin file operations: inspect, decrypt, validate.
/// No PowerShell dependency -- all crypto done natively for speed.
/// </summary>
public static class IntuneWinService
{
    /// <summary>
    /// Inspects a .intunewin file (ZIP with detection.xml) and returns its metadata.
    /// Returns null if the file is not a valid .intunewin package.
    /// </summary>
    public static EncryptionKeyInfo? InspectPackage(string intunewinPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(intunewinPath);
            var detectionEntry = zip.Entries.FirstOrDefault(e =>
                e.Name.Equals("detection.xml", StringComparison.OrdinalIgnoreCase));
            if (detectionEntry is null) return null;

            using var reader = new StreamReader(detectionEntry.Open());
            var xml = XDocument.Parse(reader.ReadToEnd());
            var appInfo = xml.Root;
            if (appInfo is null) return null;

            // Case-insensitive element lookup -- detection.xml uses mixed casing
            // (EncryptionKey=PascalCase, macKey=camelCase, fileDigest=camelCase)
            var encInfo = ElNode(appInfo, "EncryptionInfo");
            return new EncryptionKeyInfo
            {
                EncryptionKey          = El(encInfo, "EncryptionKey") ?? "",
                InitializationVector   = El(encInfo, "InitializationVector") ?? "",
                MacKey                 = El(encInfo, "macKey") ?? "",
                Mac                    = El(encInfo, "mac") ?? "",
                FileDigest             = El(encInfo, "fileDigest") ?? "",
                FileDigestAlgorithm    = El(encInfo, "fileDigestAlgorithm") ?? "SHA256",
                ProfileIdentifier      = "ProfileVersion1",
                PackageName            = El(appInfo, "Name") ?? "",
                SetupFile              = El(appInfo, "SetupFile") ?? "",
                InnerFileName          = El(appInfo, "FileName") ?? "",
                UnencryptedContentSize = long.TryParse(El(appInfo, "UnencryptedContentSize"), out var sz) ? sz : 0,
                SourcePath             = Path.GetFullPath(intunewinPath),
            };
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"IntuneWin: inspect failed -- {ex.Message}");
            return null;
        }
    }

    /// <summary>Case-insensitive XML element value lookup.</summary>
    private static string? El(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(e =>
            e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>Case-insensitive XML child element lookup (returns the element, not its value).</summary>
    private static XElement? ElNode(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(e =>
            e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Decrypts an encrypted file using AES-256-CBC.
    /// The source is either an inner file extracted from .intunewin or a raw Azure blob.
    /// Skips the 48-byte header (32 HMAC + 16 IV).
    /// Returns true on success, false on failure (wrong keys, corrupt data).
    /// </summary>
    public static async Task<bool> DecryptAsync(
        string encryptedPath, string outputPath,
        string base64Key, string base64IV,
        IProgress<int>? progress = null,
        CancellationToken ct = default,
        string? base64MacKey = null)
    {
        try
        {
            var key = Convert.FromBase64String(base64Key);
            var iv = Convert.FromBase64String(base64IV);

            // Verify the file's HMAC-SHA256 before
            // decrypting, when the MAC key is available. The .intunewin layout
            // is [HMAC(32)][IV(16)][ciphertext] and the HMAC covers IV+ciphertext
            // keyed by MacKey. Without this, a tampered blob would decrypt to
            // attacker-influenced plaintext that is then written to disk and
            // parsed (Config.json). Inputs can be arbitrary (dropped .intunewin,
            // downloaded Azure blobs), so authenticate-before-decrypt matters.
            if (!string.IsNullOrEmpty(base64MacKey))
            {
                var macKey = Convert.FromBase64String(base64MacKey);
                if (!await VerifyHmacAsync(encryptedPath, macKey, ct).ConfigureAwait(false))
                {
                    AppLogger.Warn($"IntuneWin: HMAC verification FAILED for {Path.GetFileName(encryptedPath)} " +
                                   "-- refusing to decrypt (tampered content or wrong MAC key)");
                    return false;
                }
            }

            using var aes = Aes.Create();
            using var decryptor = aes.CreateDecryptor(key, iv);
            using var source = File.OpenRead(encryptedPath);
            source.Seek(48, SeekOrigin.Begin); // skip HMAC + IV header

            using var target = File.Create(outputPath);
            using var crypto = new CryptoStream(target, decryptor, CryptoStreamMode.Write);

            var buffer = new byte[2097152]; // 2MB chunks
            long total = source.Length - 48, done = 0;
            int read;
            // ConfigureAwait(false): callers don't need a Task.Run wrap to keep
            // continuations off the UI thread -- progress reports already
            // marshal back to the captured context via Progress<T>.
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await crypto.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                progress?.Report(total > 0 ? (int)(done * 100 / total) : 0);
            }
            crypto.FlushFinalBlock();
            return true;
        }
        catch (CryptographicException)
        {
            // must-stay-silent: wrong key triggers PKCS7 padding validation
            // failure. This is the brute-force happy path - we try every
            // cached key until one succeeds. Logging here would dump 50+
            // failures per import.
            TryDelete(outputPath);
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"IntuneWin: decryption failed -- {ex.Message}");
            TryDelete(outputPath);
            return false;
        }
    }

    /// <summary>
    /// Verifies the 32-byte HMAC-SHA256 prefix of an encrypted blob against
    /// the MAC recomputed over the remainder of the file (IV + ciphertext = bytes
    /// from offset 32) keyed by <paramref name="macKey"/>. Constant-time compare.
    /// Returns false on mismatch, a too-short file, or any read error.
    /// </summary>
    private static async Task<bool> VerifyHmacAsync(string encryptedPath, byte[] macKey, CancellationToken ct)
    {
        try
        {
            using var fs = File.OpenRead(encryptedPath);
            if (fs.Length < 48) return false; // too short to hold HMAC(32) + IV(16)

            var storedMac = new byte[32];
            await fs.ReadExactlyAsync(storedMac, ct).ConfigureAwait(false);

            // Stream is now positioned at offset 32; ComputeHashAsync hashes
            // from here to EOF (IV + ciphertext), which is exactly the HMAC input.
            using var hmac = new HMACSHA256(macKey);
            var computed = await hmac.ComputeHashAsync(fs, ct).ConfigureAwait(false);

            return CryptographicOperations.FixedTimeEquals(storedMac, computed);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"IntuneWin: HMAC verify error for {Path.GetFileName(encryptedPath)} -- {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Fast key validation: decrypts only the first AES block (16 bytes) after the
    /// 48-byte header and checks for known file signatures (ZIP, EXE, MSI, CAB).
    /// Returns true if the decrypted header matches a known signature, false otherwise.
    /// This is near-instant compared to full file decryption.
    /// Works on raw encrypted blobs -- use <see cref="ExtractInnerBlob"/> first for .intunewin files.
    /// </summary>
    public static bool TryKeyQuick(string encryptedBlobPath, string base64Key, string base64IV)
    {
        try
        {
            var key = Convert.FromBase64String(base64Key);
            var iv = Convert.FromBase64String(base64IV);

            using var aes = Aes.Create();
            aes.Padding = PaddingMode.None; // don't validate padding on partial decrypt
            using var decryptor = aes.CreateDecryptor(key, iv);
            using var source = File.OpenRead(encryptedBlobPath);

            if (source.Length < 48 + 16) return false; // too small
            source.Seek(48, SeekOrigin.Begin);

            var block = new byte[16];
            if (source.Read(block, 0, 16) < 16) return false;

            var decrypted = decryptor.TransformBlock(block, 0, 16, block, 0);
            if (decrypted < 4) return false;

            // Check known file signatures in the decrypted first block
            if (block[0] == 0x50 && block[1] == 0x4B) return true; // ZIP (PK)
            if (block[0] == 0xD0 && block[1] == 0xCF) return true; // MSI/OLE
            if (block[0] == 0x4D && block[1] == 0x5A) return true; // EXE/DLL (MZ)
            if (block[0] == 0x4D && block[1] == 0x53 && block[2] == 0x43 && block[3] == 0x46) return true; // CAB (MSCF)

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// If the file is a .intunewin ZIP, extracts the inner encrypted blob from Contents/
    /// to a temp file and returns its path. If the file is already a raw blob (not a ZIP),
    /// returns the original path unchanged. Caller must delete the temp file when done
    /// (check if the returned path differs from the input).
    /// </summary>
    public static string? ExtractInnerBlob(string filePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(filePath);
            var innerEntry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Contains("Contents/", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(e.Name));
            if (innerEntry is null) return filePath; // valid ZIP but no Contents/ -- treat as raw

            var tempPath = Path.Combine(Path.GetTempPath(), $"wrapp_blob_{Guid.NewGuid():N}.bin");
            innerEntry.ExtractToFile(tempPath, overwrite: true);
            AppLogger.Info($"IntuneWin: extracted inner blob ({innerEntry.FullName}, {innerEntry.Length:N0} bytes) to temp");
            return tempPath;
        }
        catch (InvalidDataException)
        {
            // must-stay-silent: file is not a ZIP -- already a raw encrypted
            // blob. Returning the input path is the documented fallback so
            // the caller can decrypt directly without unwrapping.
            return filePath;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"IntuneWin: failed to extract inner blob -- {ex.Message}");
            return filePath;
        }
    }

    /// <summary>
    /// Extracts the inner encrypted file from a .intunewin ZIP and decrypts it
    /// using the keys embedded in detection.xml. Full end-to-end operation.
    /// </summary>
    public static async Task<string?> ExtractAndDecryptAsync(
        string intunewinPath, string outputDir,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var metadata = InspectPackage(intunewinPath);
        if (metadata is null || string.IsNullOrEmpty(metadata.EncryptionKey))
        {
            AppLogger.Warn("IntuneWin: no valid metadata/keys in package");
            return null;
        }

        // Extract the inner encrypted file from Contents/
        string? extractedPath = null;
        try
        {
            using var zip = ZipFile.OpenRead(intunewinPath);
            var innerEntry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Contains("Contents/", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(e.Name));
            if (innerEntry is null)
            {
                AppLogger.Warn("IntuneWin: no inner file found in Contents/");
                return null;
            }

            extractedPath = Path.Combine(outputDir, $"{innerEntry.Name}.extracted");
            innerEntry.ExtractToFile(extractedPath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"IntuneWin: extraction failed -- {ex.Message}");
            return null;
        }

        // Decrypt -- use only the filename; SetupFile can contain subdirectories
        // (e.g. "Script\InstallScript.ps1") that don't exist in the temp folder
        var outputName = Path.GetFileName(metadata.SetupFile);
        if (string.IsNullOrEmpty(outputName)) outputName = "decrypted";
        var decryptedPath = Path.Combine(outputDir, outputName);
        var success = await DecryptAsync(extractedPath, decryptedPath,
            metadata.EncryptionKey, metadata.InitializationVector, progress, ct,
            base64MacKey: metadata.MacKey)
            .ConfigureAwait(false);

        TryDelete(extractedPath);
        return success ? decryptedPath : null;
    }

    /// <summary>
    /// Validates that a decrypted file looks reasonable (non-empty, has known file signature).
    /// </summary>
    public static bool ValidateDecryptedFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length == 0) return false;

            // Check for common file signatures
            using var fs = File.OpenRead(path);
            var header = new byte[4];
            if (fs.Read(header) < 4) return false;

            // ZIP (PK), MSI (D0 CF), EXE/DLL (MZ), CAB (MSCF)
            if (header[0] == 0x50 && header[1] == 0x4B) return true; // ZIP/DOCX/XLSX
            if (header[0] == 0xD0 && header[1] == 0xCF) return true; // MSI/OLE
            if (header[0] == 0x4D && header[1] == 0x5A) return true; // EXE/DLL
            if (header[0] == 0x4D && header[1] == 0x53 && header[2] == 0x43 && header[3] == 0x46) return true; // CAB

            // If none matched but file is large enough, assume it's valid
            return fi.Length > 1024;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts app identity (name + version) from a decryptable .intunewin package.
    /// Decrypts to a temp file, checks if the result is a ZIP containing Config.json,
    /// parses App.Name and App.Version/DotVersion.
    /// Falls back to: MSI/EXE filename in the package -> SetupFile from detection.xml.
    /// Returns (name, version) or null if nothing found.
    /// </summary>
    public static async Task<(string Name, string Version)?> ExtractAppIdentityAsync(string intunewinPath)
    {
        // Run all heavy I/O (decrypt + ZIP parse) off the calling thread
        // so the UI stays responsive during inspect
        return await Task.Run(async () =>
        {
            var metadata = InspectPackage(intunewinPath);
            if (metadata is null) return null;
            if (string.IsNullOrEmpty(metadata.EncryptionKey))
                return FallbackIdentity(metadata);

            var tempDir = Path.Combine(Path.GetTempPath(), $"wrapp_inspect_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                var decryptedPath = await ExtractAndDecryptAsync(intunewinPath, tempDir);
                if (decryptedPath is null) return FallbackIdentity(metadata);

                // Try opening decrypted file as a ZIP and look for Config.json
                try
                {
                    using var zip = ZipFile.OpenRead(decryptedPath);

                    // Look for Config.json in common locations
                    var configEntry = zip.Entries.FirstOrDefault(e =>
                        e.Name.Equals("Config.json", StringComparison.OrdinalIgnoreCase))
                        ?? zip.Entries.FirstOrDefault(e =>
                        e.FullName.EndsWith("Script/Config.json", StringComparison.OrdinalIgnoreCase));

                    if (configEntry is not null)
                    {
                        using var reader = new StreamReader(configEntry.Open());
                        var json = reader.ReadToEnd();
                        var doc = System.Text.Json.JsonDocument.Parse(json);

                        string? name = null, version = null;
                        if (doc.RootElement.TryGetProperty("App", out var app))
                        {
                            if (app.TryGetProperty("Name", out var n)) name = n.GetString();
                            if (app.TryGetProperty("Version", out var v)) version = v.GetString();
                            if (string.IsNullOrEmpty(version) && app.TryGetProperty("DotVersion", out var dv))
                                version = dv.GetString()?.Replace(".", "_");
                        }

                        if (!string.IsNullOrEmpty(name))
                        {
                            AppLogger.Info($"IntuneWin: extracted app identity from Config.json: {name} {version}");
                            return (name, version ?? "0_0_0_0");
                        }
                    }

                    // Fallback: look for MSI/EXE in the ZIP and scrape version metadata
                    var installer = zip.Entries.FirstOrDefault(e =>
                        Path.GetExtension(e.Name).Equals(".msi", StringComparison.OrdinalIgnoreCase))
                        ?? zip.Entries.FirstOrDefault(e =>
                        Path.GetExtension(e.Name).Equals(".exe", StringComparison.OrdinalIgnoreCase));
                    if (installer is not null)
                    {
                        var installerName = Path.GetFileNameWithoutExtension(installer.Name);
                        var version = ScrapeInstallerVersion(installer, tempDir);
                        AppLogger.Info($"IntuneWin: using installer as identity: {installerName}, version={version}");
                        return (installerName, version);
                    }
                }
                catch
                {
                    // Not a ZIP -- single file, use the filename
                }

                return FallbackIdentity(metadata);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); }
                catch (Exception ex) { AppLogger.Warn($"IntuneWin: temp dir cleanup failed for {tempDir} -- {ex.Message}"); }
            }
        });
    }

    private static (string Name, string Version)? FallbackIdentity(EncryptionKeyInfo metadata)
    {
        // Use SetupFile name if it's not a generic script name
        var setup = metadata.SetupFile;
        if (!string.IsNullOrEmpty(setup)
            && !setup.Equals("InstallScript.ps1", StringComparison.OrdinalIgnoreCase)
            && !setup.StartsWith("Invoke-AppDeployToolkit", StringComparison.OrdinalIgnoreCase))
        {
            return (Path.GetFileNameWithoutExtension(setup), "0_0_0_0");
        }

        // Fallback: package name from detection.xml
        if (!string.IsNullOrEmpty(metadata.PackageName))
            return (metadata.PackageName, "0_0_0_0");

        // Ultimate fallback: .intunewin filename
        if (!string.IsNullOrEmpty(metadata.SourcePath))
            return (Path.GetFileNameWithoutExtension(metadata.SourcePath), "0_0_0_0");

        return null;
    }

    /// <summary>
    /// Extracts a ZIP entry (MSI/EXE) to a temp file and scrapes its version metadata.
    /// Returns the version as underscore-delimited (e.g. "105_2_1_3") or "0_0_0_0" if not found.
    /// </summary>
    private static string ScrapeInstallerVersion(ZipArchiveEntry entry, string tempDir)
    {
        var tempPath = Path.Combine(tempDir, entry.Name);
        try
        {
            entry.ExtractToFile(tempPath, overwrite: true);
            var ext = Path.GetExtension(entry.Name).ToLowerInvariant();

            string? version = null;
            if (ext == ".msi")
            {
                try
                {
                    var (_, _, productVersion) = MsiPropertyService.GetMsiMetadata(tempPath);
                    version = productVersion?.Trim();
                }
                // must-stay-silent: best-effort version scrape; null `version`
                // is the documented fallback path and triggers the outer
                // FallbackIdentity branch.
                catch { }
            }
            else
            {
                try
                {
                    var fvi = FileVersionInfo.GetVersionInfo(tempPath);
                    version = fvi.FileVersion?.Trim();
                }
                // must-stay-silent: same rationale as the MSI branch above.
                catch { }
            }

            if (!string.IsNullOrEmpty(version))
                return version.Replace(".", "_");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"IntuneWin: failed to scrape installer version -- {ex.Message}");
        }
        finally
        {
            TryDelete(tempPath);
        }
        return "0_0_0_0";
    }

    /// <summary>
    /// After decryption, detects the output file type and handles it:
    /// - ZIP: extracts contents into a subfolder named after the file, deletes the ZIP
    /// - Other (MSI, EXE, CAB): renames with the correct extension
    /// - Unknown: leaves as-is with .decrypted extension
    /// Returns the final output path (folder for ZIP, file for others).
    /// </summary>
    public static string FinalizeDecryptedOutput(string decryptedPath)
    {
        try
        {
            using var fs = File.OpenRead(decryptedPath);
            var header = new byte[4];
            if (fs.Read(header) < 4) return decryptedPath;
            fs.Close();

            var dir = Path.GetDirectoryName(decryptedPath)!;
            var name = Path.GetFileNameWithoutExtension(decryptedPath);

            // ZIP: extract to a folder
            if (header[0] == 0x50 && header[1] == 0x4B)
            {
                var extractDir = Path.Combine(dir, name);
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
                ZipFile.ExtractToDirectory(decryptedPath, extractDir);
                TryDelete(decryptedPath);
                AppLogger.Info($"IntuneWin: extracted ZIP contents to {extractDir}");
                return extractDir;
            }

            // Non-ZIP: rename with detected extension
            var ext = DetectExtension(header);
            if (ext is null) return decryptedPath;

            var newPath = Path.Combine(dir, $"{name}{ext}");
            if (File.Exists(newPath) && newPath != decryptedPath)
            {
                int suffix = 2;
                while (File.Exists(Path.Combine(dir, $"{name}_{suffix}{ext}"))) suffix++;
                newPath = Path.Combine(dir, $"{name}_{suffix}{ext}");
            }
            File.Move(decryptedPath, newPath);
            return newPath;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"IntuneWin: failed to finalize decrypted output -- {ex.Message}");
            return decryptedPath;
        }
    }

    private static string? DetectExtension(byte[] header)
    {
        if (header[0] == 0x50 && header[1] == 0x4B) return ".zip";
        if (header[0] == 0xD0 && header[1] == 0xCF) return ".msi";
        if (header[0] == 0x4D && header[1] == 0x5A) return ".exe";
        if (header[0] == 0x4D && header[1] == 0x53 && header[2] == 0x43 && header[3] == 0x46) return ".cab";
        return null;
    }

    private static void TryDelete(string path)
    {
        // must-stay-silent: helper IS the silent-delete pattern. Used in
        // cleanup paths where the file may not exist or be locked by an
        // antivirus scan; logging would create noise during routine cleanup.
        try { File.Delete(path); } catch { }
    }
}
