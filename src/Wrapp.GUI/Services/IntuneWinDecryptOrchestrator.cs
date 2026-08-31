using System.IO;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// UI-agnostic orchestrator for .intunewin decryption. Composes IntuneWinService
/// primitives with key source logic (embedded, manual, vault, brute force, CSV).
/// Used by ToolsViewModel, GeneralViewModel, and InventoryViewModel.
/// </summary>
public class IntuneWinDecryptOrchestrator
{
    private readonly EncryptionKeyStoreService _keyStore;

    public IntuneWinDecryptOrchestrator(EncryptionKeyStoreService keyStore)
    {
        _keyStore = keyStore;
    }

    /// <summary>Result of a decrypt operation.</summary>
    public record DecryptResult(
        bool Success,
        string? OutputPath,
        string Message,
        string? WinningKey = null,
        string? WinningIV = null);

    // ------------------------------------------------------------------
    // Blob preparation
    // ------------------------------------------------------------------

    /// <summary>
    /// If the file is a .intunewin ZIP, extracts the inner encrypted blob to a temp file.
    /// If it's already a raw blob, returns the original path.
    /// Caller must delete the temp file when done (check if returned path differs from input).
    /// </summary>
    public static string? PrepareBlob(string filePath) =>
        IntuneWinService.ExtractInnerBlob(filePath);

    // ------------------------------------------------------------------
    // Mode 1: Embedded keys (original .intunewin with detection.xml)
    // ------------------------------------------------------------------

    public async Task<DecryptResult> DecryptWithEmbeddedKeysAsync(
        string intunewinPath, string outputDir,
        IProgress<int>? progress = null)
    {
        AppLogger.Info($"Decrypt orchestrator: embedded keys -- {intunewinPath}");

        // IntuneWinService.ExtractAndDecryptAsync is genuinely async with
        // ConfigureAwait(false) on its inner streams, so we await directly --
        // no Task.Run thread hop needed to keep UI responsive.
        var outputPath = await IntuneWinService.ExtractAndDecryptAsync(
            intunewinPath, outputDir, progress);

        if (outputPath is not null)
        {
            var valid = IntuneWinService.ValidateDecryptedFile(outputPath);
            var finalPath = IntuneWinService.FinalizeDecryptedOutput(outputPath);
            var msg = valid
                ? $"Decrypted successfully to {finalPath}"
                : $"Decrypted to {finalPath} (file signature not recognized -- verify manually)";
            AppLogger.Info($"Decrypt orchestrator: embedded -- output={finalPath}, valid={valid}");
            return new DecryptResult(true, finalPath, msg);
        }

        AppLogger.Warn("Decrypt orchestrator: embedded keys failed");
        return new DecryptResult(false, null, "Decryption failed -- embedded keys could not decrypt the content.");
    }

    // ------------------------------------------------------------------
    // Mode 2: Manual key + IV
    // ------------------------------------------------------------------

    public async Task<DecryptResult> DecryptWithKeyPairAsync(
        string blobPath, string outputDir, string key, string iv,
        IProgress<int>? progress = null, string? outputBaseName = null,
        string? macKey = null)
    {
        var baseName = outputBaseName ?? Path.GetFileNameWithoutExtension(blobPath);
        var tempOutput = Path.Combine(outputDir, $"{baseName}.decrypted");

        AppLogger.Info("Decrypt orchestrator: key pair");
        // Forward the MAC key when the caller has it so DecryptAsync can
        // authenticate the blob before decrypting (null for manual/CSV sources).
        var success = await IntuneWinService.DecryptAsync(
            blobPath, tempOutput, key, iv, progress, base64MacKey: macKey);

        if (success)
        {
            var valid = IntuneWinService.ValidateDecryptedFile(tempOutput);
            var finalPath = IntuneWinService.FinalizeDecryptedOutput(tempOutput);
            var msg = valid
                ? $"Decrypted successfully to {finalPath}"
                : $"Decrypted to {finalPath} (file signature not recognized -- verify manually)";
            AppLogger.Info($"Decrypt orchestrator: key pair success -- output={finalPath}, valid={valid}");
            return new DecryptResult(true, finalPath, msg);
        }

        AppLogger.Warn("Decrypt orchestrator: key pair failed -- wrong key or corrupt data");
        return new DecryptResult(false, null, "Decryption failed -- wrong key or corrupted file.");
    }

    // ------------------------------------------------------------------
    // Mode 3: Vault lookup (tenant + app ID)
    // ------------------------------------------------------------------

    public async Task<DecryptResult> DecryptWithVaultAsync(
        string blobPath, string outputDir, string tenantId, string appId,
        IProgress<int>? progress = null, IProgress<string>? status = null,
        string? outputBaseName = null)
    {
        status?.Report("Looking up keys in vault...");
        AppLogger.Info($"Decrypt orchestrator: vault lookup -- tenant={tenantId}, app={appId}");

        var vaultKeys = await _keyStore.GetKeysAsync(tenantId, appId);
        if (vaultKeys is null)
        {
            AppLogger.Warn("Decrypt orchestrator: no vault keys found");
            return new DecryptResult(false, null, "No keys found in vault for that tenant/app ID.");
        }

        AppLogger.Info($"Decrypt orchestrator: vault keys loaded -- displayName={vaultKeys.DisplayName}");
        var result = await DecryptWithKeyPairAsync(blobPath, outputDir,
            vaultKeys.EncryptionKey, vaultKeys.InitializationVector, progress, outputBaseName,
            macKey: vaultKeys.MacKey);
        return result with { WinningKey = vaultKeys.EncryptionKey, WinningIV = vaultKeys.InitializationVector };
    }

    // ------------------------------------------------------------------
    // Mode 4: Vault brute force (try all saved keys)
    // ------------------------------------------------------------------

    public async Task<DecryptResult> BruteForceDecryptAsync(
        string blobPath, string outputDir,
        IProgress<int>? progress = null, IProgress<string>? status = null,
        string? outputBaseName = null)
    {
        // DevOps vault is the single source of brute-force keys -- per-workstation
        // local caches are no longer used.
        status?.Report("Loading DevOps vault keys...");
        var allKeys = new List<(EncryptionKeyInfo Key, string RelativePath)>();
        try
        {
            allKeys = await _keyStore.LoadAllDevOpsKeysAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Decrypt orchestrator: DevOps key load failed -- {ex.Message}");
        }

        if (allKeys.Count == 0)
        {
            AppLogger.Warn("Decrypt orchestrator: brute force -- no keys in DevOps vault");
            // The empty-list outcome can mean either "vault is
            // configured but has no keys" OR "user opted out of the vault
            // entirely via the feature gate". The service is UI-agnostic
            // so it can't disambiguate -- name both options in the message
            // so the user knows which lever to pull.
            return new DecryptResult(false, null,
                "No keys available -- the Azure DevOps vault is empty or disabled in Settings -> Key Vault. "
                + "Configure the vault and save some keys, or use the Manual / CSV key sources.");
        }

        // Count only - per-key logging at Info flooded 2 lines per vault key
        // (400+ lines for a 200-key vault) into a 1MB×5 rotation, flushing the
        // session's history. The status reporter already shows live per-key
        // progress; the log needs the count and the eventual match.
        AppLogger.Info($"Decrypt orchestrator: brute force -- {allKeys.Count} DevOps key(s)");

        // Phase 1: fast scan
        status?.Report($"Quick-scanning {allKeys.Count} keys...");
        (EncryptionKeyInfo Key, string RelativePath)? match = null;

        for (int i = 0; i < allKeys.Count; i++)
        {
            var (k, relPath) = allKeys[i];
            var label = !string.IsNullOrEmpty(k.DisplayName) ? k.DisplayName
                : !string.IsNullOrEmpty(k.PackageName) ? k.PackageName
                : k.AppId;
            status?.Report($"Testing key {i + 1}/{allKeys.Count}: {label} ({relPath})");
            progress?.Report((int)((i + 1) * 100.0 / allKeys.Count));

            var hit = await Task.Run(() =>
                IntuneWinService.TryKeyQuick(blobPath, k.EncryptionKey, k.InitializationVector));

            if (hit)
            {
                AppLogger.Info($"Decrypt orchestrator: brute force match -- '{label}' ({relPath})");
                match = (k, relPath);
                break;
            }
        }

        if (match is null)
        {
            var msg = $"None of the {allKeys.Count} vault keys could decrypt this file.";
            AppLogger.Warn($"Decrypt orchestrator: brute force -- {msg}");
            return new DecryptResult(false, null, msg);
        }

        // Phase 2: full decrypt
        var winner = match.Value;
        var winLabel = !string.IsNullOrEmpty(winner.Key.DisplayName) ? winner.Key.DisplayName
            : !string.IsNullOrEmpty(winner.Key.PackageName) ? winner.Key.PackageName
            : winner.Key.AppId;
        status?.Report($"Match found: {winLabel} ({winner.RelativePath}). Decrypting...");
        progress?.Report(0);

        var baseName = outputBaseName ?? Path.GetFileNameWithoutExtension(blobPath);
        var tempOutput = Path.Combine(outputDir, $"{baseName}.decrypted");
        var success = await IntuneWinService.DecryptAsync(
            blobPath, tempOutput,
            winner.Key.EncryptionKey, winner.Key.InitializationVector, progress,
            base64MacKey: winner.Key.MacKey);

        if (success)
        {
            var finalPath = IntuneWinService.FinalizeDecryptedOutput(tempOutput);
            var msg = $"Decrypted with '{winLabel}' ({winner.RelativePath}). Output: {finalPath}";
            AppLogger.Info($"Decrypt orchestrator: brute force decrypted to {finalPath}");
            return new DecryptResult(true, finalPath, msg, winner.Key.EncryptionKey, winner.Key.InitializationVector);
        }

        AppLogger.Warn($"Decrypt orchestrator: brute force header match but full decrypt failed for '{winLabel}'");
        return new DecryptResult(false, null,
            $"Key '{winLabel}' matched header but full decryption failed. File may be corrupted.");
    }

    // ------------------------------------------------------------------
    // Mode 5: CSV key list
    // ------------------------------------------------------------------

    public async Task<DecryptResult> CsvDecryptAsync(
        string blobPath, string outputDir, string csvPath,
        IProgress<int>? progress = null, IProgress<string>? status = null,
        string? outputBaseName = null)
    {
        if (!File.Exists(csvPath))
            return new DecryptResult(false, null, "CSV file not found.");

        status?.Report("Parsing CSV...");
        var keyPairs = await Task.Run(() => ParseCsvKeys(csvPath));
        if (keyPairs.Count == 0)
        {
            AppLogger.Warn("Decrypt orchestrator: CSV -- no valid key pairs");
            return new DecryptResult(false, null,
                "No valid key pairs found in CSV. Expected columns: EncryptionKey (or Key) and InitializationVector (or IV).");
        }

        AppLogger.Info($"Decrypt orchestrator: CSV -- {keyPairs.Count} key pair(s) from {csvPath}");

        // Phase 1: fast scan
        int matchIdx = -1;
        for (int i = 0; i < keyPairs.Count; i++)
        {
            var (key, iv) = keyPairs[i];
            status?.Report($"Testing CSV key {i + 1}/{keyPairs.Count}...");
            progress?.Report((int)((i + 1) * 100.0 / keyPairs.Count));

            var hit = await Task.Run(() => IntuneWinService.TryKeyQuick(blobPath, key, iv));
            if (hit) { matchIdx = i; break; }
        }

        if (matchIdx < 0)
        {
            var msg = $"None of the {keyPairs.Count} CSV keys could decrypt this file.";
            AppLogger.Warn($"Decrypt orchestrator: CSV -- {msg}");
            return new DecryptResult(false, null, msg);
        }

        // Phase 2: full decrypt
        var (winKey, winIV) = keyPairs[matchIdx];
        status?.Report($"Match found: CSV row #{matchIdx + 1}. Decrypting...");
        progress?.Report(0);
        AppLogger.Info($"Decrypt orchestrator: CSV match on row #{matchIdx + 1}");

        var baseName = outputBaseName ?? Path.GetFileNameWithoutExtension(blobPath);
        var tempOutput = Path.Combine(outputDir, $"{baseName}.decrypted");
        var success = await IntuneWinService.DecryptAsync(
            blobPath, tempOutput, winKey, winIV, progress);

        if (success)
        {
            var finalPath = IntuneWinService.FinalizeDecryptedOutput(tempOutput);
            var msg = $"Decrypted with CSV key #{matchIdx + 1}. Output: {finalPath}";
            AppLogger.Info($"Decrypt orchestrator: CSV decrypted to {finalPath}");
            return new DecryptResult(true, finalPath, msg, winKey, winIV);
        }

        AppLogger.Warn($"Decrypt orchestrator: CSV header match but full decrypt failed for row #{matchIdx + 1}");
        return new DecryptResult(false, null,
            $"CSV key #{matchIdx + 1} matched header but full decryption failed.");
    }

    // ------------------------------------------------------------------
    // CSV parsing
    // ------------------------------------------------------------------

    public static List<(string Key, string IV)> ParseCsvKeys(string csvPath)
    {
        var results = new List<(string, string)>();
        var lines = File.ReadAllLines(csvPath);
        if (lines.Length < 2) return results;

        var headers = lines[0].Split(',').Select(h => h.Trim().Trim('"')).ToArray();
        int keyIdx = -1, ivIdx = -1;
        for (int i = 0; i < headers.Length; i++)
        {
            var h = headers[i];
            if (h.Equals("EncryptionKey", StringComparison.OrdinalIgnoreCase) || h.Equals("Key", StringComparison.OrdinalIgnoreCase))
                keyIdx = i;
            else if (h.Equals("InitializationVector", StringComparison.OrdinalIgnoreCase) || h.Equals("IV", StringComparison.OrdinalIgnoreCase))
                ivIdx = i;
        }

        if (keyIdx < 0 || ivIdx < 0) return results;

        for (int row = 1; row < lines.Length; row++)
        {
            var cols = lines[row].Split(',').Select(c => c.Trim().Trim('"')).ToArray();
            if (cols.Length <= Math.Max(keyIdx, ivIdx)) continue;
            var key = cols[keyIdx];
            var iv = cols[ivIdx];
            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(iv))
                results.Add((key, iv));
        }

        return results;
    }
}
