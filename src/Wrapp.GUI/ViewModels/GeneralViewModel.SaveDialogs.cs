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

public partial class GeneralViewModel
{
    // -----------------------------------------------------------------------
    // Full-apply + upgrade save dialogs. Each names the result, not the
    // mechanism: "Replace in place" / "Create the new version folder" show
    // the folder they produce; "Save as…" opens the save sheet. Both end in
    // the one save pipeline. The version replication helpers that carry a
    // new version into every package / assignment / tag live here too.
    // -----------------------------------------------------------------------

    /// <summary>
    /// A dropped installer carries a different name or company. Offers
    /// "Replace in place" (the current folder keeps its name) and "Save as…"
    /// (the sheet, pre-filled with the new name). Applies the metadata and
    /// saves on confirm. Returns true when saved.
    /// </summary>
    private async Task<bool> ShowFullModeSaveDialogAsync(
        string installerPath,
        string newName, string newCompany,
        string newDotVersion, string newVersion,
        string newEXEFile, string newMSIFile,
        ImageSource? extractedIcon,
        bool iconUserChosen)
    {
        var pathPanel = BuildPathPanel(BundleRootDir, newPath: null, newLabel: null,
            note: "Replace in place keeps this folder and its name. Save as… copies the bundle to a folder you choose and leaves this one as it is.");

        string? lastKey = "inplace";
        while (true)
        {
            var options = new ActionPickerOption[]
            {
                new()
                {
                    Key = "inplace", Icon = "",
                    Title = "Replace in place",
                    Description = "The current folder keeps its name; the config, scripts and installer are replaced."
                },
                new()
                {
                    Key = "saveas", Icon = "",
                    Title = "Save as…",
                    Description = "Choose where the renamed bundle goes. The save dialog opens with the new name filled in."
                },
            };

            var dialog = new Views.ActionPickerDialog(
                $"The installer is {newName} {newDotVersion} by {newCompany}. Choose where the updated bundle goes.",
                options, pathPanel, lastKey);

            var confirmed = await FluentDialog.ShowSelectAsync(
                "Apply Installer", dialog, "Continue", "Cancel");

            if (!confirmed)
            {
                AppLogger.Info("General: full-mode save cancelled by user");
                return false;
            }

            lastKey = dialog.SelectedKey;
            string dir;
            string? copySource = null;

            if (dialog.SelectedKey == "saveas")
            {
                var target = await ShowSaveSheetAsync("Save bundle as", new AppSection
                {
                    Company = newCompany, Name = newName,
                    DotVersion = newDotVersion, Version = newVersion,
                    Language = App.Language
                });
                if (target is null)
                    continue; // Re-show the dialog
                dir = target;
                copySource = BundleRootDir; // Copy old bundle to new location first
            }
            else // "inplace"
            {
                dir = BundleRootDir;
            }

            if (!await ConfirmOverwriteAsync(dir)) continue; // Re-show the dialog

            // -- User confirmed: apply all metadata changes --
            ClearInstallerFields();
            App.Name = newName;
            App.Company = newCompany;
            App.DotVersion = newDotVersion;
            App.Version = newVersion;
            App.EXEFile = newEXEFile;
            App.MSIFile = newMSIFile;
            _installerFullPath = installerPath;
            OnPropertyChanged(nameof(InstallerDisplayPath));

            if (extractedIcon is not null)
                InstallerIconSource = extractedIcon;
            App.IconUserChosen = iconUserChosen;

            // Set up deferred installer for FlushPendingInstallerToDiskAsync
            _pendingInstallerPath = installerPath;
            _pendingIconBitmap = InstallerIconSource as BitmapSource;
            App.IconFile = Path.Combine(_settings.IconFolderName, BundleService.IconFileName(_settings, newName));

            return await SaveBundleToDirectoryAsync(dir, copySource, "Full apply");
        }
    }

    /// <summary>
    /// Replaces old version strings with new ones across all package AppNames,
    /// assignment AppNames, deployment AppNames, SoftwareVersion fields, and Tags.
    /// </summary>
    private void ReplicateVersionToPackages(string oldDotVersion, string oldVersion)
    {
        if (string.IsNullOrEmpty(oldDotVersion) && string.IsNullOrEmpty(oldVersion))
            return; // Nothing to replace (first-time drop)

        var newDotVersion = App.DotVersion;
        var newVersion = App.Version;

        // Intune packages
        foreach (var pkg in _config.Script.IntunePackager.Packages)
            pkg.AppName = ReplaceVersion(pkg.AppName, oldDotVersion, newDotVersion, oldVersion, newVersion);

        // SCCM packages
        foreach (var pkg in _config.Script.SCCMPackager.Packages)
        {
            pkg.AppName = ReplaceVersion(pkg.AppName, oldDotVersion, newDotVersion, oldVersion, newVersion);
            pkg.SoftwareVersion = newDotVersion;
        }

        // Intune assignments (per package)
        foreach (var pkg in _config.Script.IntunePackager.Packages)
            foreach (var assignment in pkg.Assignments)
                assignment.AppName = ReplaceVersion(assignment.AppName, oldDotVersion, newDotVersion, oldVersion, newVersion);

        // SCCM deployments (per package)
        foreach (var pkg in _config.Script.SCCMPackager.Packages)
            foreach (var deployment in pkg.Deployments)
                deployment.AppName = ReplaceVersion(deployment.AppName, oldDotVersion, newDotVersion, oldVersion, newVersion);

        // Tags
        _config.Script.IntunePackager.Tag = ReplaceVersion(_config.Script.IntunePackager.Tag, oldDotVersion, newDotVersion, oldVersion, newVersion);
        _config.Script.SCCMPackager.Tag = ReplaceVersion(_config.Script.SCCMPackager.Tag, oldDotVersion, newDotVersion, oldVersion, newVersion);
        _config.Script.Install.Tag = ReplaceVersion(_config.Script.Install.Tag, oldDotVersion, newDotVersion, oldVersion, newVersion);
        _config.Script.Uninstall.Tag = ReplaceVersion(_config.Script.Uninstall.Tag, oldDotVersion, newDotVersion, oldVersion, newVersion);
        _config.Script.Console.Tag = ReplaceVersion(_config.Script.Console.Tag, oldDotVersion, newDotVersion, oldVersion, newVersion);
        // (Generated tags are empty here and follow the new version on save; this patches hand-edited ones.)

        AppLogger.Info($"General: replicated version {oldDotVersion} -> {newDotVersion} across packages/assignments/tags");
    }

    /// <summary>
    /// Replaces old version strings in text. Replaces DotVersion first (more specific, has dots),
    /// then underscore Version (skipped if same as DotVersion to avoid double-replace).
    /// </summary>
    private static string ReplaceVersion(string text, string oldDot, string newDot, string oldUnderscore, string newUnderscore)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Replace dot version first (e.g. "138.0.7204.158" -> "139.0.7258.128")
        if (!string.IsNullOrEmpty(oldDot) && !string.IsNullOrEmpty(newDot))
            text = text.Replace(oldDot, newDot);

        // Replace underscore version (e.g. "138_0_7204_158" -> "139_0_7258_128")
        // Skip if it would be the same replacement (oldDot == oldUnderscore means no dots)
        if (!string.IsNullOrEmpty(oldUnderscore) && !string.IsNullOrEmpty(newUnderscore)
            && oldUnderscore != oldDot)
            text = text.Replace(oldUnderscore, newUnderscore);

        return text;
    }

    // ------------------------------------------------------------------
    // Full/Upgrade mode: the path panel under the option cards
    // ------------------------------------------------------------------

    /// <summary>
    /// The current bundle's folder, optionally the folder an option would
    /// produce, and a note. The new folder is shown in the accent colour so
    /// the operator reads the result before pressing Continue.
    /// </summary>
    private static FrameworkElement BuildPathPanel(string currentPath, string? newPath, string? newLabel, string note)
    {
        var accent = Application.Current.TryFindResource("AccentBgBrush") as Brush;
        var primary = Application.Current.TryFindResource("TextPrimaryBrush") as Brush;
        var secondary = Application.Current.TryFindResource("TextSecondaryBrush") as Brush;
        var muted = Application.Current.TryFindResource("TextMutedBrush") as Brush;

        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

        panel.Children.Add(new TextBlock
        {
            Text = "Current bundle",
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = secondary, Margin = new Thickness(0, 0, 0, 2)
        });
        panel.Children.Add(PathRow(currentPath, muted));

        if (newPath is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = newLabel ?? "New bundle",
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = primary, Margin = new Thickness(0, 6, 0, 2)
            });
            panel.Children.Add(PathRow(newPath, accent));
        }

        panel.Children.Add(new TextBlock
        {
            Text = note,
            FontSize = 11, Foreground = muted, FontStyle = FontStyles.Italic,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });

        return panel;
    }

    private static StackPanel PathRow(string path, Brush? brush)
    {
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 4) };
        row.Children.Add(new TextBlock
        {
            Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14, Foreground = brush, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        row.Children.Add(new TextBlock
        {
            Text = path, FontSize = 11, FontFamily = new FontFamily("Consolas"),
            Foreground = brush, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        return row;
    }

    /// <summary>True when <paramref name="folderName"/> is the old version, in any of the ways Wrapp writes it.</summary>
    private bool FolderIsNamedAfter(string folderName, string version, string dotVersion)
    {
        foreach (var candidate in new[] { BundleService.FolderName(_settings, version), version, dotVersion })
            if (!string.IsNullOrEmpty(candidate) && string.Equals(folderName, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// A dropped installer carries a new version. Offers "Create the new
    /// version folder" (a sibling of the current bundle named after the new
    /// version, the spaces rule applied) and "Save as…" (the sheet in direct
    /// mode, opened at the current bundle's parent). Applies the version,
    /// replicates it into the packages, and saves on confirm. The current
    /// bundle is never modified. Returns true when saved.
    /// </summary>
    private async Task<bool> ShowUpgradeSaveDialogAsync(
        string installerPath,
        string oldDotVersion, string oldVersion,
        string newDotVersion, string newVersion,
        string newEXEFile, string newMSIFile,
        ImageSource? confirmedIcon, bool keepOldIcon,
        bool iconUserChosen)
    {
        var currentRoot = BundleRootDir.TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(currentRoot) ?? string.Empty;
        var currentFolder = Path.GetFileName(currentRoot);
        var newFolderName = BundleService.FolderName(_settings, newVersion);
        var canCreate = !string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(newFolderName);
        var siblingPath = canCreate ? Path.Combine(parent, newFolderName) : string.Empty;

        var note = "The current bundle is not modified.";
        if (canCreate && !FolderIsNamedAfter(currentFolder, oldVersion, oldDotVersion))
            note = $"The current folder is named '{currentFolder}', not after {oldDotVersion}. "
                 + "If the version is not the last folder in your layout, choose Save as… and pick the folder yourself. "
                 + "The current bundle is not modified either way.";

        var pathPanel = BuildPathPanel(BundleRootDir, canCreate ? siblingPath : null, "New version folder", note);

        string? lastKey = canCreate ? "create" : "saveas";
        while (true)
        {
            var options = new ActionPickerOption[]
            {
                new()
                {
                    Key = "create", Icon = "",
                    Title = "Create the new version folder",
                    Description = "A folder next to the current bundle, named after the new version, with the bundle copied there and the new installer.",
                    IsEnabled = canCreate,
                    DisabledReason = "The installer carries no version to name the folder after"
                },
                new()
                {
                    Key = "saveas", Icon = "",
                    Title = "Save as…",
                    Description = "Choose or make the folder yourself. The save dialog opens with the new version filled in."
                },
            };

            var dialog = new Views.ActionPickerDialog(
                $"New installer: {App.Name} {newDotVersion} (the bundle is {oldDotVersion}). Choose where the upgraded bundle goes.",
                options, pathPanel, lastKey);

            var confirmed = await FluentDialog.ShowSelectAsync(
                "Upgrade Bundle", dialog, "Continue", "Cancel");

            if (!confirmed)
            {
                AppLogger.Info("General: upgrade save cancelled by user");
                return false;
            }

            lastKey = dialog.SelectedKey;
            string dir;

            if (dialog.SelectedKey == "saveas")
            {
                var target = await ShowSaveSheetAsync("Save upgraded bundle as", new AppSection
                {
                    Company = App.Company, Name = App.Name,
                    DotVersion = newDotVersion, Version = newVersion,
                    Language = App.Language
                }, SaveTargetMode.Direct, parent);
                if (target is null)
                    continue; // Re-show the dialog
                dir = target;
            }
            else // "create"
            {
                dir = siblingPath;
            }

            if (!await ConfirmOverwriteAsync(dir)) continue; // Re-show the dialog

            // -- User confirmed: now apply all changes --
            App.DotVersion = newDotVersion;
            App.Version = newVersion;
            App.EXEFile = newEXEFile;
            App.MSIFile = newMSIFile;

            if (!keepOldIcon && confirmedIcon is not null)
                InstallerIconSource = confirmedIcon;
            App.IconUserChosen = iconUserChosen;

            ReplicateVersionToPackages(oldDotVersion, oldVersion);

            // Set up deferred installer for FlushPendingInstallerToDiskAsync
            _pendingInstallerPath = installerPath;
            _pendingIconBitmap = InstallerIconSource as BitmapSource;
            App.IconFile = Path.Combine(_settings.IconFolderName, BundleService.IconFileName(_settings, App.Name));

            return await SaveBundleToDirectoryAsync(dir, BundleRootDir, "Upgrade");
        }
    }
}
