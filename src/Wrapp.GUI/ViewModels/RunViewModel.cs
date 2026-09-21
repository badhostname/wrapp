using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.ViewModels;

/// <summary>A single line in the run log output with optional color code.</summary>
public sealed class LogEntry
{
    public string Content   { get; init; } = string.Empty;
    public string ColorCode { get; init; } = "Default";

    public System.Windows.Media.Brush ForegroundBrush => ColorCode switch
    {
        "Error"   => System.Windows.Media.Brushes.Salmon,
        "Warning" => System.Windows.Media.Brushes.Orange,
        "Info"    => System.Windows.Media.Brushes.White,
        "Verbose" => System.Windows.Media.Brushes.LightGray,
        _         => System.Windows.Media.Brushes.Gainsboro
    };
}

public partial class RunViewModel : StatusViewModelBase
{
    private readonly PowerShellService _ps;
    private readonly GeneralViewModel  _appInfoVm;
    private readonly MsalAuthService   _authService;
    private EncryptionKeyStoreService? _keyStore;
    private IFeatureGate? _featureGate;
    private Services.BackgroundJobTracker? _jobTracker;
    private Models.JobHandle _currentRunJob;
    private readonly PhaseDetector     _phaseDetector = new();
    private readonly ConnectionChecker _connectionChecker;
    private readonly object _logLock = new();

    private CancellationTokenSource? _cts;
    private DispatcherTimer? _tokenTimer;
    private MsalTokenResult? _lastToken;
    private string? _activePackageName;

    /// <summary>
    /// Package names being processed in the current per-tenant/site PackageAsync
    /// pass. A multi-tenant run makes one PackageAsync call per tenant, each
    /// handling only that tenant's subset -- so "global" progress steps
    /// (collision/wrapping) and finalization must be scoped to this subset,
    /// otherwise the first tenant's completion prematurely finalizes packages
    /// that belong to later tenants. Empty = no scoping (affects all).
    /// </summary>
    private readonly HashSet<string> _currentPassPackages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True while the active pass's progress is driven by the module's typed
    /// <c>Write-WrappStep</c> events (the Intune path), which causes
    /// <see cref="OnPhaseChanged"/> to suppress the equivalent regex phases.
    /// Cleared for the SCCM path, whose module still reports progress via log
    /// text (regex). Read on PS streaming threads, written on the run thread
    /// -- volatile for visibility.
    /// </summary>
    private volatile bool _stepEventsOwnProgress;

    // Prevents overlapping RefreshConnectionStatusAsync calls (which stack
    // interactive MSAL prompts). Only one refresh runs at a time; concurrent
    // callers are silently skipped.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // Per-tenant token cache (keyed by tenant ID)
    private readonly Dictionary<string, MsalTokenResult> _tenantTokens =
        new(StringComparer.OrdinalIgnoreCase);

    public RunViewModel(PowerShellService ps, GeneralViewModel appInfoVm, MsalAuthService authService)
    {
        _ps                = ps;
        _appInfoVm         = appInfoVm;
        _authService       = authService;
        _connectionChecker = new ConnectionChecker(ps);

        // Enable thread-safe collection access for WPF binding
        BindingOperations.EnableCollectionSynchronization(LogLines, _logLock);

        _phaseDetector.PhaseChanged += OnPhaseChanged;

        // Defer connection check until a config is loaded (avoids network calls
        // during startup when no tenants are configured yet).
        _appInfoVm.ConfigLoaded += (_, _) => _ = SafeRefreshAsync();

        // Keep token cache and connection status up to date when any component
        // acquires a fresh token (e.g. AccountViewModel auto-refresh).
        _authService.TokenAcquired += OnTokenAcquired;

        // SilentAuthGate unlock triggers: connectivity returning and
        // resume-from-sleep are the moments a previously-failed silent auth
        // becomes plausible again. (RunViewModel lives for the whole process,
        // so these static subscriptions are never leaked.)
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>Wire EncryptionKeyStoreService for capturing keys during upload.</summary>
    public void WireKeyStore(Services.EncryptionKeyStoreService keyStore) => _keyStore = keyStore;

    /// <summary>
    /// Wires the feature-gate so <see cref="CaptureEncryptionKeys"/>
    /// can short-circuit cleanly when the user has opted out of the vault.
    /// Without this hook, the capture path fires-and-forgets a <c>SaveKeysAsync</c>
    /// call that the service rejects via <see cref="EncryptionKeyStoreService.DevOpsVaultNotConfiguredException"/>
    /// and the user gets the "KEYS LOST" dialog on every packaging run.
    /// </summary>
    public void WireFeatureGate(Services.IFeatureGate featureGate) => _featureGate = featureGate;

    public void WireJobTracker(Services.BackgroundJobTracker tracker) => _jobTracker = tracker;

    /// <summary>
    /// Handles freshly acquired tokens from the auth service. Updates the
    /// per-tenant cache and refreshes the matching connection's expiry so
    /// the countdown timer always reflects the real token lifetime.
    /// </summary>
    private void OnTokenAcquired(MsalTokenResult token)
    {
        if (string.IsNullOrEmpty(token.TenantId)) return;

        _tenantTokens[token.TenantId] = token;
        _silentGate.Record(token.TenantId, Services.SilentAttemptOutcome.Success, SystemClock.UtcNow);

        // Update matching connection status on UI thread. BeginInvoke, not
        // Invoke: silent acquisitions now complete on worker threads, and a
        // blocking marshal from there would tie the worker to dispatcher load.
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var conn = IntuneConnections.FirstOrDefault(c =>
                string.Equals(c.TenantId, token.TenantId, StringComparison.OrdinalIgnoreCase));
            if (conn is null || !conn.IsEnabled) return;

            // PROMOTE, don't just annotate: a fresh token makes this tenant
            // deployable, and the run pipeline filters targets on
            // State == Connected. With the 15s poll gone, this event is the
            // path that lights a card up after a sign-in performed anywhere
            // in the app (Account flyout, Inventory, another card).
            ConnectionChecker.ApplyTokenStatus(conn, token, graphReachable: true);
        });
    }

    /// <summary>
    /// CommunityToolkit callback: fires when the Target radio button changes.
    /// Proactively checks connections for the newly selected target.
    /// </summary>
    partial void OnTargetChanged(RunTarget value)
    {
        _ = SafeRefreshAsync();
    }

    // -----------------------------------------------------------------------
    // Connection status
    // -----------------------------------------------------------------------

    /// <summary>One ConnectionStatus per configured Intune tenant.</summary>
    public ObservableCollection<ConnectionStatus> IntuneConnections { get; } = new();

    /// <summary>One ConnectionStatus per configured SCCM site.</summary>
    public ObservableCollection<ConnectionStatus> SccmConnections { get; } = new();

    public bool IsIntuneConnectionVisible => Target is RunTarget.Intune or RunTarget.Both;
    public bool IsSccmConnectionVisible   => Target is RunTarget.SCCM or RunTarget.Both;

    // -----------------------------------------------------------------------
    // Package progress
    // -----------------------------------------------------------------------

    public ObservableCollection<PackageProgress> PackageProgressItems { get; } = new();

    // -----------------------------------------------------------------------
    // Target selection
    // -----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTargetIntune))]
    [NotifyPropertyChangedFor(nameof(IsTargetSCCM))]
    [NotifyPropertyChangedFor(nameof(IsTargetBoth))]
    [NotifyPropertyChangedFor(nameof(IsIntuneConnectionVisible))]
    [NotifyPropertyChangedFor(nameof(IsSccmConnectionVisible))]
    [NotifyPropertyChangedFor(nameof(LogFilePath))]
    private RunTarget _target = RunTarget.Intune;

    public bool IsTargetIntune
    {
        get => Target == RunTarget.Intune;
        set { if (value) Target = RunTarget.Intune; }
    }
    public bool IsTargetSCCM
    {
        get => Target == RunTarget.SCCM;
        set { if (value) Target = RunTarget.SCCM; }
    }
    public bool IsTargetBoth
    {
        get => Target == RunTarget.Both;
        set { if (value) Target = RunTarget.Both; }
    }

    // -----------------------------------------------------------------------
    // Mode selection: ValidateOnly | PackageOnly | FullRun
    // -----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModeValidateOnly))]
    [NotifyPropertyChangedFor(nameof(IsModePackageOnly))]
    [NotifyPropertyChangedFor(nameof(IsModeFullRun))]
    private string _mode = "FullRun";

    public bool IsModeValidateOnly
    {
        get => Mode == "ValidateOnly";
        set { if (value) Mode = "ValidateOnly"; }
    }
    public bool IsModePackageOnly
    {
        get => Mode == "PackageOnly";
        set { if (value) Mode = "PackageOnly"; }
    }
    public bool IsModeFullRun
    {
        get => Mode == "FullRun";
        set { if (value) Mode = "FullRun"; }
    }

    // -----------------------------------------------------------------------
    // Run state
    // -----------------------------------------------------------------------

    /// <summary>Forwards inherited <see cref="StatusViewModelBase.IsBusy"/> under the legacy domain-specific name.</summary>
    public bool IsRunning => IsBusy;

    public bool CanStart  => !IsRunning;
    public bool CanCancel => IsRunning;

    /// <summary>
    /// Bridge inherited IsBusy changes back onto the legacy IsRunning name and
    /// re-raise CanStart / CanCancel + StartCommand / CancelCommand
    /// CanExecute, replacing the [NotifyPropertyChangedFor] / [NotifyCanExecuteChangedFor]
    /// attributes that were attached to the old `_isRunning` field.
    /// </summary>
    protected override void OnIsBusyChangedInternal(bool oldValue, bool newValue)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanCancel));
        StartCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty] private string _resultSummary = string.Empty;
    [ObservableProperty] private bool   _lastRunSucceeded;

    /// <summary>
    /// Actual log file path. Updated dynamically from the PS module output
    /// ("Log initialized: {path}"). Falls back to a computed default before
    /// the run starts, using the same Tag logic as the PS module.
    /// </summary>
    private string _actualLogFilePath = string.Empty;

    public string LogFilePath
    {
        get
        {
            if (!string.IsNullOrEmpty(_actualLogFilePath))
                return _actualLogFilePath;

            var tag = Target switch
            {
                RunTarget.SCCM => _appInfoVm.FullConfig.Script.SCCMPackager.Tag,
                _              => _appInfoVm.FullConfig.Script.IntunePackager.Tag
            };

            // If the tag hasn't been persisted yet, compute the same default
            // that ConfigFileService would write on save.
            if (string.IsNullOrWhiteSpace(tag))
            {
                var appName = _appInfoVm.FullConfig.App.Name;
                var appVer  = _appInfoVm.FullConfig.App.Version;
                var baseTag = string.IsNullOrWhiteSpace(appName) ? appVer : $"{appName}_{appVer}";
                if (string.IsNullOrWhiteSpace(baseTag))
                    return string.Empty;
                var suffix = Target switch
                {
                    RunTarget.SCCM => "SCCMPackager",
                    _              => "IntunePackager"
                };
                tag = $"{baseTag}_{suffix}";
            }

            // Mirrors the module's default log path (Invoke-WrappIntune /
            // Invoke-WrappSccm) -- used only until the module reports the
            // actual path via its "Log initialized:" line.
            var logDir = System.IO.Path.Combine(Services.PlatformConfig.WrappRoot, "Logs");
            return System.IO.Path.Combine(logDir, $"{tag}.log");
        }
    }

    // -----------------------------------------------------------------------
    // Log output
    // -----------------------------------------------------------------------

    public ObservableCollection<LogEntry> LogLines { get; } = new();

    // -----------------------------------------------------------------------
    // Commands
    // -----------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var configPath = _appInfoVm.ConfigPath;
        if (string.IsNullOrWhiteSpace(configPath))
        {
            AddLog("[ERROR] No config file loaded. Load a Config.json first.", "Error");
            return;
        }

        // Flip IsBusy BEFORE any await so CanStart immediately returns false
        // and a double-click on the Run button can't launch two concurrent runs
        // while the pre-flight dialogs (duplicate guard, collision check,
        // deployment plan) are still open. Every early return below must reset it.
        IsBusy = true;

        // Package selection and step events are keyed on AppName. Two ENABLED
        // packages sharing a name would both run even though the operator only
        // intended one -- including a disabled twin of an enabled package.
        // Refuse rather than publish something the operator excluded.
        if (FindDuplicateEnabledName() is { } duplicateName)
        {
            AddLog($"[ERROR] Two enabled packages are named \"{duplicateName}\". Package selection matches on name, " +
                   "so both would run. Rename one (or disable it) before starting.", "Error");
            await FluentDialog.ShowWarningAsync(
                "Duplicate package name",
                $"Two enabled packages are named \"{duplicateName}\".\n\n" +
                "Packages are selected for a run by name, so both would be processed - including one you " +
                "may have meant to exclude.\n\nRename one of them, or disable it, then start the run again.");
            IsBusy = false;
            return;
        }

        // Ask Intune what already exists BEFORE wrapping anything, so a
        // collision is a decision the operator makes rather than a failure they
        // watch happen ten minutes in.
        if (!await ResolveIntuneCollisionsAsync())
        {
            IsBusy = false;
            return;
        }
        // Snapshot the plan once so both the confirmation dialog and the
        // background-job card render the same tree, independent of any
        // subsequent VM edits while the run is in-flight.
        var runContext = BuildPackagingRunContext();
        try
        {
            var planPanel = DeploymentPlanRenderer.Render(runContext);
            var proceed = await FluentDialog.ShowSelectAsync(
                "Deployment Plan", planPanel, "Proceed", "Cancel");
            if (!proceed)
            {
                IsBusy = false;
                return;
            }
        }
        catch
        {
            IsBusy = false;
            throw;
        }

        lock (_logLock) { LogLines.Clear(); }
        PackageProgressItems.Clear();
        ResultSummary      = string.Empty;
        _actualLogFilePath = string.Empty;
        OnPropertyChanged(nameof(LogFilePath));
        _cts               = new CancellationTokenSource();

        var runTitle = BuildRunJobTitle(runContext);
        _currentRunJob = _jobTracker?.BeginJob(runTitle, runContext.BundleRootDir, runContext) ?? default;
        var job = _currentRunJob;

        AddLog($"[START] Target={Target} Mode={Mode}", "Info");

        // Flush in-memory config to disk (bundle Config.json) with sentinels
        // in place of plaintext ClientSecrets. The bundle repo is git-committed,
        // so secrets must never land here.
        try
        {
            await ConfigFileService.SaveAsync(_appInfoVm.FullConfig, configPath);
        }
        catch (Exception ex)
        {
            AddLog($"[ERROR] Failed to save config before run: {ex.Message}", "Error");
        }

        PopulatePackageProgress();

        bool success = true;
        // Hold the pool for this run so an account/identity switch can't
        // recycle it or clobber its token mid-flight (see PowerShellService).
        var packagingSession = _ps.BeginPackagingSession();
        try
        {
            // Wire progress callback to feed both log and phase detector
            var progress = UiProgress.ForStatus(line =>
            {
                AddLogFromRaw(line);
                // Progress lines are handled by AddLogFromRaw directly
                if (!line.StartsWith("[PROG:"))
                    _phaseDetector.ProcessLine(line);
            });

            if (Mode is "ValidateOnly" or "FullRun")
            {
                AddLog("[PHASE] Validating config...", "Info");
                var issues = await _ps.ValidateConfigAsync(configPath, "IntunePackager");
                var errors = issues.Where(i => i.IsError).ToList();
                if (errors.Any())
                {
                    foreach (var e in errors)
                        AddLog($"[VALIDATE ERROR] {e.FieldPath}: {e.Message}", "Error");
                    if (Mode == "ValidateOnly")
                    {
                        ResultSummary = $"Validation failed - {errors.Count} error(s)";
                        LastRunSucceeded = false;
                        return;
                    }
                    success = false;
                }
                else
                {
                    AddLog("[PHASE] Validation passed.", "Info");
                }
            }

            WarnPastDates();

            if (Mode is "PackageOnly" or "FullRun")
            {
                if (Target is RunTarget.Intune or RunTarget.Both)
                {
                    await AcquireAndInjectTokenAsync();

                    var enabledTenants = IntuneConnections
                        .Where(c => c.IsEnabled && c.State == ConnectionState.Connected)
                        .ToList();

                    if (enabledTenants.Count == 0)
                    {
                        AddLog("[WARN] No enabled Intune tenants with valid tokens. Skipping Intune.", "Warning");
                    }
                    else
                    {
                        // The tenant loop lives in the MODULE (Invoke-WrappPackaging
                        // runs one pass per config TenantId), so UI and CLI share
                        // the same orchestration path. The UI contributes its
                        // enabled/connected subset as the -TenantIds filter and one
                        // MSAL token per enabled tenant (no cross-tenant reuse).
                        // Per-pass progress scoping comes from the module's
                        // TenantPass boundary events (see ApplyStepEvent).
                        var msalApp = await _authService.GetPublicClientAppAsync();
                        var tokenEntries = new List<(MsalTokenResult Token, Microsoft.Identity.Client.IAccount? Account)>();
                        foreach (var conn in enabledTenants)
                        {
                            if (_tenantTokens.TryGetValue(conn.TenantId, out var token))
                                tokenEntries.Add((token, await _authService.GetAccountForTenantAsync(conn.TenantId)));
                        }

                        // Intune module emits typed step events -> they own
                        // progress; suppress the equivalent regex phases. The
                        // pass set starts empty and is populated by the first
                        // TenantPass event from the module.
                        _currentPassPackages.Clear();
                        _stepEventsOwnProgress = true;

                        var tenantIds = enabledTenants.Select(c => c.TenantId).ToList();

                        // Disabled packages are excluded by the MODULE from the
                        // saved config's IsEnabled flag (the config was flushed to
                        // disk above) -- flag-based, not name-based, and identical
                        // for CLI runs. No -PackageNames filter needed here.
                        AddLog($"[PHASE] Running Invoke-WrappPackaging for {tenantIds.Count} tenant(s)...", "Info");
                        var ok = await _ps.PackageAllTenantsAsync(
                            configPath, tenantIds, progress, _cts.Token,
                            tokenEntries, msalApp,
                            onOutput: obj =>
                            {
                                // Events carry their own TenantId (module-emitted);
                                // the empty fallback only applies to legacy objects.
                                CaptureEncryptionKeys(obj, string.Empty);
                                HandleStepEvent(obj, string.Empty);
                            },
                            // The pre-flight dialog already ran the SAME collision
                            // check when covered=true; don't pay for it twice.
                            skipCollisionCheck: _collisionPreflightCovered);
                        if (!ok) success = false;
                    }
                }

                if (Target is RunTarget.SCCM or RunTarget.Both)
                {
                    var enabledSccmSites = SccmConnections
                        .Where(c => c.IsEnabled && c.State == ConnectionState.Connected)
                        .ToList();

                    if (enabledSccmSites.Count == 0)
                    {
                        AddLog("[SKIP] No connected SCCM sites enabled for this run.", "Verbose");
                    }
                    else
                    {
                        var sccmPackages = _appInfoVm.FullConfig.Script.SCCMPackager.Packages;

                        foreach (var conn in enabledSccmSites)
                        {
                            _cts.Token.ThrowIfCancellationRequested();

                            var sccmSiteCode = conn.TenantId; // Site code stored in TenantId field
                            var sccmTargeted = sccmPackages
                                .Where(p => p.IsEnabled
                                    && !string.IsNullOrEmpty(p.SiteCode) &&
                                    string.Equals(p.SiteCode, sccmSiteCode, StringComparison.OrdinalIgnoreCase))
                                .Select(p => p.AppName)
                                .ToArray();

                            if (sccmTargeted.Length == 0)
                            {
                                AddLog($"[SKIP] {conn.TargetName}: no packages target this site.", "Verbose");
                                continue;
                            }

                            // Scope global progress steps + finalization to this
                            // site's packages (see _currentPassPackages).
                            _currentPassPackages.Clear();
                            foreach (var n in sccmTargeted) _currentPassPackages.Add(n);

                            // SCCM module still reports progress via log text;
                            // let the regex phases drive it.
                            _stepEventsOwnProgress = false;

                            AddLog($"[PHASE] Running Invoke-WrappSccm for {conn.TargetName} ({sccmTargeted.Length} package(s))...", "Info");
                            var ok = await _ps.PackageAsync(
                                configPath, "SCCM", string.Empty, progress, _cts.Token,
                                packageNames: sccmTargeted, siteCode: sccmSiteCode);
                            if (!ok) success = false;
                        }
                    }
                }
            }

            // A per-package collision (or any failure captured via PackageProgress)
            // does not surface through the PowerShell exit code -- the module exits
            // cleanly after recording the failure through phase events. Reconcile
            // the outer success flag with the actual per-package outcomes so the
            // Background Jobs badge (Done vs Error) reflects reality.
            if (PackageProgressItems.Any(p => p.Outcome == PackageOutcome.Failed))
                success = false;

            LastRunSucceeded = success;
            ResultSummary    = success ? "Run completed successfully." : "Run completed with errors.";
        }
        catch (OperationCanceledException)
        {
            AddLog("[CANCELLED] Run was cancelled.", "Warning");
            ResultSummary    = "Cancelled.";
            LastRunSucceeded = false;

            foreach (var pkg in PackageProgressItems.Where(p => p.Outcome == PackageOutcome.Running))
                pkg.Outcome = PackageOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            AddLog($"[FATAL] {ex.Message}", "Error");
            ResultSummary    = "Run failed with an unhandled error.";
            LastRunSucceeded = false;
        }
        finally
        {
            packagingSession.Dispose();
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            if (job.IsActive)
            {
                var summary = BuildRunSummary(runContext);
                if (LastRunSucceeded)
                    job.Complete(summary.OneLine, summary);
                else
                    job.Fail(ResultSummary);
            }
            _currentRunJob = default;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        AddLog("[CANCEL] Cancelling run and stopping sub-processes...", "Warning");
        _cts?.Cancel();
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        var path = LogFilePath;
        if (string.IsNullOrEmpty(path)) return;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", dir);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to open log folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Always-available app log directory (independent of whether a run is
    /// active). Powers the header-top-right log button so the user can jump
    /// to the log folder at any time, matching the Logs / Settings pattern.
    /// </summary>
    public string AppLogFolderPath => AppLogger.LogDirectory;

    [RelayCommand]
    private void OpenAppLogFolder()
    {
        var dir = AppLogFolderPath;
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to open log folder: {ex.Message}");
        }
    }
    /// <summary>
    /// Builds a frozen snapshot of the current deployment plan -- the same data
    /// that drives the pre-run confirmation dialog. Attached to the packaging
    /// job so the Background Jobs pop-up can render the same tree at any point
    /// during the run or after completion.
    /// </summary>
    private PackagingRunContext BuildPackagingRunContext()
    {
        var config = _appInfoVm.FullConfig;
        var ctx = new PackagingRunContext
        {
            Mode          = Mode,
            Target        = Target.ToString(),
            BundleRootDir = _appInfoVm.BundleRootDir,
        };

        if (Target is RunTarget.Intune or RunTarget.Both)
        {
            var enabledTenants = IntuneConnections.Where(c => c.IsEnabled).ToList();
            foreach (var pkg in config.Script.IntunePackager.Packages)
            {
                var targetTenant = string.IsNullOrEmpty(pkg.TenantId)
                    ? null
                    : enabledTenants.FirstOrDefault(c =>
                        string.Equals(c.TenantId, pkg.TenantId, StringComparison.OrdinalIgnoreCase));

                ctx.IntunePackages.Add(new PackagingRunIntunePackage
                {
                    AppName           = pkg.AppName ?? "",
                    UpdateMode        = pkg.UpdateMode,
                    TenantId          = pkg.TenantId ?? "",
                    TenantDisplayName = targetTenant?.TargetName,
                    Assignments       = pkg.Assignments.Select(a => new PackagingRunAssignment
                    {
                        Intent       = a.Intent ?? "",
                        Type         = a.Type ?? "",
                        GroupMode    = a.GroupMode ?? "",
                        GroupID      = a.GroupID ?? "",
                        Label        = a.Label ?? "",
                        DisplayName  = a.DisplayName ?? "",
                        Notification = a.Notification ?? "",
                    }).ToList(),
                });
            }
        }

        if (Target is RunTarget.SCCM or RunTarget.Both)
        {
            var enabledSccm = SccmConnections
                .Where(c => c.IsEnabled && c.State == ConnectionState.Connected)
                .ToList();
            foreach (var pkg in config.Script.SCCMPackager.Packages)
            {
                var targetSiteConn = string.IsNullOrEmpty(pkg.SiteCode)
                    ? null
                    : enabledSccm.FirstOrDefault(c =>
                        string.Equals(c.TenantId, pkg.SiteCode, StringComparison.OrdinalIgnoreCase));

                ctx.SccmPackages.Add(new PackagingRunSccmPackage
                {
                    AppName         = pkg.AppName ?? "",
                    SiteCode        = pkg.SiteCode ?? "",
                    SiteDisplayName = targetSiteConn?.TargetName,
                    Deployments     = pkg.Deployments.Select(d => new PackagingRunDeployment
                    {
                        DeployPurpose = d.DeployPurpose ?? "",
                        DeployAction  = d.DeployAction ?? "",
                        Collection    = d.Collection ?? "",
                        Label         = d.Label ?? "",
                        DisplayName   = d.DisplayName ?? "",
                    }).ToList(),
                });
            }
        }

        return ctx;
    }

    /// <summary>
    /// First AppName shared by two or more ENABLED packages in the targets this
    /// run touches, or null when every enabled name is unique. Disabled
    /// packages are ignored - they are filtered out before the module sees
    /// them, so they cannot be caught by a name match.
    /// </summary>
    private string? FindDuplicateEnabledName()
    {
        var config = _appInfoVm.FullConfig;
        var names = new List<string>();

        if (Target is RunTarget.Intune or RunTarget.Both)
            names.AddRange(config.Script.IntunePackager.Packages
                .Where(p => p.IsEnabled).Select(p => p.AppName));

        if (Target is RunTarget.SCCM or RunTarget.Both)
            names.AddRange(config.Script.SCCMPackager.Packages
                .Where(p => p.IsEnabled).Select(p => p.AppName));

        return names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1)?.Key;
    }

    /// <summary>
    /// True when the LAST pre-flight collision check successfully covered every
    /// targeted tenant. Only then may the run tell the module to skip its own
    /// (identical) check - otherwise the run would double every Graph query
    /// for information the operator has already acted on.
    /// </summary>
    private bool _collisionPreflightCovered;

    /// <summary>
    /// Pre-flight collision check. Returns true to continue with the run,
    /// false when the operator cancels.
    /// <para>
    /// FAILS OPEN: no token, no enabled Intune packages, or a query error just
    /// continues - and in those cases <see cref="_collisionPreflightCovered"/>
    /// stays false, so the module keeps its own authoritative check. A
    /// pre-flight problem must never block work.
    /// </para>
    /// </summary>
    private async Task<bool> ResolveIntuneCollisionsAsync()
    {
        _collisionPreflightCovered = false;
        if (Target is not (RunTarget.Intune or RunTarget.Both)) return true;

        var candidates = _appInfoVm.FullConfig.Script.IntunePackager.Packages
            .Where(p => p.IsEnabled
                && !string.IsNullOrWhiteSpace(p.AppName)
                && !string.IsNullOrWhiteSpace(p.TenantId))
            .ToList();
        if (candidates.Count == 0)
        {
            _collisionPreflightCovered = true;   // nothing to check = fully covered
            return true;
        }

        // One query per tenant, using that tenant's token.
        var allCovered = true;
        var found = new List<(IntunePackageEntry Package, Models.IntuneCollision Collision)>();
        foreach (var group in candidates.GroupBy(p => p.TenantId, StringComparer.OrdinalIgnoreCase))
        {
            if (!_tenantTokens.TryGetValue(group.Key, out var token))
            {
                allCovered = false;   // not signed in: the module must check this tenant
                continue;
            }
            try
            {
                var collisions = await _ps.TestIntuneCollisionsAsync(
                    group.Select(p => (p.AppName, p.UpdateMode.ToString())).ToList(), token);

                foreach (var c in collisions)
                {
                    var pkg = group.FirstOrDefault(p =>
                        string.Equals(p.AppName, c.PackageName, StringComparison.OrdinalIgnoreCase));
                    if (pkg is not null) found.Add((pkg, c));
                }
            }
            catch (Exception ex)
            {
                allCovered = false;
                AddLog($"[WARN] Collision pre-check failed for tenant {group.Key}: {ex.Message}. " +
                       "Continuing; the run performs its own check.", "Warning");
            }
        }
        _collisionPreflightCovered = allCovered;

        if (found.Count == 0) return true;

        var list = string.Join("\n", found.Select(f =>
            $"  • {f.Package.AppName} - already in Intune" +
            (string.IsNullOrWhiteSpace(f.Collision.Version) ? "" : $" (v{f.Collision.Version})") +
            (string.IsNullOrWhiteSpace(f.Collision.Publisher) ? "" : $" by {f.Collision.Publisher}")));

        // Three-outcome on purpose: BOTH named actions mutate packages, so ESC /
        // close must mean "cancel the run", never silently pick one of them.
        // "Proceed anyway" is deliberately NOT offered: the module takes only
        // the non-colliding packages and fails the rest, so it would be a lie.
        // Switching to Update mode is the action that actually does what an
        // operator usually means by a name collision.
        var choice = await FluentDialog.ShowChoiceAsync(
            "Apps already exist in Intune",
            BuildCollisionPanel(list, found.Count),
            "Switch to Update", "Skip these packages", "Cancel run");

        switch (choice)
        {
            case Wpf.Ui.Controls.ContentDialogResult.Primary:
                foreach (var (pkg, collision) in found)
                {
                    pkg.UpdateMode    = UpdateMode.Update;
                    pkg.ExistingAppID = collision.Id;
                    AddLog($"[INFO] {pkg.AppName}: switched to Update mode targeting existing app {collision.Id}.", "Info");
                }
                await FluentDialog.ShowInfoAsync(
                    "Switched to Update mode",
                    $"{found.Count} package(s) now target the existing Intune app instead of creating a new one. " +
                    "Save the bundle to keep this change.");
                return true;

            case Wpf.Ui.Controls.ContentDialogResult.Secondary:
                foreach (var (pkg, _) in found)
                {
                    pkg.IsEnabled = false;
                    AddLog($"[INFO] {pkg.AppName}: disabled for this run (already exists in Intune).", "Info");
                }
                return true;

            default:
                AddLog("[INFO] Run cancelled at the collision pre-flight.", "Info");
                return false;
        }
    }

    private static FrameworkElement BuildCollisionPanel(string list, int count)
    {
        var panel = new StackPanel { MaxWidth = 520 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{count} package(s) have a display name that already exists in the target tenant:",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = list,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Margin = new Thickness(0, 0, 0, 10),
        });
        // Explainer sourced from HelpContent so the dialog and the Run help
        // never drift apart; the terse fallback covers a missing resource.
        if (System.Windows.Application.Current?.TryFindResource("Help.Run.CollisionCheck") is string help
            && System.Windows.Application.Current.MainWindow is FrameworkElement src)
        {
            panel.Children.Add(HelpMarkdownRenderer.Render(help, src));
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Switch to Update: the packages target the existing apps instead of creating duplicates.\n"
                     + "Skip these packages: they are disabled for this run; everything else proceeds.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
            });
        }
        return panel;
    }

    private FrameworkElement BuildDeploymentPlanPanel()
        => DeploymentPlanRenderer.Render(BuildPackagingRunContext());

    /// <summary>
    /// Builds a human-friendly job title like "Packaging: Intune (FullRun) - AppName"
    /// so the Background Jobs pop-up card reads meaningfully at a glance.
    /// </summary>
    private string BuildRunJobTitle(PackagingRunContext ctx)
    {
        var totalPkgs = ctx.IntunePackages.Count + ctx.SccmPackages.Count;
        var firstName = ctx.IntunePackages.FirstOrDefault()?.AppName
                     ?? ctx.SccmPackages.FirstOrDefault()?.AppName
                     ?? "";
        return totalPkgs switch
        {
            0 => $"Packaging: {ctx.Target} ({ctx.Mode})",
            1 => $"Packaging: {ctx.Target} ({ctx.Mode}) - {firstName}",
            _ => $"Packaging: {ctx.Target} ({ctx.Mode}) - {totalPkgs} packages"
        };
    }

    /// <summary>
    /// Tallies final per-package outcomes into a small summary object that the
    /// Background Jobs pop-up renders beneath the card title for completed runs.
    /// </summary>
    private PackagingRunSummary BuildRunSummary(PackagingRunContext ctx)
    {
        var items = PackageProgressItems.ToList();

        PackageProgress? Match(string appName) => items.FirstOrDefault(p =>
            string.Equals(p.PackageName, appName, StringComparison.OrdinalIgnoreCase));

        // Stamp each package on the context with its final outcome + failure
        // reason so the Background Jobs expanded card can colour-highlight per
        // package. The context was frozen at run kickoff; outcomes are
        // write-once at run completion.
        foreach (var pkg in ctx.IntunePackages)
        {
            var m = Match(pkg.AppName);
            if (m is not null)
            {
                pkg.Outcome       = m.Outcome;
                pkg.FailureReason = m.FailureReason;
            }
        }
        foreach (var pkg in ctx.SccmPackages)
        {
            var m = Match(pkg.AppName);
            if (m is not null)
            {
                pkg.Outcome       = m.Outcome;
                pkg.FailureReason = m.FailureReason;
            }
        }

        // Only count assignments / deployments whose owning package actually
        // reached a Succeeded (or PartialSuccess) outcome. A collision or any
        // other failure that short-circuits the run never gets to the assignment
        // phase, so we shouldn't claim those assignments were applied.
        bool PackageSucceeded(string appName)
        {
            var match = Match(appName);
            return match?.Outcome == PackageOutcome.Succeeded
                || match?.Outcome == PackageOutcome.PartialSuccess;
        }

        var intuneApplied = ctx.IntunePackages
            .Where(p => PackageSucceeded(p.AppName))
            .Sum(p => p.Assignments.Count);
        var sccmApplied = ctx.SccmPackages
            .Where(p => PackageSucceeded(p.AppName))
            .Sum(p => p.Deployments.Count);

        return new PackagingRunSummary
        {
            PackagesAttempted  = items.Count,
            PackagesSucceeded  = items.Count(p => p.Outcome == PackageOutcome.Succeeded
                                                 || p.Outcome == PackageOutcome.PartialSuccess),
            PackagesFailed     = items.Count(p => p.Outcome == PackageOutcome.Failed),
            TenantsTargeted    = ctx.IntunePackages.Select(p => p.TenantId)
                                     .Concat(ctx.SccmPackages.Select(p => p.SiteCode))
                                     .Where(s => !string.IsNullOrEmpty(s))
                                     .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            AssignmentsApplied = intuneApplied + sccmApplied,
        };
    }

    // -----------------------------------------------------------------------
    // Package progress population
    // -----------------------------------------------------------------------

    private void PopulatePackageProgress()
    {
        var config = _appInfoVm.FullConfig;

        if (Target is RunTarget.Intune or RunTarget.Both)
        {
            var enabledTenants = IntuneConnections
                .Where(c => c.IsEnabled)
                .ToList();
            var enabledTenantIds = enabledTenants
                .Select(c => c.TenantId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var intuneLabel = enabledTenants.Count == 1
                ? $"Intune - {enabledTenants[0].TargetName}"
                : "Intune";

            foreach (var pkg in config.Script.IntunePackager.Packages)
            {
                // Empty TenantId = no selection = package skipped
                bool hasEnabledTarget = pkg.IsEnabled
                    && !string.IsNullOrEmpty(pkg.TenantId)
                    && enabledTenantIds.Contains(pkg.TenantId);

                var pkgLabel = intuneLabel;
                if (!string.IsNullOrEmpty(pkg.TenantId))
                {
                    var match = enabledTenants.FirstOrDefault(
                        c => string.Equals(c.TenantId, pkg.TenantId, StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                        pkgLabel = $"Intune - {match.TargetName}";
                }

                var pp = new PackageProgress
                {
                    PackageName = pkg.AppName,
                    Target = pkgLabel,
                    TotalSteps = 5,
                    Outcome = hasEnabledTarget ? PackageOutcome.Pending : PackageOutcome.Skipped,
                    CurrentStepName = hasEnabledTarget ? string.Empty
                        : !pkg.IsEnabled ? "Disabled"
                        : string.IsNullOrEmpty(pkg.TenantId) ? "No tenant selected" : "Tenant not enabled"
                };
                pp.Steps.Add(new StepStatus { Name = "Collision Check" });
                pp.Steps.Add(new StepStatus { Name = "Wrapping" });
                pp.Steps.Add(new StepStatus { Name = "App Creation" });
                pp.Steps.Add(new StepStatus { Name = "Dependencies" });
                pp.Steps.Add(new StepStatus { Name = "Assignment" });
                PackageProgressItems.Add(pp);
            }
        }

        if (Target is RunTarget.SCCM or RunTarget.Both)
        {
            var enabledSccm = SccmConnections
                .Where(c => c.IsEnabled && c.State == ConnectionState.Connected)
                .ToList();

            foreach (var pkg in config.Script.SCCMPackager.Packages)
            {
                bool hasEnabledSite = !string.IsNullOrEmpty(pkg.SiteCode) &&
                    enabledSccm.Any(c => string.Equals(c.TenantId, pkg.SiteCode, StringComparison.OrdinalIgnoreCase));
                // Disabled beats site state, mirroring the Intune branch - the
                // module skips disabled packages regardless of targeting.
                bool willRun = pkg.IsEnabled && hasEnabledSite;

                var targetLabel = "SCCM";

                var skipReason = !pkg.IsEnabled ? "Disabled"
                    : string.IsNullOrEmpty(pkg.SiteCode) ? "No site selected"
                    : enabledSccm.Count == 0 ? "No connected sites"
                    : "Site not connected";

                var pp = new PackageProgress
                {
                    PackageName = pkg.AppName,
                    Target = targetLabel,
                    TotalSteps = 4,
                    Outcome = willRun ? PackageOutcome.Pending : PackageOutcome.Skipped,
                    CurrentStepName = willRun ? string.Empty : skipReason
                };
                pp.Steps.Add(new StepStatus { Name = "Collision Check" });
                pp.Steps.Add(new StepStatus { Name = "Detection Script" });
                pp.Steps.Add(new StepStatus { Name = "App Creation" });
                pp.Steps.Add(new StepStatus { Name = "Content Distribution" });
                PackageProgressItems.Add(pp);
            }
        }
    }


}
