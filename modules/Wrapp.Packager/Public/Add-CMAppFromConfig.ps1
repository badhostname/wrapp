function Add-CMAppFromConfig {
    <#
    .SYNOPSIS
        Creates an SCCM application with deployment type from Config.json.

    .DESCRIPTION
        Creates a new SCCM application using New-CMApplication with metadata
        from Config.json. Adds a script-based deployment type with the
        injected detection script. Optionally moves the app to a folder,
        adds install behaviors for running processes, and adds dependencies.

    .PARAMETER Package
        Package object from Config.Script.SCCMPackager.Packages.

    .PARAMETER App
        The Config.App object.

    .PARAMETER Config
        The full Config object (for detection script building).

    .PARAMETER SCCMSiteConfig
        The SCCMSite configuration for the detected site code.

    .PARAMETER DomainConfig
        The Domain configuration for the current domain.

    .PARAMETER DetectScript
        The prepared detection script content string.

    .PARAMETER Creator
        The creator/operator display name.

    .PARAMETER Validate
        If specified, logs actions without making changes.

    .OUTPUTS
        The created SCCM Application object, or a validate-mode placeholder.
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
        $SCCMSiteConfig,

        [Parameter(Mandatory = $true)]
        $DomainConfig,

        [Parameter(Mandatory = $true)]
        [string]$DetectScript,

        [string]$Creator = $env:USERNAME,

        [switch]$Validate
    )

    $TodayDate = Get-Date -UFormat '%Y-%m-%d'
    $AppName = $Package.AppName

    # Build the SCCM application arguments (use Package config with fallbacks)
    $SCCMAppArgs = @{
        Name                     = $AppName
        Description              = Get-ConfigValue $Package 'Description'    "Created $TodayDate by $Creator"
        Owner                    = Get-ConfigValue $Package 'Owner'          $env:USERNAME
        Publisher                = Get-ConfigValue $Package 'Publisher'      $App.Company
        ReleaseDate              = Get-ConfigValue $Package 'ReleaseDate'    $TodayDate
        SoftwareVersion          = Get-ConfigValue $Package 'SoftwareVersion' $App.DotVersion
        SupportContact           = Get-ConfigValue $Package 'SupportContact' $env:USERNAME
        LocalizedApplicationName = Get-ConfigValue $Package 'LocalizedName'  $AppName
    }

    # Conditionally add optional New-CMApplication fields
    if ($Package.LocalizedDescription) { $SCCMAppArgs['LocalizedDescription'] = $Package.LocalizedDescription }
    if ($Package.Keywords) { $SCCMAppArgs['Keyword'] = $Package.Keywords -split ',' | ForEach-Object { $_.Trim() } }
    if ($Package.IsFeatured) { $SCCMAppArgs['IsFeatured'] = $true }
    if ($Package.AutoInstall) { $SCCMAppArgs['AutoInstall'] = $true }
    if ($Package.PrivacyUrl) { $SCCMAppArgs['PrivacyUrl'] = $Package.PrivacyUrl }
    if ($Package.UserDocumentation) { $SCCMAppArgs['OptionalReference'] = $Package.UserDocumentation }
    if ($Package.LinkText) { $SCCMAppArgs['LinkText'] = $Package.LinkText }

    # Add icon if available -- construct path from content location + package icon
    if ($Package.Icon -and $DomainConfig.isDistPath -and $DomainConfig.AppFolder) {
        # Phase 11 hardening (S-7): Package.Icon is user-supplied config and
        # is concatenated into a UNC join below. Without a guard a value
        # like '..\..\windows\system32\shell32.dll' would resolve outside
        # the bundle's content folder and feed an arbitrary file to the
        # SCCM cmdlet. Test-SafeRelativePath rejects traversal segments,
        # drive-letter roots, and UNC prefixes.
        if (-not (Test-SafeRelativePath $Package.Icon)) {
            Write-Log "Package.Icon '$($Package.Icon)' rejected (path traversal or rooted). Skipping icon." -Type 2
            $IconPath = $null
        }
        else {
            $IconPath = Join-Path -Path $DomainConfig.isDistPath -ChildPath "$($DomainConfig.AppFolder)\$($App.Version)\$($Package.Icon)"
        }
        if ($IconPath -and (Test-Path -Path "FileSystem::$IconPath")) {
            # SCCM's New-CMApplication rejects icons larger than its
            # (undocumented) cap with an opaque "Validation of input
            # parameters failed". Observed reject: 1425x1425. Wrapp's GUI
            # downscales at save time (Phase 11 / 0.6.0.0223), but legacy
            # bundles on disk may still carry oversized icons -- so the
            # packager reads PNG header dimensions here and skips the arg
            # with a warning rather than letting the cmdlet hard-fail.
            $iconDims = Get-PngDimensions -Path $IconPath
            if ($null -ne $iconDims -and ($iconDims.Width -gt 512 -or $iconDims.Height -gt 512)) {
                Write-Log ("Icon at {0} is {1}x{2}, which exceeds the SCCM 512x512 cap -- " +
                           "skipping IconLocationFile to avoid cmdlet rejection. Re-save the bundle in " +
                           "Wrapp to regenerate the icon at the safe resolution." -f
                           $IconPath, $iconDims.Width, $iconDims.Height) -Type 2
            }
            else {
                $SCCMAppArgs['IconLocationFile'] = $IconPath
                Write-Log "Icon resolved: $IconPath"
            }
        }
        elseif ($IconPath) {
            # $IconPath is null when Test-SafeRelativePath rejected it above;
            # the rejection was already logged. This branch only fires when
            # the path was well-formed but no file exists at the location.
            Write-Log "Icon file not found at content location: $IconPath" -Type 2
        }
    }

    Write-Log "Creating SCCM application: $AppName"
    Write-Log "  Publisher: $($SCCMAppArgs.Publisher)"
    Write-Log "  Version: $($SCCMAppArgs.SoftwareVersion)"

    if ($Validate) {
        Write-Log "[VALIDATE] Would create SCCM application '$AppName' with the following args:"
        foreach ($key in $SCCMAppArgs.Keys) {
            Write-Log "[VALIDATE]   $key = $($SCCMAppArgs[$key])"
        }

        # Build deployment type args for logging
        $ContentLocation = "$($DomainConfig.isDistPath)\$($DomainConfig.AppFolder)\$($App.Version)"
        Write-Log "[VALIDATE] Would create deployment type '$($Package.Name)'"
        Write-Log "[VALIDATE]   ContentLocation: $ContentLocation"
        Write-Log "[VALIDATE]   InstallCommand: $($Package.InstallCommand)"
        Write-Log "[VALIDATE]   DetectScript: $($DetectScript.Length) characters"

        return [PSCustomObject]@{
            LocalizedDisplayName = $AppName
            SoftwareVersion      = $SCCMAppArgs.SoftwareVersion
            Manufacturer         = $SCCMAppArgs.Publisher
        }
    }

    try {
        # Create the SCCM Application
        $null = New-CMApplication @SCCMAppArgs

        # Move to AppFolder if specified
        if ($SCCMSiteConfig.AppFolder) {
            Write-Log "Moving '$AppName' to folder: $($SCCMSiteConfig.AppFolder)"
            try {
                Get-CMApplication -Name $AppName | Move-CMObject -FolderPath $SCCMSiteConfig.AppFolder
            }
            catch {
                Write-Log "Failed to move '$AppName' to folder (non-fatal): $_" -Type 2
            }
        }

        # Create the Deployment Type
        $ContentLocation = "$($DomainConfig.isDistPath)\$($DomainConfig.AppFolder)\$($App.Version)"

        $DeploymentTypeArgs = @{
            ApplicationName           = $AppName
            DeploymentTypeName        = $Package.Name
            Comment                   = "$($Package.Comment)Created $TodayDate by $Creator"
            InstallCommand            = $Package.InstallCommand
            ContentLocation           = $ContentLocation
            InstallationBehaviorType  = $Package.InstallationBehaviorType
            LogonRequirementType      = $Package.LogonRequirementType
            UninstallCommand          = $Package.UninstallCommand
            UserInteractionMode       = Get-ConfigValue $Package 'UserInteractionMode' $script:ModuleDefaults.DefaultSCCMPackageProperties.UserInteractionMode
            ScriptLanguage            = 'Powershell'
            ScriptText                = $DetectScript
        }

        # Add optional deployment type fields from config
        if ($Package.RepairCommand) { $DeploymentTypeArgs['RepairProgram'] = $Package.RepairCommand }
        if ($Package.RebootBehavior) { $DeploymentTypeArgs['RebootBehavior'] = $Package.RebootBehavior }
        if ($Package.EstimatedRuntimeMins) { $DeploymentTypeArgs['EstimatedRuntimeMins'] = $Package.EstimatedRuntimeMins }
        if ($Package.MaximumAllowedRuntimeMins) { $DeploymentTypeArgs['MaximumRuntimeMins'] = $Package.MaximumAllowedRuntimeMins }
        if ($Package.SlowNetworkDeploymentMode -and $Package.SlowNetworkDeploymentMode -ne 'DoNothing') {
            $DeploymentTypeArgs['SlowNetworkDeploymentMode'] = $Package.SlowNetworkDeploymentMode
        }
        if ($Package.ContentFallback) { $DeploymentTypeArgs['ContentFallback'] = $true }

        Write-Log "Creating deployment type: $($Package.Name)"
        $null = Add-CMScriptDeploymentType @DeploymentTypeArgs

        # Get a handle to the new deployment type for install behaviors
        $DeploymentType = Get-CMDeploymentType -ApplicationName $AppName -DeploymentTypeName $Package.Name

        # Add Install Behaviors (processes to close before install)
        # Prefer per-package InstallBehaviors, fall back to App.DetectRunning
        $IBList = if ($Package.InstallBehaviors -and @($Package.InstallBehaviors).Count -gt 0) {
            @($Package.InstallBehaviors)
        } elseif ($App.DetectRunning) {
            @($App.DetectRunning)
        } else { @() }

        if ($Package.InstallBehavior -and $IBList.Count -gt 0) {
            Write-Log "Adding install behaviors..."
            foreach ($IB in $IBList) {
                Write-Log "  Install behavior: $($IB.DisplayName) ($($IB.ExeFileName))"
                try {
                    $null = Add-CMDeploymentTypeInstallBehavior -InputObject $DeploymentType -ExeFileName $IB.ExeFileName -DisplayName $IB.DisplayName
                }
                catch {
                    Write-Log "Failed to add install behavior for '$($IB.DisplayName)': $_" -Type 2
                }
            }
        }

        # Add Dependencies (prefer per-package, fall back to App-level)
        $DepList = if ($Package.Dependencies -and @($Package.Dependencies).Count -gt 0) {
            @($Package.Dependencies)
        } elseif ($App.Dependencies -and @($App.Dependencies).Count -gt 0) {
            @($App.Dependencies)
        } else { @() }

        if ($DepList.Count -gt 0) {
            Write-Log "Adding dependencies..."
            $DepResult = Set-CMAppDependencyFromConfig -DeploymentType $DeploymentType -Dependencies $DepList

            foreach ($e in $DepResult.Errors) {
                Write-Log "Dependency error: $e" -Type 2
            }
        }

        # Return the created application object
        $CreatedApp = Get-CMApplication -Name $AppName
        Write-Log "SCCM application '$AppName' created successfully."
        return $CreatedApp
    }
    catch {
        $err = $_
        Write-Log "Failed to create SCCM application '$AppName': $($err.Exception.Message)" -Type 3

        # ConfigMgr cmdlets often wrap the specific validation failure inside
        # a generic "Validation of input parameters failed" outer exception.
        # Walk the chain so the actual reject reason (e.g. "IconLocationFile
        # is not accessible", "Parameter X is null") lands in the log.
        $inner = $err.Exception.InnerException
        while ($null -ne $inner) {
            Write-Log "  Inner: $($inner.GetType().FullName): $($inner.Message)" -Type 3
            $inner = $inner.InnerException
        }

        # ErrorRecord metadata pinpoints which cmdlet binding rule rejected.
        if ($err.CategoryInfo) {
            Write-Log "  Category: $($err.CategoryInfo.Category) / Reason: $($err.CategoryInfo.Reason) / Target: $($err.CategoryInfo.TargetName)" -Type 3
        }
        if ($err.FullyQualifiedErrorId) {
            Write-Log "  ErrorId: $($err.FullyQualifiedErrorId)" -Type 3
        }

        # Dump every arg we passed to New-CMApplication so the operator can
        # spot the offender (empty string, oversized field, unreachable UNC,
        # bad URL). Long values truncated; nulls and empties surfaced
        # explicitly because those are the most common rejection causes.
        Write-Log "  New-CMApplication args:" -Type 3
        foreach ($key in ($SCCMAppArgs.Keys | Sort-Object)) {
            $val = $SCCMAppArgs[$key]
            $valStr = if ($null -eq $val) { '<null>' }
                      elseif ($val -is [string] -and $val.Length -eq 0) { '<empty string>' }
                      elseif ($val -is [string] -and $val.Length -gt 200) {
                          "$($val.Substring(0, 200))... [+$($val.Length - 200) chars]"
                      }
                      else { "$val" }
            Write-Log "    $key = $valStr" -Type 3
        }

        throw
    }
}
