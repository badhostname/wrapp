using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wrapp.Helpers;
using Wrapp.Models;
using Wrapp.Services;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;

namespace Wrapp.ViewModels;

/// <summary>
/// Browse + drag-and-drop entry path for <see cref="GeneralViewModel"/>:
/// <c>BrowseInstallerAsync</c>, <c>HandleDropAsync</c>, the .intunewin
/// drop branch, and the in-dialog installer-preview panel renderer.
/// Moved into a partial file so the core VM focuses on bundle lifecycle
/// (load / save / dirty tracking) without the ~400 lines of file-handling
/// flow plumbed through it.
/// </summary>
public partial class GeneralViewModel
{
    // -----------------------------------------------------------------------
    // Busy lock for drop discovery
    // -----------------------------------------------------------------------

    /// <summary>
    /// True while dropped-file discovery (installer metadata, MSI icon table,
    /// .intunewin inspection, icon extraction) runs off the UI thread. The
    /// view mirrors this into the drag overlay (blur + live status) and
    /// rejects new drops -- so a slow read (Defender scanning a Downloads
    /// file, an MSI on a network share) shows a working overlay instead of a
    /// frozen "Not Responding" window.
    /// </summary>
    [ObservableProperty] private bool _isDropBusy;

    /// <summary>Status line shown on the busy overlay while discovery runs.</summary>
    [ObservableProperty] private string _dropBusyText = string.Empty;

    /// <summary>
    /// Runs one discovery step on the thread pool with the busy overlay up.
    /// The property writes happen on the UI thread (before the await and in
    /// its continuation), so the view's PropertyChanged mirror needs no
    /// dispatcher hop.
    /// </summary>
    private async Task<T> RunDropDiscoveryAsync<T>(string status, Func<T> work)
    {
        IsDropBusy = true;
        DropBusyText = status;
        try { return await Task.Run(work); }
        finally { IsDropBusy = false; }
    }

    // -----------------------------------------------------------------------
    // Browse installer (alternative to drag-and-drop)
    // -----------------------------------------------------------------------

    [RelayCommand]
    private async Task BrowseInstallerAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Installers (*.exe;*.msi;*.msp;*.intunewin)|*.exe;*.msi;*.msp;*.intunewin|All files (*.*)|*.*",
            Title = "Select Installer or IntuneWin File"
        };
        if (dialog.ShowDialog() == true)
            await HandleDropAsync(new[] { dialog.FileName });
    }

    // -----------------------------------------------------------------------
    // Drag and drop handler (called from code-behind after validation)
    // -----------------------------------------------------------------------

    public async Task HandleDropAsync(string[] paths)
    {
        if (paths.Length == 0) return;
        // Discovery in progress: the view also refuses drops while busy, but
        // guard here too so Browse and any future callers can't overlap.
        if (IsDropBusy) return;
        var first = paths[0];

        AppLogger.Info($"General: drop accepted - {first}");

        // Folder: look for Config.json inside (Script/ or root)
        if (Directory.Exists(first))
        {
            var jsonPath = BundleService.FindConfigJson(first);
            if (jsonPath is not null)
            {
                AppLogger.Info($"General: package folder dropped: {first}");
                await LoadFromPathAsync(jsonPath);
            }
            else
            {
                AppLogger.Warn($"General: drop rejected - no Config.json in: {first}");
                StatusText = $"No Config.json found in: {first}";
            }
            return;
        }

        if (!File.Exists(first)) return;

        var ext = Path.GetExtension(first).ToLowerInvariant();
        if (ext == ".exe" || ext == ".msi" || ext == ".msp")
        {
            // Preview metadata off-thread: MsiOpenDatabase blocks for seconds
            // on Mark-of-the-Web files (Defender scans on first open) and
            // network shares -- the "drop freezes the app" field report.
            var (previewName, previewCompany, previewVersion) = await RunDropDiscoveryAsync(
                $"Reading {Path.GetFileName(first)}...", () => PreviewInstallerMetadata(first));

            bool canUpgrade = !string.IsNullOrWhiteSpace(App.Name)
                           && !string.IsNullOrWhiteSpace(App.DotVersion);

            var options = new ActionPickerOption[]
            {
                new() { Key = "full", Icon = "\uE74E", Title = "Apply Full Installer",
                    Description = "Replace Name, Company, Version, filename, and icon" },
                new() { Key = "upgrade", Icon = "\uE72C", Title = "Upgrade Installer",
                    Description = "Replace Version and filename only. Keep Name and Company.",
                    IsEnabled = canUpgrade, DisabledReason = "No existing app to upgrade" },
                new() { Key = "icon", Icon = "\uE8B9", Title = "Extract Icon Only",
                    Description = "Capture the icon without changing any metadata" },
            };
            var detailPanel = BuildInstallerPreviewPanel(
                Path.GetFileName(first), previewName, previewCompany, previewVersion);
            var dialog = new Views.ActionPickerDialog(
                "Choose how to apply this installer to the current configuration.",
                options, detailPanel, "full");

            var confirmed = await FluentDialog.ShowSelectAsync(
                "Installer Dropped", dialog, "Continue", "Cancel");

            if (!confirmed)
            {
                AppLogger.Info("General: installer drop cancelled by user");
                return;
            }

            AppLogger.Info($"General: installer drop mode = {dialog.SelectedKey}");

            // MSI/MSP Icon table picker (all modes)
            _msiPickedIcon = null;
            if (ext is ".msi" or ".msp")
            {
                // Off-thread: reopens the MSI database and decodes the Icon
                // table's binary streams (bitmaps are frozen by the service,
                // so they're safe to hand back to the UI thread).
                var msiIcons = await RunDropDiscoveryAsync(
                    "Extracting embedded icons...", () => MsiPropertyService.GetIcons(first));
                if (msiIcons.Count > 0)
                {
                    AppLogger.Info($"General: installer contains {msiIcons.Count} embedded icon(s)");
                    var picker = new Views.MsiIconPickerDialog(msiIcons);
                    var picked = await FluentDialog.ShowSelectAsync(
                        "Installer Icons", picker, "Use Selected", "Skip");
                    if (picked && picker.SelectedIcon is not null)
                    {
                        _msiPickedIcon = picker.SelectedIcon;
                        AppLogger.Info("General: user selected an embedded icon");
                    }
                }
            }

            switch (dialog.SelectedKey)
            {
                case "icon":
                    await ApplyIconOnlyAsync(first);
                    _msiPickedIcon = null;
                    return;

                case "full":
                    await ApplyInstallerFile(first);
                    _msiPickedIcon = null;
                    return;

                case "upgrade":
                    await ApplyUpgradeInstaller(first);
                    _msiPickedIcon = null;
                    return;
            }
        }
        else if (ext == ".intunewin")
        {
            await HandleIntuneWinDropAsync(first);
        }
        else if (ext is ".png" or ".jpg" or ".jpeg" or ".ico")
        {
            AppLogger.Info($"General: image file dropped for icon: {first}");
            ApplyIconFile(first);
        }
    }

    // -----------------------------------------------------------------------
    // .intunewin drop handling
    // -----------------------------------------------------------------------

    private async Task HandleIntuneWinDropAsync(string path)
    {
        AppLogger.Info($"General: .intunewin dropped -- {path}");

        if (IsActiveBundle)
        {
            StatusText = "Cannot import .intunewin on an active bundle. Create a new bundle first.";
            AppLogger.Warn("General: .intunewin drop rejected -- active bundle");
            return;
        }

        if (_decryptOrchestrator is null)
        {
            StatusText = "Decrypt service not available.";
            return;
        }

        var bundleRoot = BundleRootDir;
        if (string.IsNullOrEmpty(bundleRoot))
        {
            StatusText = "No workspace active.";
            return;
        }

        IsImportingIntuneWin = true;
        var job = _jobTracker?.BeginJob($"Importing {Path.GetFileName(path)}", BundleRootDir) ?? default;

        // Inspect for embedded keys (off-thread ZIP + XML read, busy overlay up)
        var metadata = await RunDropDiscoveryAsync(
            $"Inspecting {Path.GetFileName(path)}...", () => IntuneWinService.InspectPackage(path));
        IntuneWinDecryptOrchestrator.DecryptResult result;

        var tempDir = Path.Combine(Path.GetTempPath(), $"wrapp_iwin_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            if (metadata is not null && !string.IsNullOrEmpty(metadata.EncryptionKey))
            {
                // Embedded keys -- auto-decrypt
                AppLogger.Info("General: .intunewin has embedded keys, auto-decrypting");
                StatusText = "Decrypting .intunewin with embedded keys...";
                result = await _decryptOrchestrator.DecryptWithEmbeddedKeysAsync(path, tempDir);
            }
            else
            {
                // No embedded keys -- show key source options
                AppLogger.Info("General: .intunewin has no embedded keys, showing options");

                var blob = await Task.Run(() => IntuneWinDecryptOrchestrator.PrepareBlob(path));
                if (blob is null)
                {
                    StatusText = "Could not read the .intunewin file.";
                    return;
                }

                // Hide the "Vault brute force" option when the
                // user has opted out of the DevOps key vault. The orchestrator
                // would silently return an empty key list anyway, but it's
                // clearer to remove the option entirely so the user isn't
                // offered a path that's guaranteed to no-op.
                var vaultGateOn = _featureGate?.IsEnabled(WrappFeatures.AzureDevOpsKeyVault) ?? true;
                var optionsList = new List<ActionPickerOption>();
                if (vaultGateOn)
                    optionsList.Add(new() { Key = "bruteforce", Icon = "\uE72E", Title = "Vault brute force",
                        Description = "Try every encryption key saved in the Azure DevOps vault." });
                optionsList.Add(new() { Key = "manual", Icon = "\uE8D7", Title = "Manual key",
                    Description = "Paste an encryption key and initialization vector (Base64)." });
                optionsList.Add(new() { Key = "csv", Icon = "\uE8A5", Title = "CSV key list",
                    Description = "Browse for a CSV file with EncryptionKey and InitializationVector columns." });
                var options = optionsList.ToArray();
                var dialog = new Views.ActionPickerDialog(
                    "No embedded encryption keys found. Select a key source to decrypt.",
                    options,
                    defaultKey: vaultGateOn ? "bruteforce" : "manual");
                var confirmed = await FluentDialog.ShowSelectAsync("Decrypt .intunewin", dialog, "Continue", "Cancel");
                if (!confirmed)
                {
                    if (blob != path) try { File.Delete(blob); } catch { }
                    return;
                }

                var statusProgress = UiProgress.ForStatus(s => StatusText = s);

                if (dialog.SelectedKey == "manual")
                {
                    var keyDialog = new Views.ActionPickerDialog(
                        "Paste the encryption key and initialization vector.",
                        Array.Empty<ActionPickerOption>());
                    // No in-place key/IV input yet -- route to Tools > Decrypt for full manual input.
                    StatusText = "For manual key input, use Tools > Intunewin Decrypt. The file has been loaded.";
                    if (blob != path) try { File.Delete(blob); } catch { }
                    return;
                }
                else if (dialog.SelectedKey == "bruteforce")
                {
                    StatusText = "Brute-force decrypting...";
                    result = await _decryptOrchestrator.BruteForceDecryptAsync(blob, tempDir, status: statusProgress);
                }
                else if (dialog.SelectedKey == "csv")
                {
                    var csvDialog = new Microsoft.Win32.OpenFileDialog
                    {
                        Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                        Title = "Select CSV Key File"
                    };
                    if (csvDialog.ShowDialog() != true)
                    {
                        if (blob != path) try { File.Delete(blob); } catch { }
                        return;
                    }
                    StatusText = "Decrypting with CSV keys...";
                    result = await _decryptOrchestrator.CsvDecryptAsync(blob, tempDir, csvDialog.FileName, status: statusProgress);
                }
                else
                {
                    if (blob != path) try { File.Delete(blob); } catch { }
                    return;
                }

                // Clean up extracted blob if it was a temp file
                if (blob != path) try { File.Delete(blob); } catch { }
            }

            if (result.Success && result.OutputPath is not null)
            {
                StatusText = "Populating workspace from decrypted content...";
                await BundleService.PopulateFromDecryptedContentAsync(result.OutputPath, bundleRoot, _config, _settings);

                // Load the Config.json that now exists in the workspace.
                // PopulateFromDecryptedContentAsync copied the decrypted content's
                // Config.json into the bundle -- load it fresh so the UI picks up
                // the real app name, version, etc.
                var configInBundle = BundleService.FindConfigJson(bundleRoot);
                if (configInBundle is not null)
                {
                    await LoadFromPathAsync(configInBundle);
                }
                else
                {
                    // No Config.json in decrypted content -- save the framework-updated config
                    await ConfigFileService.SaveAsync(_config, _configPath);
                    await LoadFromPathAsync(_configPath, _config);
                }

                StatusText = $"Imported .intunewin -- framework: {_config.App.ScriptFramework}";
                AppLogger.Info($"General: .intunewin imported successfully, framework={_config.App.ScriptFramework}");
                job.Complete();
            }
            else
            {
                StatusText = result.Message;
                AppLogger.Warn($"General: .intunewin decrypt failed -- {result.Message}");
                job.Fail("Decrypt failed");
            }
        }
        finally
        {
            IsImportingIntuneWin = false;
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Reads metadata from an installer file without applying it.
    /// Returns (ProductName, Company, Version).
    /// </summary>
    private static (string Name, string Company, string Version) PreviewInstallerMetadata(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (ext == ".msp")
            {
                // MSP (patch) files use the MsiPatchMetadata table, not Property
                var (displayName, manufacturer, targetProduct) = MsiPropertyService.GetMspMetadata(path);
                var name = displayName?.Trim() ?? "";
                // Fall back to TargetProductName if DisplayName is empty
                if (string.IsNullOrEmpty(name))
                    name = targetProduct?.Trim() ?? "";
                return (name, manufacturer?.Trim() ?? "", "");
            }
            else if (ext == ".msi")
            {
                var (name, company, version) = MsiPropertyService.GetMsiMetadata(path);
                return (name?.Trim() ?? "", company?.Trim() ?? "", version?.Trim() ?? "");
            }
            else
            {
                var fvi = FileVersionInfo.GetVersionInfo(path);
                return (
                    fvi.ProductName?.Trim() ?? "",
                    fvi.CompanyName?.Trim() ?? "",
                    fvi.FileVersion?.Trim() ?? ""
                );
            }
        }
        catch
        {
            return ("", "", "");
        }
    }

    /// <summary>
    /// Builds the file preview panel shown below the action cards in the installer drop dialog.
    /// </summary>
    private static System.Windows.FrameworkElement BuildInstallerPreviewPanel(
        string fileName, string? name, string? company, string? version)
    {
        var panel = new System.Windows.Controls.StackPanel();

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Dropped file", FontSize = 11,
            Foreground = System.Windows.Application.Current.TryFindResource("TextSecondaryBrush")
                as System.Windows.Media.Brush,
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = fileName,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = System.Windows.Application.Current.TryFindResource("TextPrimaryBrush")
                as System.Windows.Media.Brush,
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis
        });

        var mutedBrush = System.Windows.Application.Current.TryFindResource("TextMutedBrush")
            as System.Windows.Media.Brush;
        var secondaryBrush = System.Windows.Application.Current.TryFindResource("TextSecondaryBrush")
            as System.Windows.Media.Brush;

        var meta = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 4, 0, 0) };
        AddMetaRow(meta, "Name:", name ?? "(unknown)", secondaryBrush, mutedBrush);
        AddMetaRow(meta, "Company:", company ?? "(unknown)", secondaryBrush, mutedBrush);

        if (string.IsNullOrWhiteSpace(version))
        {
            var warningBrush = System.Windows.Application.Current.TryFindResource("WarningBrush")
                as System.Windows.Media.Brush
                ?? new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xD4, 0xA8, 0x40));
            AddMetaRow(meta, "Version:", "Manual input required", secondaryBrush, warningBrush);
        }
        else
        {
            AddMetaRow(meta, "Version:", version, secondaryBrush, mutedBrush);
        }

        panel.Children.Add(meta);
        return panel;

        static void AddMetaRow(System.Windows.Controls.StackPanel parent,
            string label, string value,
            System.Windows.Media.Brush? labelBrush,
            System.Windows.Media.Brush? valueBrush)
        {
            var row = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal
            };
            row.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = label + " ", FontSize = 11, Foreground = labelBrush
            });
            row.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = value, FontSize = 11, Foreground = valueBrush
            });
            parent.Children.Add(row);
        }
    }

    /// <summary>Logs a rejected drop event (called from code-behind when ValidateDroppedPaths returns false).</summary>
    public static void LogDropRejected(string[] paths)
    {
        if (paths.Length == 0)
            AppLogger.Warn("General: drop rejected - empty paths");
        else
            AppLogger.Warn($"General: drop rejected - unsupported file type: {paths[0]}");
    }
}
