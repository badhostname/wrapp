using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.ViewModels;

public partial class TenantsViewModel : StatusViewModelBase
{
    private readonly GeneralViewModel _appInfoVm;
    private readonly MainViewModel    _mainVm;

    /// <summary>
    /// Shared MSAL auth service instance. Set by App.xaml.cs after construction.
    /// </summary>
    public MsalAuthService? AuthService { get; set; }

    /// <summary>
    /// User preferences source (settings.json). Wired from App.xaml.cs via
    /// <see cref="WireSettings"/>. Null until wired; the Sync commands
    /// gracefully no-op if unavailable.
    /// </summary>
    private AppSettings? _settings;

    public TenantsViewModel(GeneralViewModel appInfoVm, MainViewModel mainVm)
    {
        _appInfoVm = appInfoVm;
        _mainVm    = mainVm;
        _appInfoVm.ConfigLoaded += OnConfigLoaded;
    }

    /// <summary>
    /// Supplies the persisted <see cref="AppSettings"/> so the Sync commands
    /// can pull tenants/sites from the user's saved Preferences.
    /// </summary>
    public void WireSettings(AppSettings settings) => _settings = settings;

    private void OnConfigLoaded(object? sender, (AppConfigModel Config, string Path) e)
    {
        OnPropertyChanged(nameof(IntuneTenants));
        OnPropertyChanged(nameof(SCCMSites));
        SelectedIntuneTenant = null;
        SelectedSCCMSite     = null;
    }

    // -----------------------------------------------------------------------
    // Exposed model collections
    // -----------------------------------------------------------------------

    public System.Collections.ObjectModel.ObservableCollection<IntuneTenantEntry> IntuneTenants
        => _appInfoVm.FullConfig.IntuneTenants;

    public System.Collections.ObjectModel.ObservableCollection<SCCMSiteEntry> SCCMSites
        => _appInfoVm.FullConfig.SccmSites;

    public System.Collections.ObjectModel.ObservableCollection<DomainEntry> Domains
        => _appInfoVm.FullConfig.Domains;

    public ModuleDefaults Defaults => _mainVm.ModuleDefaults;

    // -----------------------------------------------------------------------
    // Intune tenant selection
    // -----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTenant))]
    private IntuneTenantEntry? _selectedIntuneTenant;

    public bool HasSelectedTenant => SelectedIntuneTenant is not null;

    // -----------------------------------------------------------------------
    // SCCM site selection
    // -----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedSite))]
    private SCCMSiteEntry? _selectedSCCMSite;

    public bool HasSelectedSite => SelectedSCCMSite is not null;

    // -----------------------------------------------------------------------
    // Intune tenant commands
    // -----------------------------------------------------------------------

    [RelayCommand]
    private void AddIntuneTenant()
    {
        var t = new IntuneTenantEntry
        {
            Key      = "",
            Name     = "New Tenant",
            AuthFlow = AuthFlow.Interactive
        };
        IntuneTenants.Add(t);
        SelectedIntuneTenant = t;
    }

    [RelayCommand]
    private void RemoveIntuneTenant()
    {
        if (SelectedIntuneTenant is null) return;
        AppLogger.Info($"Tenants: removed Intune tenant \"{SelectedIntuneTenant.Name}\" ({SelectedIntuneTenant.Key})");
        IntuneTenants.Remove(SelectedIntuneTenant);
        SelectedIntuneTenant = IntuneTenants.LastOrDefault();
    }

    [RelayCommand]
    private async Task ConfirmRemoveIntuneTenantAsync(IntuneTenantEntry entry)
    {
        var displayName = !string.IsNullOrWhiteSpace(entry.Name) ? entry.Name : entry.Key;
        var name = string.IsNullOrWhiteSpace(displayName) ? "this tenant" : $"\"{displayName}\"";
        var confirmed = await FluentDialog.ConfirmAsync(
            "Remove Tenant",
            $"Remove {name}? This tenant configuration will be deleted.",
            "Remove", "Cancel");
        if (confirmed)
        {
            AppLogger.Info($"Tenants: removed Intune tenant \"{entry.Name}\" ({entry.Key})");
            IntuneTenants.Remove(entry);
        }
    }

    // -----------------------------------------------------------------------
    // Auth test / sign-out
    // -----------------------------------------------------------------------

    /// <summary>Forwards inherited <see cref="StatusViewModelBase.StatusText"/> under the legacy name bound by TenantsView.xaml.</summary>
    public string AuthStatusText => StatusText;
    /// <summary>Forwards inherited <see cref="StatusViewModelBase.StatusIsError"/> under the legacy name.</summary>
    public bool AuthStatusIsError => StatusIsError;
    /// <summary>Forwards inherited <see cref="StatusViewModelBase.IsBusy"/> under the legacy name.</summary>
    public bool IsAuthTesting => IsBusy;

    /// <summary>Success badge — separate from the error flag because the success state has its own UI affordance.</summary>
    [ObservableProperty]
    private bool _authStatusIsSuccess;

    protected override void OnIsBusyChangedInternal(bool oldValue, bool newValue)
        => OnPropertyChanged(nameof(IsAuthTesting));
    protected override void OnStatusTextChangedInternal(string oldValue, string newValue)
        => OnPropertyChanged(nameof(AuthStatusText));
    protected override void OnStatusIsErrorChangedInternal(bool oldValue, bool newValue)
        => OnPropertyChanged(nameof(AuthStatusIsError));

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (SelectedIntuneTenant is null || AuthService is null) return;
        var tenant = SelectedIntuneTenant;

        if (string.IsNullOrWhiteSpace(tenant.Key))
        {
            StatusText = "Tenant ID is required.";
            StatusIsError = true;
            AuthStatusIsSuccess = false;
            return;
        }

        AuthStatusIsSuccess = false;

        await RunBusyAsync(async () =>
        {
            await AuthService.InitializeForTenantAsync(tenant, WindowHelper.GetMainWindowHwnd());
            var result = await AuthService.AcquireTokenAsync();

            StatusText = !string.IsNullOrEmpty(result.UserPrincipalName)
                ? $"Authenticated as {result.UserPrincipalName}"
                : "App authenticated (client credentials)";
            AuthStatusIsSuccess = true;
        }, "Tenant test connection");
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        if (AuthService is null) return;
        try
        {
            await AuthService.SignOutAsync();
            StatusText = "Signed out.";
            AuthStatusIsSuccess = false;
            StatusIsError = false;
        }
        catch (Exception ex)
        {
            StatusText = $"Sign-out failed: {ex.Message}";
            StatusIsError = true;
        }
    }

    [RelayCommand]
    private void AddScopeTag()
    {
        SelectedIntuneTenant?.ScopeTags.Add(string.Empty);
    }

    [RelayCommand]
    private void RemoveScopeTag(string tag)
    {
        SelectedIntuneTenant?.ScopeTags.Remove(tag);
    }

    // -----------------------------------------------------------------------
    // SCCM site commands
    // -----------------------------------------------------------------------

    [RelayCommand]
    private void AddSCCMSite()
    {
        var s = new SCCMSiteEntry { Key = "NewSite" };
        SCCMSites.Add(s);
        SelectedSCCMSite = s;
    }

    [RelayCommand]
    private void RemoveSCCMSite()
    {
        if (SelectedSCCMSite is null) return;
        AppLogger.Info($"Tenants: removed SCCM site \"{SelectedSCCMSite.Key}\"");
        SCCMSites.Remove(SelectedSCCMSite);
        SelectedSCCMSite = SCCMSites.LastOrDefault();
    }

    [RelayCommand]
    private async Task ConfirmRemoveSCCMSiteAsync(SCCMSiteEntry entry)
    {
        var name = string.IsNullOrWhiteSpace(entry.Key) ? "this site" : $"\"{entry.Key}\"";
        var confirmed = await FluentDialog.ConfirmAsync(
            "Remove Site",
            $"Remove {name}? This site configuration will be deleted.",
            "Remove", "Cancel");
        if (confirmed)
        {
            AppLogger.Info($"Tenants: removed SCCM site \"{entry.Key}\"");
            SCCMSites.Remove(entry);
        }
    }

    [RelayCommand]
    private void AddDeploymentGroup()
    {
        SelectedSCCMSite?.DeploymentGroups.Add(string.Empty);
    }

    [RelayCommand]
    private void RemoveDeploymentGroup(string group)
    {
        SelectedSCCMSite?.DeploymentGroups.Remove(group);
    }

    // -----------------------------------------------------------------------
    // Bundle-context sync menu
    //
    // Each command mutates the live AppConfigModel collection in memory only.
    // User must click Save Bundle to persist. Overwrite variants require a
    // confirmation dialog because they're destructive.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds <see cref="IntuneTenantEntry"/> instances from the persisted
    /// <see cref="SavedTenantEntry"/> list, decrypting ClientSecret for
    /// in-memory use.
    /// </summary>
    private IEnumerable<IntuneTenantEntry> PreferredTenants()
    {
        if (_settings is null) return Array.Empty<IntuneTenantEntry>();
        return _settings.IntuneTenants.Select(saved => new IntuneTenantEntry
        {
            Key                            = saved.Key,
            Name                           = saved.Name,
            Comment                        = saved.Comment,
            ClientID                       = saved.ClientID,
            AuthFlow                       = saved.AuthFlow,
            ClientSecretCipher             = saved.ClientSecret,
            CertThumbprint                 = saved.CertThumbprint,
            Architecture                   = saved.Architecture,
            MinimumSupportedWindowsRelease = saved.MinimumSupportedWindowsRelease,
            IntuneWinPath                  = saved.IntuneWinPath,
            IconFolder                     = saved.IconFolder,
        });
    }

    private IEnumerable<SCCMSiteEntry> PreferredSites()
    {
        if (_settings is null) return Array.Empty<SCCMSiteEntry>();
        return _settings.SccmSites.Select(saved =>
        {
            var entry = new SCCMSiteEntry
            {
                Key        = saved.Key,
                Comment    = saved.Comment,
                AppFolder  = saved.AppFolder,
                IconFolder = saved.IconFolder,
            };
            foreach (var g in saved.DeploymentGroups)
                entry.DeploymentGroups.Add(g);
            return entry;
        });
    }

    [RelayCommand]
    private async Task SyncIntuneOverwriteFromPrefs()
    {
        var ok = await FluentDialog.ConfirmAsync(
            "Overwrite tenants with Preferences",
            "Replace this bundle's Intune tenants with your saved preferences? " +
            "Existing tenant entries in this bundle will be lost (Save Bundle is still required to persist).",
            "Overwrite", "Cancel");
        if (!ok) return;
        var n = PreferencesSync.OverwriteTenants(IntuneTenants, PreferredTenants());
        AppLogger.Info($"Sync: overwrote {n} Intune tenant(s) from Preferences");
    }

    [RelayCommand]
    private void SyncIntuneAddFromPrefs()
    {
        var n = PreferencesSync.AddMissingTenants(IntuneTenants, PreferredTenants());
        AppLogger.Info($"Sync: added {n} missing Intune tenant(s) from Preferences");
    }

    [RelayCommand]
    private async Task SyncIntuneOverwriteFromDefaults()
    {
        var ok = await FluentDialog.ConfirmAsync(
            "Overwrite tenants with App Defaults",
            "Replace this bundle's Intune tenants with the app's shipped defaults " +
            "(defaults.local.json)? Existing entries will be lost (Save Bundle is still required to persist).",
            "Overwrite", "Cancel");
        if (!ok) return;
        var n = PreferencesSync.OverwriteTenants(IntuneTenants, SettingsViewModel.DefaultTenants);
        AppLogger.Info($"Sync: overwrote {n} Intune tenant(s) from App Defaults");
    }

    [RelayCommand]
    private void SyncIntuneAddFromDefaults()
    {
        var n = PreferencesSync.AddMissingTenants(IntuneTenants, SettingsViewModel.DefaultTenants);
        AppLogger.Info($"Sync: added {n} missing Intune tenant(s) from App Defaults");
    }

    [RelayCommand]
    private async Task SyncSccmOverwriteFromPrefs()
    {
        var ok = await FluentDialog.ConfirmAsync(
            "Overwrite sites with Preferences",
            "Replace this bundle's SCCM sites with your saved preferences? " +
            "Existing site entries in this bundle will be lost (Save Bundle is still required to persist).",
            "Overwrite", "Cancel");
        if (!ok) return;
        var n = PreferencesSync.OverwriteSites(SCCMSites, PreferredSites());
        AppLogger.Info($"Sync: overwrote {n} SCCM site(s) from Preferences");
    }

    [RelayCommand]
    private void SyncSccmAddFromPrefs()
    {
        var n = PreferencesSync.AddMissingSites(SCCMSites, PreferredSites());
        AppLogger.Info($"Sync: added {n} missing SCCM site(s) from Preferences");
    }

    [RelayCommand]
    private async Task SyncSccmOverwriteFromDefaults()
    {
        var ok = await FluentDialog.ConfirmAsync(
            "Overwrite sites with App Defaults",
            "Replace this bundle's SCCM sites with the app's shipped defaults " +
            "(defaults.local.json)? Existing entries will be lost (Save Bundle is still required to persist).",
            "Overwrite", "Cancel");
        if (!ok) return;
        var n = PreferencesSync.OverwriteSites(SCCMSites, SettingsViewModel.DefaultSites);
        AppLogger.Info($"Sync: overwrote {n} SCCM site(s) from App Defaults");
    }

    [RelayCommand]
    private void SyncSccmAddFromDefaults()
    {
        var n = PreferencesSync.AddMissingSites(SCCMSites, SettingsViewModel.DefaultSites);
        AppLogger.Info($"Sync: added {n} missing SCCM site(s) from App Defaults");
    }

    // -------------------------------------------------------------------
    // Domains — same shape as tenants/sites, mutates Domains (which
    // aliases _appInfoVm.FullConfig.Domains i.e. the bundle Config.Domain
    // section). Called from the Sync Domains dropdown on ConfigJsonView.
    // -------------------------------------------------------------------

    private IEnumerable<DomainEntry> PreferredDomains()
    {
        if (_settings is null) return Array.Empty<DomainEntry>();
        return _settings.Domains.Select(saved => new DomainEntry
        {
            Key        = saved.Key,
            IsDistPath = saved.IsDistPath,
            AppFolder  = saved.AppFolder,
            TagFolder  = saved.TagFolder,
        });
    }

    [RelayCommand]
    private async Task SyncDomainsOverwriteFromPrefs()
    {
        var ok = await FluentDialog.ConfirmAsync(
            "Overwrite domains with Preferences",
            "Replace this bundle's Config.json Domain section with your saved preferences? " +
            "Existing domain entries in this bundle will be lost (Save Bundle is still required to persist).",
            "Overwrite", "Cancel");
        if (!ok) return;
        var n = PreferencesSync.OverwriteDomains(Domains, PreferredDomains());
        AppLogger.Info($"Sync: overwrote {n} domain(s) from Preferences");
    }

    [RelayCommand]
    private void SyncDomainsAddFromPrefs()
    {
        var n = PreferencesSync.AddMissingDomains(Domains, PreferredDomains());
        AppLogger.Info($"Sync: added {n} missing domain(s) from Preferences");
    }

    [RelayCommand]
    private async Task SyncDomainsOverwriteFromDefaults()
    {
        var ok = await FluentDialog.ConfirmAsync(
            "Overwrite domains with App Defaults",
            "Replace this bundle's Config.json Domain section with the app's shipped defaults " +
            "(defaults.local.json)? Existing entries will be lost (Save Bundle is still required to persist).",
            "Overwrite", "Cancel");
        if (!ok) return;
        var n = PreferencesSync.OverwriteDomains(Domains, SettingsViewModel.DefaultDomainsExternal);
        AppLogger.Info($"Sync: overwrote {n} domain(s) from App Defaults");
    }

    [RelayCommand]
    private void SyncDomainsAddFromDefaults()
    {
        var n = PreferencesSync.AddMissingDomains(Domains, SettingsViewModel.DefaultDomainsExternal);
        AppLogger.Info($"Sync: added {n} missing domain(s) from App Defaults");
    }
}
