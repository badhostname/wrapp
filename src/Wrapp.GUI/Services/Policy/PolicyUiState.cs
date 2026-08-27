using System.Windows;

namespace Wrapp.Services.Policy;

/// <summary>Per-setting UI state for XAML binding, mirroring the
/// <c>FieldStates[...]</c> idiom: <c>IsEnabled="{Binding Policy[UpdateMode].IsEditable}"</c>.</summary>
public sealed record PolicyUiState(bool IsLocked, bool IsHidden)
{
    public bool IsEditable => !IsLocked;
    public Visibility Visibility => IsHidden ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Padlock glyph visibility beside a locked control.</summary>
    public Visibility LockVisibility => IsLocked ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Tooltip for locked controls (ShowOnDisabled is wired app-wide).</summary>
    public string? Reason => IsLocked ? "Managed by your organization" : null;
}

/// <summary>
/// Indexer over the policy snapshot. A block name (e.g.
/// <c>IntunePackageDefaults</c>) reads as locked when ANY of its leaves is
/// mandated — v1 locks preference blocks at card granularity.
/// </summary>
public sealed class PolicyUiStateAccessor
{
    public PolicyUiState this[string key]
    {
        get
        {
            var snap = PolicyService.Current;
            var locked = snap.IsManaged(key)
                || snap.Mandatory.Keys.Any(k => k.StartsWith(key + ".", StringComparison.OrdinalIgnoreCase));
            return new PolicyUiState(locked, snap.IsHidden(key));
        }
    }
}

/// <summary>Settings-tab visibility: <c>Visibility="{Binding PolicyTabs[KeyVault]}"</c>.</summary>
public sealed class PolicyTabAccessor
{
    public Visibility this[string tab]
        => PolicyService.Current.IsTabHidden(tab) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// Tab-header padlock: Visible when ANY policy touches that tab's content —
/// so a visible-but-managed tab announces itself before it's even opened
/// (hidden tabs are simply absent; this is the "see it but can't touch it"
/// half of lock-vs-hide). Usage: <c>Visibility="{Binding PolicyTabLocks[Updates]}"</c>.
/// </summary>
public sealed class PolicyTabLockAccessor
{
    public Visibility this[string tab]
        => IsTabTouched(tab) ? Visibility.Visible : Visibility.Collapsed;

    private static bool IsTabTouched(string tab)
    {
        var s = PolicyService.Current;
        bool AnyKey(params string[] prefixes) =>
            s.Mandatory.Keys.Any(k => prefixes.Any(p =>
                k.Equals(p, StringComparison.OrdinalIgnoreCase)
                || k.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase)
                || (p.EndsWith("*", StringComparison.Ordinal)
                    && k.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase))));

        return tab switch
        {
            "General" => AnyKey("Theme") || s.ThemeFilePath is not null,
            "Bundle" => AnyKey("DirectoryFormat", "IconFolderName", "PsadtTemplatePath"),
            "Domains" => s.DomainEntries.Count > 0,
            "Endpoint" => AnyKey("EndpointTagFolder", "EndpointLocalAppFolder"),
            "Intune" => AnyKey("IntunePackageDefaults", "IntuneMetadataDefaults", "IntuneAssignmentDefaults")
                        || s.TenantEntries.Count > 0,
            "SCCM" => AnyKey("SccmPackageDefaults", "SccmMetadataDefaults", "SccmDeploymentDefaults")
                      || s.SiteEntries.Count > 0,
            "KeyVault" => AnyKey("KeyVault*", "EnableAzureDevOpsKeyVault"),
            "Updates" => AnyKey("UpdateFeedUrl", "UpdateMode"),
            "Placeholders" => s.Placeholders.Count > 0,
            "Provisioning" => s.OrgDefaultsPath is not null || s.DisableSettingsImport || s.DisableOrgDefaultsImport,
            _ => false,
        };
    }
}

/// <summary>Nav-section visibility for MainWindow: <c>Visibility="{Binding Nav[Inventory]}"</c>.</summary>
public sealed class PolicyNavAccessor
{
    public Visibility this[string section]
        => PolicyService.Current.HiddenSections.Contains(section)
            ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>One row of the effective-policy table (Provisioning tab).</summary>
public sealed record ManagedPolicyRow(string Key, string Value, string Source);
