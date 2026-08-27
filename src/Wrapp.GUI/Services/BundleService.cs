using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wrapp.Helpers;
using Wrapp.Models;

namespace Wrapp.Services;

public static class BundleService
{
    /// <summary>
    /// Resolves the subdirectory path from settings tokens using App data.
    /// </summary>
    public static string ResolveSubDirectory(AppSettings settings, AppSection app)
    {
        var fmt = string.IsNullOrWhiteSpace(settings.DirectoryFormat)
            ? @"{Company}\{Name}\{Version}"
            : settings.DirectoryFormat;

        return fmt
            .Replace("{Company}",    Sanitize(app.Company))
            .Replace("{Name}",       Sanitize(app.Name))
            .Replace("{Version}",    Sanitize(app.Version))
            .Replace("{DotVersion}", Sanitize(app.DotVersion))
            .Replace("{Language}",   Sanitize(app.Language));
    }

    /// <summary>
    /// Given the path to Config.json, returns the bundle root directory.
    /// If Config.json lives in a "Script" subfolder (new layout), returns its parent.
    /// If Config.json is at the root (old layout), returns its directory directly.
    /// </summary>
    public static string GetBundleRoot(string configPath)
    {
        var dir = Path.GetDirectoryName(configPath) ?? configPath;
        if (string.Equals(Path.GetFileName(dir), BundlePaths.ScriptFolder, StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(dir) ?? dir;
        return dir;
    }

    /// <summary>
    /// Locates Config.json within a dropped/browsed folder.
    /// Checks Script/Config.json (new layout) first, then root (old layout).
    /// Returns null if not found.
    /// </summary>
    public static string? FindConfigJson(string folder)
    {
        var scriptPath = BundlePaths.ConfigJson(folder);
        if (File.Exists(scriptPath)) return scriptPath;
        var rootPath = Path.Combine(folder, BundlePaths.ConfigFileName);
        if (File.Exists(rootPath)) return rootPath;
        return null;
    }

    /// <summary>
    /// Scans the bundle's binary folder (<c>B/</c> for Appease, <c>Files/</c>
    /// for PSADT) and returns the first <c>.exe</c> and first <c>.msi</c>/
    /// <c>.msp</c> filename found. Used as a fill-if-empty fallback by paths
    /// that populate a bundle without going through the usual drag-drop
    /// detection (e.g., Import-to-Wrapp after a full-clone decrypt).
    /// Alphabetical ordering makes the result deterministic for tests.
    /// </summary>
    public static (string? ExeFileName, string? MsiFileName) DetectInstallersInBinaryFolder(
        string bundleRoot, AppConfigModel config)
    {
        var fw     = ScriptFrameworkProvider.Parse(config.App.ScriptFramework);
        var binDir = BundlePaths.BinaryFolder(bundleRoot, fw);
        if (!Directory.Exists(binDir)) return (null, null);

        string? exe = null;
        string? msi = null;
        foreach (var file in Directory.EnumerateFiles(binDir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".exe" && exe is null) exe = Path.GetFileName(file);
            else if ((ext == ".msi" || ext == ".msp") && msi is null) msi = Path.GetFileName(file);
        }
        return (exe, msi);
    }

    /// <summary>
    /// Creates the full bundle folder structure and writes all files.
    /// Layout: B/, Script/, Shortcuts/, Icon/ under bundleDirectory.
    /// Config.json and scripts go into Script/.
    /// </summary>
    public static async Task CreateBundleAsync(
        AppConfigModel config,
        AppSettings settings,
        string bundleDirectory,
        ImageSource? icon)
    {
        // Sanity guard: CreateBundleAsync creates Script/, B/, Shortcuts/
        // under bundleDirectory. If the caller accidentally passes the
        // Script folder itself (e.g., because an earlier buggy save
        // chained `_configPath` through a Script/Script/Config.json path
        // and GetBundleRoot's single-level step-up didn't unwind far
        // enough), we'd end up creating Script/Script/, Script/B/, etc.
        // Step up one level instead and log so the call site can be
        // investigated.
        var trimmed = bundleDirectory.TrimEnd('/', '\\');
        var leafName = Path.GetFileName(trimmed);
        if (string.Equals(leafName, BundlePaths.ScriptFolder, StringComparison.OrdinalIgnoreCase))
        {
            var corrected = Path.GetDirectoryName(trimmed);
            if (!string.IsNullOrEmpty(corrected))
            {
                AppLogger.Warn(
                    $"Bundle: CreateBundleAsync was passed a Script folder as bundleDirectory " +
                    $"('{bundleDirectory}'). Correcting to parent ('{corrected}') to avoid " +
                    $"creating nested Script/Script/B/... layout. Investigate the caller.");
                bundleDirectory = corrected;
            }
        }

        Directory.CreateDirectory(bundleDirectory);

        var fw = ScriptFrameworkProvider.Parse(config.App.ScriptFramework);

        // Create subfolders
        var scriptDir    = BundlePaths.ScriptDir(bundleDirectory);
        var binDir       = BundlePaths.BinaryFolder(bundleDirectory, fw);
        var shortcutDir  = BundlePaths.ShortcutDir(bundleDirectory);
        Directory.CreateDirectory(scriptDir);
        Directory.CreateDirectory(binDir);
        Directory.CreateDirectory(shortcutDir);

        // Update DomainConfig.AppFolder to match the current app identity.
        // The SCCM module builds ContentLocation as: isDistPath\AppFolder\Version
        // so AppFolder should be Company\Name (version is appended by the module).
        var appFolder = Path.Combine(Sanitize(config.App.Company), Sanitize(config.App.Name));
        if (!string.IsNullOrEmpty(appFolder))
        {
            foreach (var domain in config.Domains)
                domain.AppFolder = appFolder;
        }

        // Write Config.json into Script/
        var configPath = Path.Combine(scriptDir, BundlePaths.ConfigFileName);
        await ConfigFileService.SaveAsync(config, configPath);
        AppLogger.Info($"Bundle: wrote Config.json to {configPath}");

        // Write framework-specific scripts into Script/ (only if they don't exist yet).
        // Existing scripts are preserved so user edits are not overwritten.
        foreach (var scriptName in ScriptFrameworkProvider.GetBundleScripts(fw))
            await WriteScriptIfMissingAsync(scriptDir, scriptName, config.App);

        // PSADT: copy template structure (bundled or custom path)
        if (fw == Models.ScriptFramework.PSADT)
        {
            var psadtPath = ResolvePsadtTemplatePath(settings.PsadtTemplatePath);
            if (!string.IsNullOrEmpty(psadtPath))
                await CopyPsadtTemplateAsync(psadtPath, bundleDirectory, config.App);
            else
                AppLogger.Warn("Bundle: PSADT framework selected but no template path found. Set the path in Settings or ensure psadt-template/ exists alongside the application.");
        }

        // Remove any legacy Install.cmd/Uninstall.cmd left over from the
        // batch-file shortcut era, then write the real .lnk shortcuts
        // (only if missing so user customizations are preserved).
        CleanupLegacyCmdShortcuts(shortcutDir);
        foreach (var (lnkName, targetPath, arguments) in ScriptFrameworkProvider.GetShortcuts(fw))
        {
            if (!File.Exists(Path.Combine(shortcutDir, lnkName)))
                WriteLnkShortcut(shortcutDir, lnkName, targetPath, arguments);
        }

        // Save icon as PNG -- respect App.IconFile as the source of truth.
        // SEC-2: IconFile comes straight from the bundle's Config.json, so it
        // must resolve INSIDE the bundle — a rooted or ..-escaping value would
        // otherwise write a file anywhere on disk. Escapees fall through to
        // the derive-from-App.Name branch.
        var containedIconPath = ResolveInsideBundle(bundleDirectory, config.App.IconFile);
        if (containedIconPath is not null && icon is not null)
        {
            // Always write/overwrite -- handles both first save and deferred icon replacement
            Directory.CreateDirectory(Path.GetDirectoryName(containedIconPath)!);
            await SaveIconAsPngAsync(icon, containedIconPath);
            AppLogger.Info($"Bundle: wrote icon to {containedIconPath}");
        }
        else if (icon is not null)
        {
            // No IconFile set yet -- fallback: derive from App.Name (first-time save)
            var iconFolder = Path.Combine(bundleDirectory, settings.IconFolderName);
            Directory.CreateDirectory(iconFolder);
            var iconName = Sanitize(config.App.Name);
            if (string.IsNullOrEmpty(iconName)) iconName = "Icon";
            var relPath = Path.Combine(settings.IconFolderName, $"{iconName}.png");
            var iconPath = Path.Combine(bundleDirectory, relPath);
            await SaveIconAsPngAsync(icon, iconPath);
            config.App.IconFile = relPath;
            AppLogger.Info($"Bundle: wrote icon to {iconPath}, set IconFile={relPath}");

            // Re-save Config.json since we updated IconFile after the initial write
            var configRewritePath = BundlePaths.ConfigJson(bundleDirectory);
            await ConfigFileService.SaveAsync(config, configRewritePath);
        }
    }

    // -----------------------------------------------------------------------
    // Populate workspace from decrypted .intunewin content
    // -----------------------------------------------------------------------

    /// <summary>
    /// Takes decrypted output (folder or single file) and populates a temp workspace
    /// with the content, detecting framework and ensuring required files exist.
    /// </summary>
    public static async Task PopulateFromDecryptedContentAsync(
        string decryptedPath, string bundleRoot,
        AppConfigModel config, AppSettings settings)
    {
        if (Directory.Exists(decryptedPath))
        {
            // Decrypted content is a folder (extracted ZIP) -- copy into workspace
            var fw = ScriptFrameworkProvider.DetectFromFolder(decryptedPath);
            config.App.ScriptFramework = fw.ToString();
            AppLogger.Info($"Bundle: populating workspace from folder, framework={fw}");

            await CopyDirectoryContentsAsync(decryptedPath, bundleRoot);

            // Ensure required directories exist
            var scriptDir = BundlePaths.ScriptDir(bundleRoot);
            var binDir = BundlePaths.BinaryFolder(bundleRoot, fw);
            var shortcutDir = BundlePaths.ShortcutDir(bundleRoot);
            Directory.CreateDirectory(scriptDir);
            Directory.CreateDirectory(binDir);
            Directory.CreateDirectory(shortcutDir);

            // Ensure required scripts exist (write defaults for any missing ones)
            foreach (var scriptName in ScriptFrameworkProvider.GetBundleScripts(fw))
                await WriteScriptIfMissingAsync(scriptDir, scriptName, config.App);

            // PSADT: ensure template files exist
            if (fw == ScriptFramework.PSADT)
            {
                var psadtPath = ResolvePsadtTemplatePath(settings.PsadtTemplatePath);
                if (!string.IsNullOrEmpty(psadtPath)
                    && !File.Exists(Path.Combine(bundleRoot, "Invoke-AppDeployToolkit.exe")))
                {
                    await CopyPsadtTemplateAsync(psadtPath, bundleRoot, config.App);
                }
            }

            // Clean up legacy .cmd shortcuts and write .lnk shortcuts if missing
            CleanupLegacyCmdShortcuts(shortcutDir);
            foreach (var (lnkName, targetPath, arguments) in ScriptFrameworkProvider.GetShortcuts(fw))
            {
                if (!File.Exists(Path.Combine(shortcutDir, lnkName)))
                    WriteLnkShortcut(shortcutDir, lnkName, targetPath, arguments);
            }

            AppLogger.Info($"Bundle: workspace populated from decrypted folder ({fw})");
        }
        else if (File.Exists(decryptedPath))
        {
            // Single file (MSI/EXE/CAB) -- place in binary folder
            var fw = ScriptFrameworkProvider.Parse(config.App.ScriptFramework);
            var binDir = Path.Combine(bundleRoot, ScriptFrameworkProvider.GetBinaryFolderName(fw));
            Directory.CreateDirectory(binDir);

            var fileName = Path.GetFileName(decryptedPath);
            var destPath = Path.Combine(binDir, fileName);
            // Large installer payloads (EXE/MSI) can be hundreds of MB on
            // network storage. .NET offers no native async File.Move, so we
            // run on the thread pool to keep the UI thread responsive.
            await Task.Run(() => File.Move(decryptedPath, destPath, overwrite: true));

            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ext is ".msi" or ".msp")
                config.App.MSIFile = fileName;
            else
                config.App.EXEFile = fileName;

            AppLogger.Info($"Bundle: placed single file {fileName} in {ScriptFrameworkProvider.GetBinaryFolderName(fw)}/");
        }
    }

    /// <summary>
    /// Recursively copies all files and subdirectories from source to destination.
    /// Existing files are overwritten. Runs on the thread pool so a multi-GB
    /// copy (typical of decrypted Intune payloads) doesn't block the UI thread.
    /// </summary>
    /// <summary>PowerShell single-quoted-literal escape: ' doubles. The only
    /// safe way to splice a value into <c>'...'</c> in generated PS.</summary>
    internal static string PsQuote(string? value) => (value ?? string.Empty).Replace("'", "''");

    /// <summary>
    /// SEC-2 containment: resolves <paramref name="relative"/> against the
    /// bundle root and returns the full path only when it stays inside the
    /// bundle. Rooted paths and <c>..</c> escapes return null (logged).
    /// </summary>
    internal static string? ResolveInsideBundle(string bundleDirectory, string? relative)
    {
        if (string.IsNullOrEmpty(relative)) return null;
        try
        {
            var root = Path.GetFullPath(bundleDirectory);
            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return full;
            AppLogger.Warn($"Bundle: refused path escaping the bundle: \"{relative}\"");
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Bundle: unusable relative path \"{relative}\" -- {ex.Message}");
            return null;
        }
    }

    private static Task CopyDirectoryContentsAsync(string sourceDir, string destDir)
        => Task.Run(() => CopyDirectoryContentsCore(sourceDir, destDir));

    private static void CopyDirectoryContentsCore(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
            CopyDirectoryContentsCore(subDir, destSubDir);
        }
    }

    // -----------------------------------------------------------------------
    // Internal
    // -----------------------------------------------------------------------

    private static async Task WriteScriptIfMissingAsync(string dir, string scriptName, AppSection app)
    {
        var destPath = Path.Combine(dir, scriptName);
        if (File.Exists(destPath))
        {
            AppLogger.Info($"Bundle: skipping {scriptName} (already exists)");
            return;
        }
        await WriteScriptAsync(dir, scriptName, app);
    }

    private static async Task WriteScriptAsync(string dir, string scriptName, AppSection app)
    {
        // Read from the bundled framework template folder
        var fw = ScriptFrameworkProvider.Parse(app.ScriptFramework);
        var templateFolder = ScriptFrameworkProvider.GetTemplateFolderName(fw);
        var templatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, templateFolder, scriptName);

        if (!File.Exists(templatePath))
        {
            AppLogger.Warn($"Bundle: template file not found: {templatePath}");
            return;
        }

        var content = await File.ReadAllTextAsync(templatePath, Encoding.UTF8);
        content = ApplyTokens(content, app);

        var destPath = Path.Combine(dir, scriptName);
        await File.WriteAllTextAsync(destPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        AppLogger.Info($"Bundle: wrote {scriptName} to {destPath}");
    }

    // One resolver façade for the whole app: TemplateService.ApplyTokens
    // (→ PlaceholderService.Expand). The former ApplyTokensPublic duplicate
    // is gone — callers use TemplateService.ApplyTokens directly.
    private static string ApplyTokens(string content, AppSection app)
        => TemplateService.ApplyTokens(content, app);

    // Icon ceiling. SCCM's New-CMApplication current-branch cmdlet accepts up
    // to 512x512; older site builds capped at 256x256. Intune Win32 and PSADT
    // both accept 512x512. 512 keeps high-DPI detail while still landing under
    // every cap we've seen reject -- observed rejection on this surface was
    // 1425x1425 (Microsoft's own dotNetFx35setup.exe native icon).
    private const int IconMaxDimension = 512;

    public static async Task SaveIconAsPngAsync(ImageSource icon, string path)
    {
        try
        {
            if (icon is not BitmapSource bmp) return;

            // Downscale icons that exceed IconMaxDimension before encoding so
            // the resulting PNG always fits within SCCM/Intune/PSADT caps.
            // Installers (especially Microsoft's) often ship native icon
            // resources at 1024px+, which SCCM's icon validator rejects with
            // an opaque "Validation of input parameters failed" message.
            BitmapSource toEncode = bmp;
            if (bmp.PixelWidth > IconMaxDimension || bmp.PixelHeight > IconMaxDimension)
            {
                var scale = Math.Min(
                    (double)IconMaxDimension / bmp.PixelWidth,
                    (double)IconMaxDimension / bmp.PixelHeight);
                toEncode = new TransformedBitmap(bmp, new System.Windows.Media.ScaleTransform(scale, scale));
                AppLogger.Info(
                    $"Bundle: icon downscaled {bmp.PixelWidth}x{bmp.PixelHeight} -> "
                    + $"{toEncode.PixelWidth}x{toEncode.PixelHeight} (SCCM/Intune {IconMaxDimension}x{IconMaxDimension} cap)");
            }

            // Encode to byte array on UI thread (BitmapSource requires STA)
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(toEncode));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            var bytes = ms.ToArray();

            // Write to disk off UI thread
            await Task.Run(() => File.WriteAllBytes(path, bytes));
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Bundle: failed to save icon PNG: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes a Windows .lnk shortcut via the WScript.Shell COM API.
    /// Arguments should use relative paths (e.g. "..\Script\InstallScript.ps1")
    /// since WorkingDirectory is left empty so Windows uses the .lnk's own
    /// folder at launch time. This keeps shortcuts portable across moved
    /// bundles, mapped drives, and UNC paths.
    /// </summary>
    private static void WriteLnkShortcut(string dir, string lnkName, string targetPath, string arguments)
    {
        var destPath = Path.Combine(dir, lnkName);

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            AppLogger.Warn($"Bundle: WScript.Shell COM type not available, skipping {lnkName}");
            return;
        }

        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            dynamic dShell = shell!;
            dynamic shortcut = dShell.CreateShortcut(destPath);
            shortcut.TargetPath = targetPath;
            shortcut.Arguments = arguments;
            // Leave WorkingDirectory empty so relative paths in Arguments resolve
            // against the .lnk's own folder at launch time (the only way to keep
            // a .lnk portable across moved bundles).
            shortcut.WorkingDirectory = string.Empty;
            shortcut.Save();
            Marshal.FinalReleaseComObject(shortcut);
            AppLogger.Info($"Bundle: wrote shortcut {lnkName} to {destPath}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Bundle: failed to write shortcut {lnkName}: {ex.Message}");
        }
        finally
        {
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>
    /// Removes any legacy Install.cmd/Uninstall.cmd batch files from a
    /// Shortcuts folder. Wrapp used to create these as a stand-in for real
    /// Windows shortcuts; they're replaced by .lnk files now.
    /// </summary>
    private static void CleanupLegacyCmdShortcuts(string shortcutDir)
    {
        if (!Directory.Exists(shortcutDir)) return;
        foreach (var name in new[] { "Install.cmd", "Uninstall.cmd" })
        {
            var path = Path.Combine(shortcutDir, name);
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                    AppLogger.Info($"Bundle: removed legacy shortcut file {path}");
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Bundle: could not remove legacy shortcut {path}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Resolves the PSADT template path: uses the settings override if configured,
    /// otherwise falls back to the bundled psadt-template/ folder next to the application.
    /// </summary>
    private static string? ResolvePsadtTemplatePath(string? settingsPath)
    {
        // Settings override takes priority
        if (!string.IsNullOrEmpty(settingsPath) && Directory.Exists(settingsPath))
            return settingsPath;

        // Bundled template: {app_dir}/psadt-template/
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var bundled = Path.Combine(appDir, "psadt-template");
        if (Directory.Exists(bundled))
            return bundled;

        return null;
    }

    /// <summary>
    /// Copies the PSADT v4 template structure into the bundle directory.
    /// Tokenizes Invoke-AppDeployToolkit.ps1 with app metadata.
    /// Copies the PSAppDeployToolkit module, exe wrapper, Assets, Config, Strings, SupportFiles.
    /// Only copies files that don't already exist (preserves user edits).
    /// </summary>
    private static async Task CopyPsadtTemplateAsync(string templatePath, string bundleDir, Models.AppSection app)
    {
        if (!Directory.Exists(templatePath))
        {
            AppLogger.Warn($"Bundle: PSADT template path not found: {templatePath}");
            return;
        }

        // Copy Invoke-AppDeployToolkit.exe (binary, no tokenization)
        var exeSrc = Path.Combine(templatePath, "Invoke-AppDeployToolkit.exe");
        var exeDst = Path.Combine(bundleDir, "Invoke-AppDeployToolkit.exe");
        if (File.Exists(exeSrc) && !File.Exists(exeDst))
        {
            File.Copy(exeSrc, exeDst);
            AppLogger.Info("Bundle: copied Invoke-AppDeployToolkit.exe");
        }

        // Copy Invoke-AppDeployToolkit.ps1 with token replacement (only if missing)
        var ps1Src = Path.Combine(templatePath, "Invoke-AppDeployToolkit.ps1");
        var ps1Dst = Path.Combine(bundleDir, "Invoke-AppDeployToolkit.ps1");
        if (File.Exists(ps1Src) && !File.Exists(ps1Dst))
        {
            var content = await File.ReadAllTextAsync(ps1Src);
            // Replace the default empty adtSession variables with app metadata.
            // Values MUST be PS-single-quote-escaped (' -> ''): this script runs
            // on every deployment endpoint, typically as SYSTEM, so an unescaped
            // quote in a bundle value is remote code execution there — and a
            // legitimate "O'Brien Ltd" breaks the script outright (SEC-1).
            content = content
                .Replace("AppVendor = ''", $"AppVendor = '{PsQuote(app.Company)}'")
                .Replace("AppName = ''", $"AppName = '{PsQuote(app.Name)}'")
                .Replace("AppVersion = ''", $"AppVersion = '{PsQuote(app.DotVersion)}'")
                .Replace("AppScriptAuthor = '<author name>'", $"AppScriptAuthor = '{PsQuote(Environment.UserName)}'")
                .Replace("AppScriptDate = '2026-01-14'", $"AppScriptDate = '{SystemClock.Now:yyyy-MM-dd}'");
            await File.WriteAllTextAsync(ps1Dst, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            AppLogger.Info("Bundle: wrote tokenized Invoke-AppDeployToolkit.ps1");
        }

        // Copy supporting folders (only files that don't exist yet)
        var foldersToSync = new[] { "PSAppDeployToolkit", "PSAppDeployToolkit.Extensions", "Assets", "Config", "Strings", "SupportFiles" };
        await Task.Run(() =>
        {
            foreach (var folder in foldersToSync)
            {
                var srcDir = Path.Combine(templatePath, folder);
                if (!Directory.Exists(srcDir)) continue;

                foreach (var file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(templatePath, file);
                    var dst = Path.Combine(bundleDir, rel);
                    if (File.Exists(dst)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(file, dst);
                }
            }
        });

        AppLogger.Info($"Bundle: PSADT template synced from {templatePath}");
    }

    /// <summary>
    /// Sanitises a single directory/file-name token VALUE (Company, Name,
    /// Version, ...). Each value must collapse to exactly one path segment:
    /// it can neither climb out of its parent (<c>..</c>) nor introduce its
    /// own folder boundary (<c>\</c> or <c>/</c>). The separators in a
    /// resolved path come only from the literal DirectoryFormat template the
    /// operator chose -- that template is never passed through here, so a
    /// value containing a separator is always an injection, never intent.
    /// Mirrors the contract of the sibling <see cref="VaultPathTemplate"/>
    /// sanitiser.
    /// </summary>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        // Collapse parent-traversal segments first so a `..` can't survive
        // (a `.` is not an invalid filename char, so the loop below would
        // otherwise let it through).
        value = value.Replace("..", "_");
        var invalid = Path.GetInvalidFileNameChars();   // includes both \ and / on Windows
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (!Array.Exists(invalid, x => x == c))
                sb.Append(c);
        }
        return sb.ToString().Trim();
    }
}
