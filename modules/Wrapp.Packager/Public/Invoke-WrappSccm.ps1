function Invoke-WrappSccm {
    <#
    .SYNOPSIS
        Main orchestrator for SCCMPackager. Packages Win32 apps for SCCM.

    .DESCRIPTION
        Coordinates all SCCM packaging operations: config loading, validation,
        ConfigurationManager module import, site detection, preflight checks,
        collision detection, app creation, content distribution, and deployment.
        Returns a structured result object.

    .PARAMETER ConfigPath
        Path to Config.json.

    .PARAMETER Validate
        Run in validation mode - no changes made to SCCM.

    .PARAMETER SiteCode
        Override automatic site detection with an explicit SCCM site code.

    .PARAMETER PackageNames
        Optional list of package AppNames to process. When specified, only
        packages whose AppName matches an entry in this list are processed.
        All other packages are skipped. If omitted, all packages are processed.

    .PARAMETER LogPath
        Override the default log file path.

    .OUTPUTS
        [PSCustomObject] with:
            Success      [bool]
            DeployedApps [hashtable] AppName -> SCCM Application object
            Collisions   [array]
            LogFile      [string] Path to the log file
            Errors       [array] Collection of non-fatal errors
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ConfigPath,

        [switch]$Validate,

        [string]$SiteCode,

        [string[]]$PackageNames,

        [string]$LogPath
    )

    $ErrorActionPreference = 'Stop'

    # CLI/UI parity: layer the user's shared defaults over Defaults.psd1
    # (see the identical block in Invoke-WrappIntune for rationale).
    $BaseDefaults = if ($script:DefaultsPath -and (Test-Path -Path $script:DefaultsPath)) {
        Import-PowerShellDataFile -Path $script:DefaultsPath
    } else { $script:ModuleDefaults }
    $script:ModuleDefaults = Merge-WrappUserDefaults -Base $BaseDefaults -User (Get-WrappUserDefaults)

    $Result = [PSCustomObject]@{
        Success      = $false
        DeployedApps = @{}
        Collisions   = @()
        LogFile      = ''
        Errors       = [System.Collections.Generic.List[string]]::new()
    }

    try {
        # ==================================================================
        # Phase 1: Load Config.json
        # ==================================================================
        if (-not (Test-Path -Path $ConfigPath)) {
            throw "Config.json not found at: $ConfigPath"
        }

        $Config = Get-Content -Raw -Path $ConfigPath | ConvertFrom-Json
        $ScriptType = 'SCCMPackager'
        $App = $Config.App
        $ScriptSection = $Config.Script.$ScriptType
        $ScriptPath = Split-Path -Path $ConfigPath -Parent

        # ==================================================================
        # Phase 2: Validate config
        # ==================================================================
        $Validation = Test-WrappConfig -Config $Config -ScriptType $ScriptType
        if (-not $Validation.IsValid) {
            $errorMsg = "Config validation failed:`n  " + ($Validation.Errors -join "`n  ")
            throw $errorMsg
        }

        # ==================================================================
        # Phase 3: Initialize logging
        # ==================================================================
        $Tag = $ScriptSection.Tag
        if (-not $LogPath) {
            $LogPath = Join-Path -Path $env:LOCALAPPDATA -ChildPath "Wrapp\Logs\$Tag.log"
        }
        Initialize-LogFile -LogPath $LogPath -MaxSizeKB $script:ModuleDefaults.LogFileMaxSizeKB
        $Result.LogFile = $LogPath

        if ($Validate) {
            Write-Log "=== VALIDATION MODE - no changes will be made ===" -Type 2
        }

        # Log validation warnings
        foreach ($w in $Validation.Warnings) {
            Write-Log "Config warning: $w" -Type 2
        }

        Write-Log "SCCMPackager v$($script:ModuleDefaults.ScriptVersion) started."

        # ==================================================================
        # Phase 4: Schema migration (GUID auto-gen)
        # ==================================================================
        $Config = Update-ConfigSchema -Config $Config -ConfigPath $ConfigPath
        $App = $Config.App
        $ScriptSection = $Config.Script.$ScriptType

        # ==================================================================
        # Phase 5: Resolve domain
        # ==================================================================
        $CurrDomain = $env:USERDNSDOMAIN
        if (-not $CurrDomain) {
            throw "Could not determine current domain. Ensure this machine is domain-joined."
        }

        $DomainConfig = $Config.Domain.$CurrDomain
        if (-not $DomainConfig) {
            throw "No Domain configuration found for '$CurrDomain' in Config.json."
        }

        Write-Log "Domain: $CurrDomain"

        # Store domain log path for end-of-run copy (always log locally during the run)
        $script:DomainLogPath = $null
        if ($DomainConfig.TagFolder -and -not $PSBoundParameters.ContainsKey('LogPath')) {
            # Phase 11 hardening (S-1): TagFolder is user-supplied config and
            # is Join-Path'd against $Tag (also user-supplied). Without a
            # guard a malformed value could redirect the log copy to an
            # unintended share or escape into a sibling path via '..'.
            # Test-SafeUncPath enforces UNC root + no traversal.
            if (-not (Test-SafeUncPath $DomainConfig.TagFolder)) {
                Write-Log "Domain.${CurrDomain}.TagFolder '$($DomainConfig.TagFolder)' rejected (path traversal or non-UNC). Logs will not be copied to domain share." -Type 2
            }
            else {
                $script:DomainLogPath = Join-Path -Path $DomainConfig.TagFolder -ChildPath "$Tag.log"
                Write-Log "Domain log copy target: $($script:DomainLogPath)"
            }
        }

        # ==================================================================
        # Phase 6: Unblock files
        # ==================================================================
        $PackagePath = (Get-Item "FileSystem::$ScriptPath").Parent.FullName
        Write-Log "Package folder: $PackagePath"

        $blockedFiles = Get-ChildItem -Path $PackagePath -Recurse |
            Get-Item -Stream "Zone.Identifier" -ErrorAction SilentlyContinue
        if ($blockedFiles) {
            foreach ($blocked in $blockedFiles) {
                Unblock-File -Path $blocked.FileName
                Write-Log "Unblocked: $($blocked.FileName)"
            }
        }
        else {
            Write-Log "No blocked files found."
        }

        # ==================================================================
        # Phase 7: Resolve creator name
        # ==================================================================
        $Creator = Resolve-CreatorName
        Write-Log "Creator: $Creator"

        # ==================================================================
        # Phase 8: Authenticate (Import ConfigurationManager + detect site)
        # ==================================================================
        $SiteInfo = Connect-WrappSccm -Validate:$Validate
        $SiteDrive = $SiteInfo.SiteDrive
        $DetectedSiteCode = $SiteInfo.SiteCode

        # Override site code if parameter was provided
        if ($SiteCode) {
            Write-Log "Site code override: $SiteCode (detected: $DetectedSiteCode)"
        }
        else {
            $SiteCode = $DetectedSiteCode
        }

        # Resolve the SCCMSite config for the detected site
        $SCCMSiteConfig = $Config.SCCMSite.$SiteCode
        if (-not $SCCMSiteConfig -and -not $Validate) {
            throw "No SCCMSite configuration found for site code '$SiteCode' in Config.json."
        }
        elseif ($Validate -and -not $SCCMSiteConfig) {
            # In validate mode, use the first available site config
            $firstSite = $Config.SCCMSite.PSObject.Properties | Where-Object { $_.Name -ne 'Comment' } | Select-Object -First 1
            if ($firstSite) {
                $SCCMSiteConfig = $firstSite.Value
                Write-Log "[VALIDATE] No live site detected. Using first SCCMSite config: $($firstSite.Name)"
            }
            else {
                throw "No SCCMSite configurations available in Config.json."
            }
        }

        Write-Log "SCCM Site: $SiteCode"
        Write-Log "  AppFolder: $($SCCMSiteConfig.AppFolder)"
        Write-Log "  DeploymentGroups: $($SCCMSiteConfig.DeploymentGroups -join ', ')"

        # ==================================================================
        # Phase 8.5: Post-auth preflight validation
        # ==================================================================
        Write-Log "Running SCCM preflight validation..."
        $PreflightParams = @{
            SCCMSiteConfig = $SCCMSiteConfig
            SiteDrive      = $SiteDrive
            Packages       = @($ScriptSection.Packages)
            App            = $App
            Validate       = $Validate
        }
        $PreflightResult = Test-WrappSccmPreflight @PreflightParams

        foreach ($pw in $PreflightResult.Warnings) {
            Write-Log "Preflight warning: $pw" -Type 2
        }

        if (-not $PreflightResult.IsValid) {
            $preflightMsg = "SCCM preflight failed:`n  " + ($PreflightResult.Errors -join "`n  ")
            throw $preflightMsg
        }
        Write-Log "SCCM preflight passed."

        # ==================================================================
        # Phase 9: Narrow to this run's packages, then check collisions
        # ==================================================================
        # Narrow FIRST (same ordering rationale as Invoke-WrappIntune): the
        # collision check queries ConfigMgr once per package, so disabled,
        # name-filtered, and other-site packages must be excluded BEFORE the
        # check, not after -- previously every config package was queried and
        # the results of the excluded ones were thrown away.
        $PackagesToProcess = @($ScriptSection.Packages)

        # Persistent operator intent (GUI enable checkbox); absent = enabled.
        $EnabledOnly = @($PackagesToProcess | Where-Object { (Get-ConfigValue $_ 'IsEnabled' $true) -ne $false })
        if ($EnabledOnly.Count -lt $PackagesToProcess.Count) {
            Write-Log "IsEnabled filter: $($PackagesToProcess.Count) -> $($EnabledOnly.Count) package(s) (disabled packages skipped)."
            $PackagesToProcess = $EnabledOnly
        }

        # Filter by PackageNames if specified
        if ($PackageNames -and $PackageNames.Count -gt 0) {
            $Before = $PackagesToProcess.Count
            $PackagesToProcess = @($PackagesToProcess | Where-Object { $_.AppName -in $PackageNames })
            Write-Log "PackageNames filter: $Before -> $($PackagesToProcess.Count) package(s)."
        }

        # Filter by per-package TargetSites (if present on the package object)
        $SiteFiltered = @($PackagesToProcess | Where-Object {
            -not $_.TargetSites -or @($_.TargetSites).Count -eq 0 -or $SiteCode -in @($_.TargetSites)
        })
        if ($SiteFiltered.Count -lt $PackagesToProcess.Count) {
            Write-Log "TargetSites filter: $($PackagesToProcess.Count) -> $($SiteFiltered.Count) package(s) for site '$SiteCode'."
            $PackagesToProcess = $SiteFiltered
        }

        if ($PackagesToProcess.Count -eq 0) {
            Write-Log "No packages remain for site '$SiteCode' after narrowing." -Type 2
            $Result.Success = $true
            return $Result
        }

        Write-Log "Checking for app name collisions..."

        if (-not $Validate) {
            # Switch to SCCM PSDrive for CM cmdlets
            $OriginalLocation = Get-Location
            Set-Location -Path $SiteDrive
        }

        try {
            $CollisionResults = Test-CMAppCollisions -PackageList $PackagesToProcess
            $Result.Collisions = $CollisionResults.Collisions

            if (-not $Validate) {
                if ($CollisionResults.Collisions.Count -gt 0) {
                    foreach ($c in $CollisionResults.Collisions) {
                        Write-Log "Collision: $($c.ExistingAppName) (v$($c.Version))" -Type 2
                    }
                }

                if ($CollisionResults.AllBlocked) {
                    throw "All packages have name collisions. Nothing to package."
                }

                if ($CollisionResults.Valid.Count -eq 0) {
                    throw "No valid packages remaining after collision filtering."
                }

                Write-Log "Proceeding with $($CollisionResults.Valid.Count) package(s)."
                $PackagesToProcess = $CollisionResults.Valid
            }
            else {
                Write-Log "[VALIDATE] Collision results:"
                Write-Log "  Collisions: $($CollisionResults.Collisions.Count)"
                Write-Log "  Valid: $($CollisionResults.Valid.Count)"
                # $PackagesToProcess already holds the narrowed set for validation.
            }

            # ==================================================================
            # Phase 10: Per-package creation loop
            # ==================================================================
            foreach ($Package in $PackagesToProcess) {
                try {
                    # 10a. Prepare detection script
                    $DetParams = @{
                        DetectScriptPath = Join-Path -Path $ScriptPath -ChildPath 'DetectScript.ps1'
                        App              = $App
                        DetectConfig     = $Config.Script.Detect
                        PackageOption    = $Package.PackageOption
                    }
                    $PreparedDetectScript = New-CMDetectionFromConfig @DetParams

                    if (-not $PreparedDetectScript) {
                        Write-Log "Failed to prepare detection script for '$($Package.AppName)'" -Type 3
                        $Result.Errors.Add("Detection script preparation failed for '$($Package.AppName)'")
                        continue
                    }

                    # 10b. Create SCCM app + deployment type + install behaviors + dependencies
                    $CMAppParams = @{
                        Package        = $Package
                        App            = $App
                        Config         = $Config
                        SCCMSiteConfig = $SCCMSiteConfig
                        DomainConfig   = $DomainConfig
                        DetectScript   = $PreparedDetectScript
                        Creator        = $Creator
                        Validate       = $Validate
                    }
                    $AppResult = Add-CMAppFromConfig @CMAppParams

                    if ($AppResult) {
                        $Result.DeployedApps[$Package.AppName] = $AppResult
                    }

                    # 10c. Distribute content to Distribution Points
                    if ($SCCMSiteConfig.DeploymentGroups) {
                        $DistParams = @{
                            AppName                 = $Package.AppName
                            DistributionPointGroups = @($SCCMSiteConfig.DeploymentGroups)
                            Validate                = $Validate
                        }
                        $DistResult = Set-CMContentDistribution @DistParams

                        foreach ($e in $DistResult.Errors) {
                            $Result.Errors.Add($e)
                        }
                    }
                }
                catch {
                    Write-Log "Failed to process SCCM package '$($Package.AppName)': $_" -Type 3
                    $Result.Errors.Add("Package processing failed for '$($Package.AppName)': $_")
                    # Guard against the phase-12 unconditional Success=true:
                    # without this flag, a per-package throw was silently
                    # swallowed and the run still reported success.
                    $Result.Success = $false
                }
            }

            # ==================================================================
            # Phase 11: Create deployments to collections
            #
            # Canonical schema: Script.SCCMPackager.Packages[].Deployments[]
            # Legacy fallback:  SCCMSite.<SiteCode>.Deployments[] filtered by AppName
            #
            # The GUI's ConfigFileService migrates SCCMSite-scoped deployments
            # into per-package on every load, so any bundle saved by Wrapp
            # recently uses the new layout. Legacy bundles that haven't been
            # re-saved through the GUI still work via the fallback below.
            # ==================================================================
            $DeploymentList = [System.Collections.Generic.List[object]]::new()
            $LegacySeen = $false

            foreach ($Package in $PackagesToProcess) {
                $AppName = $Package.AppName

                # Prefer per-package deployments; fall back to site-level by AppName.
                $PkgDeployments = @()
                if ($Package.PSObject.Properties['Deployments'] -and $Package.Deployments) {
                    $PkgDeployments = @($Package.Deployments)
                }
                elseif ($SCCMSiteConfig.Deployments) {
                    $legacy = @($SCCMSiteConfig.Deployments | Where-Object { $_.AppName -eq $AppName })
                    if ($legacy.Count -gt 0) {
                        $PkgDeployments = $legacy
                        $LegacySeen = $true
                    }
                }

                foreach ($Deployment in $PkgDeployments) {
                    # Stamp AppName from the parent package when missing so
                    # Set-CMAppDeployment can identify the target application.
                    # Per-package deployments often omit AppName since the
                    # parent already encodes it; legacy site-scoped ones
                    # always carry it.
                    if (-not $Deployment.AppName) {
                        try {
                            Add-Member -InputObject $Deployment -NotePropertyName AppName `
                                       -NotePropertyValue $AppName -Force -ErrorAction Stop
                        }
                        # must-stay-silent: Add-Member can fail on hashtable
                        # inputs (vs PSCustomObject). The deployment is still
                        # appended -- Set-CMAppDeployment handles either shape.
                        catch { }
                    }
                    $DeploymentList.Add($Deployment)
                }
            }

            if ($LegacySeen) {
                Write-Log "Using legacy site-level deployments for at least one package. Re-save the bundle through the GUI to migrate to the per-package format." -Type 2
            }

            if ($DeploymentList.Count -gt 0) {
                Write-Log "Creating $($DeploymentList.Count) SCCM deployment(s)..."
                $DeployParams = @{
                    Deployments = $DeploymentList.ToArray()
                    Creator     = $Creator
                    Validate    = $Validate
                }
                $DeployResult = Set-CMAppDeployment @DeployParams

                foreach ($e in $DeployResult.Errors) {
                    $Result.Errors.Add($e)
                }
            }
            else {
                Write-Log "No deployments defined for any package."
            }
        }
        finally {
            if (-not $Validate -and $OriginalLocation) {
                Set-Location -Path $OriginalLocation
            }
            # Phase 11 hardening (S-2): dismount the SCCM PSDrive and unload
            # the ConfigurationManager module so a pooled runspace doesn't
            # inherit admin authority on the site for the next dispatch.
            # Both calls are best-effort -- a Validate-mode run never
            # connected, and rare cases where the module is shared with
            # another caller in the same runspace should not blow up here.
            if ($script:SccmSiteCode) {
                try { Remove-PSDrive -Name $script:SccmSiteCode -Force -ErrorAction SilentlyContinue } catch { }
                $script:SccmSiteCode = $null
            }
            try { Remove-Module -Name ConfigurationManager -Force -ErrorAction SilentlyContinue } catch { }
        }

        # ==================================================================
        # Phase 12: Success
        # ==================================================================
        # Success is derived from the accumulated errors, not set
        # unconditionally. Per-package catches Add to $Result.Errors and
        # flip Success to false earlier; deployment phase Errors also count.
        $Result.Success = ($Result.Errors.Count -eq 0)
        if ($Result.Success) {
            Write-Log "Wrapp.Packager (SCCM) completed successfully."
        }
        else {
            Write-Log "Wrapp.Packager (SCCM) completed with $($Result.Errors.Count) error(s) -- see entries above." -Type 3
        }
    }
    catch {
        Write-Log "Fatal error: $_" -Type 3
        $Result.Errors.Add("Fatal: $_")
    }

    # Copy local log to domain share (best-effort, non-blocking)
    if ($script:DomainLogPath -and $script:LogFile -and ($script:DomainLogPath -ne $script:LogFile)) {
        try {
            $domainDir = Split-Path -Path $script:DomainLogPath -Parent
            if (-not (Test-Path -Path $domainDir)) {
                New-Item -ItemType Directory -Path $domainDir -Force | Out-Null
            }
            Copy-Item -Path $script:LogFile -Destination $script:DomainLogPath -Force
            Write-Log "Log copied to domain share: $($script:DomainLogPath)"
        }
        catch {
            Write-Log "Failed to copy log to domain share ($($script:DomainLogPath)): $_" -Type 2
        }
    }

    return $Result
}
