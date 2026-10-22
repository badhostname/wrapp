using Wrapp.Models;
using Wrapp.Services;
using Wrapp.Services.Policy;

namespace Wrapp.Tests;

/// <summary>
/// The enterprise policy engine: hive precedence, mandatory-vs-recommended
/// semantics, hidden-UI parsing with the Settings/General guard, security
/// validation of policy-supplied URLs, the catalog's contracts, and the
/// re-assertion paths (settings import, machine-mandate TOFU bypass).
/// </summary>
// Swaps the static PolicyService store - serialized with the other
// static-seam test classes.
[Collection("Placeholders")]
public class PolicyServiceTests : IDisposable
{
    private readonly InMemoryPolicyStore _store = new();

    public PolicyServiceTests() => PolicyService.OverrideStore(_store);

    // Restore an EMPTY in-memory store, never the registry store (null):
    // the suite must stay blind to the host machine's real policy - see
    // PolicyTestIsolation.
    public void Dispose() => PolicyService.OverrideStore(new InMemoryPolicyStore());

    private void Reload() => PolicyService.OverrideStore(_store); // drops the cached snapshot

    // ------------------------------------------------------------------
    // Precedence + application semantics
    // ------------------------------------------------------------------

    [Fact]
    public void MachineMandatory_WinsOverUser()
    {
        _store.UserMandatory["UpdateMode"] = "Auto";
        _store.MachineMandatory["UpdateMode"] = "Disabled";
        Reload();

        var settings = new AppSettings();
        PolicyService.ApplyMandatory(settings);

        Assert.Equal("Disabled", settings.UpdateMode);
        Assert.Equal("Machine policy", PolicyService.Current.SourceByKey["UpdateMode"]);
        Assert.True(PolicyService.Current.MachineMandatedKeys.Contains("UpdateMode"));
    }

    [Fact]
    public void Mandatory_OverwritesUserEdit_RecommendedDoesNot()
    {
        _store.MachineMandatory["DirectoryFormat"] = @"{Company}\{Name}";
        _store.MachineRecommended["IconFolderName"] = "Icons";
        Reload();

        var settings = new AppSettings
        {
            DirectoryFormat = @"my\own\format",   // explicit user edit
            IconFolderName = "MyIcons",           // explicit user edit
        };
        PolicyService.ApplyRecommended(settings);
        PolicyService.ApplyMandatory(settings);

        Assert.Equal(@"{Company}\{Name}", settings.DirectoryFormat);  // mandate wins
        Assert.Equal("MyIcons", settings.IconFolderName);             // recommendation defers
    }

    [Fact]
    public void Recommended_SeedsFactoryValues()
    {
        _store.UserRecommended["IconFolderName"] = "OrgIcons";
        Reload();

        var settings = new AppSettings();  // factory
        PolicyService.ApplyRecommended(settings);
        Assert.Equal("OrgIcons", settings.IconFolderName);
    }

    [Fact]
    public void BlockLeaf_AppliesThroughDottedPath()
    {
        _store.MachineMandatory["IntunePackageDefaults.InstallExperience"] = "system";
        _store.MachineMandatory["IntunePackageDefaults.MaximumInstallationTimeInMinutes"] = 90;
        _store.MachineMandatory["SccmPackageDefaults.ContentFallback"] = 1;  // DWORD → bool
        Reload();

        var settings = new AppSettings();
        PolicyService.ApplyMandatory(settings);

        Assert.Equal("system", settings.IntunePackageDefaults.InstallExperience);
        Assert.Equal(90, settings.IntunePackageDefaults.MaximumInstallationTimeInMinutes);
        Assert.True(settings.SccmPackageDefaults.ContentFallback);
    }

    // ------------------------------------------------------------------
    // Validation: malformed policy never half-applies
    // ------------------------------------------------------------------

    [Fact]
    public void UnknownValueName_IsIgnored()
    {
        _store.MachineMandatory["NotARealSetting"] = "x";
        Reload();
        Assert.Empty(PolicyService.Current.Mandatory);
    }

    [Fact]
    public void HttpFeedUrl_IsRejected()
    {
        _store.MachineMandatory["UpdateFeedUrl"] = "http://evil.example/feed";
        Reload();
        Assert.False(PolicyService.Current.IsManaged("UpdateFeedUrl"));
    }

    [Fact]
    public void NonHttpsVaultUrl_IsRejected()
    {
        _store.MachineMandatory["KeyVaultRepoUrl"] = "ftp://vault.example/repo";
        Reload();
        Assert.False(PolicyService.Current.IsManaged("KeyVaultRepoUrl"));
    }

    [Fact]
    public void OutOfSetEnum_IsRejected()
    {
        _store.MachineMandatory["UpdateMode"] = "Sometimes";
        Reload();
        Assert.False(PolicyService.Current.IsManaged("UpdateMode"));
    }

    // ------------------------------------------------------------------
    // Hidden UI
    // ------------------------------------------------------------------

    [Fact]
    public void HiddenSections_Parse_AndProtectSettingsGeneral()
    {
        _store.MachineMandatory["HiddenSections.Inventory"] = 1;
        _store.MachineMandatory["HiddenSections.GitHistory"] = 1;
        _store.MachineMandatory["HiddenSections.Settings"] = 1;   // must be ignored
        _store.MachineMandatory["HiddenSections.General"] = 1;    // must be ignored
        _store.UserMandatory["HiddenSettingsTabs.KeyVault"] = 1;
        Reload();

        var snap = PolicyService.Current;
        Assert.True(snap.IsSectionHidden(NavigationSection.Inventory));
        Assert.True(snap.IsSectionHidden(NavigationSection.GitHistory));
        Assert.False(snap.IsSectionHidden(NavigationSection.Settings));
        Assert.False(snap.IsSectionHidden(NavigationSection.General));
        Assert.True(snap.IsTabHidden("KeyVault"));
        Assert.True(snap.AnyManaged);
    }

    // ------------------------------------------------------------------
    // Re-assertion paths
    // ------------------------------------------------------------------

    [Fact]
    public void SettingsImport_CannotSmugglePastAMandate()
    {
        _store.MachineMandatory["UpdateMode"] = "NotifyOnly";
        Reload();

        var target = new AppSettings();
        PolicyService.ApplyMandatory(target);
        var imported = new AppSettings { UpdateMode = "Auto", DirectoryFormat = "x" };

        SettingsPortability.ApplyImported(target, imported);

        Assert.Equal("NotifyOnly", target.UpdateMode);  // mandate re-asserted
        Assert.Equal("x", target.DirectoryFormat);      // unmanaged value imported
    }

    [Fact]
    public void MachineMandatedFeed_BypassesTofu_UserMandatedDoesNot()
    {
        _store.MachineMandatory["UpdateFeedUrl"] = @"\\server\wrapp\releases";
        Reload();
        Assert.True(UpdateService.IsFeedTrusted(@"\\server\wrapp\releases", storedToken: null));
        Assert.False(UpdateService.IsFeedTrusted(@"\\other\feed", storedToken: null));

        _store.MachineMandatory.Clear();
        _store.UserMandatory["UpdateFeedUrl"] = @"\\server\wrapp\releases";
        Reload();
        // HKCU is writable by the user's own processes - never a TOFU bypass.
        Assert.False(UpdateService.IsFeedTrusted(@"\\server\wrapp\releases", storedToken: null));
    }

    // ------------------------------------------------------------------
    // Keyed lists (tenants / sites / domains), placeholders, redaction
    // ------------------------------------------------------------------

    private void AddMachineListEntry(string list, string key, Dictionary<string, object> values)
    {
        if (!_store.MachineLists.TryGetValue(list, out var entries))
            _store.MachineLists[list] = entries = new(StringComparer.OrdinalIgnoreCase);
        entries[key] = values;
    }

    [Fact]
    public void TenantEntries_MergeByKey_PreservingUserExtras()
    {
        AddMachineListEntry("IntuneTenants", "tenant-guid-1", new()
        {
            ["Name"] = "Contoso Prod",
            ["ClientID"] = "client-guid",
            ["AuthFlow"] = "DeviceCode",      // string → enum
        });
        Reload();

        var settings = new AppSettings();
        settings.IntuneTenants.Add(new SavedTenantEntry { Key = "user-own-tenant", Name = "Mine" });
        settings.IntuneTenants.Add(new SavedTenantEntry { Key = "tenant-guid-1", Name = "Old Name", ClientSecret = "keep-cipher" });

        PolicyService.ApplyMandatory(settings);

        Assert.Equal(2, settings.IntuneTenants.Count);              // merged, not replaced
        var managed = settings.IntuneTenants.First(t => t.Key == "tenant-guid-1");
        Assert.Equal("Contoso Prod", managed.Name);                 // policy wins per-key
        Assert.Equal("client-guid", managed.ClientID);
        Assert.Equal(AuthFlow.DeviceCode, managed.AuthFlow);        // enum parsed
        Assert.Equal("keep-cipher", managed.ClientSecret);          // secret untouched
        Assert.Equal("Mine", settings.IntuneTenants.First(t => t.Key == "user-own-tenant").Name);
    }

    [Fact]
    public void TenantEntries_ClientSecret_IsRefused()
    {
        AddMachineListEntry("IntuneTenants", "t1", new() { ["ClientSecret"] = "plaintext!" });
        Reload();

        var settings = new AppSettings();
        PolicyService.ApplyMandatory(settings);

        Assert.Equal(string.Empty, settings.IntuneTenants.Single(t => t.Key == "t1").ClientSecret);
    }

    [Fact]
    public void SiteEntries_DeploymentGroups_FromMultiSzAndDelimited()
    {
        AddMachineListEntry("SccmSites", "PS1", new()
        {
            ["AppFolder"] = @"\\dist\apps$",
            ["DeploymentGroups"] = new[] { "Pilot", "Broad" },      // REG_MULTI_SZ
        });
        AddMachineListEntry("SccmSites", "PS2", new()
        {
            ["DeploymentGroups"] = "Ring0; Ring1",                  // delimited REG_SZ fallback
        });
        Reload();

        var settings = new AppSettings();
        PolicyService.ApplyMandatory(settings);

        Assert.Equal(new[] { "Pilot", "Broad" }, settings.SccmSites.First(s => s.Key == "PS1").DeploymentGroups);
        Assert.Equal(new[] { "Ring0", "Ring1" }, settings.SccmSites.First(s => s.Key == "PS2").DeploymentGroups);
    }

    [Fact]
    public void DomainEntries_Provision()
    {
        AddMachineListEntry("Domains", "contoso.com", new()
        {
            ["IsDistPath"] = @"\\dist\packages$",
            ["TagFolder"] = "Tags",
        });
        Reload();

        var settings = new AppSettings();
        PolicyService.ApplyMandatory(settings);

        var d = settings.Domains.Single();
        Assert.Equal("contoso.com", d.Key);
        Assert.Equal(@"\\dist\packages$", d.IsDistPath);
    }

    [Fact]
    public void MachineListEntry_WinsOverUserListEntry()
    {
        _store.UserLists["Domains"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["contoso.com"] = new(StringComparer.OrdinalIgnoreCase) { ["TagFolder"] = "UserTags" },
        };
        AddMachineListEntry("Domains", "contoso.com", new() { ["TagFolder"] = "MachineTags" });
        Reload();

        var settings = new AppSettings();
        PolicyService.ApplyMandatory(settings);
        Assert.Equal("MachineTags", settings.Domains.Single().TagFolder);
    }

    [Fact]
    public void Placeholders_UpsertNonSensitive_NeverTouchSensitive()
    {
        _store.MachineMandatory["Placeholders.SupportMail"] = "it@contoso.com";
        _store.MachineMandatory["Placeholders.Secretish"] = "value";
        _store.MachineMandatory["Placeholders.Name"] = "reserved-must-be-ignored"; // built-in name
        Reload();

        var settings = new AppSettings();
        settings.Placeholders.Add(new PlaceholderEntry { Name = "Secretish", IsSensitive = true });

        PolicyService.ApplyMandatory(settings);

        var mail = settings.Placeholders.Single(p => p.Name == "SupportMail");
        Assert.Equal("it@contoso.com", mail.Value);
        Assert.False(mail.IsSensitive);
        Assert.True(settings.Placeholders.Single(p => p.Name == "Secretish").IsSensitive); // untouched
        Assert.DoesNotContain(settings.Placeholders, p => p.Value == "reserved-must-be-ignored");
    }

    [Fact]
    public void RedactionPatterns_AreExposedForMerging()
    {
        _store.MachineMandatory["RedactionPatterns.Hosts"] = @"\\\\dist\\[a-z$]+";
        _store.UserMandatory["RedactionPatterns.Tickets"] = "INC[0-9]{7}";
        Reload();

        var patterns = PolicyService.Current.RedactionPatterns;
        Assert.Contains(@"\\\\dist\\[a-z$]+", patterns);
        Assert.Contains("INC[0-9]{7}", patterns);
        Assert.True(PolicyService.Current.AnyManaged);
    }

    // ------------------------------------------------------------------
    // Org-defaults interplay: policy outranks the org file on every path
    // ------------------------------------------------------------------

    [Fact]
    public void OrgImport_CannotDisplaceAMandate_EvenWhenMandateEqualsFactory()
    {
        // The seeder writes only onto factory values - a mandate that HAPPENS
        // to equal factory looks factory to it. ApplyImported must re-assert.
        var factoryMode = new AppSettings().UpdateMode;               // "Auto"
        _store.MachineMandatory["UpdateMode"] = factoryMode;
        Reload();

        var settings = new AppSettings();
        PolicyService.ApplyMandatory(settings);

        var org = new OrgDefaults { Update = new OrgUpdateDefaults { Mode = "Disabled" } };
        OrgDefaultsSeeder.Apply(settings, org);        // seeder itself may write it…
        PolicyService.ApplyMandatory(settings);        // …the re-assert wins (mirrors ApplyImported)

        Assert.Equal(factoryMode, settings.UpdateMode);
    }

    [Fact]
    public void ApplyMandatory_ReportsWhetherAnythingSnappedBack()
    {
        _store.MachineMandatory["UpdateMode"] = "NotifyOnly";
        AddMachineListEntry("Domains", "contoso.com", new() { ["TagFolder"] = "Tags" });
        Reload();

        var settings = new AppSettings();
        Assert.True(PolicyService.ApplyMandatory(settings));   // first pass changes things
        Assert.False(PolicyService.ApplyMandatory(settings));  // already compliant → no-op

        // A grid-style edit to a policy-keyed entry is detected and snapped.
        settings.Domains.Single().TagFolder = "MyTags";
        Assert.True(PolicyService.ApplyMandatory(settings));
        Assert.Equal("Tags", settings.Domains.Single().TagFolder);
    }

    // ------------------------------------------------------------------
    // Change detection (fingerprint drives the restart-to-apply gate)
    // ------------------------------------------------------------------

    [Fact]
    public void Fingerprint_IsStable_AndDetectsEveryKindOfChange()
    {
        _store.MachineMandatory["UpdateMode"] = "NotifyOnly";
        AddMachineListEntry("SccmSites", "PS1", new() { ["AppFolder"] = @"PS1:\Application" });
        _store.MachineMandatory["Placeholders.SupportMail"] = "it@contoso.com";
        Reload();
        var baseline = PolicyService.Current.Fingerprint();

        // Same content → same fingerprint (stability).
        Assert.Equal(baseline, PolicyService.BuildFresh().Fingerprint());

        // A scalar change drifts it.
        _store.MachineMandatory["UpdateMode"] = "Disabled";
        Assert.NotEqual(baseline, PolicyService.BuildFresh().Fingerprint());
        _store.MachineMandatory["UpdateMode"] = "NotifyOnly";
        Assert.Equal(baseline, PolicyService.BuildFresh().Fingerprint());

        // A keyed-list value change drifts it.
        _store.MachineLists["SccmSites"]["PS1"]["AppFolder"] = @"PS9:\Other";
        Assert.NotEqual(baseline, PolicyService.BuildFresh().Fingerprint());
        _store.MachineLists["SccmSites"]["PS1"]["AppFolder"] = @"PS1:\Application";

        // A hidden-section change drifts it.
        _store.MachineMandatory["HiddenSections.Inventory"] = 1;
        Assert.NotEqual(baseline, PolicyService.BuildFresh().Fingerprint());
    }

    // ------------------------------------------------------------------
    // UI accessors: tab padlocks
    // ------------------------------------------------------------------

    [Fact]
    public void TabLockAccessor_LightsUpOnlyTouchedTabs()
    {
        _store.MachineMandatory["UpdateMode"] = "NotifyOnly";
        _store.MachineMandatory["IntunePackageDefaults.InstallExperience"] = "system";
        AddMachineListEntry("Domains", "contoso.com", new() { ["TagFolder"] = "Tags" });
        Reload();

        var locks = new PolicyTabLockAccessor();
        Assert.Equal(System.Windows.Visibility.Visible, locks["Updates"]);
        Assert.Equal(System.Windows.Visibility.Visible, locks["Intune"]);
        Assert.Equal(System.Windows.Visibility.Visible, locks["Domains"]);
        Assert.Equal(System.Windows.Visibility.Collapsed, locks["KeyVault"]);
        Assert.Equal(System.Windows.Visibility.Collapsed, locks["SCCM"]);
        Assert.Equal(System.Windows.Visibility.Collapsed, locks["Placeholders"]);
    }

    // ------------------------------------------------------------------
    // Catalog contracts
    // ------------------------------------------------------------------

    [Fact]
    public void Catalog_NeverContainsNonPortableProperties()
    {
        var forbidden = SettingsPortability.StrippedProperties;
        foreach (var def in PolicyCatalog.All)
        {
            var leaf = def.Key.Contains('.') ? def.Key.Split('.')[^1] : def.Key;
            Assert.DoesNotContain(leaf, forbidden);
            Assert.DoesNotContain(def.Key, forbidden);
        }
    }

    [Fact]
    public void Catalog_EveryPathResolves_AndRoundTrips()
    {
        var settings = new AppSettings();
        foreach (var def in PolicyCatalog.All)
        {
            // Factory value must be readable…
            var factory = PolicyCatalog.GetFactoryValue(def.Key);
            // …and a kind-appropriate write must succeed.
            object value = def.Kind switch
            {
                PolicyValueKind.Bool => true,
                PolicyValueKind.Int => 42,
                _ => def.AllowedValues is { Length: > 0 } allowed ? allowed[0] : "policy-value",
            };
            Assert.True(PolicyCatalog.SetValue(settings, def.Key, value),
                $"SetValue failed for catalog key '{def.Key}'");
            Assert.Equal(value, PolicyCatalog.GetValue(settings, def.Key));
            _ = factory; // readable is the assertion; value varies
        }
    }

    [Fact]
    public void Catalog_CoversTheSixDefaultBlocks()
    {
        foreach (var block in new[]
        {
            "IntunePackageDefaults", "IntuneMetadataDefaults", "IntuneAssignmentDefaults",
            "SccmPackageDefaults", "SccmMetadataDefaults", "SccmDeploymentDefaults",
        })
            Assert.Contains(PolicyCatalog.All, d => d.Key.StartsWith(block + ".", StringComparison.Ordinal));
    }
}
