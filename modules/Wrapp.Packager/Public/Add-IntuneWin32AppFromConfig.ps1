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

    if ($Package.AdditionalRequirementRules -and @($Package.AdditionalRequirementRules).Count -gt 0) {
        $addReqs = New-RequirementRuleFromConfig -RequirementRules $Package.AdditionalRequirementRules
        if ($addReqs) {
            $DefaultAppProperties.AdditionalRequirementRule = $addReqs
        }
    }

    if ($Package.CustomReturnCodes -and @($Package.CustomReturnCodes).Count -gt 0) {
        $retCodes = New-ReturnCodeFromConfig -ReturnCodes $Package.CustomReturnCodes
        if ($retCodes) {
            $DefaultAppProperties.ReturnCode = $retCodes
        }
    }

    # Merge defaults + package overrides and validate
    # Exclude keys already handled above or used only by the orchestrator.
    # RULE (enforced by SourceLintTests.PackagerHandledKeysCoverEveryPackageRead):
    # every $Package.<Key> this function reads is listed here, so a raw
    # package value can never overwrite the transformed one it was read for.
    $HandledKeys = @(
        'AppName', 'AppVersion', 'InstallCommand', 'UninstallCommand', 'Comment',
        'Architecture', 'MinimumSupportedWindowsRelease', 'MaximumInstallationTimeInMinutes',
        'Categories', 'ScopeTags', 'DetectionRules',
        'AdditionalRequirementRules', 'CustomReturnCodes', 'IconFile',
        'PackageOption', 'PackageId', 'UpdateMode', 'ExistingAppID', 'TargetTenants',
        'Dependencies', 'Supersedence',
        # Routing / orchestrator-consumed keys that live on the package object
        # but are not parameters to Add-IntuneWin32App. TenantId is used by
        # Invoke-WrappIntune to route the package to a tenant; Assignments
        # is consumed in Phase 12 (Set-Win32AppAssignment); IsEnabled is the
        # orchestrator's package filter.
        'TenantId', 'Assignments', 'IsEnabled',
        # Notes is folded into the tracking JSON above. Letting the raw
        # package value through here replaced that JSON: an empty note was
        # stripped as blank (app created with no Notes at all) and a typed
        # note shipped as plain text -- either way Invoke-WrappIntune's
        # Notes.Guid check failed and every assignment was skipped.
        'Notes'
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

    Invoke-TokenRefreshIfNeeded -TenantID $TenantID -ClientID $ClientID

    Write-Log "Creating Intune app: $AppName"
    foreach ($key in ($LogArgs.Keys | Sort-Object)) {
        Write-Log "  $key = $($LogArgs[$key])" -NoConsole
    }

    # Add-IntuneWin32App is called once: it is NOT idempotent (it creates the
    # app in Graph before uploading content), so calling it again would create
    # a second app. When the content part fails after the app was registered,
    # only the content is retried, onto that same app, after checking it is
    # still the app this run created and still empty
    # (Invoke-WrappContentUploadRetry).
    #
    # -Verbose is applied here specifically (not globally via $VerbosePreference)
    # so the upload sub-steps (metadata gathering, body construction, storage URI
    # wait, upload method, commit) are observable while the rest of the module's
    # Graph calls stay quiet.
    #
    # Invoke-WrappVendorUpload merges the vendor's verbose, warning and error
    # streams: verbose lines become live AppCreation progress steps (they flow
    # down this uncaptured pipeline to the caller's output, so callers still
    # consume this function as a stream - see Invoke-WrappIntune Phase 11);
    # warnings and errors, which is how the vendor reports a failed upload,
    # are written to the run log and kept as the failure reason. It also
    # removes the Expand_ folder the vendor leaves beside the .intunewin when
    # an upload fails.
    $script:WrappLastAppFailure = $null
    $Upload = @{}
    $UploadParams = @{
        Call          = { Add-IntuneWin32App @Win32AppArgs -Verbose }
        IntuneWinPath = $IntuneWinPath
        State         = $Upload
        AppName       = $AppName
        TenantID      = $TenantID
        Translate     = $true
    }
    Invoke-WrappVendorUpload @UploadParams
    $Win32App = $Upload.Result

    if ($Win32App -and $Win32App.Id) {
        Write-Log "App created successfully: $AppName (ID: $($Win32App.Id))"
        return $Win32App
    }

    $Problems = [System.Collections.Generic.List[string]]::new()
    foreach ($p in @($Upload.Problems)) { if ($p) { $Problems.Add($p) } }
    $Leftovers = [System.Collections.Generic.List[string]]::new()
    foreach ($f in @($Upload.LeftoverFolders)) { if ($f) { $Leftovers.Add($f) } }
    $Attempts = 1

    # The app was registered but its content did not land: retry the content
    # onto that same app (never a second app), after checking it is still
    # the one this run created and still empty. See Invoke-WrappContentUploadRetry.
    if ($Upload.AppId) {
        Write-Log "First upload failed after the app was registered ($($Upload.AppId)): $($Problems -join ' | ')" -Type 2
        $Retry = @{}
        $RetryParams = @{
            AppId         = $Upload.AppId
            AppName       = $AppName
            IntuneWinPath = $IntuneWinPath
            TenantID      = $TenantID
            ClientID      = $ClientID
            State         = $Retry
        }
        Invoke-WrappContentUploadRetry @RetryParams
        $Attempts = $Retry.Attempts
        foreach ($p in @($Retry.Problems)) { if ($p) { $Problems.Add($p) } }
        foreach ($f in @($Retry.LeftoverFolders)) { if ($f) { $Leftovers.Add($f) } }
        if ($Retry.Stopped) { $Problems.Add("retry stopped: $($Retry.Stopped)") }
        if ($Retry.Result) {
            Write-Log "App created successfully after $Attempts upload attempt(s): $AppName (ID: $($Upload.AppId))"
            return $Retry.Result
        }
    }

    # What the run reports, and what Invoke-WrappIntune turns into a clean-up
    # notice: the reason, the half-created app (if any), what is left on disk.
    $Reason = if ($Problems.Count) { $Problems -join ' | ' } else { 'the vendor call returned nothing and reported no reason' }
    $script:WrappLastAppFailure = @{
        Reason    = $Reason
        AppId     = $Upload.AppId
        Attempts  = $Attempts
        Leftovers = $Leftovers.ToArray()
    }
    Write-Log "App creation failed for '$AppName' after $Attempts upload attempt(s): $Reason" -Type 3
    return $null
}
