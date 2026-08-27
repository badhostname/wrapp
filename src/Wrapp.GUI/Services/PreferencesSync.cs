using System.Collections.ObjectModel;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Sync helpers for applying Preferences (settings.json) or App Defaults
/// (defaults.local.json) to a bundle's tenant / site collections.
///
/// <para>Two modes per call:</para>
/// <list type="bullet">
///   <item><b>Overwrite</b> — replace the target collection entirely with a
///   deep clone of the source. Destructive; callers should confirm.</item>
///   <item><b>AddMissing</b> — append entries from the source whose Key
///   doesn't already exist in the target. Non-destructive; existing entries
///   are left untouched.</item>
/// </list>
///
/// All operations mutate the target collection in memory only. The bundle
/// must be saved explicitly via Save Bundle for changes to reach disk —
/// matching the app's "no auto-save" safety posture.
/// </summary>
public static class PreferencesSync
{
    // -----------------------------------------------------------------------
    // Intune tenants
    // -----------------------------------------------------------------------

    public static int OverwriteTenants(
        ObservableCollection<IntuneTenantEntry> target,
        IEnumerable<IntuneTenantEntry> source)
    {
        target.Clear();
        var count = 0;
        foreach (var t in source)
        {
            target.Add(CloneTenant(t));
            count++;
        }
        return count;
    }

    public static int AddMissingTenants(
        ObservableCollection<IntuneTenantEntry> target,
        IEnumerable<IntuneTenantEntry> source)
    {
        var existingKeys = new HashSet<string>(
            target.Select(t => t.Key ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var t in source)
        {
            if (string.IsNullOrEmpty(t.Key)) continue;
            if (existingKeys.Contains(t.Key)) continue;
            target.Add(CloneTenant(t));
            existingKeys.Add(t.Key);
            added++;
        }
        return added;
    }

    private static IntuneTenantEntry CloneTenant(IntuneTenantEntry src) => new()
    {
        Key                            = src.Key,
        Name                           = src.Name,
        Comment                        = src.Comment,
        ClientID                       = src.ClientID,
        AuthFlow                       = src.AuthFlow,
        // Carry both the transient SecureString (if the user just typed one)
        // and the stored DPAPI cipher — the clone needs to be functionally
        // equivalent for auth, and dropping the cipher would silently blank
        // saved secrets.
        // STA-2 (2026-07 audit): deep-copy the transient SecureString so the
        // clone owns an INDEPENDENT instance. Sharing the reference is a
        // use-after-dispose hazard: the save path (SettingsService) disposes
        // the source entry's SecureString after persisting, which would strand
        // this clone with a disposed instance -- a later `Length` check
        // (e.g. bundle serialize) then throws ObjectDisposedException.
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

    // -----------------------------------------------------------------------
    // SCCM sites
    // -----------------------------------------------------------------------

    public static int OverwriteSites(
        ObservableCollection<SCCMSiteEntry> target,
        IEnumerable<SCCMSiteEntry> source)
    {
        target.Clear();
        var count = 0;
        foreach (var s in source)
        {
            target.Add(CloneSite(s));
            count++;
        }
        return count;
    }

    public static int AddMissingSites(
        ObservableCollection<SCCMSiteEntry> target,
        IEnumerable<SCCMSiteEntry> source)
    {
        var existingKeys = new HashSet<string>(
            target.Select(s => s.Key ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var s in source)
        {
            if (string.IsNullOrEmpty(s.Key)) continue;
            if (existingKeys.Contains(s.Key)) continue;
            target.Add(CloneSite(s));
            existingKeys.Add(s.Key);
            added++;
        }
        return added;
    }

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

    // -----------------------------------------------------------------------
    // Domains (bundle Config.json Domain section)
    // -----------------------------------------------------------------------

    public static int OverwriteDomains(
        ObservableCollection<DomainEntry> target,
        IEnumerable<DomainEntry> source)
    {
        target.Clear();
        var count = 0;
        foreach (var d in source)
        {
            target.Add(CloneDomain(d));
            count++;
        }
        return count;
    }

    public static int AddMissingDomains(
        ObservableCollection<DomainEntry> target,
        IEnumerable<DomainEntry> source)
    {
        var existingKeys = new HashSet<string>(
            target.Select(d => d.Key ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var d in source)
        {
            if (string.IsNullOrEmpty(d.Key)) continue;
            if (existingKeys.Contains(d.Key)) continue;
            target.Add(CloneDomain(d));
            existingKeys.Add(d.Key);
            added++;
        }
        return added;
    }

    private static DomainEntry CloneDomain(DomainEntry src) => new()
    {
        Key        = src.Key,
        IsDistPath = src.IsDistPath,
        AppFolder  = src.AppFolder,
        TagFolder  = src.TagFolder,
    };
}
