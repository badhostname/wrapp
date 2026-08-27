function Add-IntuneWin32AppFromConfig {
    <#
    .SYNOPSIS
        Creates a single Win32 app in Intune from a package config entry.

    .DESCRIPTION
        Handles detection script injection, icon loading, Notes JSON creation,
        architecture/MinOS resolution, parameter validation, and the call to
        Add-IntuneWin32App. Returns the Intune app object on success.

    .PARAMETER Package
        The package object from Config.Script.IntunePackager.Packages[].

    .PARAMETER App
        The App section from Config.json.

    .PARAMETER Config
        The full Config object (needed for Script.Detect access).

    .PARAMETER TenantConfig
        The resolved IntuneTenant config for this tenant.

    .PARAMETER IntuneWinPath
        Full path to the .intunewin package file.

    .PARAMETER ScriptPath
        Path to the Script directory (for DetectScript.ps1 template).

    .PARAMETER Creator
        Creator name for the Notes field.

    .PARAMETER TenantID
        For token refresh calls.

    .PARAMETER ClientID
        For token refresh calls.

    .PARAMETER Validate
        Validation mode - logs what would happen without making API calls.

    .OUTPUTS
        [PSCustomObject] The Intune app object from Add-IntuneWin32App, or $null.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        $Package,

        [Parameter(Mandatory = $true)]
        $App,

        [Parameter(Mandatory = $true)]
        $Config,

        [Parameter(Mandatory = $true)]
        $TenantConfig,

        [Parameter(Mandatory = $true)]
        [string]$IntuneWinPath,

        [Parameter(Mandatory = $true)]
        [string]$ScriptPath,

        [string]$Creator = $env:USERNAME,

        [Parameter(Mandatory = $true)]
        [string]$TenantID,

        [Parameter(Mandatory = $true)]
        [string]$ClientID,

        [switch]$Validate
    )

    $AppName = $Package.AppName
    Write-Log "Processing package: $AppName"

    $packageOption = Get-ConfigValue $Package 'PackageOption' 'Default'

    # Build detection rule: multi-type dispatch or legacy script injection
    if ($Package.DetectionRules -and @($Package.DetectionRules).Count -gt 0) {
        $DetRuleParams = @{
            DetectionRules = $Package.DetectionRules
            ScriptPath     = $ScriptPath
        }
        $DetectionRule = New-DetectionRuleFromConfig @DetRuleParams
        Write-Log "Using multi-type detection rules ($(@($Package.DetectionRules).Count) rules)"
    }
    else {
        $DetectScriptPath = Join-Path -Path $ScriptPath -ChildPath 'DetectScript.ps1'
        if (-not (Test-Path -Path $DetectScriptPath)) {
            Write-Log "DetectScript.ps1 not found at: $DetectScriptPath" -Type 3
            return $null
        }

        $DetScriptParams = @{
            DetectScriptPath = $DetectScriptPath
            App              = $App
            DetectConfig     = $Config.Script.Detect
            PackageOption    = $packageOption
            OutputDirectory  = $ScriptPath
            AppName          = $AppName
        }
        $DetectionRule = New-DetectionRuleFromConfig @DetScriptParams
    }

    # Load icon: try IconFolder (CLI), then bundle folder relative to ScriptPath (GUI)
    $Icon = $null
    if ($Package.IconFile) {
        $IconPath = $null
        if ($TenantConfig.IconFolder) {
            $candidate = Join-Path -Path $TenantConfig.IconFolder -ChildPath $Package.IconFile
            if (Test-Path -Path $candidate) { $IconPath = $candidate }
        }
        if (-not $IconPath) {
            $bundleRoot = (Get-Item "FileSystem::$ScriptPath").Parent.FullName
            $candidate = Join-Path -Path $bundleRoot -ChildPath $Package.IconFile
            if (Test-Path -Path $candidate) { $IconPath = $candidate }
        }
        if ($IconPath) {
            $Icon = New-IntuneWin32AppIcon -FilePath $IconPath
            Write-Log "Loaded icon: $($Package.IconFile)"
        }
        else {
            Write-Log "Icon file not found for: $($Package.IconFile)" -Type 2
        }
    }

    # Build Notes JSON. The tracking keys (CreatedBy/Guid/Date) are load-bearing:
    # Invoke-WrappIntune matches Notes.Guid before applying assignments. The
    # operator's free-text note rides alongside them, never in place of them.
    $NotesObject = [PSCustomObject]@{
        CreatedBy = "$Creator using Wrapp.Packager"
        Guid      = $App.GUID
        Date      = (Get-Date -Format 'yyyy-MM-dd')
    }
    if ($Package.Notes) {
        $NotesObject | Add-Member -NotePropertyName Note -NotePropertyValue "$($Package.Notes)"
    }
    $Notes = $NotesObject | ConvertTo-Json -Compress

    # Resolve Architecture and MinOS: package > tenant > Defaults.psd1
    $pkgDefaults = $script:ModuleDefaults.DefaultIntunePackageProperties
    $ArchValue = if ($Package.Architecture) { $Package.Architecture }
                 elseif ($TenantConfig.Architecture) { $TenantConfig.Architecture }
                 else { $pkgDefaults.Architecture }
    $MinOSValue = if ($Package.MinimumSupportedWindowsRelease) { $Package.MinimumSupportedWindowsRelease }
                  elseif ($TenantConfig.MinimumSupportedWindowsRelease) { $TenantConfig.MinimumSupportedWindowsRelease }
                  else { $pkgDefaults.MinimumSupportedWindowsRelease }

    $RequirementRule = New-IntuneWin32AppRequirementRule -Architecture $ArchValue -MinimumSupportedWindowsRelease $MinOSValue

    # Build install/uninstall commands
    $installCommand   = "$($Package.InstallCommand) -PackageOption `"$packageOption`""
    $uninstallCommand = "$($Package.UninstallCommand) -PackageOption `"$packageOption`""

    # Default app properties. Owner/Developer may be user-defaults token
    # templates (e.g. '{{Company}} IT') -- expand against this bundle's App
    # section, mirroring the GUI's AddPackage metadata seeding.
    $DefaultAppProperties = @{
        Developer            = Expand-WrappTokens -Value $script:ModuleDefaults.DefaultAppProperties.Developer -App $App
        Owner                = Expand-WrappTokens -Value $script:ModuleDefaults.DefaultAppProperties.Owner -App $App
        CompanyPortalFeaturedApp = $script:ModuleDefaults.DefaultAppProperties.CompanyPortalFeaturedApp
        DetectionRule        = $DetectionRule
        RequirementRule      = $RequirementRule
        InstallCommandLine   = $installCommand
        UninstallCommandLine = $uninstallCommand
        FilePath             = $IntuneWinPath
        DisplayName          = $AppName
        AppVersion           = $App.DotVersion
        Notes                = $Notes
        Publisher            = $App.Company
    }

    if ($Icon) { $DefaultAppProperties.Icon = $Icon }
    if ($Package.Comment) {
        $DefaultAppProperties.Description = $Package.Comment
    }
    else {
        $DefaultAppProperties.Description = "$($App.Company) $AppName"
    }
    if ($Package.MaximumInstallationTimeInMinutes) {
        $DefaultAppProperties.MaximumInstallationTimeInMinutes = $Package.MaximumInstallationTimeInMinutes
    }

    # Categories: package > IntunePackager-level > none
    $categories = if ($Package.Categories) { $Package.Categories }
                  elseif ($Config.Script.IntunePackager.Categories) { $Config.Script.IntunePackager.Categories }
                  else { $null }
    if ($categories) {
        $DefaultAppProperties.CategoryName = @($categories)
    }

    # Scope tags: package > tenant-level > none
    $scopeTags = if ($Package.ScopeTags) { $Package.ScopeTags }
                 elseif ($TenantConfig.ScopeTags) { $TenantConfig.ScopeTags }
                 else { $null }
    if ($scopeTags) {
        $DefaultAppProperties.ScopeTagName = @($scopeTags)
    }

    # Additional requirement rules
    if ($Package.AdditionalRequirementRules -and @($Package.AdditionalRequirementRules).Count -gt 0) {
        $addReqs = New-RequirementRuleFromConfig -RequirementRules $Package.AdditionalRequirementRules
        if ($addReqs) {
            $DefaultAppProperties.AdditionalRequirementRule = $addReqs
        }
    }

    # Custom return codes
    if ($Package.CustomReturnCodes -and @($Package.CustomReturnCodes).Count -gt 0) {
        $retCodes = New-ReturnCodeFromConfig -ReturnCodes $Package.CustomReturnCodes
        if ($retCodes) {
            $DefaultAppProperties.ReturnCode = $retCodes
        }
    }

    # Merge defaults + package overrides and validate
    # Exclude keys already handled above or used only by the orchestrator
    $HandledKeys = @(
        'AppName', 'AppVersion', 'InstallCommand', 'UninstallCommand', 'Comment',
        'Architecture', 'MinimumSupportedWindowsRelease',
        'Categories', 'ScopeTags', 'DetectionRules',
        'AdditionalRequirementRules', 'CustomReturnCodes', 'IconFile',
        'PackageOption', 'PackageId', 'UpdateMode', 'ExistingAppID', 'TargetTenants',
        'Dependencies', 'Supersedence',
        # Routing / orchestrator-consumed keys that live on the package object
        # but are not parameters to Add-IntuneWin32App. TenantId is used by
        # Invoke-WrappIntune to route the package to a tenant; Assignments
        # is consumed in Phase 12 (Set-Win32AppAssignment).
        'TenantId', 'Assignments'
    )
    $Overrides = @{}
    $Package.PSObject.Properties | Where-Object { $_.Name -notin $HandledKeys } |
        ForEach-Object { $Overrides[$_.Name] = $_.Value }
    $Win32AppArgs = Get-ValidatedWin32AppParameters -Defaults $DefaultAppProperties -Overrides $Overrides

    # Log parameters (redact icon and detection script content)
    $LogArgs = $Win32AppArgs.Clone()
    if ($LogArgs['Icon']) {
        $LogArgs['Icon'] = '<redacted - binary data>'
    }
    if ($LogArgs['DetectionRule']) {
        $LogArgs['DetectionRule'] = '<script-based detection rule>'
    }

    if ($Validate) {
        Write-Log "[VALIDATE] Would call Add-IntuneWin32App with:"
        foreach ($key in ($LogArgs.Keys | Sort-Object)) {
            Write-Log "  $key = $($LogArgs[$key])"
        }
        return $null
    }

    # Refresh token before API call
    Invoke-TokenRefreshIfNeeded -TenantID $TenantID -ClientID $ClientID

    Write-Log "Creating Intune app: $AppName"
    foreach ($key in ($LogArgs.Keys | Sort-Object)) {
        Write-Log "  $key = $($LogArgs[$key])" -NoConsole
    }

    # Single attempt only. Add-IntuneWin32App is NOT idempotent -- it creates app
    # metadata in Graph before uploading content. Retrying would create ghost apps.
    #
    # -Verbose is applied here specifically (not globally via $VerbosePreference)
    # so the upload sub-steps (metadata gathering, body construction, storage URI
    # wait, upload method, commit) are observable while the rest of the module's
    # Graph calls stay quiet.
    #
    # B' (stage 3): the verbose stream is merged (4>&1) and translated LIVE into
    # typed Write-WrappStep Progress events -- pipelines stream, so each record
    # is translated the moment the vendored cmdlet writes it. The translated
    # events flow down this (uncaptured) pipeline to the caller's output; the
    # original verbose text is re-emitted (forced -Verbose, since the GUI runs
    # with $VerbosePreference = 'SilentlyContinue') so the run log keeps the raw
    # vendored diagnostics. Callers must consume this function as a stream and
    # separate WrappStep events from the final app object (see
    # Invoke-WrappIntune Phase 11).
    $Win32App = $null
    try {
        Add-IntuneWin32App @Win32AppArgs -Verbose 4>&1 | ForEach-Object {
            if ($_ -is [System.Management.Automation.VerboseRecord]) {
                Write-Verbose -Message $_.Message -Verbose
                $SubStep = Convert-VendorVerboseToStep -Message $_.Message
                if ($SubStep) {
                    $StepParams = @{
                        Package  = $AppName
                        Step     = 'AppCreation'
                        Kind     = 'Progress'
                        TenantId = $TenantID
                        Detail   = if ($SubStep.Detail) { "$($SubStep.Status)|$($SubStep.Detail)" } else { $SubStep.Status }
                        Percent  = $SubStep.Percent
                    }
                    Write-WrappStep @StepParams
                }
            }
            else {
                # ForEach-Object blocks run in the enclosing scope, so this
                # assigns the function-level $Win32App directly.
                $Win32App = $_
            }
        }
    }
    catch {
        Write-Log "Add-IntuneWin32App failed for '$AppName': $_" -Type 3
        Write-Log "The app may have been partially created in Intune. Check the portal and remove any incomplete entries before re-running." -Type 2
        return $null
    }

    if ($Win32App -and $Win32App.Id) {
        Write-Log "App created successfully: $AppName (ID: $($Win32App.Id))"
        return $Win32App
    }

    Write-Log "App creation returned no result for '$AppName'. The app may have been partially created in Intune without content." -Type 3
    Write-Log "Check the Intune portal for an incomplete app entry and remove it before re-running." -Type 2
    return $null
}
