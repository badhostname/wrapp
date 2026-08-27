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
    // Full-mode + upgrade-mode save dialogs. Wires the user-facing 'save
    // bundle' confirmation flow plus the version replication helpers that
    // propagate a new app version into every package / assignment / tag.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Shows the full-mode save dialog for active bundles. Offers "Save in Place"
    /// and "Save As" options. Applies metadata and saves directly on confirm.
    /// Returns true if saved.
    /// </summary>
    private async Task<bool> ShowFullModeSaveDialogAsync(
        string installerPath,
        string newName, string newCompany,
        string newDotVersion, string newVersion,
        string newEXEFile, string newMSIFile,
        ImageSource? extractedIcon,
        bool iconUserChosen)
    {
        var pathPanel = BuildUpgradePathPanel(BundleRootDir, BundleRootDir);

        string? lastKey = "inplace";
        while (true)
        {
            var options = new ActionPickerOption[]
            {
                new()
                {
                    Key = "inplace", Icon = "\uE74E",
                    Title = "Save in Place",
                    Description = "Overwrite the current bundle with new metadata and installer"
                },
                new()
                {
                    Key = "saveas", Icon = "\uE792",
                    Title = "Save As...",
                    Description = "Choose a different output folder for the new bundle"
                },
            };

            var dialog = new Views.ActionPickerDialog(
                "Choose where to save the updated bundle.",
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
                var root = FileDialogService.BrowseFolder("Select output folder for the bundle");
                if (string.IsNullOrEmpty(root))
                    continue; // Re-show the dialog

                var sub = BundleService.ResolveSubDirectory(_settings, new AppSection
                {
                    Company = newCompany, Name = newName,
                    DotVersion = newDotVersion, Version = newVersion,
                    Language = App.Language
                });
                dir = Path.Combine(root, sub);
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

            // Set up deferred installer
            _pendingInstallerPath = installerPath;
            _pendingIconBitmap = InstallerIconSource as BitmapSource;
            var iconName = BundleService.Sanitize(newName);
            if (string.IsNullOrEmpty(iconName)) iconName = "appIcon";
            App.IconFile = Path.Combine(_settings.IconFolderName, $"{iconName}.png");

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
    // Full/Upgrade mode: save dialogs with path visualization
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds a visual panel showing old and new bundle paths as a tree.
    /// </summary>
    private static FrameworkElement BuildUpgradePathPanel(string oldPath, string newPath)
    {
        var accent = Application.Current.TryFindResource("AccentBgBrush") as Brush;
        var primary = Application.Current.TryFindResource("TextPrimaryBrush") as Brush;
        var secondary = Application.Current.TryFindResource("TextSecondaryBrush") as Brush;
        var muted = Application.Current.TryFindResource("TextMutedBrush") as Brush;

        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

        // Old bundle
        panel.Children.Add(new TextBlock
        {
            Text = "Current bundle",
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = secondary, Margin = new Thickness(0, 0, 0, 2)
        });
        var oldRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 10) };
        oldRow.Children.Add(new TextBlock
        {
            Text = "\uE8B7", FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14, Foreground = muted, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        oldRow.Children.Add(new TextBlock
        {
            Text = oldPath, FontSize = 11, FontFamily = new FontFamily("Consolas"),
            Foreground = muted, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(oldRow);

        // New bundle
        panel.Children.Add(new TextBlock
        {
            Text = "New bundle location",
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = primary, Margin = new Thickness(0, 0, 0, 2)
        });
        var newRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 4) };
        newRow.Children.Add(new TextBlock
        {
            Text = "\uE8B7", FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14, Foreground = accent, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        newRow.Children.Add(new TextBlock
        {
            Text = newPath, FontSize = 11, FontFamily = new FontFamily("Consolas"),
            Foreground = accent, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(newRow);

        // Note
        panel.Children.Add(new TextBlock
        {
            Text = "The current bundle will not be modified.",
            FontSize = 11, Foreground = muted, FontStyle = FontStyles.Italic,
            Margin = new Thickness(0, 8, 0, 0)
        });

        return panel;
    }

    /// <summary>
    /// Shows the upgrade save dialog with path visualization and 2 options:
    /// Save Alongside and Save As. Applies version mutation and saves
    /// the bundle directly if the user confirms. Returns true if saved.
    /// The dialog's Cancel button reverts all changes.
    /// </summary>
    private async Task<bool> ShowUpgradeSaveDialogAsync(
        string installerPath,
        string oldDotVersion, string oldVersion,
        string newDotVersion, string newVersion,
        string newEXEFile, string newMSIFile,
        ImageSource? confirmedIcon, bool keepOldIcon,
        bool iconUserChosen)
    {
        // Compute display paths using a temporary AppSection (no mutation yet)
        var tempApp = new AppSection
        {
            Company = App.Company, Name = App.Name,
            DotVersion = newDotVersion, Version = newVersion,
            Language = App.Language
        };
        var newSubDir = BundleService.ResolveSubDirectory(_settings, tempApp);
        var oldParent = Path.GetDirectoryName(BundleRootDir) ?? string.Empty;
        var newLastSegment = Path.GetFileName(
            newSubDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var alongsidePath = !string.IsNullOrEmpty(oldParent) && !string.IsNullOrEmpty(newLastSegment)
            ? Path.Combine(oldParent, newLastSegment)
            : newSubDir;

        var pathPanel = BuildUpgradePathPanel(BundleRootDir, alongsidePath);

        string? lastKey = "alongside";
        while (true)
        {
            var options = new ActionPickerOption[]
            {
                new()
                {
                    Key = "alongside", Icon = "\uE74E",
                    Title = "Save Alongside",
                    Description = "Create the new version folder next to the current bundle"
                },
                new()
                {
                    Key = "saveas", Icon = "\uE792",
                    Title = "Save As...",
                    Description = "Choose a custom output folder for the new bundle"
                },
            };

            var dialog = new Views.ActionPickerDialog(
                "Choose where to save the upgraded bundle.",
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
                var root = FileDialogService.BrowseFolder("Select output folder for upgraded bundle");
                if (string.IsNullOrEmpty(root))
                    continue; // Re-show the dialog

                dir = Path.Combine(root, newSubDir);
            }
            else // "alongside"
            {
                dir = alongsidePath;
            }

            if (!await ConfirmOverwriteAsync(dir)) continue; // Re-show the dialog

            // -- User confirmed: now apply all changes --

            // Apply version and filename changes
            App.DotVersion = newDotVersion;
            App.Version = newVersion;
            App.EXEFile = newEXEFile;
            App.MSIFile = newMSIFile;

            // Apply icon
            if (!keepOldIcon && confirmedIcon is not null)
                InstallerIconSource = confirmedIcon;
            App.IconUserChosen = iconUserChosen;

            // Replicate version across packages, assignments, deployments, tags
            ReplicateVersionToPackages(oldDotVersion, oldVersion);

            // Set up deferred installer for FlushPendingInstallerToDiskAsync
            _pendingInstallerPath = installerPath;
            _pendingIconBitmap = InstallerIconSource as BitmapSource;
            var iconName = BundleService.Sanitize(App.Name ?? string.Empty);
            if (string.IsNullOrEmpty(iconName)) iconName = "appIcon";
            App.IconFile = Path.Combine(_settings.IconFolderName, $"{iconName}.png");

            return await SaveBundleToDirectoryAsync(dir, BundleRootDir, "Upgrade");
        }
    }
}
