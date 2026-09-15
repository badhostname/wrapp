using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wrapp.Models;
using Wrapp.Services;
using Wrapp.Services.Gates;

namespace Wrapp.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly PowerShellService _ps;
    public BackgroundJobTracker JobTracker { get; }
    private GeneralViewModel? _generalVm;
    private IntuneViewModel? _intuneVm;
    private SCCMViewModel? _sccmVm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsSection))]
    private NavigationSection _currentSection = NavigationSection.General;

    /// <summary>True when the Settings section is active. Used to swap the save strip button.</summary>
    public bool IsSettingsSection => CurrentSection == NavigationSection.Settings;

    /// <summary>
    /// Enterprise policy: nav-section visibility for the rail -
    /// <c>Visibility="{Binding Nav[Inventory]}"</c>. Settings and General can
    /// never be hidden (enforced when the snapshot is built).
    /// </summary>
    public Services.Policy.PolicyNavAccessor Nav { get; } = new();

    [ObservableProperty]
    private ModuleDefaults _moduleDefaults = ModuleDefaults.Empty;

    [ObservableProperty]
    private string _statusMessage = "Initializing...";

    [ObservableProperty]
    private bool _isInitialized;

    [ObservableProperty]
    private bool _isInitializing = true;

    /// <summary>Account flyout VM. Bound from the title bar account button.</summary>
    public AccountViewModel? AccountVm { get; internal set; }

    /// <summary>
    /// Settings VM. Bound from the shared save strip so the Save Settings
    /// button can reflect dirty state and show the preferences status message
    /// in the bottom-left slot (same row as the Save button).
    /// </summary>
    public SettingsViewModel? SettingsVm { get; internal set; }

    /// <summary>True when the current config has been edited since last save.</summary>
    public bool IsDirty => _generalVm?.IsDirty ?? false;

    /// <summary>True when the Save Bundle button should be enabled (dirty and not transferring).</summary>
    public bool CanSaveBundle => IsDirty && !IsTransferring;

    /// <summary>
    /// Path shown in the status bar.  Appends " *" when there are uncommitted changes.
    /// </summary>
    public string StatusBarPath
        => string.IsNullOrEmpty(_generalVm?.FolderDisplayPath)
            ? "No package loaded"
            : IsDirty
                ? $"{_generalVm.FolderDisplayPath} *"
                : _generalVm.FolderDisplayPath;

    /// <summary>Display label for the current script framework (e.g. "Appease v2.3").</summary>
    public string FrameworkLabel
    {
        get
        {
            var fw = ScriptFrameworkProvider.Parse(_generalVm?.App.ScriptFramework);
            return fw == ScriptFramework.PSADT ? "PSADT v4.1.8" : "Appease v2.3";
        }
    }

    /// <summary>Pack URI for the current script framework icon.</summary>
    public string FrameworkIconUri
    {
        get
        {
            var fw = ScriptFrameworkProvider.Parse(_generalVm?.App.ScriptFramework);
            return fw == ScriptFramework.PSADT
                ? "/Assets/PSADT-icon.png"
                : "/Assets/PowerShell-icon.png";
        }
    }

    /// <summary>Total Intune validation error count (for nav badge).</summary>
    public int IntuneErrorCount => _intuneVm?.TotalErrorCount ?? 0;

    /// <summary>Total SCCM validation error count (for nav badge).</summary>
    public int SccmErrorCount => _sccmVm?.TotalErrorCount ?? 0;

    /// <summary>Total Intune NON-BLOCKING warning count (amber nav badge).</summary>
    public int IntuneWarningCount => _intuneVm?.TotalWarningCount ?? 0;

    /// <summary>Total SCCM NON-BLOCKING warning count (amber nav badge).</summary>
    public int SccmWarningCount => _sccmVm?.TotalWarningCount ?? 0;

    /// <summary>Duplicate detection-symbol count (for the Detection nav badge).</summary>
    private DetectionViewModel? _detectionVm;
    public int DetectionErrorCount => _detectionVm?.ErrorCount ?? 0;

    /// <summary>Placeholder rows in error (for the Settings nav badge).</summary>
    public int SettingsErrorCount => SettingsVm?.PlaceholderErrorCount ?? 0;

    /// <summary>Inventory filtered match count (for nav badge). 0 = no filter active or no data.</summary>
    private InventoryViewModel? _inventoryVm;
    public int InventoryMatchCount => _inventoryVm?.MatchCount ?? 0;

    /// <summary>True while a large file operation is running (copy/delete). Shown in status bar.</summary>
    public bool IsTransferring => _generalVm?.IsTransferring ?? false;

    /// <summary>Progress 0-100 for the current file transfer.</summary>
    public double TransferProgress => _generalVm?.TransferProgress ?? 0;

    /// <summary>Status text for the current transfer (e.g. "Copying installer... 45%").</summary>
    public string TransferStatusText => _generalVm?.TransferStatusText ?? string.Empty;

    /// <summary>True while a workspace is being created / loaded. Shows the loading overlay.</summary>
    [ObservableProperty] private bool _isLoading;

    /// <summary>Current loading message shown in the overlay (cycles through burrito phrases).</summary>
    [ObservableProperty] private string _loadingMessage = string.Empty;

    /// <summary>Sidebar collapsed to icons only (hamburger toggle). The
    /// NavItem/NavLabel styles watch this to hide labels and center icons;
    /// session-scoped by design - every launch starts expanded.</summary>
    [ObservableProperty] private bool _isNavCollapsed;

    private DispatcherTimer? _loadingTimer;
    private int              _loadingMessageIndex;

    private void StartLoading()
    {
        _loadingMessageIndex = LoadingMessages.RandomIndex();
        LoadingMessage       = LoadingMessages.At(_loadingMessageIndex);
        IsLoading            = true;

        _loadingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _loadingTimer.Tick += (_, _) =>
        {
            _loadingMessageIndex = LoadingMessages.NextIndex(_loadingMessageIndex);
            LoadingMessage       = LoadingMessages.At(_loadingMessageIndex);
        };
        _loadingTimer.Start();
    }

    private void StopLoading()
    {
        _loadingTimer?.Stop();
        _loadingTimer  = null;
        IsLoading      = false;
        LoadingMessage = string.Empty;
    }

    public MainViewModel(PowerShellService ps, BackgroundJobTracker jobTracker)
    {
        _ps = ps;
        JobTracker = jobTracker;
        _copiedFlash        = new Wrapp.Helpers.TimedFlag(TimeSpan.FromSeconds(2), () => ShowCopiedFeedback = false);
        _settingsSavedFlash = new Wrapp.Helpers.TimedFlag(TimeSpan.FromSeconds(2), () => ShowSettingsSaved = false);
    }

    /// <summary>
    /// Called from App.xaml.cs so MainViewModel can bubble Intune/SCCM error counts to nav badges.
    /// </summary>
    public void WirePackageVms(IntuneViewModel intuneVm, SCCMViewModel sccmVm)
    {
        _intuneVm = intuneVm;
        _sccmVm = sccmVm;

        PropertyRelay.Wire(intuneVm, OnPropertyChanged,
            PropertyRelay.When(nameof(IntuneViewModel.TotalErrorCount),
                nameof(IntuneErrorCount)),
            PropertyRelay.When(nameof(IntuneViewModel.TotalWarningCount),
                nameof(IntuneWarningCount)));
        PropertyRelay.Wire(sccmVm, OnPropertyChanged,
            PropertyRelay.When(nameof(SCCMViewModel.TotalErrorCount),
                nameof(SccmErrorCount)),
            PropertyRelay.When(nameof(SCCMViewModel.TotalWarningCount),
                nameof(SccmWarningCount)));
    }

    /// <summary>Wires InventoryViewModel so match count bubbles to the nav badge.</summary>
    public void WireInventoryVm(InventoryViewModel inventoryVm)
    {
        _inventoryVm = inventoryVm;
        PropertyRelay.Wire(inventoryVm, OnPropertyChanged,
            PropertyRelay.When(nameof(InventoryViewModel.MatchCount),
                nameof(InventoryMatchCount)));
    }

    /// <summary>Wires DetectionViewModel so duplicate-symbol errors bubble to the nav badge.</summary>
    public void WireDetectionVm(DetectionViewModel detectionVm)
    {
        _detectionVm = detectionVm;
        PropertyRelay.Wire(detectionVm, OnPropertyChanged,
            PropertyRelay.When(nameof(DetectionViewModel.ErrorCount),
                nameof(DetectionErrorCount)));
    }

    /// <summary>
    /// Wires SettingsViewModel (also assigns <see cref="SettingsVm"/>) so the
    /// placeholder error count bubbles to the Settings nav badge - the same
    /// trickle-back the package error counts use.
    /// </summary>
    public void WireSettingsVm(SettingsViewModel settingsVm)
    {
        SettingsVm = settingsVm;
        PropertyRelay.Wire(settingsVm, OnPropertyChanged,
            PropertyRelay.When(nameof(SettingsViewModel.PlaceholderErrorCount),
                nameof(SettingsErrorCount)));
    }

    /// <summary>
    /// Called from App.xaml.cs after construction so MainViewModel can delegate IsDirty
    /// and bubble up property-changed notifications.
    /// </summary>
    public void WireGeneralVm(GeneralViewModel vm)
    {
        _generalVm = vm;

        // IsDirty + HasConfig both drive the same 5 bubbled properties; extract
        // once to keep the two relay rules symmetrical.
        var dirtyTargets = new[]
        {
            nameof(IsDirty),
            nameof(CanSaveBundle),
            nameof(StatusBarPath),
            nameof(ShowBundleSaved),
            nameof(ShowSaveChanges),
        };
        // Any of the three transfer-state changes bubble the same 4 properties.
        var transferTargets = new[]
        {
            nameof(IsTransferring),
            nameof(TransferProgress),
            nameof(TransferStatusText),
            nameof(CanSaveBundle),
        };

        PropertyRelay.Wire(vm, OnPropertyChanged,
            PropertyRelay.When(nameof(GeneralViewModel.IsDirty),   dirtyTargets),
            PropertyRelay.When(nameof(GeneralViewModel.HasConfig), dirtyTargets),
            PropertyRelay.When(nameof(GeneralViewModel.FolderDisplayPath),
                nameof(StatusBarPath)),
            PropertyRelay.When(nameof(GeneralViewModel.IsTransferring),    transferTargets),
            PropertyRelay.When(nameof(GeneralViewModel.TransferProgress),  transferTargets),
            PropertyRelay.When(nameof(GeneralViewModel.TransferStatusText), transferTargets));

        vm.ConfigLoaded += (_, _) =>
        {
            OnPropertyChanged(nameof(StatusBarPath));
            OnPropertyChanged(nameof(FrameworkLabel));
            OnPropertyChanged(nameof(FrameworkIconUri));
        };
    }

    /// <summary>True briefly after copying path to clipboard, for visual feedback.</summary>
    [ObservableProperty] private bool _showCopiedFeedback;

    private readonly Wrapp.Helpers.TimedFlag _copiedFlash;

    /// <summary>True while the bundle is being saved to disk.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBundleSaved))]
    [NotifyPropertyChangedFor(nameof(ShowSaveChanges))]
    private bool _isSaving;

    /// <summary>True when the bundle is saved and clean (no unsaved changes, not actively saving).</summary>
    public bool ShowBundleSaved => !IsDirty && (_generalVm?.HasConfig ?? false) && !IsSaving;

    /// <summary>True when the default "Save Changes" label should be shown (not saving, not in saved state).</summary>
    public bool ShowSaveChanges => !IsSaving && !ShowBundleSaved;

    /// <summary>True briefly after saving settings, for visual feedback (checkmark icon).</summary>
    [ObservableProperty] private bool _showSettingsSaved;

    private readonly Wrapp.Helpers.TimedFlag _settingsSavedFlash;

    private void FlashSettingsSaveConfirmation()
    {
        ShowSettingsSaved = true;
        _settingsSavedFlash.Trigger();
    }

    [RelayCommand]
    private void CopyPathToClipboard()
    {
        var path = _generalVm?.FolderDisplayPath;
        if (!string.IsNullOrEmpty(path))
        {
            System.Windows.Clipboard.SetText(path);
            ShowCopiedFeedback = true;
            _copiedFlash.Trigger();
        }
    }

    [RelayCommand]
    private void OpenFolderInExplorer()
    {
        var path = _generalVm?.FolderDisplayPath;
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            Process.Start("explorer.exe", path);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to open folder in Explorer: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Navigate(NavigationSection section)
    {
        CurrentSection = section;
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        IsInitializing = true;
        StatusMessage = "Loading Wrapp.Packager module...";

        var ok = await _ps.InitializeAsync();
        if (!ok)
        {
            StatusMessage = $"Module load failed: {_ps.LastInitError}";
            IsInitializing = false;
            return;
        }

        ModuleDefaults = await _ps.LoadDefaultsAsync();
        IsInitialized = true;
        IsInitializing = false;
        StatusMessage = "Ready";
    }

    [RelayCommand]
    private async Task NewAsync()
    {
        AppLogger.Info("Main: New Package requested");
        if (IsDirty && !await ConfirmDiscardAsync())
        {
            AppLogger.Info("Main: New Package cancelled - unsaved changes not discarded");
            return;
        }

        var framework = await ShowFrameworkPickerAsync();
        if (framework is null)
        {
            AppLogger.Info("Main: New Package cancelled - no framework selected");
            return;
        }

        // Capture old temp workspace before creating a new one
        string? oldTempDir = _generalVm?.IsTempWorkspace() == true
            ? _generalVm.BundleRootDir : null;

        try
        {
            StartLoading();
            AppLogger.Info($"Main: creating new {framework} workspace");
            var configPath = await TempWorkspaceService.CreateAsync();

            // Set framework on the blank config before loading
            var config = await ConfigFileService.LoadAsync(configPath);
            config.App.ScriptFramework = framework.Value.ToString();
            await ConfigFileService.SaveAsync(config, configPath);

            AppLogger.Info($"Main: workspace created at {configPath}");
            if (_generalVm is not null)
                await _generalVm.LoadFromPathAsync(configPath, config);

            if (!string.IsNullOrEmpty(oldTempDir))
                TempWorkspaceService.DeleteWorkspaceBackground(oldTempDir);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error creating workspace: {ex.Message}";
            AppLogger.Exception("MainViewModel.NewAsync", ex);
            // Surface to user; a silent status-bar line is easy to miss.
            await FluentDialog.ShowExceptionAsync("creating a new workspace", ex);
        }
        finally
        {
            StopLoading();
        }
    }

    private static async Task<ScriptFramework?> ShowFrameworkPickerAsync()
    {
        var options = new ActionPickerOption[]
        {
            new()
            {
                Key = "appease",
                IconImage = new BitmapImage(new Uri("pack://application:,,,/Assets/PowerShell-icon.png")),
                Title = "Appease v2.3",
                Description = "Separate Install, Uninstall, and Detect scripts with Appease helper module"
            },
            new()
            {
                Key = "psadt",
                IconImage = new BitmapImage(new Uri("pack://application:,,,/Assets/PSADT-icon.png")),
                Title = "PSADT v4.1.8",
                Description = "Single Deploy script with Install, Uninstall, and Repair phases"
            },
        };

        var dialog = new Views.ActionPickerDialog(
            "Choose a script framework for the new bundle.", options, defaultKey: "appease");

        var confirmed = await FluentDialog.ShowSelectAsync(
            "New Bundle", dialog, "Create", "Cancel");

        if (!confirmed) return null;

        return dialog.SelectedKey == "psadt" ? ScriptFramework.PSADT : ScriptFramework.Appease;
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        AppLogger.Info("Main: Open Package requested");
        if (IsDirty && !await ConfirmDiscardAsync())
        {
            AppLogger.Info("Main: Open Package cancelled - unsaved changes not discarded");
            return;
        }

        // Capture old temp workspace before opening a different bundle
        string? oldTempDir = _generalVm?.IsTempWorkspace() == true
            ? _generalVm.BundleRootDir : null;

        var folder = FileDialogService.BrowseFolder("Select bundle folder containing Config.json");
        if (string.IsNullOrEmpty(folder)) return;

        var configPath = Services.BundleService.FindConfigJson(folder);
        if (configPath is null)
        {
            AppLogger.Warn($"Main: Open Package - no Config.json in {folder}");
            await FluentDialog.ShowWarningAsync(
                "Config Not Found",
                $"No Config.json was found in the selected folder.\n\n{folder}");
            return;
        }

        try
        {
            StartLoading();
            AppLogger.Info($"Main: opening package from {configPath}");
            if (_generalVm is not null)
                await _generalVm.LoadFromPathAsync(configPath);

            if (!string.IsNullOrEmpty(oldTempDir))
                TempWorkspaceService.DeleteWorkspaceBackground(oldTempDir);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error opening package: {ex.Message}";
            AppLogger.Exception("MainViewModel.OpenAsync", ex);
            await FluentDialog.ShowExceptionAsync("opening the bundle", ex);
        }
        finally
        {
            StopLoading();
        }
    }

    private static async Task<bool> ConfirmDiscardAsync()
    {
        return await FluentDialog.ConfirmAsync(
            "Unsaved Changes",
            "You have unsaved changes. Discard them and continue?",
            "Discard", "Cancel");
    }

    // Set by ConfigLoaded when a config is loaded
    private string _currentConfigPath = string.Empty;

    public void SetConfigPath(string path)
    {
        _currentConfigPath = path;
    }

    /// <summary>
    /// Delegated from GeneralViewModel after construction so the always-visible sidebar buttons
    /// can bind to the MainViewModel DataContext without needing a separate DataContext.
    /// </summary>
    public IAsyncRelayCommand? SaveBundleCommand    { get; internal set; }
    public IAsyncRelayCommand? SaveBundleAsCommand  { get; internal set; }

    /// <summary>Delegated from SettingsViewModel so the save strip can show "Save Settings" on the Settings page.</summary>
    public IRelayCommand? SaveSettingsCommand { get; internal set; }

    /// <summary>Wrapper that executes Save Bundle with saving-state feedback.</summary>
    [RelayCommand]
    private async Task SaveBundleWithFeedback()
    {
        if (SaveBundleCommand is null) return;

        IsSaving = true;
        try
        {
            await SaveBundleCommand.ExecuteAsync(null);
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>Wrapper that executes Save Settings then flashes a checkmark.</summary>
    [RelayCommand]
    private void SaveSettingsWithFeedback()
    {
        SaveSettingsCommand?.Execute(null);
        FlashSettingsSaveConfirmation();
    }

    // -----------------------------------------------------------------------
    // Gate framework: status-bar "action needed" indicator (advisory gates).
    // Blocking gates are resolved at startup in App.xaml.cs; advisory gates
    // surface here so the user can resolve them on demand (e.g. re-approve a
    // Key Vault URL) without the old dirty-Save workaround.
    // -----------------------------------------------------------------------

    private GateService? _gateService;

    [ObservableProperty] private bool _hasPendingActions;
    [ObservableProperty] private int _pendingActionsCount;
    [ObservableProperty] private string _pendingActionsLabel = string.Empty;
    [ObservableProperty] private string _pendingActionsTooltip = string.Empty;

    /// <summary>Wires the gate service and does an initial evaluation.</summary>
    public void WireGateService(GateService gateService)
    {
        _gateService = gateService;
        RefreshPendingActions();
    }

    /// <summary>Re-evaluates pending advisory gates and refreshes the indicator.</summary>
    public void RefreshPendingActions()
    {
        var pending = _gateService?.PendingAdvisory() ?? new List<IAppGate>();
        PendingActionsCount = pending.Count;
        HasPendingActions   = pending.Count > 0;
        PendingActionsLabel = pending.Count == 1 ? "1 action needed" : $"{pending.Count} actions needed";
        PendingActionsTooltip = pending.Count == 0
            ? string.Empty
            : "Click to resolve:\n" + string.Join("\n", pending.Select(g => "• " + g.Title));
    }

    /// <summary>Resolves each pending advisory gate in turn, then refreshes.</summary>
    [RelayCommand]
    private async Task ResolvePendingActionsAsync()
    {
        if (_gateService is null) return;
        foreach (var gate in _gateService.PendingAdvisory())
            await _gateService.ResolveAsync(gate);
        RefreshPendingActions();
    }
}
