namespace Wrapp.Models;

/// <summary>
/// A single field dependency rule: when <see cref="DependsOnField"/> has a value
/// contained in <see cref="DisabledWhenValues"/>, <see cref="TargetField"/> is disabled.
///
/// Matching is performed via <c>value?.ToString()</c> with case-insensitive equality
/// after trimming, so bool fields can use <c>["False"]</c> and empty-string conditions
/// use <c>[""]</c>. The sentinel <see cref="NonEmpty"/> matches any non-empty value
/// (used for mutually-exclusive fields like DetectionTest's Path / Command pair).
/// </summary>
public record FieldRule(
    string TargetField,
    string DependsOnField,
    string[] DisabledWhenValues,
    string TooltipTemplate,
    bool HideWhenDisabled = false,
    bool RequiredWhenEnabled = false)
{
    /// <summary>Sentinel for "any non-empty value" matching. Use as <c>[FieldRule.NonEmpty]</c>.</summary>
    public const string NonEmpty = "\u0001__nonempty__\u0001";
}

/// <summary>
/// Centralized field dependency rules for assignments, deployments, and packages.
/// Serves as the single source of truth for which fields to disable/enable.
/// Validated against IntuneWin32App 1.5.0 source, Wrapp.Packager Set-Win32AppAssignment
/// disallowed-keys table, and Microsoft ConfigMgr cmdlet documentation.
/// </summary>
public static class FieldDependencyRules
{
    // ================================================================
    // Intune Assignment Rules
    // ================================================================
    // Source: IntuneWin32App 1.5.0 + Wrapp.Packager Set-Win32AppAssignment.ps1

    /// <summary>Rules driven by the Intent field.</summary>
    public static readonly FieldRule[] IntuneAssignmentByIntent =
    [
        new("AvailableTime", "Intent",
            ["uninstall"],
            "Not applicable for Uninstall intent"),

        new("DeadlineTime", "Intent",
            ["available", "uninstall", "availablewithoutenrollment"],
            "Only applicable for Required intent"),

        new("UseLocalTime", "Intent",
            ["available", "uninstall", "availablewithoutenrollment"],
            "Only applicable for Required intent"),

        new("EnableRestartGracePeriod", "Intent",
            ["available", "uninstall", "availablewithoutenrollment"],
            "Only applicable for Required intent"),

        new("RestartGracePeriodInMinutes", "Intent",
            ["available", "uninstall", "availablewithoutenrollment"],
            "Requires Required intent with Enable Restart Grace Period checked"),

        new("RestartCountDownDisplayInMinutes", "Intent",
            ["available", "uninstall", "availablewithoutenrollment"],
            "Requires Required intent with Enable Restart Grace Period checked"),

        new("RestartNotificationSnoozeInMinutes", "Intent",
            ["available", "uninstall", "availablewithoutenrollment"],
            "Requires Required intent with Enable Restart Grace Period checked"),

        new("AutoUpdateSupersededApps", "Intent",
            ["required", "uninstall", "availablewithoutenrollment"],
            "Only applicable for Available intent"),
    ];

    /// <summary>Rules driven by the Type field (AllDevices/AllUsers/Group).</summary>
    public static readonly FieldRule[] IntuneAssignmentByType =
    [
        new("GroupID", "Type",
            ["AllDevices", "AllUsers"],
            "Not required for {0} target type"),

        new("GroupMode", "Type",
            ["AllDevices", "AllUsers"],
            "Not required for {0} target type"),
    ];

    /// <summary>Rules driven by GroupMode (exclude disables all settings).</summary>
    public static readonly FieldRule[] IntuneAssignmentByGroupMode =
    [
        new("Notification", "GroupMode", ["exclude"],
            "Settings are not applied for exclude group assignments"),

        new("DeliveryOptimizationPriority", "GroupMode", ["exclude"],
            "Settings are not applied for exclude group assignments"),

        new("AvailableTime", "GroupMode", ["exclude"],
            "Settings are not applied for exclude group assignments"),

        new("DeadlineTime", "GroupMode", ["exclude"],
            "Settings are not applied for exclude group assignments"),

        new("UseLocalTime", "GroupMode", ["exclude"],
            "Settings are not applied for exclude group assignments"),

        new("EnableRestartGracePeriod", "GroupMode", ["exclude"],
            "Settings are not applied for exclude group assignments"),

        new("RestartGracePeriodInMinutes", "GroupMode", ["exclude"],
            "Settings are not applied for exclude group assignments"),

        new("RestartCountDownDisplayInMinutes", "GroupMode", ["exclude"],
            "Settings are not applied for exclude group assignments"),

        new("RestartNotificationSnoozeInMinutes", "GroupMode", ["exclude"],
            "Settings are not applied for exclude group assignments"),

        new("AutoUpdateSupersededApps", "GroupMode", ["exclude"],
            "Settings are not applied for exclude group assignments"),

        new("FilterName", "GroupMode", ["exclude"],
            "Settings are not applied for exclude group assignments"),

        new("FilterMode", "GroupMode", ["exclude"],
            "Settings are not applied for exclude group assignments"),
    ];

    /// <summary>Cascading conditional: FilterMode requires FilterName.</summary>
    public const string FilterModeRequiresFilterName = "Enter a Filter Name first";

    /// <summary>Cascading conditional: restart fields require EnableRestartGracePeriod.</summary>
    public const string RestartFieldsRequireGracePeriod =
        "Requires Required intent with Enable Restart Grace Period checked";

    // ================================================================
    // Intune Tenant Rules
    // ================================================================
    // Source: MSAL - ClientSecret applies to ClientSecret flow only,
    // CertThumbprint applies to Certificate flow only. Interactive uses neither.

    /// <summary>AuthFlow gates which credential cell is visible per tenant.
    /// Valid AuthFlow values: Interactive, DeviceCode, ClientSecret, ClientCert.</summary>
    public static readonly FieldRule[] IntuneTenantRules =
    [
        new("ClientSecret", "AuthFlow",
            ["Interactive", "DeviceCode", "ClientCert"],
            "Only used for ClientSecret auth flow",
            HideWhenDisabled: true),
        new("CertThumbprint", "AuthFlow",
            ["Interactive", "DeviceCode", "ClientSecret"],
            "Only used for ClientCert auth flow",
            HideWhenDisabled: true),
    ];

    // ================================================================
    // Detection Test Rules
    // ================================================================
    // Source: DetectScript logic - Path and Command are mutually exclusive.
    // Path is also locked when IsPathLocked (set when the user picks a value via Browse).

    /// <summary>Path / Command mutual exclusion + browse-lock for the Path TextBox.</summary>
    public static readonly FieldRule[] DetectionTestRules =
    [
        new("Command", "Path",
            [FieldRule.NonEmpty],
            "Cleared because Path is set"),
        new("Path", "Command",
            [FieldRule.NonEmpty],
            "Read-only because Command is set"),
        new("Path", "IsPathLocked",
            ["True"],
            "Read-only because Path was selected via Browse"),
    ];

    // ================================================================
    // Intune Package Rules
    // ================================================================
    // Source: IntuneWin32App 1.5.0 - ExistingAppID applies only to Update / UpdateContent flows.

    /// <summary>ExistingAppID is hidden + required only when UpdateMode is Update or UpdateContent.</summary>
    public static readonly FieldRule[] IntunePackageRules =
    [
        // UpdateMode is enum-typed now; ToString() yields "Create". The empty
        // sentinel from the pre-enum era is no longer reachable (enum has a
        // non-null default) so we drop it.
        new("ExistingAppID", "UpdateMode",
            ["Create"],
            "Only required for Update or UpdateContent",
            HideWhenDisabled: true,
            RequiredWhenEnabled: true),
    ];

    // ================================================================
    // SCCM Deployment Rules (informational tooltips only - no hard disables)
    // ================================================================
    // Source: Microsoft New-CMApplicationDeployment documentation
    // All parameters work with both DeployAction and DeployPurpose values.

    /// <summary>Informational tooltip for DeadlineDateTime when DeployPurpose is Available.</summary>
    public const string DeadlineAvailableHint =
        "Deadline is advisory for Available deployments unless supersedence is configured";

    /// <summary>Informational tooltip for ApprovalRequired when DeployAction is Uninstall.</summary>
    public const string ApprovalUninstallHint =
        "Approval may not apply to uninstall deployments depending on site configuration";

    // ================================================================
    // SCCM Package Rules
    // ================================================================
    // Source: Microsoft Add-CMScriptDeploymentType documentation
    // "If you set InstallationBehaviorType to InstallForUser, then you can't set this parameter."

    /// <summary>LogonRequirementType is disabled when InstallationBehaviorType is InstallForUser.</summary>
    public static readonly FieldRule[] SCCMPackageRules =
    [
        new("LogonRequirementType", "InstallationBehaviorType",
            ["InstallForUser"],
            "Cannot be set when Installation Behavior is InstallForUser"),
    ];
}
