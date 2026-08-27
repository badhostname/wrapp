using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.ViewModels;

/// <summary>One row of the update-mode step list (Phase D).</summary>
public sealed partial class UpdateStepItem : ObservableObject
{
    public UpdateFlowStep Step { get; init; }
    public string Label { get; init; } = string.Empty;
    [ObservableProperty] private string _glyph = "○";   // ○ pending
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isDone;
}

public partial class SplashViewModel : StatusViewModelBase
{
    // IsBusy, StatusText, StatusIsError now live on StatusViewModelBase.
    // Re-raise CanExecuteChanged on the two [RelayCommand]s when IsBusy
    // changes -- replaces the generator-emitted [NotifyCanExecuteChangedFor]
    // attributes that only work on properties declared in the same partial
    // class.
    protected override void OnIsBusyChangedInternal(bool oldValue, bool newValue)
    {
        NewPackageCommand.NotifyCanExecuteChanged();
        OpenExistingCommand.NotifyCanExecuteChanged();
    }

    private bool CanInteract() => !IsBusy && !ShowFrameworkPicker;

    /// <summary>Path to Config.json that the main window should load.</summary>
    public string SelectedConfigPath { get; private set; } = string.Empty;

    /// <summary>Script framework selected for new bundles.</summary>
    public ScriptFramework SelectedFramework { get; private set; } = ScriptFramework.Appease;

    /// <summary>Pre-loaded config model (avoids duplicate LoadAsync in GeneralViewModel).</summary>
    public AppConfigModel? PreloadedConfig { get; private set; }

    /// <summary>True when the user has chosen a valid path and the window should close and continue.</summary>
    public bool DialogResult { get; private set; }

    /// <summary>Raised when the splash should close (result already set).</summary>
    public event Action? CloseRequested;

    private DispatcherTimer? _messageTimer;

    /// <summary>Stops the cycling burrito messages. Called when the splash window closes.</summary>
    public void StopMessages()
    {
        _messageTimer?.Stop();
        _messageTimer = null;
    }

    // -------------------------------------------------------------------
    // Update mode (Phase D, update-flow-and-token-polling-plan): the splash
    // doubles as the update screen — step list, honest progress, version
    // info. Driven by UpdateFlowController.
    // -------------------------------------------------------------------

    [ObservableProperty] private bool _isUpdateMode;
    [ObservableProperty] private string _updateVersionLine = string.Empty;
    [ObservableProperty] private string _updateDetailLine = string.Empty;
    [ObservableProperty] private int _updatePercent;
    [ObservableProperty] private bool _updateIndeterminate = true;
    /// <summary>Startup offer: [Update now] / [Close Wrapp] buttons.</summary>
    [ObservableProperty] private bool _updateOfferVisible;
    /// <summary>Steps + progress bar + cancel, while the flow runs.</summary>
    [ObservableProperty] private bool _updateProgressVisible;
    /// <summary>Failure state: [Close Wrapp] button (handoff path only).</summary>
    [ObservableProperty] private bool _updateFailedVisible;

    public ObservableCollection<UpdateStepItem> UpdateSteps { get; } = new();

    /// <summary>True once the user committed to updating — the app's lifetime
    /// now belongs to the update flow, not the bundle-picking path.</summary>
    public bool UpdateFlowEngaged { get; private set; }

    /// <summary>True when this update screen replaced a running session
    /// (handoff) rather than appearing at startup.</summary>
    public bool UpdateFromSession { get; private set; }

    /// <summary>Settings captured when update mode was entered (the flow needs them).</summary>
    public AppSettings? UpdateSettings { get; private set; }

    /// <summary>Set by the Cancel button; the flow honors it before the apply.</summary>
    public bool UpdateCancelRequested { get; set; }

    /// <summary>True while the flow is blocked on other Wrapp windows —
    /// drives the highlighted operator-action banner.</summary>
    [ObservableProperty] private bool _updateWaitingOnWindows;

    /// <summary>Count line inside the waiting banner.</summary>
    [ObservableProperty] private string _updateWaitingDetail = string.Empty;

    public void EnterUpdateMode(string targetVersion, AppSettings settings, bool fromSession)
    {
        StopMessages();
        UpdateSettings    = settings;
        UpdateFromSession = fromSession;
        UpdateVersionLine = $"{AppInfo.VersionDisplay}  →  v{targetVersion}";
        UpdateOfferVisible = !fromSession;   // handoff already opted in
        StatusText  = string.Empty;
        StatusIsError = false;

        UpdateSteps.Clear();
        foreach (var step in new[]
        {
            UpdateFlowStep.WaitingForWindows, UpdateFlowStep.Downloading,
            UpdateFlowStep.Rebuilding, UpdateFlowStep.Applying,
            UpdateFlowStep.Restarting,
        })
            UpdateSteps.Add(new UpdateStepItem { Step = step, Label = ShortLabel(step) });

        IsUpdateMode = true;
    }

    /// <summary>The flow has started: swap the offer for live progress.</summary>
    public void EnterUpdateProgress()
    {
        UpdateFlowEngaged     = true;
        UpdateOfferVisible    = false;
        UpdateFailedVisible   = false;
        UpdateProgressVisible = true;
    }

    /// <summary>Projects the tracker's state onto the step list + status line.</summary>
    public void ApplyTracker(UpdateStepTracker tracker)
    {
        UpdatePercent       = tracker.DisplayPercent;
        UpdateIndeterminate = tracker.IsIndeterminate;
        UpdateWaitingOnWindows = tracker.Step == UpdateFlowStep.WaitingForWindows;
        StatusText = tracker.Step == UpdateFlowStep.Downloading
            ? $"{UpdateStepTracker.Label(tracker.Step)}... {tracker.DisplayPercent}%"
            : $"{UpdateStepTracker.Label(tracker.Step)}...";

        foreach (var item in UpdateSteps)
        {
            item.IsDone   = item.Step < tracker.Step;
            item.IsActive = item.Step == tracker.Step;
            item.Glyph    = item.IsDone ? "✓" : item.IsActive ? "●" : "○";
        }
    }

    /// <summary>Full package hash from the feed manifest — complete, never
    /// elided, grouped for readability (see <see cref="FormatHash"/>).</summary>
    [ObservableProperty] private string _updateHashLine = string.Empty;

    public void SetUpdateDetail(long sizeBytes, string? sha, long deltaSizeBytes = 0)
    {
        // A delta chain means only the small patch downloads; the full
        // package is rebuilt locally from the installed version + delta
        // (the "Rebuilding package" step). Say so — showing the full size
        // alone reads as a 150+ MB download that is not actually happening.
        UpdateDetailLine = deltaSizeBytes > 0 && deltaSizeBytes < sizeBytes
            ? $"{deltaSizeBytes / 1024.0 / 1024.0:0.0} MB delta download  ·  rebuilds the {sizeBytes / 1024.0 / 1024.0:0.0} MB package locally"
            : $"{sizeBytes / 1024.0 / 1024.0:0.0} MB";
        UpdateHashLine   = FormatHash(sha);
    }

    /// <summary>
    /// Formats the full hash for on-screen verification: labeled by kind,
    /// split into 8-char groups, balanced across two centered lines instead
    /// of raggedly wrapping (SHA-256: 4+4 groups, SHA-1: 3+2).
    /// </summary>
    internal static string FormatHash(string? sha)
    {
        if (string.IsNullOrEmpty(sha)) return string.Empty;
        var kind = sha!.Length switch { 64 => "SHA-256", 40 => "SHA-1", _ => "Hash" };
        var groups = new List<string>();
        for (var i = 0; i < sha.Length; i += 8)
            groups.Add(sha.Substring(i, Math.Min(8, sha.Length - i)));
        var firstLine = (groups.Count + 1) / 2;
        var top = string.Join(" ", groups.Take(firstLine));
        var bottom = string.Join(" ", groups.Skip(firstLine));
        return bottom.Length > 0 ? $"{kind}\n{top}\n{bottom}" : $"{kind}\n{top}";
    }

    public void SetWaitingCount(int otherWindows)
    {
        UpdateWaitingOnWindows = true;
        UpdateWaitingDetail = otherWindows == 1
            ? "1 other Wrapp window is open. It has been asked to close and will offer to save any open work."
            : $"{otherWindows} other Wrapp windows are open. Each has been asked to close and will offer to save any open work.";
    }

    /// <summary>Failure while the flow ran (handoff path — no cards to return to).</summary>
    public void FailUpdate(string message)
    {
        UpdateProgressVisible = false;
        UpdateFailedVisible   = true;
        StatusIsError = true;
        StatusText    = message;
    }

    /// <summary>Startup path fail-open: drop back to the bundle cards.</summary>
    public void ExitUpdateMode(string? errorMessage)
    {
        IsUpdateMode = false;
        UpdateProgressVisible = false;
        UpdateOfferVisible = false;
        UpdateFlowEngaged = false;
        if (errorMessage is not null)
        {
            StatusIsError = true;
            StatusText = errorMessage;
        }
    }

    private static string ShortLabel(UpdateFlowStep step) => step switch
    {
        UpdateFlowStep.Rebuilding => "Rebuilding package",
        _ => UpdateStepTracker.Label(step),
    };

    /// <summary>Starts the new bundle flow: show framework picker, then create workspace.</summary>
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void NewPackage()
    {
        // Show framework picker panel (handled in XAML via visibility toggle)
        ShowFrameworkPicker = true;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewPackageCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenExistingCommand))]
    private bool _showFrameworkPicker;

    /// <summary>Key of the selected main action card ("new" or "open"). Empty when no selection yet.</summary>
    [ObservableProperty] private string _selectedActionKey = string.Empty;

    /// <summary>Key of the selected framework card ("appease" or "psadt"). Empty when no selection yet.</summary>
    [ObservableProperty] private string _selectedFrameworkKey = string.Empty;

    /// <summary>Called when the user picks a framework from the picker cards.</summary>
    public async Task CreateBundleWithFrameworkAsync(ScriptFramework framework)
    {
        try
        {
            SelectedFramework = framework;
            SelectedFrameworkKey = framework == ScriptFramework.PSADT ? "psadt" : "appease";
            IsBusy = true;
            StatusIsError = false;
            AppLogger.Info($"Splash: creating new {framework} workspace");

            var msgIndex = LoadingMessages.RandomIndex();
            StatusText   = LoadingMessages.At(msgIndex);

            _messageTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
            _messageTimer.Tick += (_, _) =>
            {
                msgIndex   = LoadingMessages.NextIndex(msgIndex);
                StatusText = LoadingMessages.At(msgIndex);
            };
            _messageTimer.Start();

            var configPath = await TempWorkspaceService.CreateAsync();

            // Set framework on the blank config so it's ready when the main window loads
            var config = await ConfigFileService.LoadAsync(configPath);
            config.App.ScriptFramework = framework.ToString();
            await ConfigFileService.SaveAsync(config, configPath);
            PreloadedConfig = config;

            AppLogger.Info($"Splash: workspace created at {configPath} (framework: {framework})");
            SelectedConfigPath = configPath;
            DialogResult       = true;
            IsBusy = false;
            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            AppLogger.Exception("Splash.CreateBundleWithFrameworkAsync", ex);
            StatusIsError = true;
            StatusText    = $"Error: {ex.Message}";
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task OpenExistingAsync(object? parameter)
    {
        // Accept a pre-validated config path from the code-behind,
        // or browse for one if called without a parameter.
        string? configPath = parameter as string;
        if (string.IsNullOrEmpty(configPath))
        {
            AppLogger.Info("Splash: opening existing bundle");
            var folder = FileDialogService.BrowseFolder("Select bundle folder containing Config.json");
            if (string.IsNullOrEmpty(folder)) return;

            configPath = Services.BundleService.FindConfigJson(folder);
            if (configPath is null)
            {
                AppLogger.Warn($"Splash: no Config.json in {folder}");
                StatusIsError = true;
                StatusText = "No Config.json found in the selected folder.";
                return;
            }
        }

        AppLogger.Info($"Splash: opening config at {configPath}");

        IsBusy = true;

        // Show loading progress while pre-reading the config
        // Timer keeps running through App.StartupCore init so messages don't freeze
        StatusIsError = false;
        var msgIndex = LoadingMessages.RandomIndex();
        StatusText   = LoadingMessages.At(msgIndex);

        _messageTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _messageTimer.Tick += (_, _) =>
        {
            msgIndex   = LoadingMessages.NextIndex(msgIndex);
            StatusText = LoadingMessages.At(msgIndex);
        };
        _messageTimer.Start();

        try
        {
            // Pre-read and cache the config so GeneralViewModel can skip the duplicate load
            PreloadedConfig = await ConfigFileService.LoadAsync(configPath);
        }
        catch (Exception ex)
        {
            StopMessages();
            AppLogger.Exception("Splash.OpenExistingAsync", ex);
            StatusIsError = true;
            StatusText    = $"Error: {ex.Message}";
            IsBusy = false;
            return;
        }

        SelectedConfigPath = configPath;
        DialogResult       = true;
        IsBusy = false;
        CloseRequested?.Invoke();
    }
}
