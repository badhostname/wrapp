namespace Wrapp.Models;

/// <summary>Lightweight Intune app item for list display.</summary>
public class IntuneAppSummary
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string AppVersion { get; init; } = "";
    public string TenantId { get; init; } = "";
    public string TenantName { get; init; } = "";
    public int AssignmentCount { get; init; }
    public int DependencyCount { get; init; }
    public int SupersedenceCount { get; init; }
    public DateTime? LastModified { get; init; }

    /// <summary>Size of the .intunewin content in bytes.</summary>
    public long SizeInBytes { get; init; }

    /// <summary>Applicable architectures (x86, x64, arm, etc.).</summary>
    public string Architecture { get; init; } = "";

    /// <summary>Minimum supported Windows release.</summary>
    public string MinOSVersion { get; init; } = "";

    /// <summary>Assignment group IDs/names for group-based searching.</summary>
    public List<string> AssignmentGroups { get; init; } = new();

    /// <summary>Assignment intents for intent-based filtering.</summary>
    public List<string> AssignmentIntents { get; init; } = new();

    /// <summary>True when nested group membership has been resolved for this app's assignments.</summary>
    public bool HasNestedGroupData { get; set; }

    /// <summary>Search-friendly composite for filtering.</summary>
    public string SearchText => $"{DisplayName} {Publisher} {AppVersion}".ToLowerInvariant();

    /// <summary>Extended search text including assignment groups.</summary>
    public string FullSearchText =>
        $"{DisplayName} {Publisher} {AppVersion} {string.Join(" ", AssignmentGroups)}".ToLowerInvariant();
}
