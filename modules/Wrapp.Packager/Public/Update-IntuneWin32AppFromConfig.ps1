function Update-IntuneWin32AppFromConfig {
    <#
    .SYNOPSIS
        Updates an existing Win32 app in Intune from a package config entry.

    .DESCRIPTION
        Handles UpdateMode=Update (metadata only via Set-IntuneWin32App) and
        UpdateMode=UpdateContent (metadata + binary via Set-IntuneWin32App
        and Update-IntuneWin32AppPackageFile). Reuses the same config
        structure as Add-IntuneWin32AppFromConfig.

    .PARAMETER Package
        The package object from Config.Script.IntunePackager.Packages[].

    .PARAMETER App
        The App section from Config.json.

    .PARAMETER Config
        The full Config object.

    .PARAMETER TenantConfig
        The resolved IntuneTenant config for this tenant.

    .PARAMETER IntuneWinPath
        Full path to the .intunewin package file (required for UpdateContent).

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
        [PSCustomObject] The updated Intune app object, or $null.
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
    $UpdateMode = $Package.UpdateMode
    $ExistingAppID = $Package.ExistingAppID
    $packageOption = Get-ConfigValue $Package 'PackageOption' 'Default'

    Write-Log "Updating package: $AppName (Mode: $UpdateMode, ID: $ExistingAppID)"

    if (-not $ExistingAppID) {
        Write-Log "ExistingAppID is required for UpdateMode '$UpdateMode'" -Type 3
        return $null
    }

    $DetectionRule = $null
    if ($Package.DetectionRules -and @($Package.DetectionRules).Count -gt 0) {
        $DetectionRule = New-DetectionRuleFromConfig -DetectionRules $Package.DetectionRules -ScriptPath $ScriptPath
    }
    else {
        $DetectScriptPath = Join-Path -Path $ScriptPath -ChildPath 'DetectScript.ps1'
        if (Test-Path -Path $DetectScriptPath) {
            $p = @{
                DetectScriptPath = $DetectScriptPath
                App              = $App
                DetectConfig     = $Config.Script.Detect
                PackageOption    = $packageOption
                OutputDirectory  = $ScriptPath
                AppName          = $AppName
            }
            $DetectionRule = New-DetectionRuleFromConfig @p
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

    $ArchValue = if ($Package.Architecture) { $Package.Architecture }
                 elseif ($TenantConfig.Architecture) { $TenantConfig.Architecture }
                 else { 'x64' }
    $MinOSValue = if ($Package.MinimumSupportedWindowsRelease) { $Package.MinimumSupportedWindowsRelease }
                  elseif ($TenantConfig.MinimumSupportedWindowsRelease) { $TenantConfig.MinimumSupportedWindowsRelease }
                  else { 'W11_21H2' }

    $RequirementRule = New-IntuneWin32AppRequirementRule -Architecture $ArchValue -MinimumSupportedWindowsRelease $MinOSValue

    $installCommand   = "$($Package.InstallCommand) -PackageOption `"$packageOption`""
    $uninstallCommand = "$($Package.UninstallCommand) -PackageOption `"$packageOption`""

    $SetParams = @{
        ID               = $ExistingAppID
        DisplayName      = $AppName
        AppVersion       = $App.DotVersion
        Notes            = $Notes
        Publisher        = $App.Company
        Developer        = $script:ModuleDefaults.DefaultAppProperties.Developer
        Owner            = $script:ModuleDefaults.DefaultAppProperties.Owner
        RequirementRule  = $RequirementRule
    }

    if ($Package.InstallCommand) {
        $SetParams['InstallCommandLine'] = $installCommand
    }
    if ($Package.UninstallCommand) {
        $SetParams['UninstallCommandLine'] = $uninstallCommand
    }
    if ($DetectionRule) {
        $SetParams['DetectionRule'] = $DetectionRule
    }
    if ($Package.Comment) {
        $SetParams['Description'] = $Package.Comment
    }
    else {
        $SetParams['Description'] = "$($App.Company) $AppName"
    }
    if ($Package.RestartBehavior) {
        $SetParams['RestartBehavior'] = $Package.RestartBehavior
    }
    if ($Package.MaximumInstallationTimeInMinutes) {
        $SetParams['MaximumInstallationTimeInMinutes'] = $Package.MaximumInstallationTimeInMinutes
    }

    if ($Package.IconFile -and $TenantConfig.IconFolder) {
        $IconPath = Join-Path -Path $TenantConfig.IconFolder -ChildPath $Package.IconFile
        if (Test-Path -Path $IconPath) {
            $Icon = New-IntuneWin32AppIcon -FilePath $IconPath
            $SetParams['Icon'] = $Icon
            Write-Log "Loaded icon: $($Package.IconFile)"
        }
    }

    $categories = if ($Package.Categories) { $Package.Categories }
                  elseif ($Config.Script.IntunePackager.Categories) { $Config.Script.IntunePackager.Categories }
                  else { $null }
    if ($categories) {
        $SetParams['CategoryName'] = @($categories)
    }

    if ($Package.AdditionalRequirementRules -and @($Package.AdditionalRequirementRules).Count -gt 0) {
        $addReqs = New-RequirementRuleFromConfig -RequirementRules $Package.AdditionalRequirementRules
        if ($addReqs) { $SetParams['AdditionalRequirementRule'] = $addReqs }
    }

    if ($Package.CustomReturnCodes -and @($Package.CustomReturnCodes).Count -gt 0) {
        $retCodes = New-ReturnCodeFromConfig -ReturnCodes $Package.CustomReturnCodes
        if ($retCodes) { $SetParams['ReturnCode'] = $retCodes }
    }

    # Redact binary/script content before logging
    $LogParams = $SetParams.Clone()
    if ($LogParams['Icon']) { $LogParams['Icon'] = '<redacted - binary data>' }
    if ($LogParams['DetectionRule']) { $LogParams['DetectionRule'] = '<detection rule object>' }

    if ($Validate) {
        Write-Log "[VALIDATE] Would call Set-IntuneWin32App with:"
        foreach ($key in ($LogParams.Keys | Sort-Object)) {
            Write-Log "  $key = $($LogParams[$key])"
        }
        if ($UpdateMode -eq 'UpdateContent') {
            Write-Log "[VALIDATE] Would also call Update-IntuneWin32AppPackageFile with: $IntuneWinPath"
        }
        return $null
    }

    Invoke-TokenRefreshIfNeeded -TenantID $TenantID -ClientID $ClientID

    Write-Log "Updating Intune app: $AppName (ID: $ExistingAppID)"
    foreach ($key in ($LogParams.Keys | Sort-Object)) {
        Write-Log "  $key = $($LogParams[$key])" -NoConsole
    }

    try {
        $UpdatedApp = Set-IntuneWin32App @SetParams

        if ($UpdateMode -eq 'UpdateContent' -and $IntuneWinPath) {
            Write-Log "Uploading updated package file for: $AppName"
            Invoke-TokenRefreshIfNeeded -TenantID $TenantID -ClientID $ClientID
            Update-IntuneWin32AppPackageFile -ID $ExistingAppID -FilePath $IntuneWinPath
            Write-Log "Package file updated for: $AppName"
        }

        Write-Log "App updated successfully: $AppName (ID: $ExistingAppID)"
        return $UpdatedApp
    }
    catch {
        Write-Log "App update failed for: $AppName - $_" -Type 3
        return $null
    }
}
