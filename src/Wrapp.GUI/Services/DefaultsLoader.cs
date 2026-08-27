using System.IO;
using System.Linq;
using System.Text.Json;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Loads org-specific defaults (tenants, SCCM sites, domains, vault, update
/// feed, preference blocks) from <c>defaults.local.json</c>. The tracked
/// <c>defaults.example.json</c> is DOCUMENTATION ONLY and is never loaded.
///
/// This keeps sensitive org details out of source control while letting an
/// organization ship a fully-preset Wrapp — either by placing the file in one
/// of <see cref="CandidatePaths"/>, or by the technician choosing it in the
/// first-run setup gate (<see cref="Gates.FirstRunDefaultsGate"/>).
/// </summary>
public static class DefaultsLoader
{
    private const string LocalFile = "defaults.local.json";

    /// <summary>
    /// Candidate locations for defaults.local.json, most specific first:
    /// <list type="number">
    /// <item><description>Beside the exe (portable / dev layout).</description></item>
    /// <item><description>The install ROOT — one level above the app dir. For a
    ///   Velopack install the exe lives in <c>...\WrappApp\current\</c>, which
    ///   is REPLACED wholesale on every update; a defaults file placed there
    ///   would silently vanish at the first update. The parent
    ///   (<c>...\WrappApp\</c>) is stable across updates.</description></item>
    /// <item><description><c>%ProgramData%\Wrapp</c> — machine-wide
    ///   provisioning (per-machine installs, config-managed fleets).</description></item>
    /// <item><description>The source project dir (dotnet run / debug).</description></item>
    /// </list>
    /// </summary>
    /// <summary>
    /// Policy-supplied path checked BEFORE every built-in location
    /// (OrgDefaultsPath policy). Property injection keeps this loader
    /// policy-agnostic and testable; set by App startup from the snapshot.
    /// </summary>
    internal static string? PolicyPathOverride { get; set; }

    internal static IEnumerable<string> CandidatePaths()
    {
        if (!string.IsNullOrWhiteSpace(PolicyPathOverride))
            yield return PolicyPathOverride;

        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        yield return Path.Combine(appDir, LocalFile);

        var parent = Directory.GetParent(appDir.TrimEnd(Path.DirectorySeparatorChar))?.FullName;
        if (parent is not null) yield return Path.Combine(parent, LocalFile);

        yield return UserDataDefaultsPath();

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(programData)) yield return Path.Combine(programData, "Wrapp", LocalFile);

        var srcDir = FindSourceDirectory(appDir);
        if (srcDir is not null) yield return Path.Combine(srcDir, LocalFile);
    }

    /// <summary>
    /// <c>%LOCALAPPDATA%\Wrapp\defaults.local.json</c> — Wrapp's own data root,
    /// beside settings.json. Survives EVERYTHING: in-app updates, MSI upgrades,
    /// and uninstall (Velopack's cleanup removes the install tree and
    /// <c>%LOCALAPPDATA%\WrappApp</c>, never our data root — which is exactly
    /// why the packId is WrappApp and not Wrapp).
    /// </summary>
    internal static string UserDataDefaultsPath()
        => Path.Combine(PlatformConfig.WrappRoot, LocalFile);

    /// <summary>First existing defaults.local.json, or null. Public for the first-run setup gate.</summary>
    public static string? FindDefaultsFile()
        => CandidatePaths().FirstOrDefault(File.Exists);

    // -----------------------------------------------------------------------
    // Cached load. SettingsViewModel's DefaultTenants/Sites/Domains read the
    // org defaults on every property access, so the parse is cached — but the
    // cache must be INVALIDATED when a new file is imported at runtime
    // (FirstRunDefaultsGate.TryImport), or the just-imported tenants stay
    // invisible until the app restarts.
    // -----------------------------------------------------------------------

    private static OrgDefaults? _cached;

    /// <summary>Cached <see cref="Load"/>; refreshed after an import.</summary>
    public static OrgDefaults LoadCached() => _cached ??= Load();

    /// <summary>Drops the cache so the next <see cref="LoadCached"/> re-reads disk.</summary>
    public static void InvalidateCache() => _cached = null;

    /// <summary>
    /// Where an imported company defaults file is written: Wrapp's own data
    /// root (<see cref="UserDataDefaultsPath"/>).
    /// <para>
    /// NOT the install root, and NOT <c>current\</c>. Both are destroyed by the
    /// updater: <c>current\</c> is replaced wholesale on every in-app update,
    /// and an MSI major upgrade uninstalls the previous product first — whose
    /// cleanup action deletes the install directory AND
    /// <c>%LOCALAPPDATA%\WrappApp</c> outright. An org defaults file in either
    /// place would silently vanish at the next update, taking the technician's
    /// tenants and sites with it. The data root is the only location the
    /// installer never touches. (Both remain PROBE locations in
    /// <see cref="CandidatePaths"/> so an org can still pre-place a file there.)
    /// </para>
    /// </summary>
    public static string DurableDefaultsPath() => UserDataDefaultsPath();

    public static OrgDefaults Load()
    {
        // Workstream O: ONLY defaults.local.json is authoritative. The example
        // file is documentation -- seeding its "My Dev Tenant" / CONTOSO
        // placeholders into a real settings.json (pre-O behaviour) left fake
        // org data persisted as if real on public-build first runs.
        var filePath = FindDefaultsFile();

        if (filePath is null)
        {
            AppLogger.Info("DefaultsLoader: no defaults.local.json found (exe dir, install root, ProgramData\\Wrapp); using empty defaults");
            return new OrgDefaults();
        }

        var source = filePath;
        try
        {
            var json = File.ReadAllText(filePath);
            var defaults = JsonSerializer.Deserialize<OrgDefaults>(json, JsonDefaults.CaseInsensitive)
                           ?? new OrgDefaults();

            AppLogger.Info($"DefaultsLoader: loaded {defaults.IntuneTenants.Count} tenant(s), " +
                           $"{defaults.SCCMSites.Count} site(s), {defaults.Domains.Count} domain(s) from {source}");
            return defaults;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"DefaultsLoader: failed to parse {source}: {ex.Message}");
            return new OrgDefaults();
        }
    }

    /// <summary>
    /// When running via "dotnet run", BaseDirectory is bin/Debug/net8.0-windows/.
    /// Walk up looking for the .csproj to find the source root.
    /// </summary>
    private static string? FindSourceDirectory(string startDir)
    {
        var dir = startDir;
        for (int i = 0; i < 6; i++)
        {
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
            if (Directory.GetFiles(dir, "*.csproj").Length > 0)
                return dir;
        }
        return null;
    }
}

/// <summary>DTO matching the JSON shape of defaults.local.json / defaults.example.json.</summary>
public class OrgDefaults
{
    public List<OrgTenantDefault> IntuneTenants { get; set; } = new();
    public List<OrgSiteDefault> SCCMSites { get; set; } = new();
    public List<OrgDomainDefault> Domains { get; set; } = new();

    /// <summary>Org-specific regexes scrubbed from every log line (tenant GUIDs, hostnames, domains).</summary>
    public List<string> SensitivePatterns { get; set; } = new();

    // ---- Workstream O additions: nullable = "not specified in the file" ----

    /// <summary>DevOps key-vault settings (repo URL, path/PR templates).</summary>
    public OrgVaultDefaults? Vault { get; set; }

    /// <summary>Endpoint script paths ({{TagFolder}} / {{LocalAppFolder}} expansion).</summary>
    public OrgEndpointDefaults? Endpoint { get; set; }

    /// <summary>Workstream D: update feed URL + mode for installed Wrapp.</summary>
    public OrgUpdateDefaults? Update { get; set; }

    /// <summary>
    /// Workstream P: org-defined custom placeholders. Seeded add-missing
    /// (never overwriting a technician's own placeholder of the same name).
    /// Sensitive entries may be declared here NAME-ONLY — org files never
    /// carry secret values; each technician supplies theirs once in Settings.
    /// </summary>
    public List<PlaceholderEntry> Placeholders { get; set; } = new();

    // Preferences default blocks -- same JSON shape as the matching sections
    // of settings.json, so an org author can copy a configured settings.json
    // section straight into defaults.local.json.
    public IntunePackageDefaults?    IntunePackageDefaults    { get; set; }
    public IntuneMetadataDefaults?   IntuneMetadataDefaults   { get; set; }
    public IntuneAssignmentDefaults? IntuneAssignmentDefaults { get; set; }
    public SccmPackageDefaults?      SccmPackageDefaults      { get; set; }
    public SccmMetadataDefaults?     SccmMetadataDefaults     { get; set; }
    public SccmDeploymentDefaults?   SccmDeploymentDefaults   { get; set; }
}

/// <summary>Org defaults for the DevOps key vault. Null members = not specified.</summary>
public class OrgVaultDefaults
{
    public string? KeyVaultRepoUrl                { get; set; }
    public bool?   EnableAzureDevOpsKeyVault      { get; set; }
    public string? KeyVaultPathTemplate           { get; set; }
    public string? KeyVaultManualPathTemplate     { get; set; }
    public bool?   KeyVaultUsePullRequests        { get; set; }
    public string? KeyVaultPrSourceBranchTemplate { get; set; }
    public string? KeyVaultPrTitleTemplate        { get; set; }
    public string? KeyVaultPrDescriptionTemplate  { get; set; }
}

/// <summary>Org defaults for endpoint script paths. Null members = not specified.</summary>
public class OrgEndpointDefaults
{
    public string? TagFolder      { get; set; }
    public string? LocalAppFolder { get; set; }
}

/// <summary>
/// Workstream D: org defaults for the update channel. Seeding the feed URL
/// does NOT approve it — each technician still confirms the URL once (the
/// SEC-1 trust-token gate), so a tampered defaults file cannot silently point
/// installs at a malicious feed.
/// </summary>
public class OrgUpdateDefaults
{
    /// <summary>HTTPS URL or UNC share hosting the Velopack release feed.</summary>
    public string? FeedUrl { get; set; }

    /// <summary>"Auto", "NotifyOnly", or "Disabled" (seed NotifyOnly/Disabled for per-machine MSI fleets).</summary>
    public string? Mode { get; set; }

    /// <summary>
    /// Optional override for the "View all releases" link in About / What's-New
    /// (defaults to the public Wrapp changelog on GitHub). Point it at an
    /// internal release-notes page for fleets without GitHub access.
    /// </summary>
    public string? ReleasesUrl { get; set; }
}

public class OrgTenantDefault
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Comment { get; set; } = "";
    public string ClientID { get; set; } = "";
    public AuthFlow AuthFlow { get; set; } = AuthFlow.Interactive;
}

public class OrgSiteDefault
{
    public string Key { get; set; } = "";
    public string Comment { get; set; } = "";
    public string AppFolder { get; set; } = "";
    public List<string> DeploymentGroups { get; set; } = new();
}

public class OrgDomainDefault
{
    public string Key { get; set; } = "";
    public string IsDistPath { get; set; } = "";
    public string AppFolder { get; set; } = "";
    public string TagFolder { get; set; } = "";
}
