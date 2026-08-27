using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Wrapp.Models;
using Wrapp.Services;
using Wrapp.Services.Gates;
using Wrapp.ViewModels;
using Wrapp.Views;
using Wpf.Ui.Appearance;

namespace Wrapp;

public partial class App : Application
{
    /// <summary>
    /// Per-user named mutex that enforces a single-instance run of the app.
    /// Prevents two concurrent instances from racing on settings.json / MSAL
    /// cache / runtime config directory. Held for the lifetime of the process.
    /// </summary>
    private Mutex? _singleInstanceMutex;

    /// <summary>
    /// The service + view-model graph, built once during startup. Backs
    /// <see cref="GetService{T}"/> and <see cref="InventoryService"/>.
    /// </summary>
    private static CompositionRoot? _root;

    /// <summary>
    /// Resolves a singleton from the composition root. Valid only after
    /// startup has built the graph; throws otherwise (a caller running before
    /// the root exists is a bug, not a recoverable state).
    /// </summary>
    public static T GetService<T>() where T : class =>
        _root?.Get<T>() ?? throw new InvalidOperationException(
            $"App.GetService<{typeof(T).Name}>() called before the composition root was built.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global ComboBox wheel fix: a closed ComboBox otherwise swallows the
        // wheel and changes its selection mid-page-scroll. Registered as a
        // CLASS handler, not an implicit style — a style-based hookup shadowed
        // Wpf.Ui's themed dropdown look (see the note in App.xaml). The
        // handler bails when the dropdown is open, so open dropdowns still
        // wheel-scroll their items.
        Helpers.ScrollBubbling.RegisterClassHandler(typeof(System.Windows.Controls.ComboBox));

        // An open dropdown is a separate popup window that cannot follow a
        // scrolling page, so freeze scrolling while one is open (the calendar
        // picker's long-standing guard, now applied to every ComboBox).
        Helpers.ScrollBubbling.RegisterDropDownScrollGuard();

        // Click-to-caret for every text input in the app (form fields, grid
        // cell editors, dialogs). Class handler rather than a style: one
        // registration, no per-site markup, and no shadowing of theme styles.
        Helpers.ClickToCaret.Register();

        // Stall attribution breadcrumb: record every click's target so a
        // [STALL] log line can name what the user did right before a freeze.
        // handledEventsToo so controls that handle their clicks still record.
        EventManager.RegisterClassHandler(typeof(Window),
            UIElement.PreviewMouseDownEvent,
            new MouseButtonEventHandler((_, args) =>
            {
                if (args.OriginalSource is not DependencyObject src) return;
                var element = src as FrameworkElement;
                var name = element?.Name;
                var desc = string.IsNullOrEmpty(name)
                    ? src.GetType().Name
                    : $"{src.GetType().Name} \"{name}\"";
                UiStallMonitor.RecordInput(desc);
                if (UiTrace.Enabled)
                    UiTrace.Log($"click: {desc} in {ContainingViewName(src)}");
            }), handledEventsToo: true);

        // M5 (multi-instance): every launch is a full instance -- the old
        // exit-and-foreground gate is gone. Shared state is instance-safe now:
        // per-bundle exclusive lock (M1), cross-process RMW locks on settings /
        // sidecar / template manifest (M2), per-instance log files (M3),
        // per-instance WebView2 user-data folders (M4), plus the pre-existing
        // MSAL cache mutex (S-4) and temp-workspace locks. The mutex remains
        // only as a non-blocking signal so startup logs say whether other
        // instances are running.
        _singleInstanceMutex = new Mutex(initiallyOwned: true,
            name: "Local\\Wrapp.SingleInstance", out var createdNew);
        AppLogger.Info(createdNew
            ? "Application starting"
            : "Application starting (additional instance -- another Wrapp is already running)");

        // Phase B: truthful instance registry (pid lock) + close-request
        // channel. The update flow refuses to apply while siblings are alive
        // and can ASK them to close; a request drives the normal close
        // pipeline (save prompts included) — never a hard exit.
        InstanceCoordinator.RegisterInstance();
        InstanceCoordinator.CloseRequested += () => Dispatcher.BeginInvoke(() =>
        {
            AppLogger.Info("InstanceCoordinator: close requested by another instance");
            if (MainWindow is Views.MainWindow main)
                main.BeginClose(Services.CloseReason.SiblingCloseRequest);
            else
                Shutdown();   // still at the splash — nothing to save
        });

        // Bootstrap built-in templates to %LOCALAPPDATA%\Wrapp\Templates if missing
        TemplateService.EnsureBuiltInTemplates();

        // --- Unhandled exception hooks ---

        // UI-thread exceptions: log, show friendly dialog with Copy/Open-log actions.
        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Exception("DispatcherUnhandledException", args.Exception);
            SafeFireAndForget.Run(
                () => FluentDialog.ShowExceptionAsync("unexpected error", args.Exception),
                "dispatcher-unhandled-dialog");
            args.Handled = true;
        };

        // Non-UI-thread exceptions: process is about to terminate; log only.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception
                     ?? new Exception("Non-Exception object thrown: " + args.ExceptionObject);
            AppLogger.Exception(
                $"AppDomain.UnhandledException (terminating={args.IsTerminating})", ex);
        };

        // Unobserved task exceptions: log and mark observed so finalizer doesn't crash.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogger.Exception("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        // Kick off startup pipeline. SafeFireAndForget surfaces any unhandled
        // exception as a dialog + log entry instead of silent death.
        SafeFireAndForget.Run(StartupCoreAsync, "startup", showDialog: true);
    }

    /// <summary>Nearest containing UserControl (i.e. section view) or Window
    /// type name — trace context for the click breadcrumb.</summary>
    private static string ContainingViewName(DependencyObject node)
    {
        try
        {
            while (node is not null)
            {
                if (node is UserControl uc) return uc.GetType().Name;
                if (node is Window w) return w.GetType().Name;
                node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }
        }
        catch { /* trace context is best-effort */ }
        return "(unknown)";
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services.Policy.PolicyChangeMonitor.Stop();

        // M1: drop the cross-instance bundle lock (deletes its file). A hard
        // kill skips this harmlessly -- the orphaned file is reclaimable.
        BundleLockService.Release();

        // Phase B: drop the pid registration (deletes its file too). Then, if
        // this exit is carrying a staged update into Update.exe's apply
        // window, write the update marker LAST -- it must outlive this
        // process (the swap of current\ happens after we exit); the
        // relaunched build or staleness clears it (see InstanceCoordinator).
        InstanceCoordinator.ReleaseInstance();
        var pendingApply = UpdateService.ScheduledApplyVersion;
        if (pendingApply is not null)
            InstanceCoordinator.BeginUpdateApply(pendingApply);

        // Drain the log queue so pending entries land on disk before exit.
        AppLogger.FlushAndShutdown();

        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch { /* not owned; can happen on the "already running" exit path */ }
        finally
        {
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }
        base.OnExit(e);
    }

    /// <summary>
    /// Fired after the theme changes. Payload is the Monaco theme name ("vs-dark" or "vs").
    /// MonacoService instances subscribe to this to update their editor theme.
    /// </summary>
    /// <summary>Shared inventory service for Browse buttons in package views.
    /// Resolves from the composition root; null before startup finishes.</summary>
    public static AppInventoryService? InventoryService =>
        _root is { } r && r.TryGet<AppInventoryService>(out var svc) ? svc : null;

    public static event Action<string>? ThemeChanged;

    /// <summary>
    /// Current Monaco theme name. Used by MonacoService to initialize new editors
    /// with the correct theme before any ThemeChanged event fires.
    /// </summary>
    public static string CurrentMonacoTheme { get; private set; } = "vs-dark";

    /// <summary>
    /// Applies a theme by name: "Dark", "Light", or a custom imported theme.
    /// The engine lives in <see cref="ThemeService"/> (accent read from the
    /// dictionary, custom JSON overlays, unknown names fall back to Dark).
    /// </summary>
    public static void ApplyTheme(string? themeName) => ThemeService.Apply(themeName);

    /// <summary>Called by <see cref="ThemeService"/> after every apply so
    /// Monaco editors (and any other subscriber) follow the theme.</summary>
    internal static void NotifyThemeApplied(string monacoTheme)
    {
        CurrentMonacoTheme = monacoTheme;
        ThemeChanged?.Invoke(monacoTheme);
    }

    /// <summary>
    /// Phase D splash probe: one fast check-only feed query, 8s budget so a
    /// slow/offline share can never delay launch. Fires the splash's update
    /// offer only for the enforce case (Auto mode + no siblings + user not
    /// yet committed); anything else leaves the session to the indicator.
    /// </summary>
    private static async Task ProbeUpdateAtSplashAsync(SplashWindow splash, AppSettings settings)
    {
        if (!string.Equals(settings.UpdateMode, AppUpdateModes.Auto, StringComparison.OrdinalIgnoreCase)) return;
        if (InstanceCoordinator.GetOtherLiveInstanceIds().Count > 0)
        {
            AppLogger.Info("Update: splash probe skipped -- other Wrapp windows are running (soft path)");
            return;
        }

        var check = Task.Run(() => UpdateService.CheckAsync(settings, download: false));
        if (await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(8))) != check)
        {
            AppLogger.Info("Update: splash probe timed out; continuing launch (indicator will catch it)");
            return;
        }

        var result = await check;
        if (result.Status != UpdateService.CheckStatus.UpdateAvailable || result.Version is null) return;

        await splash.Dispatcher.InvokeAsync(() =>
        {
            if (splash.TryEnterUpdateOffer(result.Version!, settings))
                AppLogger.Info($"Update: v{result.Version} pending at splash -- offering update-or-close");
            else
                AppLogger.Info("Update: splash probe arrived after the user committed -- soft path");
        });
    }

    private async Task StartupCoreAsync()
    {
        // Exceptions propagate out to SafeFireAndForget which logs + shows a dialog.
        // No local try/catch is needed here.

        // Perf-plan P2.1/P2.2: freezes self-document, startup phases get numbers.
        UiStallMonitor.Start(Dispatcher);
        var perf = System.Diagnostics.Stopwatch.StartNew();
        long perfSplashMs = 0, perfPickedMs = 0, perfServicesMs = 0;

        // Phase B launch guard: never start an old binary while Update.exe is
        // swapping current\. Wait for the apply to finish (the updated exe is
        // what relaunches normally), give up after 2 minutes.
        if (InstanceCoordinator.IsUpdateInProgress())
        {
            AppLogger.Info("Startup: update in progress -- waiting for it to finish");
            var finished = await InstanceCoordinator.WaitForUpdateToFinishAsync(TimeSpan.FromMinutes(2));
            if (!finished)
            {
                AppLogger.Warn("Startup: update still in progress after 2 minutes -- exiting");
                System.Windows.MessageBox.Show(
                    "Wrapp is being updated. Please try again in a moment.",
                    "Wrapp update in progress",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                Shutdown();
                return;
            }
            AppLogger.Info("Startup: update finished -- continuing launch");
        }

        // Enable single-click editing for all DataGrid text cells
            EventManager.RegisterClassHandler(typeof(DataGridCell),
                DataGridCell.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler(DataGridCell_PreviewMouseLeftButtonDown));

            // Click outside a DataGrid commits edits and clears selection
            EventManager.RegisterClassHandler(typeof(Window),
                UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler(Window_PreviewMouseLeftButtonDown));

            // Dynamically limit ComboBox dropdown height so the popup never extends
            // past the window boundary (WPF mouse capture fails for those clicks).
            EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ComboBox),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) =>
                {
                    if (sender is System.Windows.Controls.ComboBox cb)
                    {
                        cb.DropDownOpened -= ComboBox_DropDownOpened;
                        cb.DropDownOpened += ComboBox_DropDownOpened;
                    }
                }));

            // Clean up stale temp workspaces from previous sessions
            TempWorkspaceService.CleanOld();

            // Load settings first so the correct theme is applied before any window appears
            var settings = SettingsService.Load();

            // Enterprise policy (Software\Policies\Wrapp). ORDER IS LOAD-
            // BEARING: recommended seeds factory values BEFORE the org file
            // can (policy > org file > factory); mandatory runs AFTER the
            // seeder, unconditionally, so the seeder needs no policy
            // awareness — whatever it wrote, the mandate wins. Restart-to-
            // apply: the snapshot is built once per launch.
            var policy = Services.Policy.PolicyService.Current;
            DefaultsLoader.PolicyPathOverride = policy.OrgDefaultsPath;
            Services.Policy.PolicyService.ApplyRecommended(settings);

            // Workstream O: org provisioning. Sensitive-pattern scrubbing is
            // wired every launch; settings seeding is one-shot per profile
            // (see OrgDefaultsSeeder); the org template pack syncs like the
            // built-ins (refreshes unedited copies, preserves edited ones).
            var orgDefaults = DefaultsLoader.Load();
            // Redaction = org file patterns + policy-supplied patterns, merged.
            AppLogger.SetOrgRedactionPatterns(
                orgDefaults.SensitivePatterns
                    .Concat(policy.RedactionPatterns)
                    .Distinct(StringComparer.Ordinal)
                    .ToList());
            UiTrace.Enabled = settings.VerboseUiTrace;
            if (UiTrace.Enabled) AppLogger.Info("UiTrace: verbose UI tracing is ON (Settings toggle)");

            // Workstream P: one call wires BOTH the placeholder resolver and
            // the sensitive-value log redaction — keeping them in one method
            // means they can never drift (a resolver without redaction would
            // leak secret values into app.log).
            PlaceholderService.RefreshFromSettings(settings);
            if (!settings.OrgDefaultsSeeded)
            {
                if (OrgDefaultsSeeder.Apply(settings, orgDefaults))
                    AppLogger.Info("OrgDefaults: seeded org-shipped settings defaults into this profile");
                settings.OrgDefaultsSeeded = true;
                Services.Policy.PolicyService.ApplyMandatory(settings);
                SettingsService.Save(settings);
            }
            else
            {
                Services.Policy.PolicyService.ApplyMandatory(settings);
            }
            TemplateService.EnsureOrgTemplatePack();

            AppLogger.Info($"Applying theme: {settings.Theme}");
            ApplyTheme(settings.Theme);

            // Prevent the default OnLastWindowClose from shutting the app down when
            // the splash closes before the main window has been shown.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Show splash screen (non-modal so it stays visible during init)
            AppLogger.Info("Showing SplashWindow");
            var splash = new SplashWindow();
            splash.Show();
            perfSplashMs = perf.ElapsedMilliseconds;

            // Phase D: race a check-only feed probe against the user's card
            // pick. Enforcement happens ONLY here — Auto mode, no sibling
            // instances, user not yet committed — before any work exists.
            // Every other combination stays soft (action-needed indicator).
            SafeFireAndForget.Run(() => ProbeUpdateAtSplashAsync(splash, settings), "splash-update-probe");

            // Wait for the user to pick New or Open (splash stays visible)
            var splashOk = await splash.ResultTask;
            perfPickedMs = perf.ElapsedMilliseconds;   // user think-time ends here
            if (splash.Vm.UpdateFlowEngaged)
            {
                // The splash became the update screen; the flow owns the
                // process lifetime from here (it ends in Shutdown/relaunch).
                AppLogger.Info("Startup: update flow engaged at splash -- normal launch abandoned");
                return;
            }
            if (!splashOk)
            {
                AppLogger.Info("Splash cancelled - shutting down");
                splash.Close();
                Shutdown();
                return;
            }
            var configPath = splash.Vm.SelectedConfigPath;
            AppLogger.Info($"Splash resolved config path: {configPath}");

            // Resolve module path from appsettings.json
            string modulePath = ResolveModulePath();
            AppLogger.Info($"Module path resolved: {modulePath}");

            // Bind settings for endpoint-token expansion and ensure the shared
            // user-defaults.json sidecar exists (the PS module reads it for
            // CLI/UI parity -- see UserDefaultsService).
            await UserDefaultsService.InitializeAsync(settings);

            // Build the service + view-model graph. Heavy services
            // (PowerShellService/MsalAuthService) are created on a background
            // thread inside BuildAsync so the splash Dispatcher keeps ticking.
            // The construction + wiring order is load-bearing -- see CompositionRoot.
            _root = await CompositionRoot.BuildAsync(settings, modulePath);
            perfServicesMs = perf.ElapsedMilliseconds;

            var window = _root.CreateMainWindow();

            window.Closed += (_, _) =>
            {
                // Phase D handoff: the process must SURVIVE this close so the
                // update splash can run. Only that path branches — a normal
                // close keeps the exact historical behavior below, including
                // the deliberate Environment.Exit(0) (runspace disposal can
                // hang; the update path pushes it to a background thread the
                // final exit will reap the same way).
                if (UpdateFlowController.IsHandoffActive)
                {
                    AppLogger.Info("MainWindow closed - handing off to the update flow");
                    UpdateFlowController.ContinueAfterMainWindowClosed(() =>
                    {
                        TempWorkspaceService.ReleaseLock();
                        TempWorkspaceService.CleanOld();
                        _root.MsalAuth.Dispose();
                        _root.Ps.Dispose();
                    });
                    return;
                }

                AppLogger.Info("MainWindow closed - disposing services");
                TempWorkspaceService.ReleaseLock();
                TempWorkspaceService.CleanOld();
                _root.MsalAuth.Dispose();
                _root.Ps.Dispose();
                Environment.Exit(0);
            };

            // Give WAM a lazy window-handle resolver so the OS auth dialog is
            // always parented to the current foreground window, even for code
            // paths (like cached-account switch) that skip InitializeAsync.
            _root.MsalAuth.SetParentWindowFunc(WindowHelper.GetMainWindowHwnd);
            _root.Get<DevOpsAuthService>().SetParentWindowFunc(WindowHelper.GetMainWindowHwnd);

            AppLogger.Info("Showing MainWindow");
            window.Show();

            // Stop burrito messages and close splash now that main window is visible
            splash.Vm.StopMessages();
            splash.Close();

            // Now that the main window is open, revert to normal shutdown behaviour.
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow   = window;
            AppLogger.Info("MainWindow shown");
            // Bundle-pick is user think-time, reported separately so the
            // work phases carry honest numbers (the field case: a 21-minute
            // coffee at the splash logged as "services 1302267ms").
            AppLogger.Info($"[PERF] startup: splash {perfSplashMs}ms, bundle-pick {perfPickedMs - perfSplashMs}ms (user), services {perfServicesMs - perfPickedMs}ms, window {perf.ElapsedMilliseconds - perfServicesMs}ms, work total {perf.ElapsedMilliseconds - (perfPickedMs - perfSplashMs)}ms");

            // STA-1 (2026-07 audit): if the primary settings.json was corrupt at
            // load, tell the user now that the UI is up. Their settings were
            // recovered from backup or reset to defaults -- silently swallowing
            // this would look like their tenants/sites vanished for no reason.
            if (SettingsService.PrimaryWasCorruptOnLoad)
            {
                SafeFireAndForget.Run(
                    () => FluentDialog.ShowWarningAsync(
                        "Settings recovered",
                        "Your settings file could not be read and appeared to be corrupt. Wrapp has " +
                        "recovered from a backup where possible, otherwise it started from defaults. " +
                        "Review Settings (tenants, sites, Key Vault) and click Save to write a clean file."),
                    "settings-corrupt-warn");
            }

            // Gate framework: resolve blocking gates (liability waiver / mandatory
            // migrations) before the user works, and surface advisory gates (vault
            // re-approval) via the status-bar "action needed" indicator. Adding a
            // future required-action is a new IAppGate registered here -- no
            // framework or UI change. See Services.Gates.
            var gateService = new GateService(settings, new IAppGate[]
            {
                new VaultUrlApprovalGate(),
                new LiabilityWaiverGate(),
                // First-run org provisioning: browse for a company defaults
                // file or continue with examples. Re-seeds immediately on
                // import (the normal seed pass above already ran).
                new FirstRunDefaultsGate(imported =>
                    OrgDefaultsSeeder.ApplyImported(settings, imported)),
                new UpdateFeedApprovalGate(),
                // Phase C: an available update surfaces HERE — the indicator —
                // never as a dialog over live work.
                new UpdatePendingGate(),
                // Registry policy changed after launch (gpupdate / Intune /
                // Apply-WrappPolicy.ps1) — restart-to-apply, surfaced through
                // the same indicator, raised by PolicyChangeMonitor below.
                new PolicyChangedGate(),
            });
            _root.Get<MainViewModel>().WireGateService(gateService);

            // Event-driven (RegNotifyChangeKeyValue — no polling): when the
            // Software\Policies subtree changes and the effective Wrapp policy
            // fingerprint drifts from the launch snapshot, the gate above goes
            // pending and the action-required indicator lights up.
            Services.Policy.PolicyChangeMonitor.Start(() =>
                Current.Dispatcher.BeginInvoke(() =>
                    _root.Get<MainViewModel>().RefreshPendingActions()));
            // D5: LastRunVersion as it was BEFORE this launch records itself --
            // RecordRun overwrites it below, and WhatsNewService needs it to
            // tell a brand-new profile from an updated one.
            var previousRunVersion = settings.LastRunVersion;
            SafeFireAndForget.Run(async () =>
            {
                var appVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? string.Empty;
                if (!await gateService.ResolveBlockingAsync())
                {
                    AppLogger.Warn("Startup: a required gate was declined; shutting down.");
                    await Current.Dispatcher.InvokeAsync(() => Shutdown());
                    return;
                }
                gateService.RecordRun(appVersion);
                await Current.Dispatcher.InvokeAsync(() => _root.Get<MainViewModel>().RefreshPendingActions());

                // Workstream D5: What's-New popup on the first launch after a
                // version change (any update path). After blocking gates so the
                // waiver always comes first.
                await WhatsNewService.MaybeShowAsync(settings, previousRunVersion);

                // Workstream D: launch-time update check, after the gate pass so
                // a just-approved feed is honored immediately. No-ops for
                // non-installed (dev / portable) runs and Disabled mode.
                // Phase C: the check only RECORDS availability; re-evaluate the
                // indicator afterwards so UpdatePendingGate lights up.
                await UpdateService.StartupCheckAsync(settings);
                await Current.Dispatcher.InvokeAsync(() => _root.Get<MainViewModel>().RefreshPendingActions());
            }, "app-gates");

            // Load the config chosen in splash after window is visible.
            // Pass the pre-loaded config to avoid a duplicate LoadAsync call.
            // Wrapped in SafeFireAndForget so load failures surface a dialog + log
            // entry instead of silent death.
            SafeFireAndForget.Run(
                () => _root.Get<GeneralViewModel>().LoadFromPathAsync(configPath, splash.Vm.PreloadedConfig),
                "initial config load",
                showDialog: true);
    }

    private static void DataGridCell_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridCell cell || cell.IsEditing || cell.IsReadOnly) return;

        // If a ComboBox inside this cell has its dropdown open, let it handle the click
        // (mouse capture routes dropdown clicks through the cell, stealing focus otherwise)
        foreach (var cb in FindVisualChildren<System.Windows.Controls.ComboBox>(cell))
        {
            if (cb.IsDropDownOpen) return;
        }

        // If the click landed on a Button (or child of one), let the button handle it directly
        var source = e.OriginalSource as DependencyObject;
        while (source != null && source != cell)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase) return;
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        var parent = System.Windows.Media.VisualTreeHelper.GetParent(cell);
        while (parent != null && parent is not DataGrid)
            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
        if (parent is not DataGrid dataGrid) return;

        cell.Focus();
        dataGrid.BeginEdit(e);

        // BeginEdit marks the cell as editing but doesn't focus the inner control
        // (TextBox/ComboBox in CellTemplate). Defer at Background priority so the
        // DataGrid finishes its internal selection/focus processing first.
        cell.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            // Skip if an inner control already has focus (user clicked directly on it)
            var focused = Keyboard.FocusedElement;
            if (focused is System.Windows.Controls.TextBox
                || focused is System.Windows.Controls.ComboBox) return;
            var focusable = FindFirstFocusableChild(cell);
            focusable?.Focus();
        });
    }

    private static UIElement? FindFirstFocusableChild(DependencyObject parent)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is UIElement uie && uie.Focusable && uie.IsVisible
                && (child is System.Windows.Controls.TextBox || child is System.Windows.Controls.ComboBox))
                return uie;
            var result = FindFirstFocusableChild(child);
            if (result != null) return result;
        }
        return null;
    }

    private static void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Window window) return;

        // Ignore clicks inside popups (ComboBox dropdowns, menus, etc.)
        // and clicks inside ContentDialog overlays (registry browser, assignments, etc.)
        var source = e.OriginalSource as DependencyObject;
        while (source != null)
        {
            if (source is System.Windows.Controls.Primitives.Popup)
                return;
            if (source is Wpf.Ui.Controls.ContentDialog)
                return;
            // VisualTreeHelper only works on Visual/Visual3D; inline elements
            // like Run/Span use the logical tree instead.
            source = source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(source)
                : System.Windows.LogicalTreeHelper.GetParent(source);
        }

        // If any ComboBox has its dropdown open, the click is likely on the
        // dropdown popup (a separate visual tree that the walk above cannot
        // reach). Let the ComboBox handle it -- do not commit/deselect.
        foreach (var cb in FindVisualChildren<System.Windows.Controls.ComboBox>(window))
        {
            if (cb.IsDropDownOpen) return;
        }

        // Commit and deselect any DataGrid the click landed outside of
        foreach (var dg in FindVisualChildren<DataGrid>(window))
        {
            if (!dg.IsVisible) continue;
            if (dg.SelectedCells.Count == 0 && dg.SelectedItems.Count == 0) continue;

            var pos = e.GetPosition(dg);
            if (pos.X >= 0 && pos.Y >= 0
                && pos.X <= dg.ActualWidth && pos.Y <= dg.ActualHeight)
                continue;

            dg.CommitEdit(DataGridEditingUnit.Cell, true);
            dg.CommitEdit(DataGridEditingUnit.Row, true);
            dg.UnselectAll();
            // Move focus to the window so the DataGrid releases cell focus
            // without clearing keyboard focus entirely (which would break single-click)
            window.Focus();
        }
    }

    private static void ComboBox_DropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox cb) return;
        var window = Window.GetWindow(cb);
        if (window is null) return;

        try
        {
            var transform = cb.TransformToAncestor(window);
            var cbBottom = transform.Transform(new System.Windows.Point(0, cb.ActualHeight));
            var spaceBelow = window.ActualHeight - cbBottom.Y - 8;
            cb.MaxDropDownHeight = Math.Max(80, Math.Min(spaceBelow, 300));
        }
        catch { /* TransformToAncestor can fail if not in same visual tree */ }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var grandchild in FindVisualChildren<T>(child))
                yield return grandchild;
        }
    }

    private static string ResolveModulePath()
    {
        try
        {
            var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(settingsPath))
            {
                using var stream = File.OpenRead(settingsPath);
                var doc = JsonDocument.Parse(stream);
                if (doc.RootElement.TryGetProperty("ModulePath", out var prop))
                {
                    var rel = prop.GetString();
                    if (!string.IsNullOrEmpty(rel))
                    {
                        var abs = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, rel));
                        if (File.Exists(abs)) return abs;
                    }
                }
            }
        }
        catch { /* fall through to default */ }

        // Default: look for bundled Wrapp.Packager alongside the exe
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            @"Modules\Wrapp.Packager\Wrapp.Packager.psd1"));
    }

    /// <summary>
    /// Invoked from the FieldLabel style's Loaded EventSetter. If the label has
    /// a ToolTip, copy it to the first input sibling in the same logical parent
    /// so the user sees the same help text whether they hover the label or the
    /// input. Explicit ToolTips already set on the input are preserved.
    /// </summary>
    private void FieldLabel_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock label) return;
        if (label.ToolTip is null) return;
        PropagateFieldHelp(label, label.ToolTip);
    }

    /// <summary>
    /// Copies a label's help tooltip onto its associated input. Looks in the
    /// label's logical parent first; when that yields no input (the common
    /// "label + Required badge in a horizontal StackPanel" wrapper), falls
    /// back to the grandparent, but only at inputs that FOLLOW the label's
    /// own panel — a preceding input belongs to a different field. Shared by
    /// the FieldLabel style's Loaded hook and <see cref="Controls.FieldLabelRow"/>.
    /// </summary>
    internal static void PropagateFieldHelp(FrameworkElement labelRoot, object tooltip)
    {
        var parent = System.Windows.LogicalTreeHelper.GetParent(labelRoot);
        if (parent is null) return;

        var target = FindFirstInput(parent, labelRoot);
        if (target is null && System.Windows.LogicalTreeHelper.GetParent(parent) is { } grandparent)
            target = FindFirstInputAfter(grandparent, parent);

        if (target is null || target.ToolTip is not null) return;
        // A bound ToolTip (e.g. FieldStates[X].DisabledReason) reads null while
        // enabled — setting a local value would silently kill the binding. Those
        // inputs opt into static help via TargetNullValue on their own binding.
        if (System.Windows.Data.BindingOperations.GetBindingBase(
                target, FrameworkElement.ToolTipProperty) is not null) return;
        target.ToolTip = tooltip;
        System.Windows.Controls.ToolTipService.SetShowOnDisabled(target, true);
    }

    private static System.Windows.Controls.Control? FindFirstInput(
        System.Windows.DependencyObject parent,
        System.Windows.DependencyObject exclude)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(parent))
        {
            if (ReferenceEquals(child, exclude)) continue;
            if (child is System.Windows.Controls.TextBox
                     or System.Windows.Controls.ComboBox
                     or System.Windows.Controls.CheckBox
                     or System.Windows.Controls.PasswordBox
                     or System.Windows.Controls.DatePicker
                     or System.Windows.Controls.RadioButton
                     or System.Windows.Controls.Slider)
                return (System.Windows.Controls.Control)child;
            if (child is System.Windows.DependencyObject dep)
            {
                var found = FindFirstInput(dep, exclude);
                if (found is not null) return found;
            }
        }
        return null;
    }

    /// <summary>
    /// Like <see cref="FindFirstInput"/> but only considers logical children
    /// AFTER <paramref name="afterSubtree"/> — used for the grandparent
    /// fallback so a label never adopts an input rendered before it.
    /// </summary>
    private static System.Windows.Controls.Control? FindFirstInputAfter(
        System.Windows.DependencyObject parent,
        System.Windows.DependencyObject afterSubtree)
    {
        var passed = false;
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(parent))
        {
            if (ReferenceEquals(child, afterSubtree)) { passed = true; continue; }
            if (!passed) continue;
            if (child is System.Windows.Controls.TextBox
                     or System.Windows.Controls.ComboBox
                     or System.Windows.Controls.CheckBox
                     or System.Windows.Controls.PasswordBox
                     or System.Windows.Controls.DatePicker
                     or System.Windows.Controls.RadioButton
                     or System.Windows.Controls.Slider)
                return (System.Windows.Controls.Control)child;
            if (child is System.Windows.DependencyObject dep)
            {
                var found = FindFirstInput(dep, afterSubtree);
                if (found is not null) return found;
            }
        }
        return null;
    }
}

// Converters moved to Helpers/Converters.cs

