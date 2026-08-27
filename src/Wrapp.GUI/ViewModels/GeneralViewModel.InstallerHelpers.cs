using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Wrapp.Helpers;
using Wrapp.Models;
using Wrapp.Services;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;

namespace Wrapp.ViewModels;

/// <summary>
/// Internal helpers for <see cref="GeneralViewModel"/>: installer-file +
/// icon application, bin-folder management, version-replication across
/// packages, the full-mode and upgrade save dialogs, and the underlying
/// file-copy / atomic-write helpers used by the save path.
/// Moved into a partial file so the core VM focuses on the high-level
/// command surface; this file holds the low-level mutators.
/// </summary>
public partial class GeneralViewModel
{
    // -----------------------------------------------------------------------
    // Internal helpers
    // -----------------------------------------------------------------------

    private Task RefreshGitStatusAsync(string dir)
    {
        // No-op: status bar no longer shows last commit info.
        // File history view provides better change tracking.
        return Task.CompletedTask;
    }

    private void ClearInstallerFields()
    {
        App.EXEFile         = string.Empty;
        App.MSIFile         = string.Empty;
        App.Name            = string.Empty;
        App.Company         = string.Empty;
        App.DotVersion      = string.Empty;
        App.Version         = string.Empty;
        InstallerIconSource = null;
        _installerFullPath  = string.Empty;
    }

    /// <summary>Returns the path to the current installer in the binary folder, or null if empty/absent.</summary>
    private string? GetCurrentBinFilePath()
    {
        var bundleRoot = BundleRootDir;
        if (string.IsNullOrEmpty(bundleRoot)) return null;
        var binDir = BundlePaths.BinaryFolder(bundleRoot, ScriptFrameworkProvider.Parse(_config.App.ScriptFramework));
        if (!Directory.Exists(binDir)) return null;
        return Directory.EnumerateFiles(binDir).FirstOrDefault();
    }

    /// <summary>Returns the full path to the current app icon, or null if absent.</summary>
    private string? GetCurrentDraftIconPath()
    {
        var bundleRoot = BundleRootDir;
        if (string.IsNullOrEmpty(bundleRoot)) return null;

        // SEC-2: IconFile is bundle data — only honor it inside the bundle.
        var iconPath = Services.BundleService.ResolveInsideBundle(bundleRoot, App.IconFile);
        if (iconPath is not null && File.Exists(iconPath)) return iconPath;

        var iconFolder = Path.Combine(bundleRoot, _settings.IconFolderName);
        if (!Directory.Exists(iconFolder)) return null;
        return Directory.EnumerateFiles(iconFolder)
            .FirstOrDefault(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                              || f.EndsWith(".ico", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Builds an icon filename from the extracted app name (sanitized).</summary>
    private static string ResolveIconFileName(string extractedAppName)
    {
        var sanitized = BundleService.Sanitize(extractedAppName);
        if (string.IsNullOrEmpty(sanitized)) sanitized = "appIcon";
        return $"{sanitized}.png";
    }

    // ------------------------------------------------------------------
    // Incoming-icon resolution: ONE decision path for every entry point
    // (full apply, upgrade, icon-only — and any future one), parameterized
    // by IconPromptPolicy instead of duplicated inline null-checks.
    // ------------------------------------------------------------------

    private enum IconResolutionKind
    {
        /// <summary>No incoming icon at all — caller keeps its existing behavior.</summary>
        None,
        /// <summary>Incoming icon applied without asking (policy said no prompt).</summary>
        AutoApply,
        /// <summary>User explicitly picked the incoming icon in the old-vs-new dialog.</summary>
        ChosenNew,
        /// <summary>User explicitly kept the current icon (Icon = the CURRENT instance, ready to re-apply after field clears).</summary>
        KeptCurrent,
        /// <summary>User cancelled the dialog and the caller opted into abort semantics.</summary>
        Cancelled,
    }

    /// <summary>
    /// Decides which icon survives when an installer brings a new one. The
    /// prompt trigger is <see cref="IconPromptDecision"/> over the persisted
    /// <c>App.IconUserChosen</c> provenance flag; the caller maps the result
    /// onto its own apply flow and flag updates.
    /// </summary>
    private async Task<(IconResolutionKind Kind, ImageSource? Icon)> ResolveIncomingIconAsync(
        ImageSource? incoming, string sourceLabel, IconPromptPolicy policy, bool cancelAborts = false)
    {
        if (incoming is null) return (IconResolutionKind.None, null);
        if (!IconPromptDecision.ShouldPrompt(
                hasCurrentIcon: InstallerIconSource is not null,
                iconUserChosen: App.IconUserChosen,
                policy))
            return (IconResolutionKind.AutoApply, incoming);

        var appLabel = string.IsNullOrWhiteSpace(App.Name) ? sourceLabel : App.Name;
        var picker = new Views.IconPickerDialog(InstallerIconSource!, incoming, appLabel);
        var picked = await FluentDialog.ShowSelectAsync("Choose Icon", picker, "Confirm", "Cancel");
        if (!picked)
            return cancelAborts
                ? (IconResolutionKind.Cancelled, null)
                : (IconResolutionKind.KeptCurrent, InstallerIconSource);
        return picker.SelectedOld
            ? (IconResolutionKind.KeptCurrent, InstallerIconSource)
            : (IconResolutionKind.ChosenNew, incoming);
    }

    private async Task ApplyInstallerFile(string path)
    {
        var ext      = Path.GetExtension(path).ToLowerInvariant();
        var fileName = Path.GetFileName(path);

        // Preview metadata without mutating App fields
        var (previewName, previewCompany, previewVersion) = PreviewInstallerMetadata(path);
        string newName = !string.IsNullOrEmpty(previewName) ? previewName : string.Empty;
        string newCompany = !string.IsNullOrEmpty(previewCompany) ? previewCompany : string.Empty;
        string newDotVersion = !string.IsNullOrEmpty(previewVersion) ? previewVersion.Trim() : string.Empty;
        string newVersion = !string.IsNullOrEmpty(newDotVersion) ? newDotVersion.Replace('.', '_') : string.Empty;
        string newEXEFile = (ext == ".msi" || ext == ".msp") ? string.Empty : fileName;
        string newMSIFile = (ext == ".msi" || ext == ".msp") ? fileName : string.Empty;

        // Extract icon (off UI thread, busy overlay up)
        var extractedIcon = _msiPickedIcon
            ?? await RunDropDiscoveryAsync("Extracting icon...", () => IconExtractorService.Extract(path));

        // Full mode replaces auto-extracted icons silently (the card promises a
        // full replace) but a deliberately chosen icon prompts keep-vs-replace.
        var (iconKind, resolvedIcon) = await ResolveIncomingIconAsync(
            extractedIcon, fileName, IconPromptPolicy.WhenUserChosen);
        var iconUserChosen = iconKind switch
        {
            IconResolutionKind.ChosenNew or IconResolutionKind.KeptCurrent => true,
            IconResolutionKind.AutoApply => _msiPickedIcon is not null, // embedded-icon pick is deliberate
            _ => false,
        };

        // ---- Active bundle: show save dialog (no mutation until confirmed) ----
        if (IsActiveBundle)
        {
            var saved = await ShowFullModeSaveDialogAsync(
                path, newName, newCompany, newDotVersion, newVersion,
                newEXEFile, newMSIFile, resolvedIcon, iconUserChosen);

            if (!saved)
                StatusText = "Installer apply cancelled.";
            return;
        }

        // ---- Draft/temp/fallback: apply immediately ----
        ClearInstallerFields();
        App.Name = newName;
        App.Company = newCompany;
        App.DotVersion = newDotVersion;
        App.Version = newVersion;
        App.EXEFile = newEXEFile;
        App.MSIFile = newMSIFile;
        _installerFullPath = path;
        OnPropertyChanged(nameof(InstallerDisplayPath));
        // KeptCurrent hands back the CURRENT instance, restoring it after the
        // field clear above; None keeps today's replace-everything null.
        InstallerIconSource = resolvedIcon;
        App.IconUserChosen  = iconUserChosen;

        string extractedAppName = newName;

        if (IsTempWorkspace())
        {
            var existingBinFile = GetCurrentBinFilePath();
            bool isSameFile = existingBinFile is not null
                && IconService.FilesAreIdentical(path, existingBinFile);

            if (isSameFile)
            {
                AppLogger.Info("General: same installer detected in B/ -- skipping binary copy");
                var oldIconPath = GetCurrentDraftIconPath();
                var newIconFileName = ResolveIconFileName(extractedAppName);
                if (oldIconPath is not null
                    && !string.Equals(Path.GetFileName(oldIconPath), newIconFileName, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(oldIconPath); AppLogger.Info($"General: deleted old draft icon {Path.GetFileName(oldIconPath)}"); }
                    catch (Exception ex) { AppLogger.Warn($"General: failed to delete old draft icon: {ex.Message}"); }
                }
            }
            else
            {
                var oldIconPath = GetCurrentDraftIconPath();
                if (oldIconPath is not null)
                {
                    try { File.Delete(oldIconPath); AppLogger.Info($"General: deleted old draft icon {Path.GetFileName(oldIconPath)}"); }
                    catch (Exception ex) { AppLogger.Warn($"General: failed to delete old draft icon: {ex.Message}"); }
                }
                await ClearBinFolderAsync();
                await CopyInstallerToBinAsync(path);
            }

            if (!string.IsNullOrEmpty(_configPath) && InstallerIconSource is not null)
                await SaveIconToDraftAsync(extractedAppName);
            _pendingInstallerPath = null;
            _pendingIconBitmap = null;
        }
        else
        {
            if (!string.IsNullOrEmpty(_configPath) && InstallerIconSource is not null)
                await SaveIconToDraftAsync(extractedAppName);
            await CopyInstallerToBinAsync(path);
        }

        HasConfig  = true;
        StatusText = $"Installer set: {fileName}";
        AppLogger.Info($"General: installer applied: {fileName}");
        ConfigLoaded?.Invoke(this, (_config, _configPath));
    }

    /// <summary>
    /// Attempts to load a BitmapImage from the given absolute path and set InstallerIconSource.
    /// Returns true on success.
    /// </summary>
    private bool TryLoadIconFromPath(string absolutePath)
    {
        if (!File.Exists(absolutePath)) return false;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource   = new Uri(absolutePath, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            InstallerIconSource = bmp;
            AppLogger.Info($"General: icon loaded from {absolutePath}");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"General: failed to load icon from {absolutePath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Extracts only the icon from an installer (EXE or MSI) without changing application metadata.
    /// If a current icon exists, shows the icon picker to let the user choose.
    /// </summary>
    private async Task ApplyIconOnlyAsync(string path)
    {
        try
        {
            var newIcon = _msiPickedIcon
                ?? await RunDropDiscoveryAsync("Extracting icon...", () => IconExtractorService.Extract(path));
            if (newIcon is null)
            {
                AppLogger.Warn($"General: no icon could be extracted from {path}");
                StatusText = "No icon could be extracted from this file.";
                return;
            }

            // Determine a name for the icon file
            string extractedName = string.Empty;
            try
            {
                var (name, _, _) = PreviewInstallerMetadata(path);
                if (!string.IsNullOrEmpty(name)) extractedName = name.Trim();
            }
            catch { /* fall back to empty */ }

            // Shared resolution path; icon-only mode always prompts when a
            // current icon exists (its historical behavior).
            var (iconKind, resolvedIcon) = await ResolveIncomingIconAsync(
                newIcon, Path.GetFileName(path), IconPromptPolicy.WhenAnyCurrentIcon);
            if (iconKind == IconResolutionKind.KeptCurrent)
            {
                // An explicit keep is a deliberate choice — protect it from
                // future silent Full-apply replacement.
                App.IconUserChosen = true;
                AppLogger.Info("General: user kept current icon");
                StatusText = "Icon unchanged.";
                return;
            }

            InstallerIconSource = resolvedIcon;
            // "Extract Icon Only" is always a deliberate act.
            App.IconUserChosen = true;

            if (IsTempWorkspace())
            {
                if (!string.IsNullOrEmpty(_configPath))
                    await SaveIconToDraftAsync(extractedName);
            }
            else if (IsActiveBundle)
            {
                _pendingIconBitmap = newIcon as BitmapSource;
                var iconName = BundleService.Sanitize(extractedName);
                if (string.IsNullOrEmpty(iconName)) iconName = "appIcon";
                App.IconFile = Path.Combine(_settings.IconFolderName, $"{iconName}.png");
            }

            StatusText = $"Icon extracted: {Path.GetFileName(path)}";
            AppLogger.Info($"General: icon-only extraction from {path}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"General: icon-only extraction failed for {path}: {ex.Message}");
            StatusText = $"Failed to extract icon: {ex.Message}";
        }
    }

    /// <summary>
    /// Upgrade mode: replaces only version and filename, keeps Name/Company.
    /// Shows icon picker, replicates version into all packages/assignments/deployments/tags.
    /// </summary>
    private async Task ApplyUpgradeInstaller(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var fileName = Path.GetFileName(path);

        // Capture current state (no mutation yet)
        var oldDotVersion = App.DotVersion;
        var oldVersion = App.Version;
        var oldEXEFile = App.EXEFile;
        var oldMSIFile = App.MSIFile;

        // Preview new filename (not applied to App yet)
        string newEXEFile, newMSIFile;
        if (ext == ".msi" || ext == ".msp")
        {
            newMSIFile = fileName;
            newEXEFile = string.Empty;
        }
        else
        {
            newEXEFile = fileName;
            newMSIFile = string.Empty;
        }

        // Preview new version from installer (not applied to App yet)
        var (_, _, extractedVersion) = PreviewInstallerMetadata(path);
        string newDotVersion, newVersion;
        if (!string.IsNullOrEmpty(extractedVersion))
        {
            newDotVersion = extractedVersion.Trim();
            newVersion = extractedVersion.Trim().Replace('.', '_');
        }
        else
        {
            newDotVersion = string.Empty;
            newVersion = string.Empty;
        }

        // Extract icon off UI thread (busy overlay up); shared resolution path
        // (upgrade prompts on ANY current icon; cancel aborts the upgrade).
        var newIcon = _msiPickedIcon
            ?? await RunDropDiscoveryAsync("Extracting icon...", () => IconExtractorService.Extract(path));
        var (iconKind, _) = await ResolveIncomingIconAsync(
            newIcon, fileName, IconPromptPolicy.WhenAnyCurrentIcon, cancelAborts: true);
        if (iconKind == IconResolutionKind.Cancelled)
        {
            AppLogger.Info("General: upgrade cancelled at icon picker");
            StatusText = "Upgrade cancelled.";
            return;
        }
        bool keepOldIcon = iconKind is IconResolutionKind.KeptCurrent or IconResolutionKind.None;
        ImageSource? confirmedIcon = iconKind == IconResolutionKind.None ? null : newIcon;
        var iconUserChosen = iconKind switch
        {
            IconResolutionKind.ChosenNew or IconResolutionKind.KeptCurrent => true,
            IconResolutionKind.AutoApply => _msiPickedIcon is not null,
            _ => App.IconUserChosen, // None: icon untouched, provenance unchanged
        };

        // ---- Active bundle: show the upgrade save dialog (no mutation until confirmed) ----
        if (IsActiveBundle)
        {
            _installerFullPath = path;
            OnPropertyChanged(nameof(InstallerDisplayPath));

            var saved = await ShowUpgradeSaveDialogAsync(
                path, oldDotVersion, oldVersion, newDotVersion, newVersion,
                newEXEFile, newMSIFile, confirmedIcon, keepOldIcon, iconUserChosen);

            if (!saved)
            {
                _installerFullPath = string.Empty;
                OnPropertyChanged(nameof(InstallerDisplayPath));
                StatusText = "Upgrade cancelled.";
            }
            return;
        }

        // ---- Draft/temp workspace: apply changes immediately (no dialog needed) ----
        App.DotVersion = newDotVersion;
        App.Version = newVersion;
        App.EXEFile = newEXEFile;
        App.MSIFile = newMSIFile;
        _installerFullPath = path;
        OnPropertyChanged(nameof(InstallerDisplayPath));

        if (!keepOldIcon && confirmedIcon is not null)
            InstallerIconSource = confirmedIcon;
        App.IconUserChosen = iconUserChosen;

        ReplicateVersionToPackages(oldDotVersion, oldVersion);

        string extractedAppName = App.Name ?? string.Empty;
        if (IsTempWorkspace())
        {
            await ClearBinFolderAsync();
            await CopyInstallerToBinAsync(path);
            if (!string.IsNullOrEmpty(_configPath) && InstallerIconSource is not null)
                await SaveIconToDraftAsync(extractedAppName);
            _pendingInstallerPath = null;
            _pendingIconBitmap = null;
        }
        else
        {
            if (!string.IsNullOrEmpty(_configPath) && InstallerIconSource is not null)
                await SaveIconToDraftAsync(extractedAppName);
            await CopyInstallerToBinAsync(path);
        }

        HasConfig = true;
        StatusText = $"Upgraded: {fileName}";
        AppLogger.Info($"General: upgrade applied: {fileName} (version {oldDotVersion} -> {newDotVersion})");
        ConfigLoaded?.Invoke(this, (_config, _configPath));
    }


}