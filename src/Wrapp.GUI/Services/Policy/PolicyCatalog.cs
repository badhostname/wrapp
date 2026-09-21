using System.Reflection;
using Wrapp.Models;

namespace Wrapp.Services.Policy;

/// <summary>Value type a policy carries (maps to REG_SZ / REG_DWORD).</summary>
public enum PolicyValueKind { String, Bool, Int }

/// <summary>
/// One administrable setting: the catalog key doubles as the registry value
/// name (dots become subkeys: <c>IntunePackageDefaults.InstallExperience</c>
/// is the <c>InstallExperience</c> value under the
/// <c>IntunePackageDefaults</c> subkey) and as the reflection path into
/// <see cref="AppSettings"/>.
/// </summary>
public sealed record PolicyDefinition(
    string Key,
    PolicyValueKind Kind,
    string Category,
    string Description)
{
    /// <summary>Allowed values for enum-like strings; null = free text.</summary>
    public string[]? AllowedValues { get; init; }
}

/// <summary>
/// The single source of truth for the policy surface. The registry reader,
/// the AppSettings applier, the offline script's JSON schema, the ADMX, and
/// the admin docs all derive from (or are contract-tested against) this
/// list. Top-level scalars are explicit; the six preference blocks are
/// reflected so a new block property is automatically administrable.
/// <para>Never-controllable properties (trust tokens, secrets, gate state)
/// are exactly <see cref="SettingsPortability.StrippedProperties"/> - a
/// contract test asserts the catalog stays disjoint from that list.</para>
/// </summary>
public static class PolicyCatalog
{
    /// <summary>The six nested default blocks reflected into the catalog.</summary>
    internal static readonly string[] BlockProperties =
    {
        nameof(AppSettings.IntunePackageDefaults),
        nameof(AppSettings.IntuneMetadataDefaults),
        nameof(AppSettings.IntuneAssignmentDefaults),
        nameof(AppSettings.SccmPackageDefaults),
        nameof(AppSettings.SccmMetadataDefaults),
        nameof(AppSettings.SccmDeploymentDefaults),
    };

    private static readonly PolicyDefinition[] TopLevel =
    {
        // ---- Tier 1: security ----
        new("UpdateFeedUrl", PolicyValueKind.String, "Updates",
            "Velopack release feed (https, UNC, or local path). Policy values must pass the same validation as user input."),
        new("UpdateMode", PolicyValueKind.String, "Updates",
            "Update behavior at launch.") { AllowedValues = AppUpdateModes.All },
        new("KeyVaultRepoUrl", PolicyValueKind.String, "Key Vault",
            "Azure DevOps repository receiving encryption keys. Policy values must be https."),
        new("EnableAzureDevOpsKeyVault", PolicyValueKind.Bool, "Key Vault",
            "Master switch for all Key Vault reads/writes."),
        new("KeyVaultPathTemplate", PolicyValueKind.String, "Key Vault",
            "Repo path template for auto-captured keys."),
        new("KeyVaultManualPathTemplate", PolicyValueKind.String, "Key Vault",
            "Repo path template for manually saved keys."),
        new("KeyVaultUsePullRequests", PolicyValueKind.Bool, "Key Vault",
            "Push keys via pull request instead of a direct commit."),
        new("KeyVaultPrSourceBranchTemplate", PolicyValueKind.String, "Key Vault",
            "Branch name template for key pull requests."),
        new("KeyVaultPrTitleTemplate", PolicyValueKind.String, "Key Vault",
            "Title template for key pull requests."),
        new("KeyVaultPrDescriptionTemplate", PolicyValueKind.String, "Key Vault",
            "Body template for key pull requests."),

        // ---- Tier 2: operational ----
        new("EndpointTagFolder", PolicyValueKind.String, "Endpoints",
            "On-endpoint folder for detection tags and transcripts (baked into generated scripts)."),
        new("EndpointLocalAppFolder", PolicyValueKind.String, "Endpoints",
            "On-endpoint root for per-app local assets."),
        new("PsadtTemplatePath", PolicyValueKind.String, "Bundle",
            "Path to the extracted PSADT v4 template folder."),
        new("DirectoryFormat", PolicyValueKind.String, "Bundle",
            "Bundle output subdirectory naming template."),
        new("IconFolderName", PolicyValueKind.String, "Bundle",
            "Folder name inside each bundle for the app icon."),
        new("VerboseUiTrace", PolicyValueKind.Bool, "Diagnostics",
            "Force verbose [TRACE] UI logging on or off."),

        // ---- Tier 3: appearance ----
        new("Theme", PolicyValueKind.String, "Appearance",
            "UI theme: Dark, Light, or the name of a custom theme."),
    };

    /// <summary>
    /// Meta-policies with no AppSettings counterpart. Read directly by
    /// <see cref="PolicyService"/>; listed here so the script schema, ADMX
    /// and docs enumerate one surface.
    /// </summary>
    public static readonly PolicyDefinition[] Meta =
    {
        new("OrgDefaultsPath", PolicyValueKind.String, "Provisioning",
            "Full path to the organization defaults JSON (checked before all built-in locations)."),
        new("ThemeFilePath", PolicyValueKind.String, "Appearance",
            "Full path to an organization .wrapptheme.json distributed by the admin."),
        new("DisableSettingsImport", PolicyValueKind.Bool, "Provisioning",
            "Hide and block the Export/Import settings card."),
        new("DisableOrgDefaultsImport", PolicyValueKind.Bool, "Provisioning",
            "Hide and block the organization-defaults import card."),
    };

    private static List<PolicyDefinition>? _all;

    /// <summary>Every settings-backed policy (top-level + reflected block leaves).</summary>
    public static IReadOnlyList<PolicyDefinition> All => _all ??= Build();

    private static List<PolicyDefinition> Build()
    {
        var list = new List<PolicyDefinition>(TopLevel);
        foreach (var blockName in BlockProperties)
        {
            var blockType = typeof(AppSettings).GetProperty(blockName)!.PropertyType;
            foreach (var leaf in blockType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!leaf.CanRead || !leaf.CanWrite) continue;
                var kind = leaf.PropertyType == typeof(bool) ? PolicyValueKind.Bool
                         : leaf.PropertyType == typeof(int) ? PolicyValueKind.Int
                         : leaf.PropertyType == typeof(string) ? PolicyValueKind.String
                         : (PolicyValueKind?)null ?? default;
                if (leaf.PropertyType != typeof(bool)
                    && leaf.PropertyType != typeof(int)
                    && leaf.PropertyType != typeof(string))
                    continue; // enum-typed leaves (e.g. package UpdateMode) stay UI-only
                list.Add(new PolicyDefinition(
                    $"{blockName}.{leaf.Name}", kind, BlockCategory(blockName),
                    $"Default for {blockName}.{leaf.Name}."));
            }
        }
        return list;
    }

    private static string BlockCategory(string block)
        => block.StartsWith("Intune", StringComparison.Ordinal)
            ? "Defaults (Intune)" : "Defaults (SCCM)";

    public static PolicyDefinition? Find(string key)
        => All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase))
           ?? Meta.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

    // -------------------------------------------------------------------
    // Reflection helpers shared by apply + factory-default comparison
    // -------------------------------------------------------------------

    private static readonly AppSettings Factory = new();

    /// <summary>Reads the value at a catalog key's path on <paramref name="settings"/>.</summary>
    public static object? GetValue(AppSettings settings, string key)
    {
        var (target, prop) = Resolve(settings, key);
        return target is null ? null : prop!.GetValue(target);
    }

    /// <summary>The factory-default value for a catalog key (from <c>new AppSettings()</c>).</summary>
    public static object? GetFactoryValue(string key) => GetValue(Factory, key);

    /// <summary>Writes <paramref name="value"/> at the catalog key's path. Returns false when the path is invalid.</summary>
    public static bool SetValue(AppSettings settings, string key, object value)
    {
        var (target, prop) = Resolve(settings, key);
        if (target is null) return false;
        try { prop!.SetValue(target, value); return true; }
        catch { return false; }
    }

    private static (object? Target, PropertyInfo? Prop) Resolve(AppSettings settings, string key)
    {
        object current = settings;
        var parts = key.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            var prop = current.GetType().GetProperty(parts[i]);
            if (prop is null) return (null, null);
            if (i == parts.Length - 1) return (current, prop);
            current = prop.GetValue(current)!;
            if (current is null) return (null, null);
        }
        return (null, null);
    }
}
