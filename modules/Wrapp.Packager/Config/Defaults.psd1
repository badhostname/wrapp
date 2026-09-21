@{
    # Well-known Microsoft Graph PowerShell client ID (no app reg needed for Interactive/DeviceCode)
    WellKnownClientID = '14d82eec-204b-4c2f-b7e8-296a70dab67e'

    # Default log settings
    LogFileMaxSizeKB = 5120  # 5 MB rotation threshold

    # Endpoint script paths (used ON TARGET MACHINES by the deployed
    # detect/install scripts). Overridable per user via
    # %LOCALAPPDATA%\Wrapp\user-defaults.json (Endpoint.TagFolder /
    # Endpoint.LocalAppFolder) -- see Get-WrappUserDefaults.
    EndpointTagFolder      = 'C:\Wrapp\Tag'
    EndpointLocalAppFolder = 'C:\Wrapp\Apps'

    # Default app properties for Add-IntuneWin32App. Owner/Developer are
    # intentionally blank -- org-specific values belong in the user's
    # preferences (GUI) or config overrides, not in the open-source module.
    DefaultAppProperties = @{
        Developer                = ''
        Owner                    = ''
        CompanyPortalFeaturedApp = $false
    }

    # Default assignment properties (Intune).
    #
    # Restart-related keys (RestartGracePeriod, RestartCountDownDisplay,
    # RestartNotificationSnooze) are merged into an assignment only when
    # EnableRestartGracePeriod is $true -- Set-Win32AppAssignment guards this.
    # The three defaults mirror the Intune admin console defaults so runs never
    # trip the IntuneWin32App cmdlet's "parameter was not specified" warnings
    # (which include silently disabling snooze when its value is absent).
    DefaultAssignmentProperties = @{
        Notification                       = 'showAll'
        DeliveryOptimizationPriority       = 'foreground'
        RestartGracePeriodInMinutes        = 1440  # 1 day
        RestartCountDownDisplayInMinutes   = 15
        RestartNotificationSnoozeInMinutes = 240   # 4 hours
    }

    # Default Intune package properties (Add-IntuneWin32App fallbacks)
    DefaultIntunePackageProperties = @{
        Architecture                     = 'x64'
        MinimumSupportedWindowsRelease   = 'W11_21H2'
        InstallExperience                = 'system'
        RestartBehavior                  = 'basedOnReturnCode'
        MaximumInstallationTimeInMinutes = 60
    }

    # Default SCCM deployment type properties (Add-CMScriptDeploymentType fallbacks)
    DefaultSCCMPackageProperties = @{
        InstallationBehaviorType  = 'InstallForSystem'
        LogonRequirementType      = 'WhetherOrNotUserLoggedOn'
        UserInteractionMode       = 'Hidden'
        RebootBehavior            = 'BasedOnExitCode'
        SlowNetworkDeploymentMode = 'DoNothing'
        EstimatedRuntimeMins      = 15
        MaximumAllowedRuntimeMins = 120
    }

    # Default SCCM deployment properties (New-CMApplicationDeployment fallbacks)
    DefaultDeploymentProperties = @{
        DeployAction     = 'Install'
        DeployPurpose    = 'Available'
        UserNotification = 'DisplayAll'
        TimeBaseOn       = 'LocalTime'
    }

    # Polling defaults
    AppAvailabilityTimeoutSeconds  = 90
    AppAvailabilityIntervalSeconds = 5

    # Validation constants
    ValidAuthFlows = @('Interactive', 'DeviceCode', 'ClientSecret', 'ClientCert')

    ValidInstallExperience = @('system', 'user')

    ValidRestartBehavior = @('allow', 'basedOnReturnCode', 'suppress', 'force')

    ValidArchitecture = @('x64', 'x86', 'arm64', 'x64x86', 'AllWithARM64')

    ValidMinOS = @(
        'W10_1607', 'W10_1703', 'W10_1709', 'W10_1803', 'W10_1809',
        'W10_1903', 'W10_1909', 'W10_2004', 'W10_20H2', 'W10_21H1',
        'W10_21H2', 'W10_22H2', 'W11_21H2', 'W11_22H2'
    )

    ValidUpdateModes = @('Create', 'Update', 'UpdateContent')

    ValidDetectionTypes = @('MSI', 'File', 'Registry', 'Script')

    ValidDetectionMethods = @{
        File     = @('Existence', 'DateModified', 'DateCreated', 'Version', 'Size')
        Registry = @('Existence', 'StringComparison', 'IntegerComparison', 'VersionComparison')
    }

    ValidRequirementTypes = @('File', 'Registry', 'Script')

    ValidReturnCodeTypes = @('success', 'softReboot', 'hardReboot', 'retry', 'failed')

    ValidDependencyTypes = @('AutoInstall', 'Detect')

    ValidSupersedenceTypes = @('Replace', 'Update')

    ValidAssignmentIntents = @('available', 'required', 'uninstall', 'availablewithoutenrollment')

    ValidAssignmentTypes = @('AllDevices', 'AllUsers', 'Group')

    ValidFilterModes = @('Include', 'Exclude')

    ValidComparisonOperators = @('equal', 'notEqual', 'greaterThanOrEqual', 'greaterThan', 'lessThanOrEqual', 'lessThan')

    ValidExistenceDetectionTypes = @('exists', 'doesNotExist')

    MaxDependencies = 100
    MaxSupersedence = 10

    # SCCM validation constants
    ValidInstallationBehaviorTypes = @('InstallForUser', 'InstallForSystem', 'InstallForSystemIfResourceIsDeviceOtherwiseInstallForUser')
    ValidLogonRequirementTypes     = @('OnlyWhenUserLoggedOn', 'WhetherOrNotUserLoggedOn', 'OnlyWhenNoUserLoggedOn')
    ValidUserNotifications         = @('DisplayAll', 'DisplaySoftwareCenterOnly', 'HideAll')
    ValidUserInteractionModes      = @('Hidden', 'Normal', 'Minimized', 'Maximized')
    ValidRebootBehaviors           = @('BasedOnExitCode', 'NoAction', 'ForceReboot', 'ProgramReboot')
    ValidDeployActions             = @('Install', 'Uninstall')
    ValidDeployPurposes            = @('Required', 'Available')

    # Script metadata
    ScriptType    = 'IntunePackager'
    ScriptVersion = '3.3.0'

    # Win32App field schema.
    # Each entry is a hashtable with the field's constraints and guidance.
    # Consumed by Invoke-FieldSchema (Private) and Get-ValidatedWin32AppParameters.
    # AllowedValues, Pattern, RangeMin/RangeMax, and Type are the constraint
    # keys. Description and Guidance are surfaced in logs and the WPF GUI.
    Win32AppFieldSchema = @{

        # Deployment type and behavior
        InstallExperience = @{
            AllowedValues = @('system', 'user')
            Description   = 'Installation context: system-wide or per-user'
            Guidance      = 'Use "system" (default) for machine-wide installs requiring elevation, such as most MSI or EXE applications. Use "user" for per-user installs such as MSIX, Microsoft Store apps, or Office add-ins. Choosing the wrong context causes install failures on locked-down managed devices.'
        }

        RestartBehavior = @{
            AllowedValues = @('allow', 'basedOnReturnCode', 'suppress', 'force')
            Description   = 'Post-installation restart behavior'
            Guidance      = '"basedOnReturnCode" is the recommended default -- it respects installer exit codes (e.g. 3010 for soft reboot). "suppress" prevents any restart, which may leave the system in an incomplete state for some installers. "force" always restarts immediately after install. "allow" lets the device restart at its discretion. Avoid "force" in production unless the app specifically requires it.'
        }

        AllowAvailableUninstall = @{
            Type        = 'Boolean'
            Description = 'Allow end users to uninstall the app from Company Portal'
            Guidance    = 'When true, users can self-service uninstall from Company Portal. Only applies to apps deployed with "Available" intent. Has no effect on Required deployments. Enable for apps that users may not always need, such as optional tools.'
        }

        CompanyPortalFeaturedApp = @{
            Type        = 'Boolean'
            Description = 'Feature this app prominently in Company Portal'
            Guidance    = 'Featured apps appear in the top "Featured" banner in Company Portal. Use sparingly -- featuring too many apps dilutes the prominence. Suitable for business-critical apps new to your fleet or apps that users frequently search for.'
        }

        # Upload behavior
        AzCopyWindowStyle = @{
            AllowedValues = @('Hidden', 'NoNewWindow')
            Description   = 'Window visibility when running AzCopy for content upload'
            Guidance      = '"Hidden" is the recommended default for automated packaging -- it runs silently in the background. "NoNewWindow" runs AzCopy in the current console session, which is useful for debugging upload failures but will show a visible console window during packaging.'
        }

        # Timeouts and numeric limits
        MaximumInstallationTimeInMinutes = @{
            Type        = 'Integer'
            RangeMin    = 1
            RangeMax    = 1440
            Description = 'Timeout in minutes before Intune marks the installation as failed'
            Guidance    = 'Intune default is 60 minutes. Increase for large applications, applications that require heavy extraction (e.g. Adobe products), or slow network locations. Maximum is 1440 (24 hours). Too short a timeout causes false failures on slow managed devices or metered connections.'
        }

        # URL fields
        InformationURL = @{
            Type    = 'Url'
            Pattern = '^(http[s]?|[s]?ftp[s]?):\/\/[^\s,]+$'
            Description = 'URL to app information shown in Company Portal'
            Guidance    = 'Must be a valid HTTP, HTTPS, or FTP URL. Shown to end users in Company Portal app details. Link to a vendor product page, an internal IT wiki article, or an IT helpdesk knowledge base entry. Leave empty if no public URL exists.'
        }

        PrivacyURL = @{
            Type    = 'Url'
            Pattern = '^(http[s]?|[s]?ftp[s]?):\/\/[^\s,]+$'
            Description = 'URL to the application privacy policy'
            Guidance    = 'Must be a valid HTTP, HTTPS, or FTP URL. Shown in Company Portal alongside app details. Required by some regulated organizations and recommended for any app that collects or transmits user data. Link to the vendor privacy policy or your organization data handling statement.'
        }

        # Non-validated pass-through fields (schema entry = known/valid key).
        # No constraints beyond being a recognized field name.
        FilePath                    = @{ Description = 'Path to the .intunewin package file' }
        DisplayName                 = @{ Description = 'Application display name shown in Intune and Company Portal' }
        Description                 = @{ Description = 'Application description shown in Company Portal' }
        Publisher                   = @{ Description = 'Publisher name shown in Intune and Company Portal' }
        AppVersion                  = @{ Description = 'Application version string stored in Intune metadata' }
        Developer                   = @{ Description = 'Developer name stored in Intune metadata' }
        Owner                       = @{ Description = 'Owner name stored in Intune metadata' }
        Notes                       = @{ Description = 'Internal notes stored in Intune metadata (JSON blob used by Wrapp.Packager)' }
        CategoryName                = @{ Description = 'Category name for the app in Intune (must exist in tenant)' }
        InstallCommandLine          = @{ Description = 'Command line used to install the application' }
        UninstallCommandLine        = @{ Description = 'Command line used to uninstall the application' }
        DetectionRule               = @{ Description = 'Detection rule object(s) for the application' }
        RequirementRule             = @{ Description = 'Requirement rule object for minimum OS and architecture' }
        AdditionalRequirementRule   = @{ Description = 'Additional requirement rule object(s)' }
        ReturnCode                  = @{ Description = 'Custom return code object(s)' }
        Icon                        = @{ Description = 'Icon image object for the application' }
        ScopeTagName                = @{ Description = 'Scope tag name(s) applied to the application' }
        UseAzCopy                   = @{ Description = 'Use AzCopy for content upload instead of the default BITS transfer' }
        UnattendedInstall           = @{ Description = 'Install silently without user interaction' }
        UnattendedUninstall         = @{ Description = 'Uninstall silently without user interaction' }
    }
}
