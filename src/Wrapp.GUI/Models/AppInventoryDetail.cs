using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace Wrapp.Models;

/// <summary>Full app detail for the right panel. Works for both Intune and SCCM.</summary>
public class AppInventoryDetail
{
    public AppPlatform Platform { get; init; } = AppPlatform.Intune;
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public string Description { get; init; } = "";
    public string CreatedDateTime { get; init; } = "";
    public string LastModifiedDateTime { get; init; } = "";
    [JsonIgnore] public BitmapImage? Icon { get; set; }
    /// <summary>Raw base64 icon data from Graph API (preserved for file export during import).</summary>
    [JsonIgnore] public string IconBase64 { get; set; } = "";

    // App Information (Intune section 1)
    public string Developer { get; init; } = "";
    public string Owner { get; init; } = "";
    public string Notes { get; init; } = "";
    public string InformationUrl { get; init; } = "";
    public string PrivacyUrl { get; init; } = "";
    public bool IsFeatured { get; init; }

    // Program (Intune section 2)
    public string InstallCommand { get; init; } = "";
    public string UninstallCommand { get; init; } = "";
    public string RepairCommand { get; init; } = "";
    public string InstallExperience { get; init; } = "";
    public string RestartBehavior { get; init; } = "";
    public int MaxInstallTime { get; init; }

    // Install script content (if command references a .ps1)
    public string InstallScript { get; init; } = "";
    public bool HasInstallScript => !string.IsNullOrEmpty(InstallScript);

    // SCCM-specific
    public string ContentLocation { get; init; } = "";
    public string InstallationBehaviorType { get; init; } = "";
    public string DeploymentTypeName { get; init; } = "";
    public string Technology { get; init; } = "";          // e.g. Script, MSI, AppV
    public bool IsEnabled { get; init; } = true;
    public bool IsExpired { get; init; }
    public bool IsSuperseded { get; init; }
    public string CreatedBy { get; init; } = "";
    public string LastModifiedBy { get; init; } = "";
    public int NumberOfDeploymentTypes { get; init; }
    public int EstimatedInstallTime { get; init; }
    public string ObjectPath { get; init; } = "";          // Console folder

    // Detection
    public string DetectionType { get; init; } = "";
    public string DetectionSummary { get; init; } = "";
    public string DetectionScript { get; init; } = "";
    public bool HasDetectionScript => !string.IsNullOrEmpty(DetectionScript);

    // Content
    public long SizeInBytes { get; init; }
    public string FileName { get; init; } = ""; // .intunewin file name
    [JsonIgnore] public string SizeDisplay => SizeInBytes > 0
        ? SizeInBytes >= 1_073_741_824 ? $"{SizeInBytes / 1_073_741_824.0:F1} GB"
        : SizeInBytes >= 1_048_576 ? $"{SizeInBytes / 1_048_576.0:F1} MB"
        : $"{SizeInBytes / 1024.0:F0} KB"
        : "";

    // Requirements -- system
    public string MinimumOSVersion { get; init; } = "";
    public string Architecture { get; init; } = "";
    public int MinimumFreeDiskSpaceMB { get; init; }
    public int MinimumMemoryMB { get; init; }
    public int MinimumProcessors { get; init; }
    public int MinimumCpuSpeedMHz { get; init; }

    // Requirements -- custom rules
    public List<InventoryRequirementInfo> Requirements { get; init; } = new();

    // Collections
    public List<string> Categories { get; init; } = new();
    [JsonIgnore] public string CategoriesDisplay => Categories.Count > 0 ? string.Join(", ", Categories) : "-";
    public List<string> ScopeTags { get; init; } = new();
    [JsonIgnore] public string ScopeTagsDisplay => ScopeTags.Count > 0 ? string.Join(", ", ScopeTags) : "-";
    public List<InventoryAssignmentInfo> Assignments { get; init; } = new();

    /// <summary>Assignments sorted: Include first, then Exclude.</summary>
    [JsonIgnore] public List<InventoryAssignmentInfo> AssignmentsSorted =>
        Assignments
            .OrderBy(a => a.GroupMode.Equals("Exclude", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(a => a.Intent)
            .ToList();
    // Relationships are bucketed four ways - by `@odata.type` (Dependency vs Supersedence)
    // and direction (`targetType=child` means downstream of this app; `parent` means
    // upstream). Graph doesn't surface the "upstream" (reverse) direction in the Intune
    // UI, so DependedOnBy / SupersededBy expose data the admin console hides.
    public List<InventoryRelationshipInfo> Dependencies { get; set; } = new();  // this app depends on X
    public List<InventoryRelationshipInfo> DependedOnBy { get; set; } = new();  // X depends on this app
    public List<InventoryRelationshipInfo> Supersedence { get; set; } = new();  // this app supersedes X
    public List<InventoryRelationshipInfo> SupersededBy { get; set; } = new();  // X supersedes this app
    [JsonIgnore] public bool RelationshipsLoaded { get; set; }
    public List<InventoryReturnCodeInfo> ReturnCodes { get; init; } = new();
}

public class InventoryAssignmentInfo
{
    public string Intent { get; init; } = "";
    public string TargetType { get; init; } = "";
    public string TargetLabel { get; set; } = ""; // "All Devices", "All Users", or resolved group name
    public string GroupId { get; init; } = "";
    public string GroupMode { get; init; } = ""; // "Include" or "Exclude"
    public string Notification { get; init; } = "";
    public string AvailableTime { get; init; } = "";
    public string DeadlineTime { get; init; } = "";
    public string DeliveryOptimization { get; init; } = "";
    public string RestartGracePeriod { get; init; } = "";
    public string FilterId { get; init; } = "";
    public string FilterMode { get; init; } = ""; // "include" or "exclude"
    public string Source { get; init; } = ""; // "direct", "policySets", etc.

    /// <summary>Nested group membership data (populated by opt-in resolution).</summary>
    [JsonIgnore] public NestedGroupData? NestedGroups { get; set; }

    /// <summary>True when nested group data has been resolved for this assignment.</summary>
    [JsonIgnore] public bool HasNestedGroups => NestedGroups is not null;

    /// <summary>UI-only: true when this assignment matches the current search query.</summary>
    [JsonIgnore] public bool IsSearchMatch { get; set; }

    /// <summary>Composite for group-based search.</summary>
    [JsonIgnore] public string SearchText => $"{TargetLabel} {GroupId} {Intent}".ToLowerInvariant();
}

public class InventoryRelationshipInfo
{
    public string AppId { get; init; } = "";
    public string AppName { get; init; } = "";
    /// <summary>For supersedence: "update" (no uninstall) or "replace" (uninstall previous). For dependencies: "Dependency".</summary>
    public string Type { get; init; } = "";
    public bool AutoInstall { get; init; }
    public bool UninstallOld { get; init; }

    /// <summary>User-friendly label: "Yes" (replace) or "No" (update) for supersedence display.</summary>
    [JsonIgnore] public string UninstallLabel => string.Equals(Type, "replace", StringComparison.OrdinalIgnoreCase)
        ? "Yes" : "No";

    /// <summary>User-friendly label for auto-install on dependencies.</summary>
    [JsonIgnore] public string AutoInstallLabel => AutoInstall ? "Yes" : "No";
}

public class InventoryReturnCodeInfo
{
    public int Code { get; init; }
    public string Type { get; init; } = "";
}

public class InventoryRequirementInfo
{
    public string RuleType { get; init; } = ""; // "Script", "Registry", "File"
    public string Summary { get; init; } = "";
    public string ScriptContent { get; init; } = "";
}

/// <summary>Result from downloading and decrypting .intunewin content.</summary>
public class ContentDownloadResult
{
    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public long Size { get; init; }
    public long SizeEncrypted { get; init; }
}

/// <summary>Detected content type after inspecting a downloaded file.</summary>
public enum ContentType
{
    SingleFile,
    AppeaseBundle,
    PsadtBundle,
    CompressedArchive
}
