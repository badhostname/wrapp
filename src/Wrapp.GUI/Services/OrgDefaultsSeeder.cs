using System.Text.Json;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Seeds org-shipped defaults (defaults.local.json) into a
/// technician's settings.json.
/// <para>
/// Semantics: runs ONCE per user profile (gated by
/// <see cref="AppSettings.OrgDefaultsSeeded"/>), and within that one pass a
/// value is applied only when the current setting still equals its factory
/// default -- a technician's explicit choice is never overwritten, even on
/// the profiles that predate this feature. Because it is one-shot, the
/// defaults file must be present at FIRST launch; adding it later reaches
/// only fresh profiles (documented in defaults.example.json).
/// </para>
/// <para>
/// Deliberately NOT seedable: client secrets (DPAPI per-user -- technicians
/// enter them once, or use the ClientCert flow whose thumbprint IS seedable
/// via tenant defaults) and the vault-URL trust approval (the per-user gate
/// still prompts even for a pre-filled KeyVaultRepoUrl -- a tampered
/// defaults file must not silently redirect key material).
/// </para>
/// </summary>
public static class OrgDefaultsSeeder
{
    /// <summary>
    /// The complete import sequence for an org defaults file - redaction
    /// wiring, seeding, one-shot flag, persistence, template pack. Extracted
    /// because it existed as two hand-copied five-step blocks (first-run gate
    /// and Settings import) that had to stay identical by discipline alone.
    /// </summary>
    public static bool ApplyImported(AppSettings settings, OrgDefaults imported)
    {
        AppLogger.SetOrgRedactionPatterns(imported.SensitivePatterns);
        var changed = Apply(settings, imported);
        if (changed)
            AppLogger.Info("OrgDefaults: seeded from the imported organization defaults file");
        settings.OrgDefaultsSeeded = true;

        // Policy outranks the org file on EVERY path. The seeder only writes
        // onto factory values - but a mandated value that happens to EQUAL
        // its factory default looks factory to SeedString, so without this
        // re-assert an org-file import could displace it until the next
        // launch.
        Policy.PolicyService.ApplyMandatory(settings);

        SettingsService.Save(settings);
        TemplateService.EnsureOrgTemplatePack();
        return changed;
    }

    /// <summary>
    /// Applies <paramref name="org"/> onto factory-default fields of
    /// <paramref name="settings"/>. Returns true when anything changed.
    /// Pure (no I/O): the caller persists and sets the one-shot flag.
    /// </summary>
    public static bool Apply(AppSettings settings, OrgDefaults org)
    {
        var factory = new AppSettings();
        var changed = false;

        // ---- Vault ----
        if (org.Vault is { } v)
        {
            changed |= SeedString(v.KeyVaultRepoUrl,                factory.KeyVaultRepoUrl,                settings.KeyVaultRepoUrl,                s => settings.KeyVaultRepoUrl = s);
            changed |= SeedString(v.KeyVaultPathTemplate,           factory.KeyVaultPathTemplate,           settings.KeyVaultPathTemplate,           s => settings.KeyVaultPathTemplate = s);
            changed |= SeedString(v.KeyVaultManualPathTemplate,     factory.KeyVaultManualPathTemplate,     settings.KeyVaultManualPathTemplate,     s => settings.KeyVaultManualPathTemplate = s);
            changed |= SeedString(v.KeyVaultPrSourceBranchTemplate, factory.KeyVaultPrSourceBranchTemplate, settings.KeyVaultPrSourceBranchTemplate, s => settings.KeyVaultPrSourceBranchTemplate = s);
            changed |= SeedString(v.KeyVaultPrTitleTemplate,        factory.KeyVaultPrTitleTemplate,        settings.KeyVaultPrTitleTemplate,        s => settings.KeyVaultPrTitleTemplate = s);
            changed |= SeedString(v.KeyVaultPrDescriptionTemplate,  factory.KeyVaultPrDescriptionTemplate,  settings.KeyVaultPrDescriptionTemplate,  s => settings.KeyVaultPrDescriptionTemplate = s);

            if (v.EnableAzureDevOpsKeyVault is { } enable
                && settings.EnableAzureDevOpsKeyVault == factory.EnableAzureDevOpsKeyVault
                && enable != settings.EnableAzureDevOpsKeyVault)
            {
                settings.EnableAzureDevOpsKeyVault = enable;
                changed = true;
            }
            if (v.KeyVaultUsePullRequests is { } usePr
                && settings.KeyVaultUsePullRequests == factory.KeyVaultUsePullRequests
                && usePr != settings.KeyVaultUsePullRequests)
            {
                settings.KeyVaultUsePullRequests = usePr;
                changed = true;
            }
        }

        // ---- Endpoint script paths ----
        if (org.Endpoint is { } ep)
        {
            changed |= SeedString(ep.TagFolder,      factory.EndpointTagFolder,      settings.EndpointTagFolder,      s => settings.EndpointTagFolder = s);
            changed |= SeedString(ep.LocalAppFolder, factory.EndpointLocalAppFolder, settings.EndpointLocalAppFolder, s => settings.EndpointLocalAppFolder = s);
        }

        // ---- Update channel. The feed URL seeds like any
        //      string; the TRUST TOKEN is deliberately never seedable -- the
        //      per-user approval gate prompts before the feed is contacted. ----
        if (org.Update is { } up)
        {
            changed |= SeedString(up.FeedUrl, factory.UpdateFeedUrl, settings.UpdateFeedUrl, s => settings.UpdateFeedUrl = s);
            changed |= SeedString(up.Mode,    factory.UpdateMode,    settings.UpdateMode,    s => settings.UpdateMode = s);
        }

        // ---- Custom placeholders: add-missing by name, never
        //      overwrite; reserved/invalid names skipped; sensitive entries
        //      arrive value-less by design (values are per-technician). ----
        foreach (var ph in org.Placeholders)
        {
            if (!PlaceholderService.IsValidCustomName(ph.Name)) continue;
            if (settings.Placeholders.Any(p =>
                    string.Equals(p.Name, ph.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            settings.Placeholders.Add(new PlaceholderEntry
            {
                Name        = ph.Name,
                Value       = ph.IsSensitive ? string.Empty : ph.Value,
                IsSensitive = ph.IsSensitive,
                Comment     = ph.Comment,
            });
            changed = true;
        }

        // ---- Preferences default blocks (applied whole, only while the
        //      technician's block is still byte-identical to factory) ----
        changed |= SeedBlock(org.IntunePackageDefaults,    settings.IntunePackageDefaults,    factory.IntunePackageDefaults,    b => settings.IntunePackageDefaults = b);
        changed |= SeedBlock(org.IntuneMetadataDefaults,   settings.IntuneMetadataDefaults,   factory.IntuneMetadataDefaults,   b => settings.IntuneMetadataDefaults = b);
        changed |= SeedBlock(org.IntuneAssignmentDefaults, settings.IntuneAssignmentDefaults, factory.IntuneAssignmentDefaults, b => settings.IntuneAssignmentDefaults = b);
        changed |= SeedBlock(org.SccmPackageDefaults,      settings.SccmPackageDefaults,      factory.SccmPackageDefaults,      b => settings.SccmPackageDefaults = b);
        changed |= SeedBlock(org.SccmMetadataDefaults,     settings.SccmMetadataDefaults,     factory.SccmMetadataDefaults,     b => settings.SccmMetadataDefaults = b);
        changed |= SeedBlock(org.SccmDeploymentDefaults,   settings.SccmDeploymentDefaults,   factory.SccmDeploymentDefaults,   b => settings.SccmDeploymentDefaults = b);

        return changed;
    }

    /// <summary>Applies an org string when provided and the current value is empty or still factory.</summary>
    private static bool SeedString(string? orgValue, string factoryValue, string currentValue, Action<string> set)
    {
        if (string.IsNullOrWhiteSpace(orgValue)) return false;
        var atFactory = string.IsNullOrWhiteSpace(currentValue)
                     || string.Equals(currentValue, factoryValue, StringComparison.Ordinal);
        if (!atFactory || string.Equals(currentValue, orgValue, StringComparison.Ordinal)) return false;
        set(orgValue);
        return true;
    }

    /// <summary>Applies an org block when provided and the current block is still factory-identical.</summary>
    private static bool SeedBlock<T>(T? orgBlock, T currentBlock, T factoryBlock, Action<T> set) where T : class
    {
        if (orgBlock is null) return false;
        if (!JsonEquals(currentBlock, factoryBlock)) return false;
        if (JsonEquals(currentBlock, orgBlock)) return false;
        set(orgBlock);
        return true;
    }

    private static bool JsonEquals<T>(T a, T b)
        => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
}
