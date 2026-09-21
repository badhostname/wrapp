using System.Linq;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectoryPreview))]
    private string _directoryFormat;

    [ObservableProperty]
    private string _iconFolderName;

    [ObservableProperty]
    private string _psadtTemplatePath;

    [ObservableProperty]
    private string _selectedTheme;

    [ObservableProperty]
    private string _keyVaultRepoUrl;

    /// <summary>
    /// Opt-in flag for the Azure DevOps key vault feature gate. When unchecked,
    /// every vault touchpoint short-circuits via <see cref="Services.IFeatureGate"/>.
    /// </summary>
    [ObservableProperty]
    private bool _enableAzureDevOpsKeyVault;

    /// <summary>
    /// Single-brace path template for auto-captured vault keys. Tokens documented
    /// in <see cref="Services.VaultPathTemplate.SupportedTokens"/>; previewed live
    /// via <see cref="KeyVaultPathPreview"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyVaultPathPreview))]
    private string _keyVaultPathTemplate;

    /// <summary>
    /// Single-brace path template for manually-saved vault keys (no Tenant / AppId
    /// available). Defaults to <c>/manual/{PackageName}.json</c> to preserve the legacy layout.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyVaultManualPathPreview))]
    private string _keyVaultManualPathTemplate;

    /// <summary>
    /// PR-mode opt-in: vault pushes create a feature branch + Pull Request instead
    /// of committing directly to main. The PR template fields only appear in the UI
    /// when this flag is checked.
    /// </summary>
    [ObservableProperty]
    private bool _keyVaultUsePullRequests;

    /// <summary>
    /// PR source branch template. Same tokens as <see cref="KeyVaultPathTemplate"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyVaultPrSourceBranchPreview))]
    private string _keyVaultPrSourceBranchTemplate;

    /// <summary>
    /// PR title template. Same tokens as <see cref="KeyVaultPathTemplate"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyVaultPrTitlePreview))]
    private string _keyVaultPrTitleTemplate;

    /// <summary>
    /// PR description template. Same tokens as <see cref="KeyVaultPathTemplate"/>.
    /// Multi-line; newlines survive the resolve.
    /// </summary>
    [ObservableProperty]
    private string _keyVaultPrDescriptionTemplate;

    /// <summary>
    /// Update feed (HTTPS URL or UNC share). Approval on Save follows the same
    /// TOFU pattern as the Key Vault URL; the manual check runs against the
    /// SAVED value, not unsaved edits.
    /// </summary>
    [ObservableProperty]
    private string _updateFeedUrl;

    /// <summary>"Auto" | "NotifyOnly" | "Disabled".</summary>
    [ObservableProperty]
    private string _updateMode;

    /// <summary>Status line under the "Check for updates" button.</summary>
    [ObservableProperty]
    private string _updateCheckStatus = string.Empty;

    /// <summary>Diagnostics: opt-in [TRACE] logging of UI interactions
    /// (navigation, clicks, dialogs, view builds). Applied live on save.</summary>
    [ObservableProperty]
    private bool _verboseUiTrace;

    /// <summary>ComboBox source for <see cref="UpdateMode"/>.</summary>
    public string[] UpdateModes { get; } = AppUpdateModes.All;

    /// <summary>Version line shown in the Updates section.</summary>
    public string CurrentAppVersion => AppInfo.NameAndVersion;

    /// <summary>Live preview of the resolved PR source branch.</summary>
    public string KeyVaultPrSourceBranchPreview => Services.VaultPathTemplate.Resolve(
        KeyVaultPrSourceBranchTemplate, PreviewKeysFixture, author: "preview-author");

    /// <summary>Live preview of the resolved PR title.</summary>
    public string KeyVaultPrTitlePreview => Services.VaultPathTemplate.Resolve(
        KeyVaultPrTitleTemplate, PreviewKeysFixture, author: "preview-author");

    /// <summary>
    /// Live preview of <see cref="KeyVaultPathTemplate"/> resolved against fixture
    /// data; re-renders whenever the template field changes.
    /// </summary>
    public string KeyVaultPathPreview => Services.VaultPathTemplate.Resolve(
        KeyVaultPathTemplate, PreviewKeysFixture, author: "preview-author");

    /// <summary>
    /// Live preview of <see cref="KeyVaultManualPathTemplate"/>, with empty
    /// Tenant / AppId so the manual case shows the path the user actually gets.
    /// </summary>
    public string KeyVaultManualPathPreview => Services.VaultPathTemplate.Resolve(
        KeyVaultManualPathTemplate, PreviewManualKeysFixture, author: "preview-author");

    // Stable fixture so the live preview is deterministic. Values like
    // "example-tenant" deliberately read as placeholders, not real GUIDs.
    private static readonly EncryptionKeyInfo PreviewKeysFixture = new()
    {
        TenantId    = "example-tenant",
        AppId       = "example-app-id",
        DisplayName = "Example App",
        PackageName = "ExampleApp_1_0_0",
    };

    private static readonly EncryptionKeyInfo PreviewManualKeysFixture = new()
    {
        DisplayName = "Example App",
        PackageName = "ExampleApp_1_0_0",
    };

    /// <summary>
    /// True when in-memory Settings differ from the baseline snapshot taken at
    /// load/save. Diff-based like <see cref="GeneralViewModel.CheckForChanges"/>,
    /// so reverting an edit clears IsDirty automatically with no explicit reset path.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreferencesBarText))]
    private bool _isDirty;

    /// <summary>
    /// Path-bar text for the Settings save strip. Mirrors the
    /// <see cref="MainViewModel.StatusBarPath"/> format exactly: raw path plus
    /// trailing " *" when there are unsaved changes.
    /// </summary>
    public string PreferencesBarText
        => IsDirty
            ? $"{PlatformConfig.SettingsPath} *"
            : PlatformConfig.SettingsPath;

    /// <summary>
    /// Raw path to settings.json (no dirty indicator) for the path row at the
    /// top of <c>SettingsView</c>; distinct from <see cref="PreferencesBarText"/>.
    /// </summary>
    public string SettingsPath => PlatformConfig.SettingsPath;

    [RelayCommand]
    private void OpenSettingsFolder()
    {
        var dir = System.IO.Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
    }

    [ObservableProperty]
    private string _devOpsAuthStatus = "Not signed in";

    /// <summary>Dark, Light, plus every valid imported/org custom theme.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> ThemeOptions { get; } =
        new(Services.ThemeService.Available().Select(t => t.Name));

    /// <summary>Re-reads the theme list (after an import).</summary>
    public void RefreshThemeOptions()
    {
        var names = Services.ThemeService.Available().Select(t => t.Name).ToList();
        ThemeOptions.Clear();
        foreach (var n in names) ThemeOptions.Add(n);
    }

    /// <summary>Imports a .wrapptheme.json into the user theme folder and selects it.</summary>
    [RelayCommand]
    private async Task ImportThemeAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = $"Wrapp themes (*{Services.ThemeService.FileExtension})|*{Services.ThemeService.FileExtension}|All files|*.*",
            Title = "Import Wrapp theme",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var imported = Services.ThemeService.Import(dialog.FileName);
            RefreshThemeOptions();
            SelectedTheme = imported.Name;   // applies live via OnSelectedThemeChanged
            AppLogger.Info($"Theme: \"{imported.Name}\" imported and applied");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Theme import failed: {ex.Message}");
            await Services.FluentDialog.ShowWarningAsync("Import theme", ex.Message);
        }
    }

    // -----------------------------------------------------------------------
    // Enterprise policy surface (lock / hide bindings + transparency card)
    // -----------------------------------------------------------------------

    /// <summary>Per-setting lock/hide state: <c>{Binding Policy[UpdateMode].IsEditable}</c>.</summary>
    public Services.Policy.PolicyUiStateAccessor Policy { get; } = new();

    /// <summary>Settings-tab visibility: <c>{Binding PolicyTabs[KeyVault]}</c>.</summary>
    public Services.Policy.PolicyTabAccessor PolicyTabs { get; } = new();

    /// <summary>Tab-header padlock when any policy touches the tab's content.</summary>
    public Services.Policy.PolicyTabLockAccessor PolicyTabLocks { get; } = new();

    /// <summary>True when any policy is active - shows the managed banner.</summary>
    public bool AnyPolicyManaged => Services.Policy.PolicyService.Current.AnyManaged;

    /// <summary>Provisioning-card visibility under the two Disable*Import policies.</summary>
    public System.Windows.Visibility OrgDefaultsImportCardVisibility =>
        Services.Policy.PolicyService.Current.DisableOrgDefaultsImport
            ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public System.Windows.Visibility SettingsImportCardVisibility =>
        Services.Policy.PolicyService.Current.DisableSettingsImport
            ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    /// <summary>Rows for the effective-policy table in the Provisioning tab.</summary>
    public IReadOnlyList<Services.Policy.ManagedPolicyRow> ManagedPolicyRows
    {
        get
        {
            var snap = Services.Policy.PolicyService.Current;
            var rows = snap.Mandatory
                .Select(kv => new Services.Policy.ManagedPolicyRow(
                    kv.Key, kv.Value.ToString() ?? string.Empty,
                    snap.SourceByKey.GetValueOrDefault(kv.Key, "Policy")))
                .OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var s in snap.HiddenSections)
                rows.Add(new($"HiddenSections.{s}", "hidden", "Policy"));
            foreach (var t in snap.HiddenSettingsTabs)
                rows.Add(new($"HiddenSettingsTabs.{t}", "hidden", "Policy"));
            if (snap.OrgDefaultsPath is { } p) rows.Add(new("OrgDefaultsPath", p, "Policy"));
            if (snap.ThemeFilePath is { } tp) rows.Add(new("ThemeFilePath", tp, "Policy"));
            if (snap.DisableSettingsImport) rows.Add(new("DisableSettingsImport", "true", "Policy"));
            if (snap.DisableOrgDefaultsImport) rows.Add(new("DisableOrgDefaultsImport", "true", "Policy"));
            foreach (var t in snap.TenantEntries.Keys.OrderBy(x => x))
                rows.Add(new($"IntuneTenants\\{t}", "provisioned entry", "Policy"));
            foreach (var s in snap.SiteEntries.Keys.OrderBy(x => x))
                rows.Add(new($"SccmSites\\{s}", "provisioned entry", "Policy"));
            foreach (var d in snap.DomainEntries.Keys.OrderBy(x => x))
                rows.Add(new($"Domains\\{d}", "provisioned entry", "Policy"));
            foreach (var ph in snap.Placeholders.Keys.OrderBy(x => x))
                rows.Add(new($"Placeholders.{ph}", "provisioned", "Policy"));
            if (snap.RedactionPatterns.Count > 0)
                rows.Add(new("RedactionPatterns", $"{snap.RedactionPatterns.Count} pattern(s)", "Policy"));
            return rows;
        }
    }

    /// <summary>Tenants/sites management exposed for the Settings view.</summary>
    public TenantsViewModel Tenants { get; }

    private Services.DevOpsAuthService? _devOpsAuth;
    private Services.IFeatureGate? _featureGate;

    partial void OnSelectedThemeChanged(string value)
    {
        // Apply the theme immediately for visual feedback; persistence is
        // deferred to the shared Save Settings button - the snapshot-diff
        // timer picks up the change automatically.
        AppLogger.Info($"Theme changed to: {value}");
        App.ApplyTheme(value);
    }

    public string DirectoryPreview
    {
        get
        {
            var fmt = string.IsNullOrWhiteSpace(DirectoryFormat)
                ? @"{Company}\{Name}\{Version}"
                : DirectoryFormat;

            return fmt
                .Replace("{Company}",    "Contoso")
                .Replace("{Name}",       "MyApp")
                .Replace("{Version}",    "1_0_0")
                .Replace("{DotVersion}", "1.0.0")
                .Replace("{Language}",   "EN");
        }
    }

    /// <summary>
    /// User-curated preferences (settings.json layer) editor. Distinct from
    /// <see cref="Tenants"/> (which is the live bundle / AppConfigModel view).
    /// Edited via the Preferences expanders in SettingsView.
    /// </summary>
    public PreferencesViewModel Preferences { get; }

    /// <summary>
    /// The Placeholders tab - live built-in values, custom placeholder editing,
    /// effective-configuration viewers and the log-redaction summary.
    /// Persists through <see cref="SaveAsync"/>.
    /// </summary>
    public PlaceholdersViewModel Placeholders { get; }

    /// <summary>
    /// Pass-through of <see cref="PlaceholdersViewModel.ErrorCount"/> for the
    /// Settings nav badge via <see cref="MainViewModel.SettingsErrorCount"/>.
    /// Change notification is wired in the constructor via <see cref="PropertyRelay"/>.
    /// </summary>
    public int PlaceholderErrorCount => Placeholders.ErrorCount;

    // Baseline snapshot of the scalar + preferences state - what's on disk.
    // IsDirty is true whenever SerializeCurrentState() != _diskSnapshot.
    private string _diskSnapshot = string.Empty;
    private readonly DispatcherTimer _changeTimer;

    public SettingsViewModel(AppSettings settings, TenantsViewModel tenantsVm)
    {
        _settings = settings;
        Tenants  = tenantsVm;
        _directoryFormat   = settings.DirectoryFormat;
        _iconFolderName    = settings.IconFolderName;
        _psadtTemplatePath = settings.PsadtTemplatePath;
        _selectedTheme     = settings.Theme ?? "Dark";
        _keyVaultRepoUrl   = settings.KeyVaultRepoUrl;
        _enableAzureDevOpsKeyVault = settings.EnableAzureDevOpsKeyVault;
        _keyVaultPathTemplate = settings.KeyVaultPathTemplate;
        _keyVaultManualPathTemplate = settings.KeyVaultManualPathTemplate;
        _keyVaultUsePullRequests = settings.KeyVaultUsePullRequests;
        _keyVaultPrSourceBranchTemplate = settings.KeyVaultPrSourceBranchTemplate;
        _keyVaultPrTitleTemplate = settings.KeyVaultPrTitleTemplate;
        _keyVaultPrDescriptionTemplate = settings.KeyVaultPrDescriptionTemplate;
        _updateFeedUrl = settings.UpdateFeedUrl;
        _updateMode = settings.UpdateMode;
        Preferences        = new PreferencesViewModel(settings);
        Placeholders       = new PlaceholdersViewModel(settings);

        PropertyRelay.Wire(Placeholders, OnPropertyChanged,
            PropertyRelay.When(nameof(PlaceholdersViewModel.ErrorCount),
                nameof(PlaceholderErrorCount)));

        TakeSnapshot();

        _changeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _changeTimer.Tick += (_, _) => CheckForChanges();
        // This tick serializes the full preferences + placeholders state (three
        // JSON passes) - heavy, and its inputs only change while the Settings
        // view is on screen. The view gates it via SetChangeTrackingActive;
        // programmatic mutation paths call CheckForChanges() directly.
    }

    /// <summary>
    /// Called by SettingsView on visibility changes: the dirty-check timer
    /// runs only while the view is visible. Both transitions run one
    /// immediate check so the dirty state is exact at the boundary (the
    /// hide-side check is what CloseGuard's IsDirty relies on afterwards).
    /// </summary>
    public void SetChangeTrackingActive(bool active)
    {
        CheckForChanges();
        if (active) _changeTimer.Start();
        else _changeTimer.Stop();
    }

    /// <summary>
    /// Serializes the current in-memory Settings state (scalars + Preferences
    /// snapshot) to a stable JSON string for diff comparison. Does not write
    /// to disk and does not call DPAPI - ClientSecret is kept plaintext so
    /// the diff is deterministic across ticks.
    /// </summary>
    private string SerializeCurrentState()
    {
        var shape = new
        {
            DirectoryFormat,
            IconFolderName,
            PsadtTemplatePath,
            SelectedTheme,
            KeyVaultRepoUrl,
            EnableAzureDevOpsKeyVault,
            KeyVaultPathTemplate,
            KeyVaultManualPathTemplate,
            KeyVaultUsePullRequests,
            KeyVaultPrSourceBranchTemplate,
            KeyVaultPrTitleTemplate,
            KeyVaultPrDescriptionTemplate,
            UpdateFeedUrl,
            UpdateMode,
            VerboseUiTrace,
            Preferences = Preferences.SerializeSnapshot(),
            Placeholders = Placeholders.SerializeSnapshot(),
        };
        return JsonSerializer.Serialize(shape);
    }

    /// <summary>
    /// Captures the current serialized state as the "what's on disk" baseline.
    /// Called after ctor (initial load) and after every successful save.
    /// </summary>
    private void TakeSnapshot()
    {
        try { _diskSnapshot = SerializeCurrentState(); }
        catch { _diskSnapshot = string.Empty; }
        IsDirty = false;
    }

    private void CheckForChanges()
    {
        try
        {
            var current = SerializeCurrentState();
            IsDirty = !string.IsNullOrEmpty(_diskSnapshot) && current != _diskSnapshot;
        }
        catch (Exception ex)
        {
            // Same latch as GeneralViewModel - a persistent fault here
            // silently freezes the settings dirty flag CloseGuard relies on.
            if (!_dirtyCheckFaultLogged)
            {
                _dirtyCheckFaultLogged = true;
                AppLogger.Warn($"Settings: dirty-check serialization failed (logged once per session) -- {ex.Message}");
            }
        }
    }

    private bool _dirtyCheckFaultLogged;

    /// <summary>
    /// Restores persisted tenants/sites from settings into the config model.
    /// When the config already has tenants (e.g. loaded from an old Config.json),
    /// merges settings data into matching entries to fill missing fields like
    /// Name, ClientID, AuthFlow, Architecture, etc.
    /// </summary>
    private bool _restoring;

    public void RestoreSavedTenants()
    {
        if (_restoring)
        {
            AppLogger.Info("SettingsViewModel.RestoreSavedTenants: reentrant call ignored");
            return;
        }
        _restoring = true;
        try
        {
            RestoreSavedTenantsCore();
        }
        finally
        {
            _restoring = false;
        }
    }

    private void RestoreSavedTenantsCore()
    {
        MigrateSettingsTenantFormat();

        if (_settings.IntuneTenants.Count > 0)
        {
            if (Tenants.IntuneTenants.Count == 0)
            {
                // No tenants from config -- restore entirely from settings.
                // Keep the DPAPI cipher on the entry; never eagerly decrypt into
                // plaintext on load. MSAL/PS consumers decrypt via
                // SecretProtection.DecryptToSecureString at the call site.
                foreach (var saved in _settings.IntuneTenants)
                {
                    Tenants.IntuneTenants.Add(new IntuneTenantEntry
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
                AppLogger.Info($"Restored {_settings.IntuneTenants.Count} Intune tenant(s) from settings");
            }
            else
            {
                EnrichTenantsFromSettings();
            }
        }

        if (_settings.SccmSites.Count > 0)
        {
            if (Tenants.SCCMSites.Count == 0)
            {
                // Build a lookup of defaults by Key so we can fill any fields
                // (especially DeploymentGroups) that may be missing from cached
                // settings written by older builds.
                var defaultsLookup = DefaultSites
                    .ToDictionary(d => d.Key, d => d, StringComparer.OrdinalIgnoreCase);

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

                    // Backfill from defaults when settings.json was written by an
                    // older build that didn't persist these fields.
                    if (defaultsLookup.TryGetValue(saved.Key, out var def))
                    {
                        if (string.IsNullOrEmpty(entry.AppFolder))
                            entry.AppFolder = def.AppFolder;
                        if (entry.DeploymentGroups.Count == 0)
                        {
                            foreach (var g in def.DeploymentGroups)
                                entry.DeploymentGroups.Add(g);
                        }
                    }

                    Tenants.SCCMSites.Add(entry);
                }
                AppLogger.Info($"Restored {_settings.SccmSites.Count} SCCM site(s) from settings");
            }
            else
            {
                EnrichSitesFromSettings();
            }
        }

        // Seed defaults when nothing exists (first launch, empty settings.json).
        // Clone each entry so in-session edits never mutate the static templates.
        if (Tenants.IntuneTenants.Count == 0)
        {
            foreach (var t in DefaultTenants)
                Tenants.IntuneTenants.Add(new IntuneTenantEntry
                {
                    Key      = t.Key,
                    Name     = t.Name,
                    Comment  = t.Comment,
                    ClientID = t.ClientID,
                    AuthFlow = t.AuthFlow,
                });
            AppLogger.Info($"Seeded {DefaultTenants.Length} default Intune tenant(s)");
        }

        if (Tenants.SCCMSites.Count == 0)
        {
            foreach (var s in DefaultSites)
            {
                var entry = new SCCMSiteEntry
                {
                    Key       = s.Key,
                    Comment   = s.Comment,
                    AppFolder = s.AppFolder,
                };
                foreach (var g in s.DeploymentGroups)
                    entry.DeploymentGroups.Add(g);
                Tenants.SCCMSites.Add(entry);
            }
            AppLogger.Info($"Seeded {DefaultSites.Length} default SCCM site(s)");
        }

        if (Tenants.Domains.Count == 0)
        {
            // Preferences-persisted Domains win over the shipped org defaults;
            // fall back to defaults.local.json / defaults.example.json only
            // when the user hasn't customised any domains in Preferences.
            var source = _settings.Domains.Count > 0
                ? _settings.Domains.Select(d => new DomainEntry
                {
                    Key        = d.Key,
                    IsDistPath = d.IsDistPath,
                    AppFolder  = d.AppFolder,
                    TagFolder  = d.TagFolder,
                }).ToArray()
                : DefaultDomains;

            foreach (var d in source)
                Tenants.Domains.Add(new DomainEntry
                {
                    Key        = d.Key,
                    IsDistPath = d.IsDistPath,
                    AppFolder  = d.AppFolder,
                    TagFolder  = d.TagFolder,
                });
            AppLogger.Info($"Seeded {source.Length} default domain(s) (source: {(_settings.Domains.Count > 0 ? "Preferences" : "OrgDefaults")})");
        }
    }

    /// <summary>
    /// Migrates old settings format where Key was display name and Domain was tenant GUID.
    /// </summary>
    private void MigrateSettingsTenantFormat()
    {
        foreach (var saved in _settings.IntuneTenants)
        {
            if (!string.IsNullOrEmpty(saved.Domain)
                && Guid.TryParse(saved.Domain, out _)
                && !Guid.TryParse(saved.Key, out _))
            {
                saved.Name   = saved.Key;    // old Key was the display name
                saved.Key    = saved.Domain; // old Domain was the tenant GUID
                saved.Domain = string.Empty;
                AppLogger.Info($"Migrated tenant \"{saved.Name}\" -> Key={saved.Key}");
            }
        }
    }

    /// <summary>
    /// Merges settings-stored tenant data into config tenants that were loaded
    /// from an old Config.json (which lacks Name, ClientID, AuthFlow, etc.).
    /// Only fills fields that are empty in the config tenant.
    /// </summary>
    private void EnrichTenantsFromSettings()
    {
        var settingsLookup = _settings.IntuneTenants
            .Where(s => !string.IsNullOrEmpty(s.Key))
            .ToDictionary(s => s.Key, s => s, StringComparer.OrdinalIgnoreCase);

        int enriched = 0;
        foreach (var tenant in Tenants.IntuneTenants)
        {
            if (!settingsLookup.TryGetValue(tenant.Key, out var saved)) continue;

            if (string.IsNullOrEmpty(tenant.Name))
                tenant.Name = saved.Name;
            if (string.IsNullOrEmpty(tenant.ClientID))
                tenant.ClientID = saved.ClientID;
            // AuthFlow used to be a string with "" meaning "not set, inherit
            // from saved preferences". Post-enum-promotion (0189) the field
            // is a non-nullable AuthFlow with default Interactive, so there's
            // no longer a way to distinguish "unset" from "explicitly
            // Interactive". Legacy configs missing the field now load as
            // Interactive regardless of saved preference. If this edge case
            // (user customised saved.AuthFlow AND loaded a legacy config)
            // surfaces, promote the field to AuthFlow? to bring the fallback
            // back.
            // Preserve the DPAPI cipher on the entry instead of decrypting -
            // plaintext materializes only at the MSAL/PS boundary via SecureString.
            if (!tenant.HasStoredSecret)
                tenant.ClientSecretCipher = saved.ClientSecret;
            if (string.IsNullOrEmpty(tenant.CertThumbprint))
                tenant.CertThumbprint = saved.CertThumbprint;
            if (string.IsNullOrEmpty(tenant.Architecture))
                tenant.Architecture = saved.Architecture;
            if (string.IsNullOrEmpty(tenant.MinimumSupportedWindowsRelease))
                tenant.MinimumSupportedWindowsRelease = saved.MinimumSupportedWindowsRelease;

            enriched++;
            AppLogger.Info($"Enriched tenant {tenant.Key} with settings (Name=\"{tenant.Name}\")");
        }

        if (enriched > 0)
            AppLogger.Info($"Enriched {enriched} Intune tenant(s) from settings");
    }

    /// <summary>
    /// Merges settings-stored SCCM site data into config sites loaded from an old Config.json.
    /// </summary>
    private void EnrichSitesFromSettings()
    {
        var settingsLookup = _settings.SccmSites
            .Where(s => !string.IsNullOrEmpty(s.Key))
            .ToDictionary(s => s.Key, s => s, StringComparer.OrdinalIgnoreCase);

        // Defaults serve as a final fallback for any field still missing after the
        // settings merge -- this catches the case where settings.json was written
        // by an older build that didn't persist DeploymentGroups/AppFolder.
        var defaultsLookup = DefaultSites
            .ToDictionary(d => d.Key, d => d, StringComparer.OrdinalIgnoreCase);

        int enriched = 0;
        foreach (var site in Tenants.SCCMSites)
        {
            if (settingsLookup.TryGetValue(site.Key, out var saved))
            {
                // Sites from old configs typically have full data, but fill any gaps
                if (string.IsNullOrEmpty(site.Comment))
                    site.Comment = saved.Comment;
                if (string.IsNullOrEmpty(site.AppFolder))
                    site.AppFolder = saved.AppFolder;
                if (string.IsNullOrEmpty(site.IconFolder))
                    site.IconFolder = saved.IconFolder;
                if (site.DeploymentGroups.Count == 0 && saved.DeploymentGroups.Count > 0)
                {
                    foreach (var g in saved.DeploymentGroups)
                        site.DeploymentGroups.Add(g);
                }
            }

            // Final fallback: backfill anything still missing from the static defaults
            if (defaultsLookup.TryGetValue(site.Key, out var def))
            {
                if (string.IsNullOrEmpty(site.AppFolder))
                    site.AppFolder = def.AppFolder;
                if (site.DeploymentGroups.Count == 0)
                {
                    foreach (var g in def.DeploymentGroups)
                        site.DeploymentGroups.Add(g);
                }
            }

            enriched++;
        }

        if (enriched > 0)
            AppLogger.Info($"Enriched {enriched} SCCM site(s) from settings");
    }

    // -----------------------------------------------------------------
    // Default tenants, sites, and domains for new installs.
    // Loaded from defaults.local.json (gitignored, your real org values)
    // or defaults.example.json (tracked, sanitized placeholders).
    // Edit defaults.local.json to change what new bundles start with.
    // -----------------------------------------------------------------

    // Reads through DefaultsLoader's cache (invalidated on runtime import) -
    // a local Lazy here would pin the org defaults from startup forever and
    // make an imported file invisible until restart.
    internal static IntuneTenantEntry[] DefaultTenants =>
        DefaultsLoader.LoadCached().IntuneTenants
            .Select(t => new IntuneTenantEntry
            {
                Key      = t.Key,
                Name     = t.Name,
                Comment  = t.Comment,
                ClientID = t.ClientID,
                AuthFlow = t.AuthFlow,
            })
            .ToArray();

    internal static SCCMSiteEntry[] DefaultSites =>
        DefaultsLoader.LoadCached().SCCMSites
            .Select(s =>
            {
                var entry = new SCCMSiteEntry
                {
                    Key       = s.Key,
                    Comment   = s.Comment,
                    AppFolder = s.AppFolder,
                };
                foreach (var g in s.DeploymentGroups)
                    entry.DeploymentGroups.Add(g);
                return entry;
            })
            .ToArray();

    private static DomainEntry[] DefaultDomains =>
        DefaultsLoader.LoadCached().Domains
            .Select(d => new DomainEntry
            {
                Key        = d.Key,
                IsDistPath = d.IsDistPath,
                AppFolder  = d.AppFolder,
                TagFolder  = d.TagFolder,
            })
            .ToArray();

    /// <summary>
    /// <see cref="DefaultDomains"/> access for <see cref="PreferencesViewModel"/>'s
    /// first-ever load of the Preferences Domains table.
    /// </summary>
    internal static DomainEntry[] DefaultDomainsExternal => DefaultDomains;

    [RelayCommand]
    private void BrowsePsadtTemplate()
    {
        var folder = FileDialogService.BrowseFolder("Select PSADT v4 template folder (contains Invoke-AppDeployToolkit.ps1)");
        if (folder is not null)
            PsadtTemplatePath = folder;
    }

    /// <summary>
    /// Copies the tenants of the CURRENTLY LOADED BUNDLE into the saved
    /// tenants. The two lists are separate by design (bundle targets vs. the
    /// technician's catalogue), so this is an explicit, non-destructive pull:
    /// new keys are added, blank fields on existing entries are filled, and
    /// nothing already set is overwritten.
    /// </summary>
    [RelayCommand]
    private async Task PullTenantsFromBundleAsync()
    {
        var added = 0;
        foreach (var t in Tenants.IntuneTenants.ToList())
            if (Preferences.UpsertTenant(t)) added++;

        if (added == 0)
        {
            await FluentDialog.ShowInfoAsync(
                "Nothing to add",
                "Every tenant in the current bundle is already in your saved tenants.");
            return;
        }

        await FluentDialog.ShowInfoAsync(
            "Tenants pulled from bundle",
            $"{added} tenant entr{(added == 1 ? "y was" : "ies were")} added or completed from the current bundle. " +
            "Review them below, then Save to keep them.");
    }

    /// <summary>SCCM counterpart of <see cref="PullTenantsFromBundleAsync"/>.</summary>
    [RelayCommand]
    private async Task PullSitesFromBundleAsync()
    {
        var added = 0;
        foreach (var s in Tenants.SCCMSites.ToList())
            if (Preferences.UpsertSite(s)) added++;

        if (added == 0)
        {
            await FluentDialog.ShowInfoAsync(
                "Nothing to add",
                "Every site in the current bundle is already in your saved sites.");
            return;
        }

        await FluentDialog.ShowInfoAsync(
            "Sites pulled from bundle",
            $"{added} site entr{(added == 1 ? "y was" : "ies were")} added or completed from the current bundle. " +
            "Review them below, then Save to keep them.");
    }

    /// <summary>
    /// Handles a tenant discovered during MSAL sign-in (raised by
    /// <see cref="AccountViewModel.TenantDiscovered"/>). The account UI adds it
    /// to the bundle; this adds it to the technician's saved tenants so the
    /// two don't drift, and persists immediately - the discovery is a
    /// deliberate user action, not a background sync.
    /// </summary>
    public async Task OnTenantDiscoveredAsync(IntuneTenantEntry tenant)
    {
        if (!Preferences.UpsertTenant(tenant)) return;

        // SavePreferencesAsync owns the DPAPI handling for tenant secrets - do
        // not hand-roll a write here.
        await SettingsService.SavePreferencesAsync(
            _settings, Preferences.IntuneTenants, Preferences.SCCMSites);
        AppLogger.Info($"Settings: saved discovered tenant '{tenant.Key}' to preferences");
    }

    /// <summary>
    /// Imports an organization defaults file (the same JSON the first-run gate
    /// accepts) at any time. Copies it to the update-surviving location and
    /// re-runs seeding for fields still at factory defaults - a technician's
    /// explicit choices are never overwritten (OrgDefaultsSeeder semantics).
    /// </summary>
    [RelayCommand]
    private async Task ImportOrgDefaultsAsync()
    {
        // Belt for the policy card-hide: the command must refuse even if
        // reached some other way (keyboard, stale visual state).
        if (Services.Policy.PolicyService.Current.DisableOrgDefaultsImport)
        {
            await Services.FluentDialog.ShowInfoAsync("Import blocked",
                "Importing organization defaults is disabled by your organization's policy.");
            return;
        }

        var picked = FileDialogService.BrowseFile(
            "JSON files (*.json)|*.json|All files (*.*)|*.*",
            "Select your organization's Wrapp defaults file");
        if (picked is null) return;

        if (!Services.Gates.FirstRunDefaultsGate.TryImport(picked, out var error))
        {
            await FluentDialog.ShowWarningAsync("Defaults file not usable", error);
            return;
        }

        var imported = Services.DefaultsLoader.Load();
        // One import sequence, shared with the first-run gate (ApplyImported):
        // redaction + seed + one-shot flag + save + template pack.
        var changed = Services.OrgDefaultsSeeder.ApplyImported(_settings, imported);

        // Same refresh set the other load paths use: scalars, the Preferences
        // tables (whose empty-settings fallback surfaces the imported
        // tenants/sites/domains), and the bundle's tenant lists.
        ReloadFromSettings();
        Preferences.LoadFromSettings();
        Placeholders.LoadFromSettings();
        Placeholders.RefreshPreferencesJson();
        Placeholders.RefreshOrgDefaults();
        Placeholders.RefreshRedaction();
        RestoreSavedTenants();

        await FluentDialog.ShowInfoAsync(
            "Organization defaults imported",
            changed
                ? "Your organization's defaults have been applied to settings that were still at " +
                  "their factory values. Your own choices were left untouched. Tenants, sites and " +
                  "domains appear in the Preferences sections below."
                : "The file was saved and is now this profile's defaults source (its tenants, " +
                  "sites and domains fill the Preferences tables whenever those are empty).\n\n" +
                  "No individual settings were changed: each value the file covers either already " +
                  "matches it, or was set on this profile and is deliberately never overwritten. " +
                  "To force a value from the file, clear that field in Settings and re-import - " +
                  "or use Import settings, which replaces the whole profile.");
    }

    /// <summary>
    /// Exports the whole configured profile (settings + preferences) to a JSON
    /// file. Secrets and per-machine trust tokens are stripped - see
    /// <see cref="Services.SettingsPortability"/>.
    /// </summary>
    [RelayCommand]
    private async Task ExportSettingsAsync()
    {
        var path = FileDialogService.SaveFile(
            "JSON files (*.json)|*.json|All files (*.*)|*.*",
            "Export Wrapp settings and preferences",
            $"wrapp-settings-{SystemClock.Now:yyyyMMdd}.json",
            "json");
        if (path is null) return;

        // Flush pending edits so the export matches what the user sees.
        await SaveAsync();

        if (!Services.SettingsPortability.TryExport(_settings, path, out var error))
        {
            await FluentDialog.ShowWarningAsync("Export failed", error);
            return;
        }
        await FluentDialog.ShowExportedAsync(
            "Settings exported",
            "Client secrets and this machine's trust approvals were deliberately left out - they " +
            "can't be used on another machine. The file can be shared with colleagues or supplied " +
            "as an organization defaults file on first run.",
            path);
    }

    /// <summary>
    /// Imports a settings/preferences JSON exported by <see cref="ExportSettingsAsync"/>
    /// (or hand-authored). Replaces the profile's values; this machine's trust
    /// approvals, secrets and gate answers are preserved.
    /// </summary>
    [RelayCommand]
    private async Task ImportSettingsAsync()
    {
        if (Services.Policy.PolicyService.Current.DisableSettingsImport)
        {
            await Services.FluentDialog.ShowInfoAsync("Import blocked",
                "Importing settings is disabled by your organization's policy.");
            return;
        }

        var path = FileDialogService.BrowseFile(
            "JSON files (*.json)|*.json|All files (*.*)|*.*",
            "Import Wrapp settings and preferences");
        if (path is null) return;

        var parsed = Services.SettingsPortability.TryParse(path, out var error);
        if (parsed is null)
        {
            await FluentDialog.ShowWarningAsync("Import failed", error);
            return;
        }

        var confirmed = await FluentDialog.ConfirmAsync(
            "Replace settings?",
            "This replaces your current settings and preferences - tenants, sites, domains, paths, " +
            "packaging defaults and update settings - with the contents of the file.\n\n" +
            "Client secrets, this machine's Key Vault and update-feed approvals, and your " +
            "acceptance of the liability waiver are kept as they are.\n\n" +
            "Tip: export your current settings first if you might want them back.",
            "Replace settings", "Cancel");
        if (!confirmed) return;

        Services.SettingsPortability.ApplyImported(_settings, parsed);
        SettingsService.Save(_settings);
        ReloadFromSettings();
        Preferences.LoadFromSettings();
        Placeholders.LoadFromSettings();
        Placeholders.RefreshPreferencesJson();

        await FluentDialog.ShowInfoAsync(
            "Settings imported",
            "Your settings and preferences have been replaced. Review the sections here, then use " +
            "Save if you make further changes.");
    }

    /// <summary>Pushes current <see cref="AppSettings"/> values back into the bound properties.</summary>
    private void ReloadFromSettings()
    {
        DirectoryFormat   = _settings.DirectoryFormat;
        IconFolderName    = _settings.IconFolderName;
        PsadtTemplatePath = _settings.PsadtTemplatePath;
        SelectedTheme     = _settings.Theme ?? "Dark";
        KeyVaultRepoUrl   = _settings.KeyVaultRepoUrl;
        EnableAzureDevOpsKeyVault = _settings.EnableAzureDevOpsKeyVault;
        KeyVaultPathTemplate = _settings.KeyVaultPathTemplate;
        KeyVaultManualPathTemplate = _settings.KeyVaultManualPathTemplate;
        KeyVaultUsePullRequests = _settings.KeyVaultUsePullRequests;
        KeyVaultPrSourceBranchTemplate = _settings.KeyVaultPrSourceBranchTemplate;
        KeyVaultPrTitleTemplate = _settings.KeyVaultPrTitleTemplate;
        KeyVaultPrDescriptionTemplate = _settings.KeyVaultPrDescriptionTemplate;
        UpdateFeedUrl = _settings.UpdateFeedUrl;
        UpdateMode = _settings.UpdateMode;
        VerboseUiTrace = _settings.VerboseUiTrace;
        TakeSnapshot();
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        UpdateCheckStatus = "Checking...";
        UpdateFoundVersion = null;
        var result = await Services.UpdateService.CheckAsync(_settings, download: false);
        UpdateCheckStatus = Services.UpdateService.Describe(result);
        if (result.Status == Services.UpdateService.CheckStatus.UpdateAvailable)
            UpdateFoundVersion = result.Version;
    }

    /// <summary>
    /// Set when a manual check found an update; reveals the install button.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateInstallVisible))]
    private string? _updateFoundVersion;

    public bool UpdateInstallVisible => !string.IsNullOrEmpty(UpdateFoundVersion);

    /// <summary>
    /// Hands off to the splash-level update flow - normal save prompts first
    /// (Cancel aborts and keeps the session), then the update screen downloads,
    /// installs, and relaunches on the new version.
    /// </summary>
    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        var started = await Services.UpdateFlowController.BeginFromSessionAsync(_settings, UpdateFoundVersion);
        if (!started)
            UpdateCheckStatus = $"Update available: {UpdateFoundVersion} - install cancelled.";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        // Placeholder rows in error (duplicate / reserved / invalid names) BLOCK
        // the whole save - nothing is persisted until the flagged rows are fixed
        // (user chose disallow over skip).
        if (Placeholders.ErrorCount > 0)
        {
            await FluentDialog.ShowWarningAsync(
                "Settings not saved", Placeholders.BuildBlockingErrorMessage());
            return;
        }

        _settings.DirectoryFormat   = DirectoryFormat;
        _settings.IconFolderName    = IconFolderName;
        _settings.PsadtTemplatePath = PsadtTemplatePath;
        _settings.Theme             = SelectedTheme;

        // TOFU guard for KeyVaultRepoUrl: if the current URL isn't already
        // trusted on this machine, prompt for explicit confirmation before
        // issuing a machine-bound trust token. The token is a DPAPI envelope of
        // the URL (not a plain hash), so an attacker who can only write
        // settings.json cannot forge approval.
        if (!string.IsNullOrEmpty(KeyVaultRepoUrl)
            && !Services.EncryptionKeyStoreService.IsKeyVaultUrlTrusted(KeyVaultRepoUrl, _settings.KeyVaultRepoUrlHash))
        {
            var approved = await FluentDialog.ConfirmAsync(
                "Approve Key Vault URL",
                $"Encryption keys will be pushed to:\n\n    {KeyVaultRepoUrl}\n\n" +
                "Confirm this is the correct Azure DevOps repo. If you didn't make this change, " +
                "cancel and verify settings.json was not edited by another tool or process.\n\n" +
                "Approve this URL for key pushes on this machine?",
                "Approve", "Cancel");
            if (!approved)
            {
                AppLogger.Info("Settings: user declined Key Vault URL approval; leaving trust token unchanged");
            }
            else
            {
                _settings.KeyVaultRepoUrlHash =
                    Services.EncryptionKeyStoreService.IssueKeyVaultTrustToken(KeyVaultRepoUrl);
                AppLogger.Info("Settings: Key Vault URL approved");
            }
        }
        _settings.KeyVaultRepoUrl   = KeyVaultRepoUrl;
        _settings.EnableAzureDevOpsKeyVault = EnableAzureDevOpsKeyVault;
        _settings.KeyVaultPathTemplate = KeyVaultPathTemplate;
        _settings.KeyVaultManualPathTemplate = KeyVaultManualPathTemplate;
        _settings.KeyVaultUsePullRequests = KeyVaultUsePullRequests;
        _settings.KeyVaultPrSourceBranchTemplate = KeyVaultPrSourceBranchTemplate;
        _settings.KeyVaultPrTitleTemplate = KeyVaultPrTitleTemplate;
        _settings.KeyVaultPrDescriptionTemplate = KeyVaultPrDescriptionTemplate;

        // Same TOFU shape for the update feed. The feed delivers executable
        // code, so an unapproved URL is never contacted -- declining just
        // leaves the token unissued (checks report FeedNotApproved).
        if (!string.IsNullOrEmpty(UpdateFeedUrl)
            && !Services.UpdateService.IsFeedTrusted(UpdateFeedUrl, _settings.UpdateFeedTrustToken))
        {
            var feedApproved = await FluentDialog.ConfirmAsync(
                "Approve update feed",
                $"Wrapp will check for and download application updates from:\n\n    {UpdateFeedUrl}\n\n" +
                "Updates are executable code -- only approve a feed operated by your organization. " +
                "If you didn't make this change, cancel and verify settings.json was not edited " +
                "by another tool or process.\n\n" +
                "Approve this feed for updates on this machine?",
                "Approve", "Cancel");
            if (feedApproved)
            {
                _settings.UpdateFeedTrustToken = Services.UpdateService.IssueFeedTrustToken(UpdateFeedUrl);
                AppLogger.Info("Settings: update feed URL approved");
            }
            else
            {
                AppLogger.Info("Settings: user declined update feed approval; leaving trust token unchanged");
            }
        }
        // The update policy governs whether the app self-updates - a flip
        // (user or org import) must leave a trace.
        if (!string.Equals(_settings.UpdateMode, UpdateMode, StringComparison.Ordinal))
            AppLogger.Info($"Settings: UpdateMode changed \"{_settings.UpdateMode}\" -> \"{UpdateMode}\"");
        if (!string.Equals(_settings.UpdateFeedUrl, UpdateFeedUrl, StringComparison.Ordinal))
            AppLogger.Info($"Settings: UpdateFeedUrl changed \"{_settings.UpdateFeedUrl}\" -> \"{UpdateFeedUrl}\"");
        _settings.UpdateFeedUrl = UpdateFeedUrl;
        _settings.UpdateMode = UpdateMode;
        if (_settings.VerboseUiTrace != VerboseUiTrace)
            AppLogger.Info($"Settings: VerboseUiTrace {( VerboseUiTrace ? "enabled" : "disabled")}");
        _settings.VerboseUiTrace = VerboseUiTrace;
        Services.UiTrace.Enabled = VerboseUiTrace;   // live, no restart needed

        // Placeholders persist through the SAME save click. Sensitive values are
        // DPAPI-encrypted into the sidecar here; on encryption failure the
        // placeholder rows are left un-persisted (the user was warned) while
        // the rest of the settings still save.
        await Placeholders.ApplyToSettingsAsync();

        // Enterprise policy: mandated values are re-asserted on every save
        // (belt - the UI disables locked fields, but nothing else may ever
        // persist a value past an administrator mandate).
        Services.Policy.PolicyService.ApplyMandatory(_settings);

        SettingsService.Save(_settings);
        AppLogger.Info("Settings saved");

        // AppSettings is a plain POCO, so the gate service doesn't observe the
        // mutations above. Fire an explicit notification so bound consumers
        // re-evaluate IsEnabled without a restart.
        _featureGate?.NotifyChanged();

        // Preferences (tenants/sites/defaults) persist through a DPAPI-aware
        // path that atomically rewrites settings.json. Chain it on the same
        // Save click so the user never has to click twice.
        await Preferences.SaveAsync();

        // Preferences.SaveAsync rewrites the tenant/site/domain lists FROM
        // THE GRIDS, after the earlier re-assert - without this second pass,
        // a user's edit to a policy-keyed row would persist until the next
        // launch. Snap back and re-persist only when something changed.
        if (Services.Policy.PolicyService.ApplyMandatory(_settings))
        {
            SettingsService.Save(_settings);
            AppLogger.Info("Policy: re-asserted managed values after preferences save");
        }

        // The effective-configuration viewers and redaction summary reflect
        // the just-saved state.
        Placeholders.RefreshPreferencesJson();
        Placeholders.RefreshRedaction();

        // Rebaseline: whatever is in memory now matches disk.
        TakeSnapshot();
    }

    /// <summary>
    /// Wires the feature-gate service so <see cref="SaveAsync"/> can fire
    /// <see cref="Services.IFeatureGate.NotifyChanged"/> after gate-relevant
    /// flags are copied back into <see cref="AppSettings"/>. Called from App.xaml.cs.
    /// </summary>
    public void WireFeatureGate(Services.IFeatureGate featureGate)
        => _featureGate = featureGate;

    /// <summary>Wire DevOpsAuthService for key vault sign-in.</summary>
    public void WireDevOpsAuth(Services.DevOpsAuthService devOpsAuth)
    {
        _devOpsAuth = devOpsAuth;
        _ = Task.Run(async () =>
        {
            var token = await devOpsAuth.TryAcquireTokenSilentAsync();
            if (token is not null)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    DevOpsAuthStatus = $"Signed in as {token.UserPrincipalName}");
            }
        });
    }

    [RelayCommand]
    private async Task SignInDevOps()
    {
        if (_devOpsAuth is null)
        {
            DevOpsAuthStatus = "DevOps auth not configured";
            return;
        }
        try
        {
            DevOpsAuthStatus = "Signing in...";
            var result = await _devOpsAuth.AcquireTokenAsync();
            DevOpsAuthStatus = result is not null
                ? $"Signed in as {result.UserPrincipalName}"
                : "Sign-in failed";
        }
        catch (OperationCanceledException)
        {
            DevOpsAuthStatus = "Cancelled";
        }
        catch (Exception ex)
        {
            DevOpsAuthStatus = $"Error: {ex.Message}";
            AppLogger.Warn($"DevOps sign-in failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ResetDefaultsAsync()
    {
        DirectoryFormat   = @"{Company}\{Name}\{Version}";
        IconFolderName    = "Icon";
        PsadtTemplatePath = string.Empty;
        KeyVaultRepoUrl   = string.Empty;
        EnableAzureDevOpsKeyVault = true;
        // Reseed key-vault templates from a fresh AppSettings so defaults stay
        // defined in exactly one place (the model).
        var freshKv = new AppSettings();
        KeyVaultPathTemplate = freshKv.KeyVaultPathTemplate;
        KeyVaultManualPathTemplate = freshKv.KeyVaultManualPathTemplate;
        KeyVaultUsePullRequests = freshKv.KeyVaultUsePullRequests;
        KeyVaultPrSourceBranchTemplate = freshKv.KeyVaultPrSourceBranchTemplate;
        KeyVaultPrTitleTemplate = freshKv.KeyVaultPrTitleTemplate;
        KeyVaultPrDescriptionTemplate = freshKv.KeyVaultPrDescriptionTemplate;
        UpdateFeedUrl = freshKv.UpdateFeedUrl;
        UpdateMode = freshKv.UpdateMode;
        SelectedTheme     = "Dark";
        AppLogger.Info("Reset scalar settings to application defaults");
        await FluentDialog.ShowInfoAsync(
            "Settings reset",
            "Directory format, icon folder, PSADT path, Key Vault URL, and theme have been restored to application defaults. " +
            "Click Save to persist.");
    }

    /// <summary>
    /// Standard shape for a user-initiated reset command:
    /// <list type="number">
    /// <item><description>Show a confirm dialog with the supplied title + message.</description></item>
    /// <item><description>If declined, return false without side effects.</description></item>
    /// <item><description>Otherwise run <paramref name="reset"/> and log <paramref name="logMessage"/>.</description></item>
    /// <item><description>Optionally show a success-info dialog whose body is produced by
    ///   <paramref name="successMessage"/> - a deferred factory so it can include values
    ///   captured by <paramref name="reset"/> (e.g. an item count).</description></item>
    /// </list>
    /// Shared by the <c>Reset*</c> commands. <see cref="ResetAllSettingsAsync"/>
    /// deliberately does NOT use this helper - its file-move + multi-stage
    /// in-place reload is a genuinely different shape.
    /// </summary>
    private static async Task<bool> ConfirmAndResetAsync(
        string title, string message, Action reset, string logMessage,
        Func<string>? successMessage = null)
    {
        var ok = await FluentDialog.ConfirmAsync(title, message, "Reset", "Cancel");
        if (!ok) return false;

        reset();
        AppLogger.Info(logMessage);

        if (successMessage is not null)
            await FluentDialog.ShowInfoAsync(title, successMessage());

        return true;
    }

    /// <summary>
    /// Clears the live Intune tenants AND the persisted copy in settings.json,
    /// then re-runs <see cref="RestoreSavedTenants"/> so <see cref="DefaultTenants"/>
    /// reseed. Use when a user wants to discard their tenant edits and start over.
    /// Reset is in-memory only -- the user must click Save to persist. TakeSnapshot
    /// is deliberately not called so IsDirty surfaces the pending change.
    /// </summary>
    [RelayCommand]
    private Task ResetIntuneTenantsAsync()
        => ConfirmAndResetAsync(
            title: "Reset Intune Tenants",
            message: "Clear all configured Intune tenants and restore the application defaults?\n\n" +
                     "Any tenant-specific ClientSecrets stored in settings.json will be forgotten.",
            reset: () =>
            {
                Tenants.IntuneTenants.Clear();
                _settings.IntuneTenants.Clear();
                RestoreSavedTenants();
                Preferences.LoadFromSettings();
            },
            logMessage: "Reset Intune tenants to defaults (unsaved - click Save to persist)");

    [RelayCommand]
    private Task ResetSccmSitesAsync()
        => ConfirmAndResetAsync(
            title: "Reset SCCM Sites",
            message: "Clear all configured SCCM sites and restore the application defaults?",
            reset: () =>
            {
                Tenants.SCCMSites.Clear();
                _settings.SccmSites.Clear();
                RestoreSavedTenants();
                Preferences.LoadFromSettings();
            },
            logMessage: "Reset SCCM sites to defaults (unsaved - click Save to persist)");

    [RelayCommand]
    private Task ResetDomainsAsync()
        => ConfirmAndResetAsync(
            title: "Reset Domains",
            message: "Clear all configured domain paths and restore the application defaults?",
            reset: () =>
            {
                Tenants.Domains.Clear();
                RestoreSavedTenants();
                Preferences.LoadFromSettings();
            },
            logMessage: "Reset domains to defaults (unsaved - click Save to persist)");

    /// <summary>
    /// Nuclear option: renames <c>settings.json</c> to a timestamped backup so
    /// the next launch starts from a fresh <see cref="AppSettings"/> with all
    /// defaults reseeded. Does not touch the MSAL cache, encryption keys, or
    /// built-in templates - only app-level preferences.
    /// </summary>
    [RelayCommand]
    private async Task ResetAllSettingsAsync()
    {
        var ok = await FluentDialog.ConfirmAsync(
            "Reset All Settings",
            "This moves your settings.json to a timestamped backup and reloads all " +
            "preferences from factory defaults in-place (no restart). " +
            "Tenant ClientSecrets and other preferences will be forgotten.\n\n" +
            "Your sign-in, encryption keys, and built-in templates are NOT affected. " +
            "The backup can be restored by renaming it.",
            "Reset", "Cancel");
        if (!ok) return;

        try
        {
            // PlatformConfig owns this path (env override included).
            var settingsPath = Services.PlatformConfig.SettingsPath;
            if (System.IO.File.Exists(settingsPath))
            {
                var stamp = SystemClock.Now.ToString("yyyyMMdd-HHmmss");
                var backup = settingsPath + $".backup.{stamp}.json";
                System.IO.File.Move(settingsPath, backup, overwrite: false);
                AppLogger.Info($"Reset all settings; moved settings.json to {backup}");
            }
        }
        catch (Exception ex)
        {
            await FluentDialog.ShowWarningAsync(
                "Reset Failed",
                $"Could not move settings.json: {ex.Message}\n\n" +
                "Another process may be holding it open. Close other Wrapp windows and try again.");
            AppLogger.Warn($"ResetAllSettings failed: {ex.Message}");
            return;
        }

        // In-place reload: mutate the existing _settings instance so every
        // binding that holds a reference keeps working, then refresh the UI.
        var fresh = new AppSettings();
        _settings.DirectoryFormat   = fresh.DirectoryFormat;
        _settings.IconFolderName    = fresh.IconFolderName;
        _settings.PsadtTemplatePath = fresh.PsadtTemplatePath;
        _settings.Theme             = fresh.Theme;
        _settings.KeyVaultRepoUrl   = fresh.KeyVaultRepoUrl;
        _settings.KeyVaultRepoUrlHash = fresh.KeyVaultRepoUrlHash;
        _settings.EnableAzureDevOpsKeyVault = fresh.EnableAzureDevOpsKeyVault;
        _settings.KeyVaultPathTemplate = fresh.KeyVaultPathTemplate;
        _settings.KeyVaultManualPathTemplate = fresh.KeyVaultManualPathTemplate;
        _settings.KeyVaultUsePullRequests = fresh.KeyVaultUsePullRequests;
        _settings.KeyVaultPrSourceBranchTemplate = fresh.KeyVaultPrSourceBranchTemplate;
        _settings.KeyVaultPrTitleTemplate = fresh.KeyVaultPrTitleTemplate;
        _settings.KeyVaultPrDescriptionTemplate = fresh.KeyVaultPrDescriptionTemplate;
        _settings.UpdateFeedUrl = fresh.UpdateFeedUrl;
        _settings.UpdateMode = fresh.UpdateMode;
        _settings.UpdateFeedTrustToken = fresh.UpdateFeedTrustToken;
        _settings.IntuneTenants.Clear();
        _settings.SccmSites.Clear();
        _settings.TenantNameCache.Clear();

        // Push the scalar resets into this VM's observable properties so the UI updates.
        DirectoryFormat   = _settings.DirectoryFormat;
        IconFolderName    = _settings.IconFolderName;
        PsadtTemplatePath = _settings.PsadtTemplatePath;
        SelectedTheme     = _settings.Theme ?? "Dark";
        KeyVaultRepoUrl   = _settings.KeyVaultRepoUrl;
        EnableAzureDevOpsKeyVault = _settings.EnableAzureDevOpsKeyVault;
        KeyVaultPathTemplate = _settings.KeyVaultPathTemplate;
        KeyVaultManualPathTemplate = _settings.KeyVaultManualPathTemplate;
        KeyVaultUsePullRequests = _settings.KeyVaultUsePullRequests;
        KeyVaultPrSourceBranchTemplate = _settings.KeyVaultPrSourceBranchTemplate;
        KeyVaultPrTitleTemplate = _settings.KeyVaultPrTitleTemplate;
        KeyVaultPrDescriptionTemplate = _settings.KeyVaultPrDescriptionTemplate;
        UpdateFeedUrl = _settings.UpdateFeedUrl;
        UpdateMode = _settings.UpdateMode;
        VerboseUiTrace = _settings.VerboseUiTrace;

        // Clear the live bundle-scoped tenant/site lists and re-seed from app defaults.
        Tenants.IntuneTenants.Clear();
        Tenants.SCCMSites.Clear();
        Tenants.Domains.Clear();
        RestoreSavedTenants();

        // Refresh the Preferences editor (now shows app defaults since settings is empty).
        Preferences.LoadFromSettings();
        _settings.Placeholders.Clear();
        Placeholders.LoadFromSettings();
        Placeholders.RefreshPreferencesJson();
        TakeSnapshot();

        AppLogger.Info("Reset all settings complete (in-place).");
        await FluentDialog.ShowInfoAsync(
            "Settings reset",
            "All preferences restored to application defaults. " +
            "A timestamped backup of your previous settings.json was saved in the Wrapp data folder.");
    }

    [RelayCommand]
    private async Task ResetTemplatesAsync()
    {
        // successMessage is a Func so the interpolation runs AFTER reset() has
        // populated the captured count.
        int count = 0;
        await ConfirmAndResetAsync(
            title: "Reset Templates",
            message: "This will overwrite all built-in templates with the defaults shipped with this version of Wrapp.\n\n"
                     + "Custom templates you created will not be affected.",
            reset: () => count = TemplateService.ResetBuiltInTemplates(),
            logMessage: "Templates reset to defaults",
            successMessage: () => $"{count} built-in template(s) have been reset to their default content.");
    }
}
