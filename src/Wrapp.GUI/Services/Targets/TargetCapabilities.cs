namespace Wrapp.Services.Targets;

/// <summary>
/// Feature flags describing what a publish target (<see cref="IPublishTarget"/>)
/// supports. Drives capability-based UI gating and service dispatch instead of
/// hard-coded <c>Platform == Intune</c> / <c>Target == "SCCM"</c> branching —
/// so adding a target, or graying/hiding a control per target, becomes a flag
/// change rather than an edit across view-models and XAML.
///
/// <para>The set mirrors the parameter divergence mapped between Intune and
/// SCCM: Intune-only detection/return-code/scope-tag/assignment/content
/// features vs SCCM-only repair-command/install-behavior/deployment features.</para>
/// </summary>
[Flags]
public enum TargetCapabilities
{
    None = 0,

    /// <summary>Raw <c>.intunewin</c> content can be downloaded from the cloud (Intune).</summary>
    ContentDownload = 1 << 0,

    /// <summary>Assignments/deployments target Azure AD groups that can be name-resolved (Intune).</summary>
    GroupResolution = 1 << 1,

    /// <summary>Per-package custom return codes (Intune).</summary>
    ReturnCodes = 1 << 2,

    /// <summary>Per-package scope tags (Intune).</summary>
    ScopeTags = 1 << 3,

    /// <summary>Per-package requirement rules (Intune).</summary>
    RequirementRules = 1 << 4,

    /// <summary>Per-package detection rules carried on the package (Intune; SCCM uses the shared detect section).</summary>
    PerPackageDetectionRules = 1 << 5,

    /// <summary>Per-package categories (Intune).</summary>
    Categories = 1 << 6,

    /// <summary>A repair command line (SCCM).</summary>
    RepairCommand = 1 << 7,

    /// <summary>Install behaviors / close-running-process rules (SCCM).</summary>
    InstallBehaviors = 1 << 8,

    /// <summary>Delivery via Intune assignments (groups, intent, filters).</summary>
    Assignments = 1 << 9,

    /// <summary>Delivery via SCCM deployments (collections, purpose).</summary>
    Deployments = 1 << 10,

    /// <summary>Requires an OAuth token to talk to its backend (Intune/Graph); SCCM runs local ConfigMgr cmdlets.</summary>
    TokenAuth = 1 << 11,
}
