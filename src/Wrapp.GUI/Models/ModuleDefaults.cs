namespace Wrapp.Models;

/// <summary>
/// Allowed-value lists loaded from Wrapp.Packager's Defaults.psd1 at startup.
/// Used to populate ComboBoxes throughout the UI without hard-coding values in C#.
/// </summary>
public record ModuleDefaults(
    AuthFlow[] ValidAuthFlows,
    string[] ValidInstallExperience,
    string[] ValidRestartBehavior,
    string[] ValidArchitecture,
    string[] ValidMinOS,
    UpdateMode[] ValidUpdateModes,
    string[] ValidDetectionTypes,
    string[] ValidRequirementTypes,
    string[] ValidReturnCodeTypes,
    string[] ValidDependencyTypes,
    string[] ValidSupersedenceTypes,
    string[] ValidAssignmentIntents,
    string[] ValidAssignmentTypes,
    string[] ValidFilterModes,
    string[] ValidGroupModes,
    string[] ValidIntuneNotifications,
    string[] ValidDeliveryOptimizationPriorities,
    string[] ValidInstallationBehaviorTypes,
    string[] ValidLogonRequirementTypes,
    string[] ValidUserNotifications,
    string[] ValidDeployActions,
    string[] ValidDeployPurposes,
    string[] ValidSlowNetworkDeploymentModes,
    string[] ValidUserInteractionModes,
    string[] ValidRebootBehaviors,
    string[] ValidTimeBaseOn
)
{
    /// <summary>
    /// Returns a copy where any empty/null arrays are replaced with values from Empty.
    /// Fixes GUI-only fields not present in the PS module's hashtable output.
    /// </summary>
    public ModuleDefaults WithFallbacks()
    {
        var e = Empty;
        return new ModuleDefaults(
            ValidAuthFlows:                 FallbackEnum(ValidAuthFlows, e.ValidAuthFlows),
            ValidInstallExperience:         Fallback(ValidInstallExperience, e.ValidInstallExperience),
            ValidRestartBehavior:           Fallback(ValidRestartBehavior, e.ValidRestartBehavior),
            ValidArchitecture:              Fallback(ValidArchitecture, e.ValidArchitecture),
            ValidMinOS:                     Fallback(ValidMinOS, e.ValidMinOS),
            ValidUpdateModes:               FallbackEnum(ValidUpdateModes, e.ValidUpdateModes),
            ValidDetectionTypes:            Fallback(ValidDetectionTypes, e.ValidDetectionTypes),
            ValidRequirementTypes:          Fallback(ValidRequirementTypes, e.ValidRequirementTypes),
            ValidReturnCodeTypes:           Fallback(ValidReturnCodeTypes, e.ValidReturnCodeTypes),
            ValidDependencyTypes:           Fallback(ValidDependencyTypes, e.ValidDependencyTypes),
            ValidSupersedenceTypes:         Fallback(ValidSupersedenceTypes, e.ValidSupersedenceTypes),
            ValidAssignmentIntents:         Fallback(ValidAssignmentIntents, e.ValidAssignmentIntents),
            ValidAssignmentTypes:           Fallback(ValidAssignmentTypes, e.ValidAssignmentTypes),
            ValidFilterModes:               Fallback(ValidFilterModes, e.ValidFilterModes),
            ValidGroupModes:                Fallback(ValidGroupModes, e.ValidGroupModes),
            ValidIntuneNotifications:       Fallback(ValidIntuneNotifications, e.ValidIntuneNotifications),
            ValidDeliveryOptimizationPriorities: Fallback(ValidDeliveryOptimizationPriorities, e.ValidDeliveryOptimizationPriorities),
            ValidInstallationBehaviorTypes: Fallback(ValidInstallationBehaviorTypes, e.ValidInstallationBehaviorTypes),
            ValidLogonRequirementTypes:     Fallback(ValidLogonRequirementTypes, e.ValidLogonRequirementTypes),
            ValidUserNotifications:         Fallback(ValidUserNotifications, e.ValidUserNotifications),
            ValidDeployActions:             Fallback(ValidDeployActions, e.ValidDeployActions),
            ValidDeployPurposes:            Fallback(ValidDeployPurposes, e.ValidDeployPurposes),
            ValidSlowNetworkDeploymentModes: Fallback(ValidSlowNetworkDeploymentModes, e.ValidSlowNetworkDeploymentModes),
            ValidUserInteractionModes:      Fallback(ValidUserInteractionModes, e.ValidUserInteractionModes),
            ValidRebootBehaviors:           Fallback(ValidRebootBehaviors, e.ValidRebootBehaviors),
            ValidTimeBaseOn:                Fallback(ValidTimeBaseOn, e.ValidTimeBaseOn)
        );
    }

    private static string[] Fallback(string[]? value, string[] fallback)
        => value is { Length: > 0 } ? value : fallback;

    private static T[] FallbackEnum<T>(T[]? value, T[] fallback)
        => value is { Length: > 0 } ? value : fallback;

    /// <summary>Fallback empty defaults used before the PS runspace has loaded.</summary>
    public static ModuleDefaults Empty { get; } = new(
        ValidAuthFlows: new[] { AuthFlow.Interactive, AuthFlow.DeviceCode, AuthFlow.ClientSecret, AuthFlow.ClientCert },
        ValidInstallExperience: new[] { "system", "user" },
        ValidRestartBehavior: new[] { "allow", "basedOnReturnCode", "suppress", "force" },
        ValidArchitecture: new[] { "x64", "x86", "arm64", "x64x86", "AllWithARM64" },
        ValidMinOS: new[] { "W10_1607", "W10_1703", "W10_1709", "W10_1803", "W10_1809",
            "W10_1903", "W10_1909", "W10_2004", "W10_20H2", "W10_21H1",
            "W10_21H2", "W10_22H2", "W11_21H2", "W11_22H2" },
        ValidUpdateModes: new[] { UpdateMode.Create, UpdateMode.Update, UpdateMode.UpdateContent },
        ValidDetectionTypes: new[] { "MSI", "File", "Registry", "Script" },
        ValidRequirementTypes: new[] { "File", "Registry", "Script" },
        ValidReturnCodeTypes: new[] { "success", "softReboot", "hardReboot", "retry", "failed" },
        ValidDependencyTypes: new[] { "AutoInstall", "Detect" },
        ValidSupersedenceTypes: new[] { "Replace", "Update" },
        ValidAssignmentIntents: new[] { "available", "required", "uninstall", "availablewithoutenrollment" },
        ValidAssignmentTypes: new[] { "AllDevices", "AllUsers", "Group" },
        ValidFilterModes: new[] { "Include", "Exclude" },
        ValidGroupModes: new[] { "include", "exclude" },
        ValidIntuneNotifications: new[] { "showAll", "showReboot", "hideAll" },
        ValidDeliveryOptimizationPriorities: new[] { "foreground", "notConfigured" },
        ValidInstallationBehaviorTypes: new[] { "InstallForUser", "InstallForSystem", "InstallForSystemIfResourceIsDeviceOtherwiseInstallForUser" },
        ValidLogonRequirementTypes: new[] { "OnlyWhenUserLoggedOn", "WhetherOrNotUserLoggedOn", "OnlyWhenNoUserLoggedOn" },
        ValidUserNotifications: new[] { "DisplayAll", "DisplaySoftwareCenterOnly", "HideAll" },
        ValidDeployActions: new[] { "Install", "Uninstall" },
        ValidDeployPurposes: new[] { "Required", "Available" },
        ValidSlowNetworkDeploymentModes: new[] { "DoNothing", "Download", "DownloadContentForStreaming" },
        ValidUserInteractionModes: new[] { "Hidden", "Normal", "Minimized", "Maximized" },
        ValidRebootBehaviors: new[] { "BasedOnExitCode", "NoAction", "ForceReboot", "ProgramReboot" },
        ValidTimeBaseOn: new[] { "LocalTime", "UTC" }
    );
}
