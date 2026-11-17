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
    /// <summary>Value of <see cref="AppSettings.FolderNameSpaces"/> that keeps spaces (the default).</summary>
    public const string FolderSpacesKeep = "Spaces";

    /// <summary>Value of <see cref="AppSettings.FolderNameSpaces"/> that writes each space as <c>_</c>.</summary>
    public const string FolderSpacesUnderscore = "Underscores";

    /// <summary>Value of <see cref="AppSettings.BundleSaveMode"/>: create the <see cref="AppSettings.DirectoryFormat"/> folders under the chosen location.</summary>
    public const string SaveModeConvention = "Convention";

    /// <summary>Value of <see cref="AppSettings.BundleSaveMode"/>: save into the chosen folder itself.</summary>
    public const string SaveModeDirect = "Direct";

    /// <summary>The two save modes, in the order the UI lists them.</summary>
    public static readonly string[] SaveModes = { SaveModeConvention, SaveModeDirect };

    /// <summary>
    /// One token value as it goes into a folder name: sanitized for the
    /// filesystem, then spaces written the way the setting says.
    /// </summary>
    public static string FolderName(AppSettings settings, string? value) => FolderName(value, settings.FolderNameSpaces);

    /// <summary>One token value as a folder name under an explicit spaces rule (<see cref="FolderSpacesKeep"/> or <see cref="FolderSpacesUnderscore"/>).</summary>
    public static string FolderName(string? value, string? spacesRule)
    {
        var clean = Sanitize(value);
        return string.Equals(spacesRule, FolderSpacesUnderscore, StringComparison.OrdinalIgnoreCase)
            ? clean.Replace(' ', '_')
            : clean;
    }

    /// <summary>
    /// The icon file name Wrapp writes for an app: the name as a folder-safe
    /// token (so the spaces setting applies here too - "App_Name.png" beside
    /// "App_Name\") plus <c>.png</c>, or <paramref name="fallback"/> when the
    /// name sanitizes to nothing. Images the user drops or browses keep their
    /// own file name (see <see cref="IconService.CopyToIconFolder"/>).
    /// </summary>
    public static string IconFileName(AppSettings settings, string? appName, string fallback = "appIcon")
    {
        var stem = FolderName(settings, appName);
        if (string.IsNullOrEmpty(stem)) stem = fallback;
        return $"{stem}.png";
    }

    /// <summary>The bundle folder convention when <see cref="AppSettings.DirectoryFormat"/> is blank.</summary>
    public const string DefaultDirectoryFormat = @"{Company}\{Name}\{Version}";

    /// <summary>Every token a directory format may use, in the order the UI lists them.</summary>
    public static readonly string[] DirectoryFormatTokens = { "{Company}", "{Name}", "{Version}", "{DotVersion}", "{Language}" };

    /// <summary>The format in force: the setting, or the default when the setting is blank.</summary>
    public static string EffectiveDirectoryFormat(AppSettings settings)
        => string.IsNullOrWhiteSpace(settings.DirectoryFormat) ? DefaultDirectoryFormat : settings.DirectoryFormat;

    /// <summary>
    /// The relative folder path the naming convention produces for an app:
    /// <see cref="EffectiveDirectoryFormat"/> with each token replaced by the
    /// app's value as a folder name. Throws when the result could not be a
    /// relative path under the chosen root; see <see cref="TryResolveSubDirectory"/>.
    /// </summary>
    public static string ResolveSubDirectory(AppSettings settings, AppSection app)
    {
        if (!TryResolveSubDirectory(settings, app, out var sub, out var error))
            throw new InvalidOperationException(error);
        return sub;
    }

    /// <summary>
    /// <see cref="ResolveSubDirectory"/> that reports instead of throwing. The
    /// result is refused when it is rooted (<c>Path.Combine</c> would silently
    /// drop the chosen root), when it contains a <c>..</c> segment, or when a
    /// segment is empty (an empty token value, or a doubled separator in the
    /// format), because each of those saves the bundle somewhere other than
    /// where the dialog said.
    /// </summary>
    public static bool TryResolveSubDirectory(AppSettings settings, AppSection app, out string subDirectory, out string error)
    {
        var fmt = EffectiveDirectoryFormat(settings);
        var resolved = fmt
            .Replace("{Company}",    FolderName(settings, app.Company))
            .Replace("{Name}",       FolderName(settings, app.Name))
            .Replace("{Version}",    FolderName(settings, app.Version))
            .Replace("{DotVersion}", FolderName(settings, app.DotVersion))
            .Replace("{Language}",   FolderName(settings, app.Language))
            .Replace('/', '\\')
            .Trim();

        subDirectory = resolved;
        error = string.Empty;

        if (resolved.Length == 0)
        {
            error = $"The directory format '{fmt}' produced an empty folder path.";
            return false;
        }
        if (RelativeFolderPathError(resolved, $"The directory format '{fmt}'") is { } pathError)
        {
            error = pathError;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Why <paramref name="relative"/> cannot be the folders under a chosen
    /// root, or null when it can: rooted, an empty folder name (a doubled or
    /// trailing backslash, an empty token), <c>.</c> or <c>..</c>, or a
    /// character Windows does not allow. <paramref name="subject"/> starts
    /// the sentence ("The directory format '…'", "The folders to create").
    /// </summary>
    public static string? RelativeFolderPathError(string relative, string subject)
    {
        if (string.IsNullOrWhiteSpace(relative))
            return $"{subject} produced an empty folder path.";
        if (Path.IsPathRooted(relative) || relative.StartsWith('\\'))
            return $"{subject} produced a rooted path ('{relative}'); it must be relative to the folder you choose.";
        foreach (var seg in relative.Split('\\'))
        {
            if (seg.Length == 0)
                return $"{subject} produced an empty folder name in '{relative}'. A value is empty, or there is a doubled or trailing backslash.";
            if (seg == "." || seg == "..")
                return $"{subject} produced '{seg}' as a folder name in '{relative}'; it must stay under the folder you choose.";
            if (seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return $"{subject} produced a folder name with characters Windows does not allow: '{seg}'.";
        }
        return null;
    }

    /// <summary>
    /// The domain <c>AppFolder</c> a bundle directory implies for a domain
    /// whose distribution root is <paramref name="distributionRoot"/>: when
    /// the bundle sits under that root, its parent folder relative to the
    /// root (<c>Template\Ricoh\Universal_Print_Driver_PCL6</c> for a bundle at
    /// <c>\\dist\packages$\Template\Ricoh\Universal_Print_Driver_PCL6\4_45_0_0</c>),
    /// so <c>isDistPath\AppFolder\&lt;version folder&gt;</c> is exactly the
    /// bundle whatever the depth. Otherwise the two folder names above the
    /// bundle, as <see cref="AppFolderFromBundleDirectory(string)"/>.
    /// </summary>
    public static string? AppFolderFromBundleDirectory(string bundleDirectory, string? distributionRoot)
    {
        if (!string.IsNullOrWhiteSpace(distributionRoot) && !string.IsNullOrWhiteSpace(bundleDirectory))
        {
            try
            {
                var root   = Path.TrimEndingDirectorySeparator(Path.GetFullPath(distributionRoot.Trim()));
                var bundle = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundleDirectory.Trim()));
                var parent = Path.GetDirectoryName(bundle);
                if (parent is not null
                    && parent.Length > root.Length
                    && parent.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return parent.Substring(root.Length + 1);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // An unusable isDistPath is not this method's problem; fall through to the two-up rule.
            }
        }
        return AppFolderFromBundleDirectory(bundleDirectory);
    }

    /// <summary>
    /// The domain <c>AppFolder</c> a bundle directory implies: the two folder
    /// names above the version folder (<c>Vendor\App</c> for the default
    /// <c>{Company}\{Name}\{Version}</c> layout), or null when the path is
    /// too shallow to hold them. Read from the real path rather than
    /// recomposed from the display name, so it names the folder the bundle
    /// is actually in whatever convention the tree uses.
    /// </summary>
    public static string? AppFolderFromBundleDirectory(string bundleDirectory)
    {
        if (string.IsNullOrWhiteSpace(bundleDirectory)) return null;
        var trimmed = bundleDirectory.TrimEnd('/', '\\');
        var version = Path.GetDirectoryName(trimmed);          // ...\Vendor\App
        if (string.IsNullOrEmpty(version)) return null;
        var app = Path.GetFileName(version);
        var vendorDir = Path.GetDirectoryName(version);         // ...\Vendor
        if (string.IsNullOrEmpty(vendorDir)) return null;
        var vendor = Path.GetFileName(vendorDir);
        if (string.IsNullOrEmpty(app) || string.IsNullOrEmpty(vendor)) return null;
        // A UNC share root or a drive root is not a vendor folder.
        if (vendorDir.EndsWith(":", StringComparison.Ordinal) || vendorDir.TrimEnd('\\').EndsWith("$", StringComparison.Ordinal)) return null;
        return Path.Combine(vendor, app);
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
    /// Returns null if not found. Looks in this folder only; see
    /// <see cref="FindBundleConfig"/> for "anywhere inside the bundle".
    /// </summary>
    public static string? FindConfigJson(string folder)
    {
        var scriptPath = BundlePaths.ConfigJson(folder);
        if (File.Exists(scriptPath)) return scriptPath;
        var rootPath = Path.Combine(folder, BundlePaths.ConfigFileName);
        if (File.Exists(rootPath)) return rootPath;
        return null;
    }

    /// <summary>How many parents <see cref="FindBundleConfig"/> tries above the starting folder.</summary>
    public const int BundleSearchDepth = 8;

    /// <summary>
    /// From any file or folder inside a bundle, the path of its Config.json:
    /// the marker is looked for in the starting folder, then in each parent
    /// in turn, stopping at the share or drive root or after
    /// <see cref="BundleSearchDepth"/> levels. A bundle inside a bundle
    /// resolves to the nearest one. Null when nothing above is a bundle or
    /// the path does not exist. The C# twin of the module's
    /// <c>Resolve-WrappBundle</c> (docs/bundle-layout.md).
    /// </summary>
    public static string? FindBundleConfig(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string? dir;
        try
        {
            var full = Path.GetFullPath(path.Trim());
            if (Directory.Exists(full)) dir = full;
            else if (File.Exists(full))
            {
                if (string.Equals(Path.GetFileName(full), BundlePaths.ConfigFileName, StringComparison.OrdinalIgnoreCase))
                    return full;
                dir = Path.GetDirectoryName(full);
            }
            else return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        for (var level = 0; dir is not null && level <= BundleSearchDepth; level++)
        {
            var found = FindConfigJson(dir);
            if (found is not null) return found;
            dir = Path.GetDirectoryName(dir); // null at the drive or share root
        }
        return null;
    }

    /// <summary>The bundle root for any path inside a bundle, or null; see <see cref="FindBundleConfig"/>.</summary>
    public static string? FindBundleRoot(string? path)
        => FindBundleConfig(path) is { } config ? GetBundleRoot(config) : null;

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

        var scriptDir    = BundlePaths.ScriptDir(bundleDirectory);
        var binDir       = BundlePaths.BinaryFolder(bundleDirectory, fw);
        var shortcutDir  = BundlePaths.ShortcutDir(bundleDirectory);
        Directory.CreateDirectory(scriptDir);
        Directory.CreateDirectory(binDir);
        Directory.CreateDirectory(shortcutDir);

        // Record the bundle's app folder on every domain entry, from the real
        // path: relative to the domain's own distribution root when the bundle
        // is under it, else the two folders above (docs/bundle-layout.md).
        // Packaging runs use the bundle directory itself as content, so this
        // is descriptive, not load-bearing - but it must still be true.
        // Recomposing it from the display name wrote "Vendor\App Name" beside
        // a tree named "Vendor\App_Name".
        foreach (var domain in config.Domains)
        {
            var appFolder = AppFolderFromBundleDirectory(bundleDirectory, domain.IsDistPath)
                ?? Path.Combine(FolderName(settings, config.App.Company), FolderName(settings, config.App.Name));
            if (!string.IsNullOrEmpty(appFolder))
                domain.AppFolder = appFolder;
        }

        var configPath = Path.Combine(scriptDir, BundlePaths.ConfigFileName);
        await ConfigFileService.SaveAsync(config, configPath);
        AppLogger.Info($"Bundle: wrote Config.json to {configPath}");

        // Only write missing scripts so user edits are not overwritten.
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
        // (only if missing so user customizations are preserved, or if the
        // existing one is an earlier Wrapp default that no longer works).
        CleanupLegacyCmdShortcuts(shortcutDir);
        foreach (var (lnkName, targetPath, arguments) in ScriptFrameworkProvider.GetShortcuts(fw))
        {
            var lnkPath = Path.Combine(shortcutDir, lnkName);
            if (!File.Exists(lnkPath)
                || (ReadLnkArguments(lnkPath) is { } existing
                    && ScriptFrameworkProvider.IsSupersededShortcut(fw, lnkName, existing)))
                WriteLnkShortcut(shortcutDir, lnkName, targetPath, arguments);
        }

        // Save icon as PNG -- respect App.IconFile as the source of truth.
        // IconFile comes straight from the bundle's Config.json, so it
        // must resolve INSIDE the bundle - a rooted or ..-escaping value would
        // otherwise write a file anywhere on disk. Escapees fall through to
        // the derive-from-App.Name branch.
        var containedIconPath = ResolveInsideBundle(bundleDirectory, config.App.IconFile);
        if (containedIconPath is not null && icon is not null)
        {
            // Always write/overwrite -- handles both first save and deferred icon replacement
            Directory.CreateDirectory(Path.GetDirectoryName(containedIconPath)!);
            if (await SaveIconAsPngAsync(icon, containedIconPath))
                AppLogger.Info($"Bundle: wrote icon to {containedIconPath}");
            else
                AppLogger.Warn($"Bundle: icon NOT written to {containedIconPath}; Config.json still points at it");
        }
        else if (icon is not null)
        {
            // No IconFile set yet -- fallback: derive from App.Name (first-time save)
            var iconFolder = Path.Combine(bundleDirectory, settings.IconFolderName);
            Directory.CreateDirectory(iconFolder);
            var relPath = Path.Combine(settings.IconFolderName, IconFileName(settings, config.App.Name, "Icon"));
            var iconPath = Path.Combine(bundleDirectory, relPath);
            if (await SaveIconAsPngAsync(icon, iconPath))
            {
                config.App.IconFile = relPath;
                AppLogger.Info($"Bundle: wrote icon to {iconPath}, set IconFile={relPath}");

                // Re-save Config.json since we updated IconFile after the initial write
                var configRewritePath = BundlePaths.ConfigJson(bundleDirectory);
                await ConfigFileService.SaveAsync(config, configRewritePath);
            }
            else
            {
                AppLogger.Warn($"Bundle: icon NOT written to {iconPath}; IconFile left unset");
            }
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

            var scriptDir = BundlePaths.ScriptDir(bundleRoot);
            var binDir = BundlePaths.BinaryFolder(bundleRoot, fw);
            var shortcutDir = BundlePaths.ShortcutDir(bundleRoot);
            Directory.CreateDirectory(scriptDir);
            Directory.CreateDirectory(binDir);
            Directory.CreateDirectory(shortcutDir);

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

    /// <summary>PowerShell single-quoted-literal escape: ' doubles. The only
    /// safe way to splice a value into <c>'...'</c> in generated PS.</summary>
    internal static string PsQuote(string? value) => (value ?? string.Empty).Replace("'", "''");

    /// <summary>
    /// Containment: resolves <paramref name="relative"/> against the
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
        await FileRetry.RunAsync(
            () => File.WriteAllTextAsync(destPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)),
            scriptName);
        AppLogger.Info($"Bundle: wrote {scriptName} to {destPath}");
    }

    // One resolver façade for the whole app: TemplateService.ApplyTokens
    // (→ PlaceholderService.Expand). The former ApplyTokensPublic duplicate
    // is gone - callers use TemplateService.ApplyTokens directly.
    private static string ApplyTokens(string content, AppSection app)
        => TemplateService.ApplyTokens(content, app);

    // Icon ceiling. SCCM's New-CMApplication current-branch cmdlet accepts up
    // to 512x512; older site builds capped at 256x256. Intune Win32 and PSADT
    // both accept 512x512. 512 keeps high-DPI detail while still landing under
    // every cap we've seen reject -- observed rejection on this surface was
    // 1425x1425 (Microsoft's own dotNetFx35setup.exe native icon).
    private const int IconMaxDimension = 512;

    /// <summary>
    /// A frozen copy of <paramref name="source"/>'s pixels with nothing behind
    /// it. A <see cref="BitmapFrame"/> straight from a decoder stays tied to
    /// that decoder even when frozen, and the decoder belongs to the thread
    /// that created it: re-encoding such a frame on another thread fails with
    /// "a different thread owns it". MSI icons are decoded on a background
    /// thread during a drop and written on the UI thread at save, which is how
    /// a save once left Icon\ empty while Config.json pointed into it.
    /// </summary>
    public static BitmapSource Detach(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * ((bgra.Format.BitsPerPixel + 7) / 8);
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        var copy = BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight,
            source.DpiX > 0 ? source.DpiX : 96, source.DpiY > 0 ? source.DpiY : 96,
            bgra.Format, null, pixels, stride);
        copy.Freeze();
        return copy;
    }

    /// <summary>
    /// Writes <paramref name="icon"/> as a PNG at <paramref name="path"/>,
    /// downscaled to the platform cap. True only when the file was written;
    /// callers report a false instead of announcing the icon.
    /// </summary>
    public static async Task<bool> SaveIconAsPngAsync(ImageSource icon, string path)
    {
        try
        {
            if (icon is not BitmapSource original) return false;
            var bmp = Detach(original);

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
            await FileRetry.RunAsync(() => Task.Run(() => File.WriteAllBytes(path, bytes)), Path.GetFileName(path));
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Bundle: failed to save icon PNG: {ex.Message}");
            return false;
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

    /// <summary>A .lnk's Arguments via WScript.Shell, or null when it can't
    /// be read (the shortcut is then left alone).</summary>
    private static string? ReadLnkArguments(string lnkPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return null;

        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            dynamic dShell = shell!;
            dynamic shortcut = dShell.CreateShortcut(lnkPath);
            string arguments = shortcut.Arguments;
            Marshal.FinalReleaseComObject(shortcut);
            return arguments;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Bundle: could not read shortcut {lnkPath}: {ex.Message}");
            return null;
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
            // quote in a bundle value is remote code execution there - and a
            // legitimate "O'Brien Ltd" breaks the script outright.
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
