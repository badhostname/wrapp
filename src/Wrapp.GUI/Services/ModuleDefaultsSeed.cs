using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// C#-side mirror of <c>modules/Wrapp.Packager/Config/Defaults.psd1</c>.
///
/// <para>These constants seed the Preferences editor and the new-package
/// AddPackage path when <c>settings.json</c> has no user-customised values.
/// The PowerShell module continues to read Defaults.psd1 at runtime; this
/// file is a parallel copy so the GUI can function without a live PS
/// runspace (e.g. at Settings-screen open time).</para>
///
/// <para><b>Keep this file in sync with Defaults.psd1.</b> Field names,
/// ordering, and comments match the psd1 deliberately. If you change a
/// default there, mirror the change here. A future batch may replace this
/// with a runtime load from the psd1; for now the duplication is intentional
/// and the cost of drift is bounded to "the GUI seeds a slightly-wrong
/// default that the user will immediately override on first save."</para>
/// </summary>
public static class ModuleDefaultsSeed
{
    // -----------------------------------------------------------------------
    // Validation arrays
    // -----------------------------------------------------------------------

    public static readonly AuthFlow[] ValidAuthFlows        = { AuthFlow.Interactive, AuthFlow.DeviceCode, AuthFlow.ClientSecret, AuthFlow.ClientCert };
    public static readonly string[] ValidInstallExperience  = { "system", "user" };
    public static readonly string[] ValidRestartBehavior    = { "allow", "basedOnReturnCode", "suppress", "force" };
    public static readonly string[] ValidArchitecture       = { "x64", "x86", "arm64", "x64x86", "AllWithARM64" };
    public static readonly string[] ValidAzCopyWindowStyle  = { "Hidden", "NoNewWindow" };
    public static readonly UpdateMode[] ValidUpdateModes    = { UpdateMode.Create, UpdateMode.Update, UpdateMode.UpdateContent };

    public static readonly string[] ValidMinOS = {
        "W10_1607", "W10_1703", "W10_1709", "W10_1803", "W10_1809",
        "W10_1903", "W10_1909", "W10_2004", "W10_20H2", "W10_21H1",
        "W10_21H2", "W10_22H2", "W11_21H2", "W11_22H2"
    };

    public static readonly string[] ValidAssignmentIntents = { "available", "required", "uninstall", "availablewithoutenrollment" };
    public static readonly string[] ValidAssignmentTypes   = { "AllDevices", "AllUsers", "Group" };
    public static readonly string[] ValidGroupModes        = { "include", "exclude" };
    public static readonly string[] ValidFilterModes       = { "Include", "Exclude" };
    public static readonly string[] ValidIntuneNotifications = { "showAll", "showReboot", "hideAll" };
    public static readonly string[] ValidDeliveryOptimizationPriorities = { "foreground", "background" };

    public static readonly string[] ValidInstallationBehaviorTypes = {
        "InstallForUser", "InstallForSystem",
        "InstallForSystemIfResourceIsDeviceOtherwiseInstallForUser"
    };
    public static readonly string[] ValidLogonRequirementTypes = {
        "OnlyWhenUserLoggedOn", "WhetherOrNotUserLoggedOn", "OnlyWhenNoUserLoggedOn"
    };
    public static readonly string[] ValidUserNotifications = { "DisplayAll", "DisplaySoftwareCenterOnly", "HideAll" };
    public static readonly string[] ValidUserInteractionModes = { "Hidden", "Normal", "Minimized", "Maximized" };
    public static readonly string[] ValidRebootBehaviors   = { "BasedOnExitCode", "NoAction", "ForceReboot", "ProgramReboot" };
    public static readonly string[] ValidSlowNetworkDeploymentModes = { "DoNothing", "Download" };
    public static readonly string[] ValidDeployActions     = { "Install", "Uninstall" };
    public static readonly string[] ValidDeployPurposes    = { "Required", "Available" };
    public static readonly string[] ValidTimeBaseOn        = { "LocalTime", "UTC" };

    // -----------------------------------------------------------------------
    // DefaultIntunePackageProperties (Add-IntuneWin32App fallbacks)
    // -----------------------------------------------------------------------

    public const string IntuneArchitecture                = "x64";
    public const string IntuneMinimumSupportedWindowsRelease = "W11_21H2";
    public const string IntuneInstallExperience           = "system";
    public const string IntuneRestartBehavior             = "basedOnReturnCode";
    public const int    IntuneMaximumInstallationTimeInMinutes = 60;

    // Extra Intune package fields with sensible defaults (not in psd1
    // explicitly, but exposed in Win32AppFieldSchema / IntunePackageEntry).
    public const bool   IntuneAllowAvailableUninstall  = false;
    public const bool   IntuneCompanyPortalFeaturedApp = false;
    public const bool   IntuneUseAzCopy                = false;
    public const string IntuneAzCopyWindowStyle        = "Hidden";
    public const UpdateMode IntuneUpdateMode           = UpdateMode.Create;

    // -----------------------------------------------------------------------
    // DefaultAppProperties (Intune metadata) + token-template defaults
    // -----------------------------------------------------------------------

    public const string IntuneAppNameTemplate   = "";
    public const string IntuneDeveloperTemplate = "";
    // Blank by default: the org name belongs in the user's preferences, not in
    // the open-source seed (was the pre-Wrapp org branding "Digital Workplace").
    public const string IntuneOwnerTemplate     = "";

    // -----------------------------------------------------------------------
    // Endpoint script defaults (paths used by the deploy/detect scripts ON
    // TARGET MACHINES). Baked into bundle scripts via the {{TagFolder}} /
    // {{LocalAppFolder}} template tokens at bundle-write time, and exported to
    // user-defaults.json for the PowerShell module (CLI parity). Org-specific
    // values (e.g. C:\Contoso\Tag) belong in the user's Settings, not here.
    // -----------------------------------------------------------------------

    public const string EndpointTagFolder      = @"C:\Wrapp\Tag";
    public const string EndpointLocalAppFolder = @"C:\Wrapp\Apps";
    public const string IntuneCommentTemplate   = "{{Company}} {{Name}}";
    public const string IntuneInformationURL    = "";
    public const string IntunePrivacyURL        = "";

    // -----------------------------------------------------------------------
    // DefaultAssignmentProperties
    // -----------------------------------------------------------------------

    public const string IntuneAssignmentIntent                    = "available";
    public const string IntuneAssignmentType                      = "Group";
    public const string IntuneAssignmentGroupMode                 = "include";
    public const string IntuneAssignmentNotification              = "showAll";
    public const string IntuneAssignmentDeliveryOptimizationPriority = "foreground";
    public const string IntuneAssignmentAvailableTime             = "2099-12-25T00:00:00.000Z";
    public const string IntuneAssignmentDeadlineTime              = "";
    public const string IntuneAssignmentFilterMode                = "";

    // Restart-grace-period defaults (only applied when EnableRestartGracePeriod
    // is true on an assignment). Values mirror Defaults.psd1 and the Intune
    // admin console defaults -- matching the IntuneWin32App cmdlet fallbacks
    // for RestartGracePeriod/RestartCountDownDisplay, and a sensible snooze
    // window (4 hours) that's otherwise silently disabled.
    public const int    IntuneAssignmentRestartGracePeriodInMinutes        = 1440;
    public const int    IntuneAssignmentRestartCountDownDisplayInMinutes   = 15;
    public const int    IntuneAssignmentRestartNotificationSnoozeInMinutes = 240;

    // -----------------------------------------------------------------------
    // DefaultSCCMPackageProperties (Add-CMScriptDeploymentType fallbacks)
    // -----------------------------------------------------------------------

    public const string SccmInstallationBehaviorType     = "InstallForSystem";
    public const string SccmLogonRequirementType         = "WhetherOrNotUserLoggedOn";
    public const string SccmUserInteractionMode          = "Hidden";
    public const string SccmRebootBehavior               = "BasedOnExitCode";
    public const string SccmSlowNetworkDeploymentMode    = "DoNothing";
    public const int    SccmEstimatedRuntimeMins         = 15;
    public const int    SccmMaximumAllowedRuntimeMins    = 120;
    public const bool   SccmInstallBehavior              = false;
    public const bool   SccmContentFallback              = false;

    // -----------------------------------------------------------------------
    // SCCM metadata + token templates
    // -----------------------------------------------------------------------

    public const string SccmPublisherTemplate            = "{{Company}}";
    public const string SccmOwnerTemplate                = "{{Author}}";
    public const string SccmSupportContactTemplate       = "{{Author}}";
    public const string SccmDescriptionTemplate          = "Created {{Date}} by {{Author}}";
    public const string SccmCommentTemplate              = "{{Company}} {{Name}}";
    public const string SccmNameTemplate                 = "{{Name}} - Script";
    public const string SccmLocalizedNameTemplate        = "{{Name}}";
    public const string SccmLocalizedDescriptionTemplate = "";
    public const string SccmKeywordsTemplate             = "";
    public const string SccmPrivacyUrl                   = "";
    public const string SccmUserDocumentation            = "";
    public const string SccmLinkText                     = "";
    public const string SccmReleaseDateTemplate          = "{{Date}}";
    public const bool   SccmIsFeatured                   = false;
    public const bool   SccmAutoInstall                  = false;

    // -----------------------------------------------------------------------
    // DefaultDeploymentProperties
    // -----------------------------------------------------------------------

    public const string SccmDeployAction      = "Install";
    public const string SccmDeployPurpose     = "Available";
    public const string SccmUserNotification  = "DisplayAll";
    public const string SccmTimeBaseOn        = "LocalTime";
    public const string SccmAvailableDateTime = "2099-12-25T00:00:00.000Z";
    public const string SccmDeadlineDateTime  = "";
}
