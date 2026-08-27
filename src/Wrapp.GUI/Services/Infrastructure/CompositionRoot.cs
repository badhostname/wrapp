using Wrapp.Models;
using Wrapp.Services;
using Wrapp.Services.Gates;
using Wrapp.ViewModels;
using Wrapp.Views;

namespace Wrapp;

/// <summary>
/// Builds and owns wrapp's service + view-model object graph. Extracted from
/// <c>App.StartupCoreAsync</c> so the composition lives in one testable place
/// and views / later features can fetch singletons via <c>App.GetService&lt;T&gt;()</c>.
///
/// <para>This is a hand-wired composition root, deliberately not a DI
/// container: the graph has a real constructor cycle (<see cref="MainViewModel"/>
/// is built first, but the package/tenant view-models take it in their ctors
/// while it needs them back via <c>Wire*</c> methods), which a container can't
/// resolve. The construction order and the post-construction wiring here are
/// load-bearing — they are moved verbatim from the previous inline startup
/// block, not reordered.</para>
/// </summary>
public sealed class CompositionRoot
{
    private readonly ServiceRegistry _registry = new();

    /// <summary>The PowerShell host, exposed for window-closed disposal.</summary>
    public PowerShellService Ps { get; private set; } = null!;

    /// <summary>The MSAL auth service, exposed for window-closed disposal + parent-window wiring.</summary>
    public MsalAuthService MsalAuth { get; private set; } = null!;

    /// <summary>Resolve a registered singleton. Throws if it was never registered.</summary>
    public T Get<T>() where T : class => _registry.Get<T>();

    /// <summary>Non-throwing resolve (used by <c>App.InventoryService</c>).</summary>
    public bool TryGet<T>(out T? instance) where T : class => _registry.TryGet(out instance);

    /// <summary>
    /// Constructs the full service + view-model graph. Heavy services
    /// (<see cref="PowerShellService"/>, <see cref="MsalAuthService"/>) are
    /// created on a background thread so the splash Dispatcher keeps ticking.
    /// </summary>
    public static async Task<CompositionRoot> BuildAsync(AppSettings settings, string modulePath)
    {
        var root = new CompositionRoot();
        var reg = root._registry;

        reg.Register(settings);

        // Create heavy services on a background thread so the Dispatcher
        // can keep processing splash timer ticks (burrito messages).
        AppLogger.Info("Creating services and view models");
        var (ps, msalAuth) = await Task.Run(() =>
        {
            var p = new PowerShellService(modulePath);
            var m = new MsalAuthService();
            return (p, m);
        });
        root.Ps = reg.Register(ps);
        root.MsalAuth = reg.Register(msalAuth);

        var jobTracker = reg.Register(new BackgroundJobTracker());
        var mainVm     = reg.Register(new MainViewModel(ps, jobTracker));
        var devOpsAuth = reg.Register(new DevOpsAuthService());
        var featureGate = reg.Register(new FeatureGateService(settings));
        var keyStore = reg.Register(new EncryptionKeyStoreService(devOpsAuth, settings, featureGate));
        var decryptOrchestrator = reg.Register(new IntuneWinDecryptOrchestrator(keyStore));
        var generalVm  = reg.Register(new GeneralViewModel(settings, decryptOrchestrator, featureGate));
        var tenantsVm    = reg.Register(new TenantsViewModel(generalVm, mainVm));
        tenantsVm.WireSettings(settings);
        var intuneVm   = reg.Register(new IntuneViewModel(generalVm, mainVm, tenantsVm, settings));
        var sccmVm     = reg.Register(new SCCMViewModel(generalVm, mainVm, tenantsVm, settings));
        var detectionVm  = reg.Register(new DetectionViewModel(generalVm));
        var scriptsVm    = reg.Register(new ScriptsViewModel(generalVm));
        var configJsonVm = reg.Register(new ConfigJsonViewModel(generalVm, tenantsVm));
        var runVm        = reg.Register(new RunViewModel(ps, generalVm, msalAuth));
        var logsVm       = reg.Register(new LogsViewModel());
        var gitHistoryVm = reg.Register(new GitHistoryViewModel(generalVm));
        tenantsVm.AuthService = msalAuth;
        var accountVm    = reg.Register(new AccountViewModel(msalAuth, generalVm, ps, settings));
        accountVm.TenantsVm = tenantsVm;
        accountVm.WireDevOpsAuth(devOpsAuth);
        var inventoryService = reg.Register(new AppInventoryService(ps, msalAuth));
        // Publish-target framework: unifies Intune/SCCM dispatch + capability
        // gating behind IPublishTarget. Adding a target = register another here.
        var targets = reg.Register(new Wrapp.Services.Targets.PublishTargetRegistry(new Wrapp.Services.Targets.IPublishTarget[]
        {
            new Wrapp.Services.Targets.IntunePublishTarget(inventoryService),
            new Wrapp.Services.Targets.SccmPublishTarget(inventoryService),
        }));
        var inventoryVm  = reg.Register(new InventoryViewModel(inventoryService, tenantsVm, msalAuth, ps, targets));
        var toolsVm      = reg.Register(new ToolsViewModel(keyStore, decryptOrchestrator, settings, featureGate));
        var settingsVm   = reg.Register(new SettingsViewModel(settings, tenantsVm));

        // Wire SaveBundle commands to MainViewModel so any section can save
        mainVm.SaveBundleCommand   = generalVm.SaveBundleCommand;
        mainVm.SaveBundleAsCommand = generalVm.SaveBundleAsCommand;
        mainVm.SaveSettingsCommand   = settingsVm.SaveCommand;
        mainVm.AccountVm             = accountVm;
        mainVm.WireSettingsVm(settingsVm);   // assigns SettingsVm + relays the placeholder error badge
        mainVm.WireGeneralVm(generalVm);
        mainVm.WirePackageVms(intuneVm, sccmVm);
        mainVm.WireInventoryVm(inventoryVm);
        mainVm.WireDetectionVm(detectionVm); // duplicate-symbol errors → Detection nav badge
        inventoryVm.WireGeneralVm(generalVm);
        inventoryVm.WireKeyStore(keyStore);
        inventoryVm.WireSettings(settings);
        inventoryVm.WireJobTracker(jobTracker);
        generalVm.WireJobTracker(jobTracker);
        toolsVm.WireJobTracker(jobTracker);
        runVm.WireJobTracker(jobTracker);
        runVm.WireKeyStore(keyStore);
        runVm.WireFeatureGate(featureGate);

        // Subscribe to ConfigLoaded BEFORE the initial restore, so if the async
        // LoadFromPathAsync below completes quickly the post-load restore still
        // fires. RestoreSavedTenants is reentrant-safe (see SettingsViewModel).
        generalVm.ConfigLoaded += (_, args) =>
        {
            mainVm.SetConfigPath(args.Path);
            settingsVm.RestoreSavedTenants();
            inventoryVm.RefreshTargets();
            // Workstream P: the Placeholders tab's grayed built-in rows track
            // the ACTIVE bundle. The AppSection instance changes on every
            // config load, so re-point the observer here.
            settingsVm.Placeholders.ObserveApp(args.Config.App);
        };

        // Initial wiring for the default (empty) config; live edits in the
        // General view flow through AppSection.PropertyChanged from here on.
        settingsVm.Placeholders.ObserveApp(generalVm.App);

        // Restore persisted tenants/sites into the default (empty) config at startup
        settingsVm.RestoreSavedTenants();
        settingsVm.WireDevOpsAuth(devOpsAuth);
        settingsVm.WireFeatureGate(featureGate);

        // A tenant discovered at sign-in lands in the bundle (AccountViewModel)
        // AND in the technician's saved tenants (SettingsViewModel owns the
        // preferences list + save path). Event-wired here so neither view-model
        // needs a reference to the other.
        accountVm.TenantDiscovered += tenant =>
            SafeFireAndForget.Run(() => settingsVm.OnTenantDiscoveredAsync(tenant),
                "tenant-discovered-sync");
        inventoryVm.RefreshTargets();

        return root;
    }

    /// <summary>
    /// Constructs the main window from the registered view-models. Kept here so
    /// the 13-VM constructor call lives next to the graph it draws from.
    /// </summary>
    public MainWindow CreateMainWindow()
    {
        AppLogger.Info("Creating MainWindow");
        return new MainWindow(
            Get<MainViewModel>(), Get<GeneralViewModel>(), Get<IntuneViewModel>(), Get<SCCMViewModel>(),
            Get<DetectionViewModel>(), Get<ScriptsViewModel>(), Get<ConfigJsonViewModel>(),
            Get<RunViewModel>(), Get<LogsViewModel>(), Get<GitHistoryViewModel>(),
            Get<InventoryViewModel>(), Get<ToolsViewModel>(), Get<SettingsViewModel>());
    }
}
