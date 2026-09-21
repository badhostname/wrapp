using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wrapp.Helpers;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.ViewModels;

/// <summary>
/// Editable view of the user's persisted preferences for Intune tenants and
/// SCCM sites. Backed by <see cref="AppSettings.IntuneTenants"/> /
/// <see cref="AppSettings.SccmSites"/> in <c>settings.json</c>.
///
/// <para>On construction the persisted values are lifted into observable
/// collections with <see cref="IntuneTenantEntry.ClientSecret"/> decrypted
/// for in-memory edit. On <see cref="SaveAsync"/> the collections are
/// round-tripped back through <see cref="SettingsService.SavePreferencesAsync"/>
/// which DPAPI-encrypts secrets.</para>
///
/// <para>Preferences are applied to NEW bundles automatically (the existing
/// <c>SettingsViewModel.RestoreSavedTenants</c> path reads from
/// <c>AppSettings.IntuneTenants</c> first, falling back to shipped defaults).
/// Existing bundles are not mutated; the user uses the Sync menu in
/// IntuneView / SCCMView to apply preferences explicitly.</para>
/// </summary>
public partial class PreferencesViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    public ObservableCollection<IntuneTenantEntry> IntuneTenants { get; } = new();
    public ObservableCollection<SCCMSiteEntry>     SCCMSites     { get; } = new();
    public ObservableCollection<DomainEntry>       Domains       { get; } = new();

    /// <summary>Drives IsEnabled on the "Remove selected" button for Intune tenants.</summary>
    public SelectionTracker<IntuneTenantEntry> IntuneTenantsSelection { get; }
        = new(t => t.IsSelected);
    /// <summary>Drives IsEnabled on the "Remove selected" button for SCCM sites.</summary>
    public SelectionTracker<SCCMSiteEntry> SccmSitesSelection { get; }
        = new(s => s.IsSelected);
    /// <summary>Drives IsEnabled on the "Remove selected" button for domain entries.</summary>
    public SelectionTracker<DomainEntry> DomainsSelection { get; }
        = new(d => d.IsSelected);

    /// <summary>Intune package default values applied when a new package is added.</summary>
    public IntunePackageDefaultsVm    IntunePackage    { get; }
    /// <summary>Intune app metadata defaults (token templates).</summary>
    public IntuneMetadataDefaultsVm   IntuneMetadata   { get; }
    /// <summary>Intune assignment default values for new assignments.</summary>
    public IntuneAssignmentDefaultsVm IntuneAssignment { get; }
    /// <summary>SCCM package default values applied when a new package is added.</summary>
    public SccmPackageDefaultsVm      SccmPackage      { get; }
    /// <summary>SCCM metadata defaults (token templates).</summary>
    public SccmMetadataDefaultsVm     SccmMetadata     { get; }
    /// <summary>SCCM deployment default values for new deployments.</summary>
    public SccmDeploymentDefaultsVm   SccmDeployment   { get; }

    /// <summary>
    /// Endpoint tag folder: where deployed packages write detection results +
    /// transcript logs ON TARGET MACHINES. Baked into new bundles' scripts via
    /// the {{TagFolder}} token; shared with the PS module via user-defaults.json.
    /// </summary>
    [ObservableProperty] private string _endpointTagFolder = string.Empty;

    /// <summary>Endpoint per-app local asset root ({{LocalAppFolder}} token).</summary>
    [ObservableProperty] private string _endpointLocalAppFolder = string.Empty;

    public PreferencesViewModel(AppSettings settings)
    {
        _settings        = settings;
        IntunePackage    = new IntunePackageDefaultsVm();
        IntuneMetadata   = new IntuneMetadataDefaultsVm();
        IntuneAssignment = new IntuneAssignmentDefaultsVm();
        SccmPackage      = new SccmPackageDefaultsVm();
        SccmMetadata     = new SccmMetadataDefaultsVm();
        SccmDeployment   = new SccmDeploymentDefaultsVm();

        LoadFromSettings();

        // Wire selection trackers now that the collections are populated.
        IntuneTenantsSelection.Bind(IntuneTenants);
        SccmSitesSelection.Bind(SCCMSites);
        DomainsSelection.Bind(Domains);
    }

    /// <summary>
    /// Produces a stable JSON string of the current in-memory editable state -
    /// tenants, sites, and all six defaults sub-VMs. Called by
    /// <see cref="SettingsViewModel"/>'s timer to diff against the snapshot
    /// captured at load/save time. Includes both the DPAPI ciphertext
    /// (<see cref="IntuneTenantEntry.ClientSecretCipher"/>, stable across ticks)
    /// and a presence-bit for any freshly-typed value
    /// (<see cref="IntuneTenantEntry.ClientSecret"/>, null on load) so either
    /// kind of edit flips IsDirty - but DPAPI's non-deterministic output never
    /// does, because we never re-encrypt here.
    /// <para>
    /// The fresh value is a <see cref="SecureString"/> rather
    /// than a managed string, so we cannot inline it into the JSON shape.
    /// <c>HasFreshSecret</c> is a boolean that flips on first keystroke and
    /// resets after save (when the model nulls the SecureString), which
    /// preserves the IsDirty signal without serialising plaintext.
    /// </para>
    /// </summary>
    public string SerializeSnapshot()
    {
        var shape = new
        {
            Tenants = IntuneTenants.Select(t => new
            {
                t.Key, t.Name, t.Comment, t.ClientID, t.AuthFlow,
                HasFreshSecret = t.ClientSecret is { Length: > 0 },
                t.ClientSecretCipher, t.CertThumbprint,
                t.Architecture, t.MinimumSupportedWindowsRelease,
                t.IntuneWinPath, t.IconFolder,
            }).ToArray(),
            Sites = SCCMSites.Select(s => new
            {
                s.Key, s.Comment, s.AppFolder, s.IconFolder,
                Groups = s.DeploymentGroups.ToArray(),
            }).ToArray(),
            Domains = Domains.Select(d => new
            {
                d.Key, d.IsDistPath, d.AppFolder, d.TagFolder,
            }).ToArray(),
            IntunePackage    = IntunePackage.Snapshot(),
            IntuneMetadata   = IntuneMetadata.Snapshot(),
            IntuneAssignment = IntuneAssignment.Snapshot(),
            SccmPackage      = SccmPackage.Snapshot(),
            SccmMetadata     = SccmMetadata.Snapshot(),
            SccmDeployment   = SccmDeployment.Snapshot(),
            EndpointTagFolder,
            EndpointLocalAppFolder,
        };
        return JsonSerializer.Serialize(shape);
    }

    /// <summary>
    /// Re-hydrates the observable collections from <see cref="AppSettings"/>.
    /// Call after destructive external changes (e.g. Reset All Settings) so
    /// the Preferences editor stays in sync.
    ///
    /// <para>When settings.json contains no persisted tenants/sites (fresh
    /// install, or after a full reset), seed the editor from the shipped
    /// <see cref="SettingsViewModel.DefaultTenants"/> /
    /// <see cref="SettingsViewModel.DefaultSites"/> so the user sees their
    /// current effective defaults rather than an empty table. Saving commits
    /// them to settings.json, at which point subsequent loads come from there.</para>
    /// </summary>
    public void LoadFromSettings()
    {
        IntuneTenants.Clear();
        if (_settings.IntuneTenants.Count > 0)
        {
            foreach (var saved in _settings.IntuneTenants)
            {
                IntuneTenants.Add(new IntuneTenantEntry
                {
                    Key                            = saved.Key,
                    Name                           = saved.Name,
                    Comment                        = saved.Comment,
                    ClientID                       = saved.ClientID,
                    AuthFlow                       = saved.AuthFlow,
                    // Keep the DPAPI ciphertext on the entry - do not decrypt
                    // into plaintext on load. The UI shows a "••••••••" placeholder
                    // when HasStoredSecret is true; consumers decrypt just-in-time
                    // via SecretProtection.DecryptToSecureString at the MSAL/PS boundary.
                    ClientSecretCipher             = saved.ClientSecret,
                    CertThumbprint                 = saved.CertThumbprint,
                    Architecture                   = saved.Architecture,
                    MinimumSupportedWindowsRelease = saved.MinimumSupportedWindowsRelease,
                    IntuneWinPath                  = saved.IntuneWinPath,
                    IconFolder                     = saved.IconFolder,
                });
            }
        }
        else
        {
            // No user preferences saved yet - surface the app defaults so the
            // editor is never empty on first-launch.
            foreach (var t in SettingsViewModel.DefaultTenants)
                IntuneTenants.Add(CloneTenant(t));
        }

        SCCMSites.Clear();
        if (_settings.SccmSites.Count > 0)
        {
            foreach (var saved in _settings.SccmSites)
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
                SCCMSites.Add(entry);
            }
        }
        else
        {
            foreach (var s in SettingsViewModel.DefaultSites)
                SCCMSites.Add(CloneSite(s));
        }

        Domains.Clear();
        if (_settings.Domains.Count > 0)
        {
            foreach (var saved in _settings.Domains)
            {
                Domains.Add(new DomainEntry
                {
                    Key        = saved.Key,
                    IsDistPath = saved.IsDistPath,
                    AppFolder  = saved.AppFolder,
                    TagFolder  = saved.TagFolder,
                });
            }
        }
        else
        {
            // Seed from the shipped org defaults (defaults.local.json /
            // defaults.example.json) so the table is never blank on first run.
            foreach (var d in SettingsViewModel.DefaultDomainsExternal)
                Domains.Add(CloneDomain(d));
        }

        // Enterprise policy: entries whose Key is policy-provisioned show a
        // padlock and their row is read-only + non-removable. The engine
        // enforces values regardless - this is the honest UI for it.
        var policy = Services.Policy.PolicyService.Current;
        foreach (var t in IntuneTenants)
            t.IsPolicyManaged = policy.TenantEntries.ContainsKey(t.Key);
        foreach (var s in SCCMSites)
            s.IsPolicyManaged = policy.SiteEntries.ContainsKey(s.Key);
        foreach (var d in Domains)
            d.IsPolicyManaged = policy.DomainEntries.ContainsKey(d.Key);

        // Scalar default sections - seed from ModuleDefaultsSeed on first-ever
        // load (identified by a signature field being at its default state).
        IntunePackage.LoadFromSettings(_settings.IntunePackageDefaults);
        IntuneMetadata.LoadFromSettings(_settings.IntuneMetadataDefaults);
        IntuneAssignment.LoadFromSettings(_settings.IntuneAssignmentDefaults);
        SccmPackage.LoadFromSettings(_settings.SccmPackageDefaults);
        SccmMetadata.LoadFromSettings(_settings.SccmMetadataDefaults);
        SccmDeployment.LoadFromSettings(_settings.SccmDeploymentDefaults);

        // Endpoint script paths (AppSettings property defaults supply the
        // shipped values; empty-on-disk falls back via the ?? chain).
        EndpointTagFolder = string.IsNullOrWhiteSpace(_settings.EndpointTagFolder)
            ? ModuleDefaultsSeed.EndpointTagFolder : _settings.EndpointTagFolder;
        EndpointLocalAppFolder = string.IsNullOrWhiteSpace(_settings.EndpointLocalAppFolder)
            ? ModuleDefaultsSeed.EndpointLocalAppFolder : _settings.EndpointLocalAppFolder;
    }

    private static IntuneTenantEntry CloneTenant(IntuneTenantEntry src) => new()
    {
        Key                            = src.Key,
        Name                           = src.Name,
        Comment                        = src.Comment,
        ClientID                       = src.ClientID,
        AuthFlow                       = src.AuthFlow,
        // Deep-copy the transient SecureString so the
        // clone owns an independent instance -- the save path disposes the
        // source's copy, which would otherwise strand this clone with a
        // disposed SecureString (ObjectDisposedException on the next Length read).
        ClientSecret                   = src.ClientSecret is { Length: > 0 }
                                            ? SecretProtection.ResolveTenantSecret(null, src.ClientSecret)
                                            : null,
        ClientSecretCipher             = src.ClientSecretCipher,
        CertThumbprint                 = src.CertThumbprint,
        Architecture                   = src.Architecture,
        MinimumSupportedWindowsRelease = src.MinimumSupportedWindowsRelease,
        IntuneWinPath                  = src.IntuneWinPath,
        IconFolder                     = src.IconFolder,
    };

    private static SCCMSiteEntry CloneSite(SCCMSiteEntry src)
    {
        var entry = new SCCMSiteEntry
        {
            Key        = src.Key,
            Comment    = src.Comment,
            AppFolder  = src.AppFolder,
            IconFolder = src.IconFolder,
        };
        foreach (var g in src.DeploymentGroups)
            entry.DeploymentGroups.Add(g);
        return entry;
    }

    private static DomainEntry CloneDomain(DomainEntry src) => new()
    {
        Key        = src.Key,
        IsDistPath = src.IsDistPath,
        AppFolder  = src.AppFolder,
        TagFolder  = src.TagFolder,
    };

    // -----------------------------------------------------------------------
    // Bundle -> preferences sync
    //
    // The two tenant lists are separate ON PURPOSE: TenantsViewModel holds the
    // tenants THIS BUNDLE targets (Config.json), while the lists here are the
    // technician's saved catalogue (settings.json). Nothing bridged them, so a
    // tenant discovered at sign-in - or one that arrived with a bundle - never
    // reached the catalogue. These helpers bridge it EXPLICITLY and
    // non-destructively; automatic syncing would pollute the catalogue with
    // one-off tenants.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Adds <paramref name="source"/> to the saved tenants, or fills only the
    /// blank fields of an existing entry with the same key. Never overwrites a
    /// value the technician already set. Returns true when anything changed.
    /// </summary>
    public bool UpsertTenant(IntuneTenantEntry source)
    {
        if (string.IsNullOrWhiteSpace(source.Key)) return false;

        var existing = IntuneTenants.FirstOrDefault(
            t => string.Equals(t.Key, source.Key, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            IntuneTenants.Add(CloneTenant(source));
            return true;
        }

        var changed = false;
        changed |= FillIfBlank(existing.Name, v => existing.Name = v, source.Name);
        changed |= FillIfBlank(existing.Comment, v => existing.Comment = v, source.Comment);
        changed |= FillIfBlank(existing.ClientID, v => existing.ClientID = v, source.ClientID);
        changed |= FillIfBlank(existing.CertThumbprint, v => existing.CertThumbprint = v, source.CertThumbprint);
        changed |= FillIfBlank(existing.Architecture, v => existing.Architecture = v, source.Architecture);
        changed |= FillIfBlank(existing.MinimumSupportedWindowsRelease, v => existing.MinimumSupportedWindowsRelease = v, source.MinimumSupportedWindowsRelease);
        changed |= FillIfBlank(existing.IntuneWinPath, v => existing.IntuneWinPath = v, source.IntuneWinPath);
        changed |= FillIfBlank(existing.IconFolder, v => existing.IconFolder = v, source.IconFolder);
        return changed;
    }

    /// <summary>SCCM counterpart of <see cref="UpsertTenant"/>.</summary>
    public bool UpsertSite(SCCMSiteEntry source)
    {
        if (string.IsNullOrWhiteSpace(source.Key)) return false;

        var existing = SCCMSites.FirstOrDefault(
            s => string.Equals(s.Key, source.Key, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            SCCMSites.Add(CloneSite(source));
            return true;
        }

        var changed = false;
        changed |= FillIfBlank(existing.Comment, v => existing.Comment = v, source.Comment);
        changed |= FillIfBlank(existing.AppFolder, v => existing.AppFolder = v, source.AppFolder);
        changed |= FillIfBlank(existing.IconFolder, v => existing.IconFolder = v, source.IconFolder);
        foreach (var g in source.DeploymentGroups)
        {
            if (existing.DeploymentGroups.Contains(g)) continue;
            existing.DeploymentGroups.Add(g);
            changed = true;
        }
        return changed;
    }

    private static bool FillIfBlank(string current, Action<string> set, string incoming)
    {
        if (!string.IsNullOrWhiteSpace(current)) return false;
        if (string.IsNullOrWhiteSpace(incoming)) return false;
        set(incoming);
        return true;
    }

    // -----------------------------------------------------------------------
    // Commands
    // -----------------------------------------------------------------------

    [RelayCommand]
    private void AddIntuneTenant()
    {
        IntuneTenants.Add(new IntuneTenantEntry
        {
            Key      = "new-tenant",
            Name     = "New tenant",
            AuthFlow = AuthFlow.Interactive,
        });
    }

    /// <summary>
    /// Removes every tenant whose <c>IsSelected</c> checkbox is ticked. Matches
    /// the table/selection convention used in IntuneView / SCCMView.
    /// </summary>
    [RelayCommand]
    private void RemoveSelectedIntuneTenants()
    {
        // Policy-managed rows are disabled (unselectable), but the engine
        // guards regardless: a provisioned entry cannot be removed.
        var toRemove = IntuneTenants.Where(t => t.IsSelected && !t.IsPolicyManaged).ToList();
        foreach (var t in toRemove) IntuneTenants.Remove(t);
    }

    [RelayCommand]
    private void AddSccmSite()
    {
        SCCMSites.Add(new SCCMSiteEntry
        {
            Key     = "NEW",
            Comment = "New site",
        });
    }

    [RelayCommand]
    private void RemoveSelectedSccmSites()
    {
        var toRemove = SCCMSites.Where(s => s.IsSelected && !s.IsPolicyManaged).ToList();
        foreach (var s in toRemove) SCCMSites.Remove(s);
    }

    [RelayCommand]
    private void AddDomain()
    {
        Domains.Add(new DomainEntry
        {
            Key = "NEW.DOMAIN.COM",
        });
    }

    [RelayCommand]
    private void RemoveSelectedDomains()
    {
        var toRemove = Domains.Where(d => d.IsSelected && !d.IsPolicyManaged).ToList();
        foreach (var d in toRemove) Domains.Remove(d);
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        var result = await SettingsService.SavePreferencesAsync(
            _settings, IntuneTenants, SCCMSites,
            domains:          Domains,
            intunePackage:    IntunePackage.Snapshot(),
            intuneMetadata:   IntuneMetadata.Snapshot(),
            intuneAssignment: IntuneAssignment.Snapshot(),
            sccmPackage:      SccmPackage.Snapshot(),
            sccmMetadata:     SccmMetadata.Snapshot(),
            sccmDeployment:   SccmDeployment.Snapshot(),
            endpointTagFolder:      EndpointTagFolder,
            endpointLocalAppFolder: EndpointLocalAppFolder);

        if (!result.Saved)
        {
            await FluentDialog.ShowWarningAsync(
                "Preferences not saved",
                $"Could not persist your preferences to settings.json.\n\n{result.Error}\n\n" +
                "Your current edits remain in the editor; try again or restart the app.");
        }
        // On success, the shared save strip's path bar (SettingsVm.PreferencesBarText)
        // reflects the new clean state automatically via the IsDirty snapshot diff.
    }
}

// =========================================================================
// Observable sub-view-models for each defaults section. Each is a thin
// mirror of its AppSettings POCO with [ObservableProperty] fields so XAML
// bindings are two-way. LoadFromSettings copies from the POCO into these
// observables (seeding from ModuleDefaultsSeed on a first-ever load);
// Snapshot() goes the other way for the save path.
// =========================================================================

public partial class IntunePackageDefaultsVm : ObservableObject
{
    [ObservableProperty] private string _architecture                     = string.Empty;
    [ObservableProperty] private string _minimumSupportedWindowsRelease   = string.Empty;
    [ObservableProperty] private string _installExperience                = string.Empty;
    [ObservableProperty] private string _restartBehavior                  = string.Empty;
    [ObservableProperty] private int    _maximumInstallationTimeInMinutes;
    [ObservableProperty] private bool   _allowAvailableUninstall;
    [ObservableProperty] private bool   _companyPortalFeaturedApp;
    [ObservableProperty] private bool   _useAzCopy;
    [ObservableProperty] private string _azCopyWindowStyle                = string.Empty;
    [ObservableProperty] private UpdateMode _updateMode                   = UpdateMode.Create;

    public void LoadFromSettings(IntunePackageDefaults p)
    {
        // Signature field "Architecture" - empty means the section has never
        // been saved, so populate with seed values. Otherwise trust the POCO.
        if (string.IsNullOrEmpty(p.Architecture))
        {
            Architecture                     = ModuleDefaultsSeed.IntuneArchitecture;
            MinimumSupportedWindowsRelease   = ModuleDefaultsSeed.IntuneMinimumSupportedWindowsRelease;
            InstallExperience                = ModuleDefaultsSeed.IntuneInstallExperience;
            RestartBehavior                  = ModuleDefaultsSeed.IntuneRestartBehavior;
            MaximumInstallationTimeInMinutes = ModuleDefaultsSeed.IntuneMaximumInstallationTimeInMinutes;
            AllowAvailableUninstall          = ModuleDefaultsSeed.IntuneAllowAvailableUninstall;
            CompanyPortalFeaturedApp         = ModuleDefaultsSeed.IntuneCompanyPortalFeaturedApp;
            UseAzCopy                        = ModuleDefaultsSeed.IntuneUseAzCopy;
            AzCopyWindowStyle                = ModuleDefaultsSeed.IntuneAzCopyWindowStyle;
            UpdateMode                       = ModuleDefaultsSeed.IntuneUpdateMode;
            return;
        }

        Architecture                     = p.Architecture;
        MinimumSupportedWindowsRelease   = p.MinimumSupportedWindowsRelease;
        InstallExperience                = p.InstallExperience;
        RestartBehavior                  = p.RestartBehavior;
        MaximumInstallationTimeInMinutes = p.MaximumInstallationTimeInMinutes;
        AllowAvailableUninstall          = p.AllowAvailableUninstall;
        CompanyPortalFeaturedApp         = p.CompanyPortalFeaturedApp;
        UseAzCopy                        = p.UseAzCopy;
        AzCopyWindowStyle                = p.AzCopyWindowStyle;
        UpdateMode                       = p.UpdateMode;
    }

    public IntunePackageDefaults Snapshot() => new()
    {
        Architecture                     = Architecture,
        MinimumSupportedWindowsRelease   = MinimumSupportedWindowsRelease,
        InstallExperience                = InstallExperience,
        RestartBehavior                  = RestartBehavior,
        MaximumInstallationTimeInMinutes = MaximumInstallationTimeInMinutes,
        AllowAvailableUninstall          = AllowAvailableUninstall,
        CompanyPortalFeaturedApp         = CompanyPortalFeaturedApp,
        UseAzCopy                        = UseAzCopy,
        AzCopyWindowStyle                = AzCopyWindowStyle,
        UpdateMode                       = UpdateMode,
    };
}

public partial class IntuneMetadataDefaultsVm : ObservableObject
{
    [ObservableProperty] private string _appNameTemplate   = string.Empty;
    [ObservableProperty] private string _developerTemplate = string.Empty;
    [ObservableProperty] private string _ownerTemplate     = string.Empty;
    [ObservableProperty] private string _commentTemplate   = string.Empty;
    [ObservableProperty] private string _informationURL    = string.Empty;
    [ObservableProperty] private string _privacyURL        = string.Empty;
    [ObservableProperty] private string _notesTemplate     = string.Empty;

    public void LoadFromSettings(IntuneMetadataDefaults p)
    {
        // All-empty state = first-ever load - seed from defaults.
        var unseeded =
            string.IsNullOrEmpty(p.AppNameTemplate) &&
            string.IsNullOrEmpty(p.DeveloperTemplate) &&
            string.IsNullOrEmpty(p.OwnerTemplate) &&
            string.IsNullOrEmpty(p.CommentTemplate) &&
            string.IsNullOrEmpty(p.InformationURL) &&
            string.IsNullOrEmpty(p.PrivacyURL);

        if (unseeded)
        {
            AppNameTemplate   = ModuleDefaultsSeed.IntuneAppNameTemplate;
            DeveloperTemplate = ModuleDefaultsSeed.IntuneDeveloperTemplate;
            OwnerTemplate     = ModuleDefaultsSeed.IntuneOwnerTemplate;
            CommentTemplate   = ModuleDefaultsSeed.IntuneCommentTemplate;
            InformationURL    = ModuleDefaultsSeed.IntuneInformationURL;
            PrivacyURL        = ModuleDefaultsSeed.IntunePrivacyURL;
            return;
        }

        AppNameTemplate   = p.AppNameTemplate;
        DeveloperTemplate = p.DeveloperTemplate;
        OwnerTemplate     = p.OwnerTemplate;
        CommentTemplate   = p.CommentTemplate;
        InformationURL    = p.InformationURL;
        PrivacyURL        = p.PrivacyURL;
        NotesTemplate     = p.NotesTemplate;
    }

    public IntuneMetadataDefaults Snapshot() => new()
    {
        AppNameTemplate   = AppNameTemplate,
        DeveloperTemplate = DeveloperTemplate,
        OwnerTemplate     = OwnerTemplate,
        CommentTemplate   = CommentTemplate,
        InformationURL    = InformationURL,
        PrivacyURL        = PrivacyURL,
        NotesTemplate     = NotesTemplate,
    };
}

public partial class IntuneAssignmentDefaultsVm : ObservableObject
{
    [ObservableProperty] private string _intent                       = string.Empty;
    [ObservableProperty] private string _type                         = string.Empty;
    [ObservableProperty] private string _groupMode                    = string.Empty;
    [ObservableProperty] private string _notification                 = string.Empty;
    [ObservableProperty] private string _deliveryOptimizationPriority = string.Empty;
    [ObservableProperty] private string _availableTime                = string.Empty;
    [ObservableProperty] private string _deadlineTime                 = string.Empty;
    [ObservableProperty] private string _filterMode                   = string.Empty;
    [ObservableProperty] private bool   _enableRestartGracePeriod;
    [ObservableProperty] private int    _restartGracePeriodInMinutes;
    [ObservableProperty] private int    _restartCountDownDisplayInMinutes;
    [ObservableProperty] private int    _restartNotificationSnoozeInMinutes;

    /// <summary>
    /// Reuses the per-assignment Intent/Type/GroupMode rule sets, plus the
    /// EnableRestartGracePeriod cascade. The FilterMode→FilterName cascade is
    /// intentionally omitted because the defaults VM has no FilterName field.
    /// </summary>
    public static readonly FieldRule[] Rules =
        FieldDependencyRules.IntuneAssignmentByIntent
            .Concat(FieldDependencyRules.IntuneAssignmentByType)
            .Concat(FieldDependencyRules.IntuneAssignmentByGroupMode)
            .Concat(new[]
            {
                new FieldRule("RestartGracePeriodInMinutes", "EnableRestartGracePeriod",
                    ["False"], FieldDependencyRules.RestartFieldsRequireGracePeriod),
                new FieldRule("RestartCountDownDisplayInMinutes", "EnableRestartGracePeriod",
                    ["False"], FieldDependencyRules.RestartFieldsRequireGracePeriod),
                new FieldRule("RestartNotificationSnoozeInMinutes", "EnableRestartGracePeriod",
                    ["False"], FieldDependencyRules.RestartFieldsRequireGracePeriod),
            })
            .ToArray();

    private readonly FieldStateProvider _fieldStateProvider = new();

    public FieldStateAccessor FieldStates => _fieldStateProvider.FieldStates;

    public IntuneAssignmentDefaultsVm()
    {
        _fieldStateProvider.Bind(this, Rules);
    }

    public void LoadFromSettings(IntuneAssignmentDefaults p)
    {
        if (string.IsNullOrEmpty(p.Intent))
        {
            Intent                             = ModuleDefaultsSeed.IntuneAssignmentIntent;
            Type                               = ModuleDefaultsSeed.IntuneAssignmentType;
            GroupMode                          = ModuleDefaultsSeed.IntuneAssignmentGroupMode;
            Notification                       = ModuleDefaultsSeed.IntuneAssignmentNotification;
            DeliveryOptimizationPriority       = ModuleDefaultsSeed.IntuneAssignmentDeliveryOptimizationPriority;
            AvailableTime                      = ModuleDefaultsSeed.IntuneAssignmentAvailableTime;
            DeadlineTime                       = ModuleDefaultsSeed.IntuneAssignmentDeadlineTime;
            FilterMode                         = ModuleDefaultsSeed.IntuneAssignmentFilterMode;
            EnableRestartGracePeriod           = false;
            RestartGracePeriodInMinutes        = ModuleDefaultsSeed.IntuneAssignmentRestartGracePeriodInMinutes;
            RestartCountDownDisplayInMinutes   = ModuleDefaultsSeed.IntuneAssignmentRestartCountDownDisplayInMinutes;
            RestartNotificationSnoozeInMinutes = ModuleDefaultsSeed.IntuneAssignmentRestartNotificationSnoozeInMinutes;
            return;
        }

        Intent                             = p.Intent;
        Type                               = p.Type;
        GroupMode                          = p.GroupMode;
        Notification                       = p.Notification;
        DeliveryOptimizationPriority       = p.DeliveryOptimizationPriority;
        AvailableTime                      = p.AvailableTime;
        DeadlineTime                       = p.DeadlineTime;
        FilterMode                         = p.FilterMode;
        EnableRestartGracePeriod           = p.EnableRestartGracePeriod;
        // Backfill the new restart defaults from ModuleDefaultsSeed when the
        // persisted settings file predates this release (zero = unpersisted).
        RestartGracePeriodInMinutes        = p.RestartGracePeriodInMinutes > 0
            ? p.RestartGracePeriodInMinutes
            : ModuleDefaultsSeed.IntuneAssignmentRestartGracePeriodInMinutes;
        RestartCountDownDisplayInMinutes   = p.RestartCountDownDisplayInMinutes > 0
            ? p.RestartCountDownDisplayInMinutes
            : ModuleDefaultsSeed.IntuneAssignmentRestartCountDownDisplayInMinutes;
        RestartNotificationSnoozeInMinutes = p.RestartNotificationSnoozeInMinutes > 0
            ? p.RestartNotificationSnoozeInMinutes
            : ModuleDefaultsSeed.IntuneAssignmentRestartNotificationSnoozeInMinutes;
    }

    public IntuneAssignmentDefaults Snapshot() => new()
    {
        Intent                             = Intent,
        Type                               = Type,
        GroupMode                          = GroupMode,
        Notification                       = Notification,
        DeliveryOptimizationPriority       = DeliveryOptimizationPriority,
        AvailableTime                      = AvailableTime,
        DeadlineTime                       = DeadlineTime,
        FilterMode                         = FilterMode,
        EnableRestartGracePeriod           = EnableRestartGracePeriod,
        RestartGracePeriodInMinutes        = RestartGracePeriodInMinutes,
        RestartCountDownDisplayInMinutes   = RestartCountDownDisplayInMinutes,
        RestartNotificationSnoozeInMinutes = RestartNotificationSnoozeInMinutes,
    };
}

public partial class SccmPackageDefaultsVm : ObservableObject
{
    [ObservableProperty] private string _installationBehaviorType  = string.Empty;
    [ObservableProperty] private string _logonRequirementType      = string.Empty;
    [ObservableProperty] private string _userInteractionMode       = string.Empty;
    [ObservableProperty] private string _rebootBehavior            = string.Empty;
    [ObservableProperty] private string _slowNetworkDeploymentMode = string.Empty;
    [ObservableProperty] private int    _estimatedRuntimeMins;
    [ObservableProperty] private int    _maximumAllowedRuntimeMins;
    [ObservableProperty] private bool   _installBehavior;
    [ObservableProperty] private bool   _contentFallback;

    public static readonly FieldRule[] Rules = FieldDependencyRules.SCCMPackageRules;

    private readonly FieldStateProvider _fieldStateProvider = new();

    public FieldStateAccessor FieldStates => _fieldStateProvider.FieldStates;

    public SccmPackageDefaultsVm()
    {
        _fieldStateProvider.Bind(this, Rules);
    }

    public void LoadFromSettings(SccmPackageDefaults p)
    {
        if (string.IsNullOrEmpty(p.InstallationBehaviorType))
        {
            InstallationBehaviorType  = ModuleDefaultsSeed.SccmInstallationBehaviorType;
            LogonRequirementType      = ModuleDefaultsSeed.SccmLogonRequirementType;
            UserInteractionMode       = ModuleDefaultsSeed.SccmUserInteractionMode;
            RebootBehavior            = ModuleDefaultsSeed.SccmRebootBehavior;
            SlowNetworkDeploymentMode = ModuleDefaultsSeed.SccmSlowNetworkDeploymentMode;
            EstimatedRuntimeMins      = ModuleDefaultsSeed.SccmEstimatedRuntimeMins;
            MaximumAllowedRuntimeMins = ModuleDefaultsSeed.SccmMaximumAllowedRuntimeMins;
            InstallBehavior           = ModuleDefaultsSeed.SccmInstallBehavior;
            ContentFallback           = ModuleDefaultsSeed.SccmContentFallback;
            return;
        }

        InstallationBehaviorType  = p.InstallationBehaviorType;
        LogonRequirementType      = p.LogonRequirementType;
        UserInteractionMode       = p.UserInteractionMode;
        RebootBehavior            = p.RebootBehavior;
        SlowNetworkDeploymentMode = p.SlowNetworkDeploymentMode;
        EstimatedRuntimeMins      = p.EstimatedRuntimeMins;
        MaximumAllowedRuntimeMins = p.MaximumAllowedRuntimeMins;
        InstallBehavior           = p.InstallBehavior;
        ContentFallback           = p.ContentFallback;
    }

    public SccmPackageDefaults Snapshot() => new()
    {
        InstallationBehaviorType  = InstallationBehaviorType,
        LogonRequirementType      = LogonRequirementType,
        UserInteractionMode       = UserInteractionMode,
        RebootBehavior            = RebootBehavior,
        SlowNetworkDeploymentMode = SlowNetworkDeploymentMode,
        EstimatedRuntimeMins      = EstimatedRuntimeMins,
        MaximumAllowedRuntimeMins = MaximumAllowedRuntimeMins,
        InstallBehavior           = InstallBehavior,
        ContentFallback           = ContentFallback,
    };
}

public partial class SccmMetadataDefaultsVm : ObservableObject
{
    [ObservableProperty] private string _publisherTemplate            = string.Empty;
    [ObservableProperty] private string _ownerTemplate                = string.Empty;
    [ObservableProperty] private string _supportContactTemplate       = string.Empty;
    [ObservableProperty] private string _descriptionTemplate          = string.Empty;
    [ObservableProperty] private string _commentTemplate              = string.Empty;
    [ObservableProperty] private string _nameTemplate                 = string.Empty;
    [ObservableProperty] private string _localizedNameTemplate        = string.Empty;
    [ObservableProperty] private string _localizedDescriptionTemplate = string.Empty;
    [ObservableProperty] private string _keywordsTemplate             = string.Empty;
    [ObservableProperty] private string _privacyUrl                   = string.Empty;
    [ObservableProperty] private string _userDocumentation            = string.Empty;
    [ObservableProperty] private string _linkText                     = string.Empty;
    [ObservableProperty] private string _releaseDateTemplate          = string.Empty;
    [ObservableProperty] private bool   _isFeatured;
    [ObservableProperty] private bool   _autoInstall;

    public void LoadFromSettings(SccmMetadataDefaults p)
    {
        var unseeded =
            string.IsNullOrEmpty(p.PublisherTemplate) &&
            string.IsNullOrEmpty(p.OwnerTemplate) &&
            string.IsNullOrEmpty(p.DescriptionTemplate) &&
            string.IsNullOrEmpty(p.NameTemplate);

        if (unseeded)
        {
            PublisherTemplate            = ModuleDefaultsSeed.SccmPublisherTemplate;
            OwnerTemplate                = ModuleDefaultsSeed.SccmOwnerTemplate;
            SupportContactTemplate       = ModuleDefaultsSeed.SccmSupportContactTemplate;
            DescriptionTemplate          = ModuleDefaultsSeed.SccmDescriptionTemplate;
            CommentTemplate              = ModuleDefaultsSeed.SccmCommentTemplate;
            NameTemplate                 = ModuleDefaultsSeed.SccmNameTemplate;
            LocalizedNameTemplate        = ModuleDefaultsSeed.SccmLocalizedNameTemplate;
            LocalizedDescriptionTemplate = ModuleDefaultsSeed.SccmLocalizedDescriptionTemplate;
            KeywordsTemplate             = ModuleDefaultsSeed.SccmKeywordsTemplate;
            PrivacyUrl                   = ModuleDefaultsSeed.SccmPrivacyUrl;
            UserDocumentation            = ModuleDefaultsSeed.SccmUserDocumentation;
            LinkText                     = ModuleDefaultsSeed.SccmLinkText;
            ReleaseDateTemplate          = ModuleDefaultsSeed.SccmReleaseDateTemplate;
            IsFeatured                   = ModuleDefaultsSeed.SccmIsFeatured;
            AutoInstall                  = ModuleDefaultsSeed.SccmAutoInstall;
            return;
        }

        PublisherTemplate            = p.PublisherTemplate;
        OwnerTemplate                = p.OwnerTemplate;
        SupportContactTemplate       = p.SupportContactTemplate;
        DescriptionTemplate          = p.DescriptionTemplate;
        CommentTemplate              = p.CommentTemplate;
        NameTemplate                 = p.NameTemplate;
        LocalizedNameTemplate        = p.LocalizedNameTemplate;
        LocalizedDescriptionTemplate = p.LocalizedDescriptionTemplate;
        KeywordsTemplate             = p.KeywordsTemplate;
        PrivacyUrl                   = p.PrivacyUrl;
        UserDocumentation            = p.UserDocumentation;
        LinkText                     = p.LinkText;
        ReleaseDateTemplate          = p.ReleaseDateTemplate;
        IsFeatured                   = p.IsFeatured;
        AutoInstall                  = p.AutoInstall;
    }

    public SccmMetadataDefaults Snapshot() => new()
    {
        PublisherTemplate            = PublisherTemplate,
        OwnerTemplate                = OwnerTemplate,
        SupportContactTemplate       = SupportContactTemplate,
        DescriptionTemplate          = DescriptionTemplate,
        CommentTemplate              = CommentTemplate,
        NameTemplate                 = NameTemplate,
        LocalizedNameTemplate        = LocalizedNameTemplate,
        LocalizedDescriptionTemplate = LocalizedDescriptionTemplate,
        KeywordsTemplate             = KeywordsTemplate,
        PrivacyUrl                   = PrivacyUrl,
        UserDocumentation            = UserDocumentation,
        LinkText                     = LinkText,
        ReleaseDateTemplate          = ReleaseDateTemplate,
        IsFeatured                   = IsFeatured,
        AutoInstall                  = AutoInstall,
    };
}

public partial class SccmDeploymentDefaultsVm : ObservableObject
{
    [ObservableProperty] private string _deployAction      = string.Empty;
    [ObservableProperty] private string _deployPurpose     = string.Empty;
    [ObservableProperty] private string _userNotification  = string.Empty;
    [ObservableProperty] private string _timeBaseOn        = string.Empty;
    [ObservableProperty] private string _availableDateTime = string.Empty;
    [ObservableProperty] private string _deadlineDateTime  = string.Empty;

    public void LoadFromSettings(SccmDeploymentDefaults p)
    {
        if (string.IsNullOrEmpty(p.DeployAction))
        {
            DeployAction      = ModuleDefaultsSeed.SccmDeployAction;
            DeployPurpose     = ModuleDefaultsSeed.SccmDeployPurpose;
            UserNotification  = ModuleDefaultsSeed.SccmUserNotification;
            TimeBaseOn        = ModuleDefaultsSeed.SccmTimeBaseOn;
            AvailableDateTime = ModuleDefaultsSeed.SccmAvailableDateTime;
            DeadlineDateTime  = ModuleDefaultsSeed.SccmDeadlineDateTime;
            return;
        }

        DeployAction      = p.DeployAction;
        DeployPurpose     = p.DeployPurpose;
        UserNotification  = p.UserNotification;
        TimeBaseOn        = p.TimeBaseOn;
        // Backfill the new scheduling defaults from the seed when the persisted
        // settings file predates this release (empty = unpersisted).
        AvailableDateTime = string.IsNullOrEmpty(p.AvailableDateTime)
            ? ModuleDefaultsSeed.SccmAvailableDateTime
            : p.AvailableDateTime;
        DeadlineDateTime  = p.DeadlineDateTime;
    }

    public SccmDeploymentDefaults Snapshot() => new()
    {
        DeployAction      = DeployAction,
        DeployPurpose     = DeployPurpose,
        UserNotification  = UserNotification,
        TimeBaseOn        = TimeBaseOn,
        AvailableDateTime = AvailableDateTime,
        DeadlineDateTime  = DeadlineDateTime,
    };
}
