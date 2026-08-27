function Test-WrappConfig {
    <#
    .SYNOPSIS
        Validates Config.json structure and returns structured results.

    .DESCRIPTION
        Comprehensive pre-auth validation of the Config.json structure.
        Checks required sections, field values, parameter combinations,
        detection rule constraints, dependency/supersedence limits,
        assignment scheduling rules, and auth coherence.

        Returns three parallel outputs for maximum compatibility:
          Errors   [string[]] - Fatal issues (existing callers use this)
          Warnings [string[]] - Non-fatal advisories (existing callers use this)
          Issues   [object[]] - Structured issue objects for the WPF GUI.
                                Each object has: FieldPath, Severity, Code,
                                Message, AttemptedValue, AllowedValues, Guidance.

        Existing callers that only read Errors, Warnings, and IsValid are
        unaffected by the addition of the Issues array.

    .PARAMETER Config
        The loaded Config object from ConvertFrom-Json.

    .PARAMETER ScriptType
        The script type key to look up (default: 'IntunePackager').

    .OUTPUTS
        [PSCustomObject] with:
            IsValid    [bool]     - True if no fatal errors
            Errors     [string[]] - Fatal issues preventing execution
            Warnings   [string[]] - Non-fatal advisories
            Issues     [object[]] - Structured issue objects (GUI-ready)
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        $Config,

        [string]$ScriptType = 'IntunePackager'
    )

    $Errors   = [System.Collections.Generic.List[string]]::new()
    $Warnings = [System.Collections.Generic.List[string]]::new()
    $Issues   = [System.Collections.Generic.List[object]]::new()
    $D        = $script:ModuleDefaults
    $Schema   = $script:ModuleDefaults.Win32AppFieldSchema
    $GuidPattern = '^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$'

    # Helper: add an Error to both flat list and Issues
    function Add-Error {
        param([string]$FieldPath, [string]$Code, [string]$Msg, $AttemptedValue = $null, [string[]]$AllowedValues = @(), [string]$Guidance = '')
        $Errors.Add($Msg)
        $Issues.Add((New-ValidationIssue -FieldPath $FieldPath -Severity 'Error' -Code $Code -Message $Msg -AttemptedValue $AttemptedValue -AllowedValues $AllowedValues -Guidance $Guidance))
    }

    # Helper: add a Warning to both flat list and Issues
    function Add-Warning {
        param([string]$FieldPath, [string]$Code, [string]$Msg, $AttemptedValue = $null, [string[]]$AllowedValues = @(), [string]$Guidance = '')
        $Warnings.Add($Msg)
        $Issues.Add((New-ValidationIssue -FieldPath $FieldPath -Severity 'Warning' -Code $Code -Message $Msg -AttemptedValue $AttemptedValue -AllowedValues $AllowedValues -Guidance $Guidance))
    }

    # Helper: run Invoke-FieldSchema and sync results into Errors/Warnings/Issues
    function Test-Field {
        param([string]$FieldPath, [string]$FieldName, $Value)
        $fieldIssues = Invoke-FieldSchema -Schema $Schema -FieldName $FieldName -Value $Value -FieldPath $FieldPath
        foreach ($issue in $fieldIssues) {
            $Issues.Add($issue)
            if ($issue.Severity -eq 'Error') { $Errors.Add($issue.Message) }
            else { $Warnings.Add($issue.Message) }
        }
    }

    # ======================================================================
    # Block 0: Required top-level sections (conditional on ScriptType)
    # ======================================================================
    if (-not $Config.App) {
        Add-Error 'Config.App' 'MISSING_REQUIRED' "Missing 'App' section" -Guidance 'The App section contains the application name, version, company, and icon settings. It is required for all packaging targets.'
    }
    if (-not $Config.Script) {
        Add-Error 'Config.Script' 'MISSING_REQUIRED' "Missing 'Script' section" -Guidance 'The Script section contains target-specific package lists (IntunePackager or SCCMPackager). It is required for all packaging targets.'
    }
    if (-not $Config.Domain) {
        Add-Error 'Config.Domain' 'MISSING_REQUIRED' "Missing 'Domain' section" -Guidance 'The Domain section maps domain names to file server paths used during packaging. It is required for both Intune and SCCM targets.'
    }

    # Target-specific required sections
    if ($ScriptType -eq 'IntunePackager') {
        if (-not $Config.IntuneTenant) {
            Add-Error 'Config.IntuneTenant' 'MISSING_REQUIRED' "Missing 'IntuneTenant' section" -Guidance 'The IntuneTenant section defines authentication and assignment settings for each Intune tenant. It is required when running IntunePackager.'
        }
    }
    elseif ($ScriptType -eq 'SCCMPackager') {
        if (-not $Config.SCCMSite) {
            Add-Error 'Config.SCCMSite' 'MISSING_REQUIRED' "Missing 'SCCMSite' section" -Guidance 'The SCCMSite section defines distribution point groups and deployment targets for each SCCM site. It is required when running SCCMPackager.'
        }
    }

    # Required App fields
    if ($Config.App) {
        if (-not $Config.App.Company) {
            Add-Error 'Config.App.Company' 'MISSING_REQUIRED' "App.Company is required" -Guidance 'App.Company is used to construct the application display name and publisher metadata. Set it to your organization name or the software vendor name.'
        }
        if (-not $Config.App.Name) {
            Add-Error 'Config.App.Name' 'MISSING_REQUIRED' "App.Name is required" -Guidance 'App.Name is the base application name used to construct display names, log file names, and SCCM application names. Keep it short and descriptive (e.g. "7-Zip", "Adobe Acrobat Reader").'
        }
        if (-not $Config.App.Version) {
            Add-Error 'Config.App.Version' 'MISSING_REQUIRED' "App.Version is required" -Guidance 'App.Version is used in display names, version strings, and detection logic. Use the full version string from the installer (e.g. "24.001.20629").'
        }
        if (-not $Config.App.DotVersion) {
            Add-Warning 'Config.App.DotVersion' 'MISSING_REQUIRED' "App.DotVersion is missing (used for AppVersion)" -Guidance 'App.DotVersion is the dot-separated version string submitted to Intune as the AppVersion metadata field. If missing, AppVersion will be empty in the Intune console. Recommended format: "24.1.20629".'
        }
    }

    # Script type section
    $ScriptSection = $Config.Script.$ScriptType
    if (-not $ScriptSection) {
        Add-Error "Config.Script.$ScriptType" 'MISSING_REQUIRED' "Script.$ScriptType section is missing" -Guidance "The Script.$ScriptType section defines the package list for this target. Add a 'Packages' array with at least one entry. See Config.Template.json for the required structure."
    }
    else {
        if (-not $ScriptSection.Tag) {
            Add-Error "Config.Script.$ScriptType.Tag" 'MISSING_REQUIRED' "Script.$ScriptType.Tag is required" -Guidance 'Tag is used in application display names to identify the packaging run. It typically contains the application version and build environment (e.g. "v24.1 - Production").'
        }
        if (-not $ScriptSection.Packages -or $ScriptSection.Packages.Count -eq 0) {
            Add-Error "Config.Script.$ScriptType.Packages" 'MISSING_REQUIRED' "Script.$ScriptType.Packages must contain at least one package" -Guidance 'The Packages array defines what gets created. Each entry represents one Intune Win32 app or SCCM application. Add at least one package definition.'
        }
    }

    # IntuneTenant: at least one tenant entry beyond Comment (Intune only)
    if ($ScriptType -eq 'IntunePackager' -and $Config.IntuneTenant) {
        $TenantEntries = $Config.IntuneTenant.PSObject.Properties | Where-Object { $_.Name -ne 'Comment' }
        if (-not $TenantEntries -or @($TenantEntries).Count -eq 0) {
            Add-Error 'Config.IntuneTenant' 'MISSING_REQUIRED' "IntuneTenant section has no tenant configurations" -Guidance 'Add at least one tenant entry (e.g. "Production" or "Dev") to IntuneTenant. Each entry must have an AuthFlow and the credentials appropriate for that flow. See Config.Template.json for examples.'
        }
    }

    # If structural errors exist, return early (blocks below depend on valid structure)
    if ($Errors.Count -gt 0) {
        return [PSCustomObject]@{
            IsValid  = $false
            Errors   = $Errors.ToArray()
            Warnings = $Warnings.ToArray()
            Issues   = $Issues.ToArray()
        }
    }

    $Packages     = @($ScriptSection.Packages)
    $IsEXEPackage = -not $Config.App.MSIFile

    # ======================================================================
    # Block 0.5: SCCMPackager package validation
    # ======================================================================
    if ($ScriptType -eq 'SCCMPackager') {
        foreach ($pkg in $Packages) {
            $pLabel = if ($pkg.AppName) { $pkg.AppName } else { '(unnamed)' }
            $pBase  = "Config.Script.SCCMPackager.Packages[$pLabel]"

            if (-not $pkg.AppName) {
                Add-Error "$pBase.AppName" 'MISSING_REQUIRED' "SCCM Package missing 'AppName' field" -Guidance 'AppName is the SCCM application name created in the console. It must be unique within the site. Use a clear, descriptive name such as "7-Zip 24.1 x64".'
                continue
            }
            if (-not $pkg.Name) {
                Add-Error "$pBase.Name" 'MISSING_REQUIRED' "SCCM Package '${pLabel}': 'Name' (deployment type name) is required" -Guidance 'Name is the deployment type name within the SCCM application. It identifies the installation method (e.g. "Script Installer"). Must be unique within the application.'
            }
            if (-not $pkg.InstallCommand) {
                Add-Error "$pBase.InstallCommand" 'MISSING_REQUIRED' "SCCM Package '${pLabel}': InstallCommand is required" -Guidance 'InstallCommand is the command line SCCM runs to install the application on the managed device. Include all silent flags required by the installer.'
            }
            if (-not $pkg.UninstallCommand) {
                Add-Warning "$pBase.UninstallCommand" 'MISSING_REQUIRED' "SCCM Package '${pLabel}': UninstallCommand is not set" -Guidance 'Without an UninstallCommand, SCCM cannot remove the application via a deployment. Add the uninstall command line to support Uninstall deployments and revision cleanup.'
            }
            $p = @{
                Value       = $pkg.InstallationBehaviorType
                ValidValues = $D.ValidInstallationBehaviorTypes
                FieldPath   = "$pBase.InstallationBehaviorType"
                Label       = "SCCM Package '${pLabel}': InstallationBehaviorType"
                Guidance    = 'InstallForSystem installs in the SYSTEM context (machine-wide, requires no logged-on user). InstallForUser installs in the user context for the current user. Use InstallForSystem for most business applications.'
                AddError    = { param($p) Add-Error @p }
            }
            Test-EnumField @p
            $p = @{
                Value       = $pkg.LogonRequirementType
                ValidValues = $D.ValidLogonRequirementTypes
                FieldPath   = "$pBase.LogonRequirementType"
                Label       = "SCCM Package '${pLabel}': LogonRequirementType"
                Guidance    = 'OnlyWhenUserLoggedOn requires an active user session. WhetherOrNotUserLoggedOn runs regardless of session state (recommended for system-context installs). OnlyWhenNoUserLoggedOn runs only during maintenance windows.'
                AddError    = { param($p) Add-Error @p }
            }
            Test-EnumField @p

            # ----------------------------------------------------------------
            # Per-package SCCM deployments (canonical new format).
            # AppName is implicit from the parent package and stamped onto
            # each entry by Invoke-WrappSccm before passing to the cmdlet,
            # so it's not required here. Site-scoped deployments under
            # SCCMSite.<Site>.Deployments[] are validated as the legacy path
            # in Block 0.6.
            # ----------------------------------------------------------------
            if ($pkg.Deployments) {
                foreach ($dep in $pkg.Deployments) {
                    $depLabel = if ($dep.Collection) { "${pLabel}->$($dep.Collection)" } else { "${pLabel}/(unnamed)" }
                    $dBase    = "$pBase.Deployments[$($dep.Collection)]"

                    if (-not $dep.Collection) {
                        Add-Error "$dBase.Collection" 'MISSING_REQUIRED' "SCCM deployment on '${pLabel}': Collection is required" -Guidance 'Collection is the SCCM device or user collection that will receive the deployment. The collection must exist in SCCM before the packager runs.'
                        continue
                    }

                    $p = @{
                        Value       = $dep.DeployAction
                        ValidValues = $D.ValidDeployActions
                        FieldPath   = "$dBase.DeployAction"
                        Label       = "SCCM deployment '${depLabel}': DeployAction"
                        Guidance    = 'Install deploys the application to devices. Uninstall removes it. Most deployments use Install.'
                        AddError    = { param($p) Add-Error @p }
                    }
                    Test-EnumField @p
                    $p = @{
                        Value       = $dep.DeployPurpose
                        ValidValues = $D.ValidDeployPurposes
                        FieldPath   = "$dBase.DeployPurpose"
                        Label       = "SCCM deployment '${depLabel}': DeployPurpose"
                        Guidance    = 'Required deployments install automatically on the deadline. Available deployments appear in Software Center for users to install on demand.'
                        AddError    = { param($p) Add-Error @p }
                    }
                    Test-EnumField @p
                    $p = @{
                        Value       = $dep.UserNotification
                        ValidValues = $D.ValidUserNotifications
                        FieldPath   = "$dBase.UserNotification"
                        Label       = "SCCM deployment '${depLabel}': UserNotification"
                        Guidance    = 'DisplayAll shows all Software Center notifications and toast messages. DisplaySoftwareCenterOnly shows progress in Software Center but suppresses toasts. HideAll silently installs with no user-visible feedback.'
                        AddError    = { param($p) Add-Error @p }
                    }
                    Test-EnumField @p

                    # Date validation: parse + reject the Intune MinValue
                    # sentinel ("0001-01-01...") which underflows when
                    # New-CMApplicationDeployment converts to UTC. Also
                    # checks the Required deadlock case where
                    # DeadlineDateTime <= AvailableDateTime would make
                    # SCCM emit "Invalid operation CMPSNoMask = True".
                    $availDt = $null
                    if ($dep.AvailableDateTime) {
                        try { $availDt = [datetime]::Parse($dep.AvailableDateTime, [System.Globalization.CultureInfo]::InvariantCulture) }
                        catch {
                            Add-Error "$dBase.AvailableDateTime" 'INVALID_VALUE' "SCCM deployment '${depLabel}': AvailableDateTime '$($dep.AvailableDateTime)' is not a valid date/time" -Guidance 'Use ISO 8601 format like "2026-01-15T09:00:00.000Z". The ASAP button in the deployment dialog writes a valid current-UTC value.'
                        }
                        if ($null -ne $availDt -and $availDt.Year -le 1) {
                            Add-Error "$dBase.AvailableDateTime" 'INVALID_VALUE' "SCCM deployment '${depLabel}': AvailableDateTime '$($dep.AvailableDateTime)' is the Intune ASAP sentinel (DateTime.MinValue). SCCM rejects this -- the cmdlet underflows when converting MinValue to UTC." -Guidance 'Click the ASAP button on the SCCM deployment dialog to write current UTC time, or clear the field to inherit the module default.'
                        }
                    }

                    $deadDt = $null
                    if ($dep.DeadlineDateTime) {
                        try { $deadDt = [datetime]::Parse($dep.DeadlineDateTime, [System.Globalization.CultureInfo]::InvariantCulture) }
                        catch {
                            Add-Error "$dBase.DeadlineDateTime" 'INVALID_VALUE' "SCCM deployment '${depLabel}': DeadlineDateTime '$($dep.DeadlineDateTime)' is not a valid date/time" -Guidance 'Use ISO 8601 format like "2026-01-15T09:00:00.000Z" or leave empty if the deployment has no hard deadline.'
                        }
                        if ($null -ne $deadDt -and $deadDt.Year -le 1) {
                            Add-Error "$dBase.DeadlineDateTime" 'INVALID_VALUE' "SCCM deployment '${depLabel}': DeadlineDateTime '$($dep.DeadlineDateTime)' is the Intune ASAP sentinel (DateTime.MinValue). SCCM rejects this." -Guidance 'Clear the deadline (leave empty) or pick a future date/time. DeadlineDateTime is only meaningful for Required deployments.'
                        }
                    }

                    # Required deployments: deadline must be strictly after
                    # availability. SCCM cmdlet rejects equal values with
                    # "Invalid operation CMPSNoMask = True".
                    if ($dep.DeployPurpose -eq 'Required' -and
                        $null -ne $availDt -and $availDt.Year -gt 1 -and
                        $null -ne $deadDt  -and $deadDt.Year  -gt 1 -and
                        $deadDt -le $availDt)
                    {
                        Add-Error "$dBase.DeadlineDateTime" 'INVALID_VALUE' "SCCM deployment '${depLabel}': Required deployment with DeadlineDateTime '$($dep.DeadlineDateTime)' <= AvailableDateTime '$($dep.AvailableDateTime)'. SCCM rejects this with 'Invalid operation' (CMPSNoMask)." -Guidance 'For Required deployments, the deadline must be strictly after the available time. Pick a later deadline, set DeployPurpose to Available, or clear the deadline.'
                    }
                }
            }
        }
    }

    # ======================================================================
    # Block 0.6: SCCMSite section validation
    # ======================================================================
    if ($ScriptType -eq 'SCCMPackager' -and $Config.SCCMSite) {
        $SiteEntries = $Config.SCCMSite.PSObject.Properties | Where-Object { $_.Name -ne 'Comment' }
        if (-not $SiteEntries -or @($SiteEntries).Count -eq 0) {
            Add-Error 'Config.SCCMSite' 'MISSING_REQUIRED' "SCCMSite section has no site configurations" -Guidance 'Add at least one site entry to SCCMSite (e.g. "CB1" for site code CB1). Each entry defines the distribution point groups and deployment targets for that site.'
        }
        else {
            foreach ($siteProp in $SiteEntries) {
                $sLabel = $siteProp.Name
                $site   = $siteProp.Value
                $sBase  = "Config.SCCMSite[$sLabel]"

                if (-not $site.DeploymentGroups -or @($site.DeploymentGroups).Count -eq 0) {
                    Add-Error "$sBase.DeploymentGroups" 'MISSING_REQUIRED' "SCCMSite '${sLabel}': DeploymentGroups array is required and must not be empty" -Guidance 'DeploymentGroups lists the SCCM distribution point groups that will receive the application content. Add at least one group. The group names must match exactly what is configured in the SCCM console.'
                }

                # Legacy site-scoped deployments path. New bundles store
                # deployments at Script.SCCMPackager.Packages[].Deployments[]
                # (validated in Block 0.5). This block validates the
                # pre-migration on-disk shape for bundles that haven't been
                # re-saved through the GUI yet.
                if ($site.Deployments) {
                    foreach ($dep in $site.Deployments) {
                        $dLabel = "${sLabel}/$($dep.AppName)"
                        $dBase  = "$sBase.Deployments[$($dep.AppName)]"
                        if (-not $dep.AppName) {
                            Add-Error "$sBase.Deployments" 'MISSING_REQUIRED' "SCCMSite '${sLabel}': Deployment missing 'AppName'" -Guidance 'AppName in a Deployment entry must match an AppName in the Packages array. It identifies which application this deployment targets.'
                            continue
                        }
                        if (-not $dep.Collection) {
                            Add-Error "$dBase.Collection" 'MISSING_REQUIRED' "SCCMSite deployment '${dLabel}': Collection is required" -Guidance 'Collection is the SCCM device or user collection that will receive the deployment. The collection must exist in SCCM before the packager runs.'
                        }
                        $p = @{
                            Value       = $dep.DeployAction
                            ValidValues = $D.ValidDeployActions
                            FieldPath   = "$dBase.DeployAction"
                            Label       = "SCCMSite deployment '${dLabel}': DeployAction"
                            Guidance    = 'Install deploys the application to devices. Uninstall removes it. Most deployments use Install.'
                            AddError    = { param($p) Add-Error @p }
                        }
                        Test-EnumField @p
                        $p = @{
                            Value       = $dep.DeployPurpose
                            ValidValues = $D.ValidDeployPurposes
                            FieldPath   = "$dBase.DeployPurpose"
                            Label       = "SCCMSite deployment '${dLabel}': DeployPurpose"
                            Guidance    = 'Required deployments install automatically on the deadline. Available deployments appear in Software Center for users to install on demand.'
                            AddError    = { param($p) Add-Error @p }
                        }
                        Test-EnumField @p
                        $p = @{
                            Value       = $dep.UserNotification
                            ValidValues = $D.ValidUserNotifications
                            FieldPath   = "$dBase.UserNotification"
                            Label       = "SCCMSite deployment '${dLabel}': UserNotification"
                            Guidance    = 'DisplayAll shows all Software Center notifications and toast messages. DisplaySoftwareCenterOnly shows progress in Software Center but suppresses toasts. HideAll silently installs with no user-visible feedback.'
                            AddError    = { param($p) Add-Error @p }
                        }
                        Test-EnumField @p
                    }
                }
            }
        }
    }

    # ======================================================================
    # Blocks 1-8: Intune-specific validation (only when ScriptType is IntunePackager)
    # ======================================================================
    if ($ScriptType -eq 'IntunePackager') {

    # ======================================================================
    # Block 1: Auth hardening per tenant
    # ======================================================================
    foreach ($prop in ($Config.IntuneTenant.PSObject.Properties | Where-Object { $_.Name -ne 'Comment' })) {
        $tc     = $prop.Value
        $tLabel = $prop.Name
        $tBase  = "Config.IntuneTenant[$tLabel]"
        $flow   = if ($tc.AuthFlow) { $tc.AuthFlow } else { 'Interactive' }

        $p = @{
            Value       = $tc.AuthFlow
            ValidValues = $D.ValidAuthFlows
            FieldPath   = "$tBase.AuthFlow"
            Label       = "Tenant '${tLabel}': AuthFlow"
            Guidance    = 'Interactive opens a browser window for the operator to sign in. DeviceCode shows a code to enter at aka.ms/devicelogin (useful for headless sessions). ClientSecret and ClientCert use an Entra ID app registration for unattended automation. See SETUP.md for app registration guidance.'
            AddError    = { param($p) Add-Error @p }
        }
        Test-EnumField @p

        if ($flow -eq 'ClientSecret' -and -not $tc.ClientSecret) {
            Add-Error "$tBase.ClientSecret" 'REQUIRES_FIELD' "Tenant '${tLabel}': AuthFlow 'ClientSecret' requires a non-empty ClientSecret" -Guidance 'Create an Entra ID app registration, generate a client secret under Certificates and Secrets, and paste the secret value into ClientSecret. The secret must have the DeviceManagementApps.ReadWrite.All Graph permission.'
        }
        if ($flow -eq 'ClientCert') {
            if (-not $tc.CertThumbprint) {
                Add-Error "$tBase.CertThumbprint" 'REQUIRES_FIELD' "Tenant '${tLabel}': AuthFlow 'ClientCert' requires CertThumbprint" -Guidance 'Generate or import a certificate into the CurrentUser\My or LocalMachine\My certificate store, then paste the 40-character hex thumbprint into CertThumbprint. The certificate must be uploaded to the Entra ID app registration.'
            }
            elseif ($tc.CertThumbprint -notmatch '^[A-Fa-f0-9]{40}$') {
                $p = @{
                    FieldPath      = "$tBase.CertThumbprint"
                    Code           = 'CERT_FORMAT'
                    Msg            = "Tenant '${tLabel}': CertThumbprint must be a 40-character hex string"
                    AttemptedValue = $tc.CertThumbprint
                    Guidance       = 'Copy the thumbprint exactly from the Windows Certificate Manager (certmgr.msc) or from the Entra ID app registration Certificates page. Remove any spaces or colons.'
                }
                Add-Error @p
            }
            else {
                $certFound = $false
                foreach ($storePath in @('Cert:\CurrentUser\My', 'Cert:\LocalMachine\My')) {
                    try {
                        $cert = Get-ChildItem -Path $storePath -ErrorAction SilentlyContinue |
                            Where-Object { $_.Thumbprint -eq $tc.CertThumbprint }
                        if ($cert) { $certFound = $true; break }
                    }
                    catch { }
                }
                if (-not $certFound) {
                    $p = @{
                        FieldPath      = "$tBase.CertThumbprint"
                        Code           = 'CERT_NOT_FOUND'
                        Msg            = "Tenant '${tLabel}': Certificate with thumbprint '$($tc.CertThumbprint)' not found in CurrentUser or LocalMachine stores"
                        AttemptedValue = $tc.CertThumbprint
                        Guidance       = "Import the certificate PFX into the CurrentUser\My certificate store using certmgr.msc or Import-PfxCertificate. The private key must be included for authentication to work."
                    }
                    Add-Warning @p
                }
            }
        }

        $p = @{
            Value       = $tc.Architecture
            ValidValues = $D.ValidArchitecture
            FieldPath   = "$tBase.Architecture"
            Label       = "Tenant '${tLabel}': Architecture"
            Guidance    = 'x64 targets 64-bit Intel/AMD devices (most modern PCs). x86 targets 32-bit. arm64 targets ARM64 devices (Surface Pro X, Copilot+ PCs). x64x86 installs both architectures. AllWithARM64 covers all supported architectures.'
            AddError    = { param($p) Add-Error @p }
        }
        Test-EnumField @p
        $p = @{
            Value       = $tc.MinimumSupportedWindowsRelease
            ValidValues = $D.ValidMinOS
            FieldPath   = "$tBase.MinimumSupportedWindowsRelease"
            Label       = "Tenant '${tLabel}': MinimumSupportedWindowsRelease"
            Guidance    = 'Set this to the oldest Windows version in your managed fleet. Apps will not be offered to devices running older versions. Use W11_21H2 or later for Windows 11 only deployments.'
            AddError    = { param($p) Add-Error @p }
        }
        Test-EnumField @p
    }

    # ======================================================================
    # Block 2: Per-package app parameter validation
    # ======================================================================
    foreach ($pkg in $Packages) {
        $pLabel = if ($pkg.AppName) { $pkg.AppName } else { '(unnamed)' }
        $pBase  = "Config.Script.IntunePackager.Packages[$pLabel]"

        if (-not $pkg.AppName) {
            Add-Error "$pBase.AppName" 'MISSING_REQUIRED' "Package missing 'AppName' field" -Guidance 'AppName is the display name used in Intune. It must be unique within the tenant. Use a descriptive name including the version, such as "7-Zip 24.1 x64".'
            continue
        }

        # Block 3: Install/Uninstall command severity
        if ($IsEXEPackage) {
            if (-not $pkg.InstallCommand) {
                Add-Error "$pBase.InstallCommand" 'MISSING_REQUIRED' "Package '${pLabel}': InstallCommand is required for EXE packages" -Guidance 'EXE installers do not have a standard install command -- you must specify it explicitly. Include all silent flags (e.g. "/S", "/quiet /norestart"). Test the command in a VM before deploying.'
            }
            if (-not $pkg.UninstallCommand) {
                Add-Error "$pBase.UninstallCommand" 'MISSING_REQUIRED' "Package '${pLabel}': UninstallCommand is required for EXE packages" -Guidance 'Intune requires an uninstall command even if you do not plan to deploy uninstall. Typically uses msiexec /x or the vendor uninstaller. Check the vendor documentation or use the registry uninstall string from HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall.'
            }
        }
        else {
            if (-not $pkg.InstallCommand) {
                Add-Warning "$pBase.InstallCommand" 'MISSING_REQUIRED' "Package '${pLabel}': InstallCommand not set (MSI defaults will be used)" -Guidance 'For MSI packages, Intune uses "msiexec /i <file> /qn" by default. Set InstallCommand only if you need additional properties or transforms (e.g. "msiexec /i app.msi TRANSFORMS=transforms.mst /qn").'
            }
            if (-not $pkg.UninstallCommand) {
                Add-Warning "$pBase.UninstallCommand" 'MISSING_REQUIRED' "Package '${pLabel}': UninstallCommand not set (MSI defaults will be used)" -Guidance 'For MSI packages, Intune uses "msiexec /x {ProductCode} /qn" by default. Set UninstallCommand only if the default is insufficient.'
            }
        }

        # Enum fields validated via schema (carries AllowedValues and Guidance automatically)
        if ($pkg.InstallExperience) {
            Test-Field "$pBase.InstallExperience" 'InstallExperience' $pkg.InstallExperience
        }
        if ($pkg.RestartBehavior) {
            Test-Field "$pBase.RestartBehavior" 'RestartBehavior' $pkg.RestartBehavior
        }

        $p = @{
            Value       = $pkg.Architecture
            ValidValues = $D.ValidArchitecture
            FieldPath   = "$pBase.Architecture"
            Label       = "Package '${pLabel}': Architecture"
            Guidance    = 'x64 is correct for most modern business applications. Use x86 only for 32-bit only applications. arm64 for ARM native builds. x64x86 for dual-architecture packages.'
            AddError    = { param($p) Add-Error @p }
        }
        Test-EnumField @p
        $p = @{
            Value       = $pkg.MinimumSupportedWindowsRelease
            ValidValues = $D.ValidMinOS
            FieldPath   = "$pBase.MinimumSupportedWindowsRelease"
            Label       = "Package '${pLabel}': MinimumSupportedWindowsRelease"
            Guidance    = 'Set this to the oldest Windows version supported by the application. Devices below this version will not receive the app.'
            AddError    = { param($p) Add-Error @p }
        }
        Test-EnumField @p

        if ($null -ne $pkg.MaximumInstallationTimeInMinutes) {
            Test-Field "$pBase.MaximumInstallationTimeInMinutes" 'MaximumInstallationTimeInMinutes' $pkg.MaximumInstallationTimeInMinutes
        }

        if ($pkg.InformationURL) {
            Test-Field "$pBase.InformationURL" 'InformationURL' $pkg.InformationURL
        }
        if ($pkg.PrivacyURL) {
            Test-Field "$pBase.PrivacyURL" 'PrivacyURL' $pkg.PrivacyURL
        }

        # ==================================================================
        # Block 4: UpdateMode validation
        # ==================================================================
        if ($pkg.UpdateMode) {
            $p = @{
                Value       = $pkg.UpdateMode
                ValidValues = $D.ValidUpdateModes
                FieldPath   = "$pBase.UpdateMode"
                Label       = "Package '${pLabel}': UpdateMode"
                Guidance    = '"Create" creates a new Intune application (default for new packages). "Update" updates an existing app metadata and content. "UpdateContent" updates only the content package without changing metadata. Use "Update" or "UpdateContent" when deploying a new version to an existing app ID.'
                AddError    = { param($p) Add-Error @p }
            }
            Test-EnumField @p
            if ($pkg.UpdateMode -in @('Update', 'UpdateContent') -and -not $pkg.ExistingAppID) {
                Add-Error "$pBase.ExistingAppID" 'REQUIRES_FIELD' "Package '${pLabel}': UpdateMode '$($pkg.UpdateMode)' requires ExistingAppID" -Guidance 'Copy the Intune application GUID from the Intune console URL or from the app properties page. Format: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx.'
            }
            if ($pkg.ExistingAppID -and $pkg.ExistingAppID -notmatch $GuidPattern) {
                $p = @{
                    FieldPath      = "$pBase.ExistingAppID"
                    Code           = 'GUID_FORMAT'
                    Msg            = "Package '${pLabel}': ExistingAppID '$($pkg.ExistingAppID)' is not a valid GUID"
                    AttemptedValue = $pkg.ExistingAppID
                    Guidance       = 'The ExistingAppID must be the 36-character GUID from the Intune portal. Copy it from the app URL: ...apps/{this-is-the-id}/properties.'
                }
                Add-Error @p
            }
        }

        # ==================================================================
        # Block 5: Detection rule constraints
        # ==================================================================
        if ($pkg.DetectionRules -and @($pkg.DetectionRules).Count -gt 0) {
            $detRules        = @($pkg.DetectionRules)
            $scriptRuleCount = @($detRules | Where-Object { $_.Type -eq 'Script' }).Count

            if ($scriptRuleCount -gt 0 -and $detRules.Count -gt 1) {
                Add-Error "$pBase.DetectionRules" 'INVALID_COMBINATION' "Package '${pLabel}': Script detection rule must be the only rule (found $($detRules.Count) rules)" -Guidance 'Intune requires that a Script detection rule be the sole detection method -- it cannot be combined with File, Registry, or MSI rules. Use a single Script rule and include all detection logic in the script.'
            }

            foreach ($dr in $detRules) {
                $drBase = "$pBase.DetectionRules[$($dr.Type)]"
                if (-not $dr.Type -or $dr.Type -notin $D.ValidDetectionTypes) {
                    $p = @{
                        FieldPath      = $drBase
                        Code           = 'INVALID_ENUM_VALUE'
                        Msg            = "Package '${pLabel}': Detection rule Type '$($dr.Type)' invalid. Must be one of: $($D.ValidDetectionTypes -join ', ')"
                        AttemptedValue = $dr.Type
                        AllowedValues  = $D.ValidDetectionTypes
                        Guidance       = 'MSI uses the product code from the MSI database. File checks for a file or folder. Registry checks a registry key or value. Script runs a PowerShell detection script and exits 0 for detected.'
                    }
                    Add-Error @p
                    continue
                }

                switch ($dr.Type) {
                    'MSI' {
                        if (-not $dr.ProductCode) {
                            Add-Error "$drBase.ProductCode" 'MISSING_REQUIRED' "Package '${pLabel}': MSI detection rule requires ProductCode" -Guidance 'The ProductCode is the MSI product GUID, found in the MSI database or the registry under HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall. Format: {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}.'
                        }
                        elseif ($dr.ProductCode -notmatch '^\{?[0-9A-Fa-f]{8}(-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}?$') {
                            $p = @{
                                FieldPath      = "$drBase.ProductCode"
                                Code           = 'GUID_FORMAT'
                                Msg            = "Package '${pLabel}': MSI ProductCode '$($dr.ProductCode)' is not a valid GUID format"
                                AttemptedValue = $dr.ProductCode
                                Guidance       = 'ProductCode must be a valid GUID with or without curly braces. Example: {12345678-1234-1234-1234-123456789012}.'
                            }
                            Add-Error @p
                        }
                        if ($dr.ProductVersionOperator -and $dr.ProductVersionOperator -ne 'notConfigured' -and -not $dr.ProductVersion) {
                            Add-Warning "$drBase.ProductVersion" 'REQUIRES_FIELD' "Package '${pLabel}': MSI ProductVersionOperator set but ProductVersion is empty" -Guidance 'When ProductVersionOperator is set (e.g. "greaterThanOrEqual"), Intune compares the installed MSI version against ProductVersion. Set ProductVersion to the minimum acceptable version string.'
                        }
                    }
                    'File' {
                        if (-not $dr.Path) {
                            Add-Error "$drBase.Path" 'MISSING_REQUIRED' "Package '${pLabel}': File detection rule requires Path" -Guidance 'Path is the folder containing the file or folder to detect (e.g. "C:\Program Files\7-Zip"). Do not include the file name -- that goes in FileOrFolder.'
                        }
                        if (-not $dr.FileOrFolder) {
                            Add-Error "$drBase.FileOrFolder" 'MISSING_REQUIRED' "Package '${pLabel}': File detection rule requires FileOrFolder" -Guidance 'FileOrFolder is the name of the file or folder to detect within Path (e.g. "7zFM.exe"). For folder detection, use the folder name without a trailing slash.'
                        }
                        $p = @{
                            Value       = $dr.DetectionMethod
                            ValidValues = $D.ValidDetectionMethods.File
                            FieldPath   = "$drBase.DetectionMethod"
                            Label       = "Package '${pLabel}': File DetectionMethod"
                            Guidance    = 'Existence checks whether the file or folder exists. DateModified/DateCreated compare timestamps. Version compares file version metadata. Size compares file size in MB.'
                            AddError    = { param($p) Add-Error @p }
                        }
                        Test-EnumField @p
                        if ($dr.DetectionMethod -eq 'Existence') {
                            $p = @{
                                Value       = $dr.DetectionType
                                ValidValues = $D.ValidExistenceDetectionTypes
                                FieldPath   = "$drBase.DetectionType"
                                Label       = "Package '${pLabel}': File DetectionType"
                                Guidance    = 'Existence DetectionType must be one of: exists or doesNotExist.'
                                AddError    = { param($p) Add-Error @p }
                            }
                            Test-EnumField @p
                        }
                        if ($dr.DetectionMethod -and $dr.DetectionMethod -ne 'Existence') {
                            if (-not $dr.Operator) {
                                Add-Error "$drBase.Operator" 'REQUIRES_FIELD' "Package '${pLabel}': File detection '$($dr.DetectionMethod)' requires Operator" -Guidance 'Operator defines the comparison. Valid operators: equal, notEqual, greaterThanOrEqual, greaterThan, lessThanOrEqual, lessThan.'
                            }
                            if ($null -eq $dr.Value -and -not $dr.Value) {
                                Add-Error "$drBase.Value" 'REQUIRES_FIELD' "Package '${pLabel}': File detection '$($dr.DetectionMethod)' requires Value" -Guidance 'Value is the comparison target. For Version, use the version string (e.g. "24.1.0"). For Size, use the size in MB. For dates, use an ISO 8601 string.'
                            }
                        }
                    }
                    'Registry' {
                        if (-not $dr.KeyPath) {
                            Add-Error "$drBase.KeyPath" 'MISSING_REQUIRED' "Package '${pLabel}': Registry detection rule requires KeyPath" -Guidance 'KeyPath is the full registry key path to check (e.g. "HKEY_LOCAL_MACHINE\SOFTWARE\7-Zip"). Intune will check whether this key exists (Existence) or compare a value within it.'
                        }
                        $p = @{
                            Value       = $dr.DetectionMethod
                            ValidValues = $D.ValidDetectionMethods.Registry
                            FieldPath   = "$drBase.DetectionMethod"
                            Label       = "Package '${pLabel}': Registry DetectionMethod"
                            Guidance    = 'Existence checks whether the key or value exists. StringComparison compares a REG_SZ value. IntegerComparison compares a REG_DWORD value. VersionComparison compares a version-formatted REG_SZ value.'
                            AddError    = { param($p) Add-Error @p }
                        }
                        Test-EnumField @p
                        if ($dr.DetectionMethod -eq 'Existence') {
                            $p = @{
                                Value       = $dr.DetectionType
                                ValidValues = $D.ValidExistenceDetectionTypes
                                FieldPath   = "$drBase.DetectionType"
                                Label       = "Package '${pLabel}': Registry DetectionType"
                                Guidance    = 'Existence DetectionType must be one of: exists or doesNotExist.'
                                AddError    = { param($p) Add-Error @p }
                            }
                            Test-EnumField @p
                        }
                    }
                    'Script' {
                        # Script detection uses the existing template path; no extra fields needed
                    }
                }
            }
        }

        # ==================================================================
        # Block 6: Dependency/Supersedence limits and self-reference
        # ==================================================================
        if ($pkg.Dependencies -and @($pkg.Dependencies).Count -gt 0) {
            $depCount = @($pkg.Dependencies).Count
            if ($depCount -gt $D.MaxDependencies) {
                $p = @{
                    FieldPath      = "$pBase.Dependencies"
                    Code           = 'COUNT_EXCEEDED'
                    Msg            = "Package '${pLabel}': Max $($D.MaxDependencies) dependencies allowed, found $depCount"
                    AttemptedValue = $depCount
                    Guidance       = "Intune supports up to $($D.MaxDependencies) dependencies per application. Split the package into multiple applications if you require more."
                }
                Add-Error @p
            }
            foreach ($dep in $pkg.Dependencies) {
                $depBase = "$pBase.Dependencies[$($dep.TargetAppName)]"
                if (-not $dep.TargetAppName -and -not $dep.TargetAppID) {
                    Add-Error $depBase 'MISSING_REQUIRED' "Package '${pLabel}': Dependency must have TargetAppName or TargetAppID" -Guidance 'Set TargetAppName to the AppName of another package in this config, or set TargetAppID to the Intune GUID of an existing app in the tenant.'
                }
                if ($dep.TargetAppName -eq $pkg.AppName) {
                    Add-Error $depBase 'SELF_REFERENCE' "Package '${pLabel}': Cannot depend on itself" -Guidance 'Self-referencing dependencies are logically invalid and rejected by Intune. Check the TargetAppName value.'
                }
                $p = @{
                    Value       = $dep.DependencyType
                    ValidValues = $D.ValidDependencyTypes
                    FieldPath   = "$depBase.DependencyType"
                    Label       = "Package '${pLabel}': DependencyType"
                    Guidance    = '"AutoInstall" causes Intune to automatically install the dependency before the dependent app. "Detect" checks whether the dependency is already installed and blocks if not, but does not install it.'
                    AddError    = { param($p) Add-Error @p }
                }
                Test-EnumField @p
                if ($dep.TargetAppID -and $dep.TargetAppID -notmatch $GuidPattern) {
                    $p = @{
                        FieldPath      = "$depBase.TargetAppID"
                        Code           = 'GUID_FORMAT'
                        Msg            = "Package '${pLabel}': Dependency TargetAppID '$($dep.TargetAppID)' is not a valid GUID"
                        AttemptedValue = $dep.TargetAppID
                        Guidance       = 'Copy the GUID from the Intune portal app URL. Format: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx.'
                    }
                    Add-Error @p
                }
            }
        }

        if ($pkg.Supersedence -and @($pkg.Supersedence).Count -gt 0) {
            $supCount = @($pkg.Supersedence).Count
            if ($supCount -gt $D.MaxSupersedence) {
                $p = @{
                    FieldPath      = "$pBase.Supersedence"
                    Code           = 'COUNT_EXCEEDED'
                    Msg            = "Package '${pLabel}': Max $($D.MaxSupersedence) supersedence entries allowed, found $supCount"
                    AttemptedValue = $supCount
                    Guidance       = "Intune supports up to $($D.MaxSupersedence) supersedence relationships per application. Combine older versions or remove supersedence for very old versions that are no longer managed."
                }
                Add-Error @p
            }
            foreach ($sup in $pkg.Supersedence) {
                $supBase = "$pBase.Supersedence[$($sup.TargetAppName)]"
                if (-not $sup.TargetAppName -and -not $sup.TargetAppID) {
                    Add-Error $supBase 'MISSING_REQUIRED' "Package '${pLabel}': Supersedence must have TargetAppName or TargetAppID" -Guidance 'Set TargetAppName to the AppName of another package, or TargetAppID to the Intune GUID of the older version being superseded.'
                }
                if ($sup.TargetAppName -eq $pkg.AppName) {
                    Add-Error $supBase 'SELF_REFERENCE' "Package '${pLabel}': Cannot supersede itself" -Guidance 'An application cannot supersede itself. Check the TargetAppName value -- it must refer to a different application (typically an older version).'
                }
                $p = @{
                    Value       = $sup.SupersedenceType
                    ValidValues = $D.ValidSupersedenceTypes
                    FieldPath   = "$supBase.SupersedenceType"
                    Label       = "Package '${pLabel}': SupersedenceType"
                    Guidance    = '"Replace" uninstalls the superseded app and installs the new version. "Update" installs over the top without explicit uninstall. Use "Replace" when the package name or product code changes between versions.'
                    AddError    = { param($p) Add-Error @p }
                }
                Test-EnumField @p
                if ($sup.TargetAppID -and $sup.TargetAppID -notmatch $GuidPattern) {
                    $p = @{
                        FieldPath      = "$supBase.TargetAppID"
                        Code           = 'GUID_FORMAT'
                        Msg            = "Package '${pLabel}': Supersedence TargetAppID '$($sup.TargetAppID)' is not a valid GUID"
                        AttemptedValue = $sup.TargetAppID
                        Guidance       = 'Copy the GUID from the Intune portal app URL of the older version being superseded.'
                    }
                    Add-Error @p
                }
            }
        }

        # Cross-check: same app in both dependency and supersedence
        if ($pkg.Dependencies -and $pkg.Supersedence) {
            $depNames = @($pkg.Dependencies | ForEach-Object { $_.TargetAppName } | Where-Object { $_ })
            $supNames = @($pkg.Supersedence | ForEach-Object { $_.TargetAppName } | Where-Object { $_ })
            $overlap  = $depNames | Where-Object { $_ -in $supNames }
            foreach ($o in $overlap) {
                Add-Warning "$pBase.Dependencies" 'INVALID_COMBINATION' "Package '${pLabel}': '$o' appears in both Dependencies and Supersedence" -Guidance 'Having the same app as both a dependency and a supersedence target is unusual and may cause unexpected Intune behavior. Verify the intent -- typically an app is either a prerequisite (dependency) or an older version being replaced (supersedence), not both.'
            }
        }

        # ==================================================================
        # Block 7: Custom return code validation
        # ==================================================================
        if ($pkg.CustomReturnCodes -and @($pkg.CustomReturnCodes).Count -gt 0) {
            foreach ($rc in $pkg.CustomReturnCodes) {
                $rcBase = "$pBase.CustomReturnCodes[$($rc.ReturnCode)]"
                if ($null -eq $rc.ReturnCode) {
                    Add-Error $rcBase 'MISSING_REQUIRED' "Package '${pLabel}': CustomReturnCode entry missing ReturnCode field" -Guidance 'ReturnCode is the numeric exit code returned by the installer. Common codes: 0 (success), 1603 (fatal error), 3010 (soft reboot required), 1641 (hard reboot required).'
                }
                if (-not $rc.Type) {
                    Add-Error "$rcBase.Type" 'MISSING_REQUIRED' "Package '${pLabel}': CustomReturnCode entry missing Type field" -Guidance 'Type defines how Intune interprets this exit code. Valid types: success, softReboot, hardReboot, retry, failed.'
                }
                $p = @{
                    Value       = $rc.Type
                    ValidValues = $D.ValidReturnCodeTypes
                    FieldPath   = "$rcBase.Type"
                    Label       = "Package '${pLabel}': ReturnCode type"
                    Guidance    = '"success" marks the installation as successful. "softReboot" schedules a pending restart. "hardReboot" triggers an immediate restart. "retry" causes Intune to retry the installation. "failed" marks the installation as failed.'
                    AddError    = { param($p) Add-Error @p }
                }
                Test-EnumField @p
            }
        }
    }

    # ======================================================================
    # Block 8: Assignment scheduling and filter validation
    # ======================================================================
    foreach ($prop in ($Config.IntuneTenant.PSObject.Properties | Where-Object { $_.Name -ne 'Comment' })) {
        $tc     = $prop.Value
        $tLabel = $prop.Name
        $tBase  = "Config.IntuneTenant[$tLabel]"

        if (-not $tc.Assignments) { continue }

        foreach ($asn in $tc.Assignments) {
            $aLabel = "${tLabel}/$($asn.AppName)"
            $aBase  = "$tBase.Assignments[$($asn.AppName)]"

            $p = @{
                Value       = $asn.Type
                ValidValues = $D.ValidAssignmentTypes
                FieldPath   = "$aBase.Type"
                Label       = "Assignment '${aLabel}': Type"
                Guidance    = '"AllDevices" targets all enrolled devices. "AllUsers" targets all licensed users. "Group" targets a specific Entra ID group (most common for production deployments).'
                AddError    = { param($p) Add-Error @p }
            }
            Test-EnumField @p
            $p = @{
                Value       = $asn.Intent
                ValidValues = $D.ValidAssignmentIntents
                FieldPath   = "$aBase.Intent"
                Label       = "Assignment '${aLabel}': Intent"
                Guidance    = '"required" installs automatically. "available" appears in Company Portal for user-initiated install. "uninstall" removes the app. "availablewithoutenrollment" shows the app in Company Portal without Intune enrollment (requires group type).'
                AddError    = { param($p) Add-Error @p }
            }
            Test-EnumField @p

            $intentLower = if ($asn.Intent) { $asn.Intent.ToLowerInvariant() } else { '' }
            $typeLower   = if ($asn.Type) { $asn.Type.ToLowerInvariant() } else { '' }

            if ($intentLower -eq 'availablewithoutenrollment' -and $typeLower -ne 'group') {
                Add-Error "$aBase.Intent" 'INVALID_COMBINATION' "Assignment '${aLabel}': Intent 'availablewithoutenrollment' is only valid with Type 'Group'" -Guidance 'The availablewithoutenrollment intent requires targeting a specific Entra ID group. Set Type to "Group" and provide a GroupID.'
            }

            if ($typeLower -eq 'group') {
                if (-not $asn.GroupID) {
                    Add-Error "$aBase.GroupID" 'REQUIRES_FIELD' "Assignment '${aLabel}': Group assignment requires GroupID" -Guidance 'GroupID is the Entra ID object ID of the group, or the display name of the group. GUIDs are used directly; display names are resolved via Graph API at runtime.'
                }
                elseif ($asn.GroupID -notmatch $GuidPattern) {
                    $p = @{
                        FieldPath      = "$aBase.GroupID"
                        Code           = 'GROUP_NAME'
                        Msg            = "Assignment '${aLabel}': GroupID '$($asn.GroupID)' is a display name, not a GUID. It will be resolved via Graph API at runtime."
                        AttemptedValue = $asn.GroupID
                        Guidance       = 'Display names are resolved to object IDs via Resolve-EntraGroupId. For faster execution and to avoid ambiguity, use the GUID directly.'
                    }
                    Add-Warning @p
                }
                if (-not $asn.GroupMode) {
                    Add-Warning "$aBase.GroupMode" 'MISSING_REQUIRED' "Assignment '${aLabel}': GroupMode not set, will default to 'include'" -Guidance 'GroupMode controls whether this is an include or exclude assignment. "Include" (default) targets the group. "Exclude" exempts the group from an otherwise broader assignment.'
                }
                else {
                    $p = @{
                        Value       = $asn.GroupMode
                        ValidValues = $D.ValidFilterModes
                        FieldPath   = "$aBase.GroupMode"
                        Label       = "Assignment '${aLabel}': GroupMode"
                        Guidance    = 'GroupMode must be Include or Exclude. Include targets the group; Exclude exempts it.'
                        AddError    = { param($p) Add-Error @p }
                    }
                    Test-EnumField @p
                }
            }

            if ($intentLower -eq 'uninstall') {
                if ($asn.AvailableTime -or $asn.DeadlineTime) {
                    Add-Warning "$aBase.AvailableTime" 'INVALID_COMBINATION' "Assignment '${aLabel}': AvailableTime/DeadlineTime are ignored for 'uninstall' intent" -Guidance 'Scheduling settings have no effect on uninstall deployments. The uninstall runs as soon as the assignment is evaluated on the device.'
                }
            }

            if ($asn.AvailableTime -and $asn.DeadlineTime) {
                try {
                    $avail    = [datetime]::Parse($asn.AvailableTime)
                    $deadline = [datetime]::Parse($asn.DeadlineTime)
                    # Skip validation when either is ASAP (year 0001 = DateTime.MinValue)
                    # Both ASAP is a valid "deploy immediately" scenario
                    if ($avail.Year -gt 1 -and $deadline.Year -gt 1 -and $deadline -le $avail) {
                        Add-Error "$aBase.DeadlineTime" 'SCHEDULE_ORDER' "Assignment '${aLabel}': DeadlineTime must be after AvailableTime" -Guidance 'AvailableTime is when the app appears in Company Portal. DeadlineTime is when Intune installs it automatically on required deployments. The deadline must be in the future relative to the available time.'
                    }
                }
                catch {
                    Add-Warning "$aBase.AvailableTime" 'MISSING_REQUIRED' "Assignment '${aLabel}': Could not parse AvailableTime/DeadlineTime for cross-validation" -Guidance 'Use ISO 8601 format: "2026-06-01T08:00:00" or "2026-06-01T08:00:00Z". Both fields must be parseable for deadline ordering validation.'
                }
            }

            if ($asn.AutoUpdateSupersededApps -and $asn.AutoUpdateSupersededApps -ne 'notConfigured') {
                if ($intentLower -ne 'available') {
                    Add-Error "$aBase.AutoUpdateSupersededApps" 'INVALID_COMBINATION' "Assignment '${aLabel}': AutoUpdateSupersededApps is only valid with intent 'available'" -Guidance 'AutoUpdateSupersededApps tells Intune to automatically update devices that have an older superseded version installed. This feature only applies to Available deployments -- on Required deployments, updates happen via the required intent itself.'
                }
            }

            if ($asn.EnableRestartGracePeriod -eq $true) {
                if ($null -ne $asn.RestartGracePeriod -and ($asn.RestartGracePeriod -lt 1 -or $asn.RestartGracePeriod -gt 20160)) {
                    $p = @{
                        FieldPath      = "$aBase.RestartGracePeriod"
                        Code           = 'OUT_OF_RANGE'
                        Msg            = "Assignment '${aLabel}': RestartGracePeriod must be 1-20160 minutes"
                        AttemptedValue = $asn.RestartGracePeriod
                        Guidance       = 'RestartGracePeriod is the window in minutes after installation before Intune forces a restart. 20160 minutes = 14 days. Typical values are 1440 (1 day) to 10080 (7 days).'
                    }
                    Add-Error @p
                }
                if ($null -ne $asn.RestartCountDownDisplay -and ($asn.RestartCountDownDisplay -lt 1 -or $asn.RestartCountDownDisplay -gt 240)) {
                    $p = @{
                        FieldPath      = "$aBase.RestartCountDownDisplay"
                        Code           = 'OUT_OF_RANGE'
                        Msg            = "Assignment '${aLabel}': RestartCountDownDisplay must be 1-240 minutes"
                        AttemptedValue = $asn.RestartCountDownDisplay
                        Guidance       = 'RestartCountDownDisplay is how many minutes before the forced restart the countdown notification appears. 240 minutes = 4 hours. Typical values are 15-60 minutes.'
                    }
                    Add-Error @p
                }
                if ($null -ne $asn.RestartNotificationSnooze -and ($asn.RestartNotificationSnooze -lt 1 -or $asn.RestartNotificationSnooze -gt 712)) {
                    $p = @{
                        FieldPath      = "$aBase.RestartNotificationSnooze"
                        Code           = 'OUT_OF_RANGE'
                        Msg            = "Assignment '${aLabel}': RestartNotificationSnooze must be 1-712 minutes"
                        AttemptedValue = $asn.RestartNotificationSnooze
                        Guidance       = 'RestartNotificationSnooze is how long the user can snooze the restart notification. 712 minutes is approximately 12 hours. Typical values are 30-240 minutes.'
                    }
                    Add-Error @p
                }
                if ($null -ne $asn.RestartCountDownDisplay -and $null -ne $asn.RestartGracePeriod) {
                    if ($asn.RestartCountDownDisplay -gt $asn.RestartGracePeriod) {
                        Add-Error "$aBase.RestartCountDownDisplay" 'RANGE_EXCEEDED' "Assignment '${aLabel}': RestartCountDownDisplay ($($asn.RestartCountDownDisplay)) cannot exceed RestartGracePeriod ($($asn.RestartGracePeriod))" -Guidance 'The countdown notification must appear before the grace period ends. Set RestartCountDownDisplay to a value less than RestartGracePeriod.'
                    }
                }
            }

            if ($asn.FilterMode -and -not $asn.FilterName) {
                Add-Error "$aBase.FilterName" 'REQUIRES_FIELD' "Assignment '${aLabel}': FilterMode requires a corresponding FilterName" -Guidance 'FilterName is the display name of an Intune assignment filter. Create the filter in the Intune portal under Filters before referencing it here.'
            }
            $p = @{
                Value       = $asn.FilterMode
                ValidValues = $D.ValidFilterModes
                FieldPath   = "$aBase.FilterMode"
                Label       = "Assignment '${aLabel}': FilterMode"
                Guidance    = '"Include" applies the assignment only to devices that match the filter. "Exclude" applies the assignment to all devices except those matching the filter.'
                AddError    = { param($p) Add-Error @p }
            }
            Test-EnumField @p

            if ($typeLower -eq 'group' -and $asn.GroupMode -and $asn.GroupMode.ToLowerInvariant() -eq 'exclude') {
                if ($asn.AvailableTime -or $asn.DeadlineTime -or $asn.EnableRestartGracePeriod) {
                    Add-Warning "$aBase.GroupMode" 'INVALID_COMBINATION' "Assignment '${aLabel}': Scheduling settings are ignored for Exclude group assignments" -Guidance 'Exclude assignments only affect who receives the app -- they do not have their own scheduling. Scheduling is controlled by the include assignments for the same app.'
                }
            }
        }
    }

    } # End of IntunePackager-specific validation (Blocks 1-8)

    return [PSCustomObject]@{
        IsValid  = ($Errors.Count -eq 0)
        Errors   = $Errors.ToArray()
        Warnings = $Warnings.ToArray()
        Issues   = $Issues.ToArray()
    }
}
