function Invoke-WrappIntune {
    <#
    .SYNOPSIS
        Main orchestrator for IntunePackager. Packages Win32 apps for Intune.

    .DESCRIPTION
        Replaces the monolithic IntunePackager.ps1 script body. Coordinates all
        operations: config loading, validation, authentication, packaging,
        app creation, and assignment. Returns a structured result object.

    .PARAMETER ConfigPath
        Path to Config.json.

    .PARAMETER Validate
        Run in validation mode - no changes made to Intune.

    .PARAMETER TenantId
        Override automatic tenant detection with an explicit tenant ID.

    .PARAMETER PackageNames
        Optional list of package AppNames to process. When specified, only
        packages whose AppName matches an entry in this list are processed.
        All other packages are skipped. If omitted, all packages are processed.

    .PARAMETER SkipCollisionCheck
        Skips the per-package Intune collision query. Pass ONLY when the caller
        has just performed the same check itself (the Wrapp GUI runs
        Test-Win32AppCollisions as a pre-flight so the operator can resolve
        collisions before anything is wrapped; re-querying here would double
        every Graph lookup). CLI callers should omit this.

    .PARAMETER LogPath
        Override the default log file path.

    .OUTPUTS
        [PSCustomObject] with:
            Success      [bool]
            DeployedApps [hashtable] AppName -> App object
            Collisions   [array]
            LogFile      [string] Path to the log file
            Errors       [array] Collection of non-fatal errors
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ConfigPath,

        [switch]$Validate,

        [string]$TenantId,

        [string[]]$PackageNames,

        [switch]$SkipCollisionCheck,

        [string]$LogPath
    )

    $ErrorActionPreference = 'Stop'

    # CLI/UI parity: layer the user's shared defaults (GUI Settings >
    # Preferences, exported to %LOCALAPPDATA%\Wrapp\user-defaults.json) over
    # the shipped Defaults.psd1. Base is re-read from disk each run so the
    # merge is deterministic (removed user values fall back cleanly).
    # Precedence: bundle Config.json > user-defaults.json > Defaults.psd1.
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
        $ScriptType = $script:ModuleDefaults.ScriptType
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
        $DomainConfig = $null  # Resolved after tenant, but we need log path first

        # Determine log path: parameter > Config > default
        if (-not $LogPath) {
            $LogPath = Join-Path -Path $env:LOCALAPPDATA -ChildPath "Wrapp\Logs\$Tag.log"
        }
        Initialize-LogFile -LogPath $LogPath -MaxSizeKB $script:ModuleDefaults.LogFileMaxSizeKB
        $Result.LogFile = $LogPath

        if ($Validate) {
            Write-Log "=== VALIDATION MODE - no changes will be made ==="  -Type 2
        }

        # Log validation warnings
        foreach ($w in $Validation.Warnings) {
            Write-Log "Config warning: $w" -Type 2
        }

        # ==================================================================
        # Phase 4: Schema migration (GUID auto-gen)
        # ==================================================================
        $Config = Update-ConfigSchema -Config $Config -ConfigPath $ConfigPath
        $App = $Config.App
        $ScriptSection = $Config.Script.$ScriptType

        # ==================================================================
        # Phase 5: Resolve tenant and domain
        # ==================================================================
        $ResolvedTenantId = if ($TenantId) { $TenantId } else { Resolve-TenantId }
        if (-not $ResolvedTenantId) {
            throw "Could not determine tenant ID. Use -TenantId parameter or ensure machine is Azure AD joined."
        }

        $TenantConfig = $Config.IntuneTenant.$ResolvedTenantId
        if (-not $TenantConfig) {
            throw "No IntuneTenant configuration found for tenant '$ResolvedTenantId'."
        }

        $DomainBinding = $TenantConfig.Domain
        $DomainConfig = $Config.Domain.$DomainBinding

        Write-Log "Tenant: $ResolvedTenantId"
        Write-Log "Domain: $DomainBinding"

        # Store domain log path for end-of-run copy (always log locally during the run)
        $script:DomainLogPath = $null
        if ($DomainConfig.TagFolder -and -not $PSBoundParameters.ContainsKey('LogPath')) {
            $script:DomainLogPath = Join-Path -Path $DomainConfig.TagFolder -ChildPath "$Tag.log"
            Write-Log "Domain log copy target: $($script:DomainLogPath)"
        }

        # ==================================================================
        # Phase 6: Unblock files and import IntuneWin32App module
        # ==================================================================
        $PackagePath = (Get-Item "FileSystem::$ScriptPath").Parent.FullName
        Write-Log "Package folder: $PackagePath"

        # Unblock downloaded files
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

        # Import IntuneWin32App module
        # Try vendored path relative to package folder first, then fall back to PSModulePath
        $ModulesBasePath = Join-Path -Path $PackagePath -ChildPath 'Script\Modules'
        $IntuneWin32AppPath = Join-Path -Path $ModulesBasePath -ChildPath 'IntuneWin32App\1.5.0'
        $IntuneManifest = $null

        if (Test-Path -Path $IntuneWin32AppPath) {
            if ($env:PSModulePath -notlike "*$ModulesBasePath*") {
                $env:PSModulePath = "$ModulesBasePath;$env:PSModulePath"
            }
            $IntuneManifest = Get-ChildItem -Path $IntuneWin32AppPath -Filter '*.psd1' -ErrorAction SilentlyContinue |
                Select-Object -First 1
            if (-not $IntuneManifest) {
                $IntuneManifest = Get-ChildItem -Path $IntuneWin32AppPath -Filter '*.psm1' -ErrorAction SilentlyContinue |
                    Select-Object -First 1
            }
        }

        if ($IntuneManifest) {
            Import-Module -Name $IntuneManifest.FullName -Force -ErrorAction Stop
            Write-Log "Imported IntuneWin32App from: $($IntuneManifest.FullName)"
        }
        else {
            # Vendored path not found - resolve via PSModulePath (GUI sets this)
            Import-Module -Name 'IntuneWin32App' -Force -ErrorAction Stop
            $loadedModule = Get-Module -Name 'IntuneWin32App'
            Write-Log "Imported IntuneWin32App from PSModulePath: $($loadedModule.ModuleBase)"
        }

        # ==================================================================
        # Phase 7: Resolve creator name
        # ==================================================================
        $Creator = Resolve-CreatorName
        Write-Log "Creator: $Creator"

        # ==================================================================
        # Phase 8: Authenticate
        # ==================================================================
        $AuthParams = @{
            TenantID = $ResolvedTenantId
            AuthFlow = if ($TenantConfig.AuthFlow) { $TenantConfig.AuthFlow } else { 'Interactive' }
        }
        if ($TenantConfig.ClientID) { $AuthParams['ClientID'] = $TenantConfig.ClientID }
        # Config.json stores ClientSecret as a plaintext string on disk (CLI
        # convenience). Wrap it in a SecureString at the earliest opportunity
        # so the narrow plaintext lifetime in Connect-WrappIntune's unwrap
        # block is the only place a plaintext .NET string holds the secret.
        # GUI runs emit an empty ClientSecret (token is pre-injected), so this
        # branch is a no-op in that path and Connect-WrappIntune
        # short-circuits on the pre-injected globals.
        if ($TenantConfig.ClientSecret) {
            $AuthParams['ClientSecret'] = ConvertTo-SecureString `
                -String $TenantConfig.ClientSecret -AsPlainText -Force
        }
        if ($TenantConfig.CertThumbprint) { $AuthParams['CertThumbprint'] = $TenantConfig.CertThumbprint }

        Connect-WrappIntune @AuthParams

        # Determine effective ClientID for token refresh
        $EffectiveClientID = if ($TenantConfig.ClientID) {
            $TenantConfig.ClientID
        }
        else {
            $script:ModuleDefaults.WellKnownClientID
        }

        # ==================================================================
        # Phase 8.5: Post-auth preflight validation
        # ==================================================================
        Write-Log "Running post-auth preflight validation..."
        $PreflightParams = @{
            Config      = $Config
            TenantConfig = $TenantConfig
            Packages    = @($ScriptSection.Packages)
            Validate    = $Validate
        }
        $PreflightResult = Test-WrappIntunePreflight @PreflightParams

        foreach ($pw in $PreflightResult.Warnings) {
            Write-Log "Preflight warning: $pw" -Type 2
        }

        if (-not $PreflightResult.IsValid) {
            $preflightMsg = "Post-auth preflight failed:`n  " + ($PreflightResult.Errors -join "`n  ")
            throw $preflightMsg
        }
        Write-Log "Post-auth preflight passed."

        # ==================================================================
        # Phase 9: Narrow to THIS tenant's packages, then check collisions
        # ==================================================================
        # Narrow FIRST, so collision detection (and the "Proceeding with N"
        # count) only considers packages actually targeting the current tenant.
        # A multi-tenant run invokes this once per tenant with PackageNames
        # scoped to that tenant; checking all config packages here would look
        # for collisions in the wrong tenant (e.g. checking a package that
        # targets tenant A against tenant B) and mis-report the package count.
        $PackagesToProcess = $ScriptSection.Packages

        # Persistent operator intent (GUI enable checkbox); absent = enabled.
        # Filtered here as well as in Invoke-WrappPackaging so a direct CLI
        # call (`Invoke-WrappIntune -ConfigPath ...`) honors the flag too.
        $EnabledOnly = @($PackagesToProcess | Where-Object { (Get-ConfigValue $_ 'IsEnabled' $true) -ne $false })
        if ($EnabledOnly.Count -lt @($PackagesToProcess).Count) {
            Write-Log "IsEnabled filter: $(@($PackagesToProcess).Count) -> $($EnabledOnly.Count) package(s) (disabled packages skipped)."
            $PackagesToProcess = $EnabledOnly

            if ($PackagesToProcess.Count -eq 0) {
                Write-Log "Every package is disabled. Nothing to do." -Type 2
                $Result.Success = $true
                Write-WrappStep -Package '*' -Step 'RunComplete' -Kind 'Success' -TenantId $ResolvedTenantId
                return $Result
            }
        }

        # Filter by PackageNames if specified
        if ($PackageNames -and $PackageNames.Count -gt 0) {
            $Before = $PackagesToProcess.Count
            $PackagesToProcess = @($PackagesToProcess | Where-Object { $_.AppName -in $PackageNames })
            Write-Log "PackageNames filter: $Before -> $($PackagesToProcess.Count) package(s)."

            if ($PackagesToProcess.Count -eq 0) {
                Write-Log "No packages remain after PackageNames filter." -Type 2
                $Result.Success = $true
                Write-WrappStep -Package '*' -Step 'RunComplete' -Kind 'Success' -TenantId $ResolvedTenantId
                return $Result
            }
        }

        # Filter by per-package TargetTenants (if present on the package object)
        $TenantFiltered = @($PackagesToProcess | Where-Object {
            -not $_.TargetTenants -or @($_.TargetTenants).Count -eq 0 -or $ResolvedTenantId -in @($_.TargetTenants)
        })
        if ($TenantFiltered.Count -lt $PackagesToProcess.Count) {
            Write-Log "TargetTenants filter: $($PackagesToProcess.Count) -> $($TenantFiltered.Count) package(s) for tenant '$ResolvedTenantId'."
            $PackagesToProcess = $TenantFiltered

            if ($PackagesToProcess.Count -eq 0) {
                Write-Log "No packages target tenant '$ResolvedTenantId'." -Type 2
                $Result.Success = $true
                Write-WrappStep -Package '*' -Step 'RunComplete' -Kind 'Success' -TenantId $ResolvedTenantId
                return $Result
            }
        }

        # Collision detection -- only over the packages targeting this tenant.
        # The caller may have JUST run the identical check itself (the GUI's
        # pre-flight dialog); repeating it would double every Graph query for
        # zero new information, so honor the skip -- but still emit the
        # per-package Collision Success step events the UI progress depends on.
        if ($SkipCollisionCheck -and -not $Validate) {
            Write-Log "Collision check skipped (caller pre-checked via Test-Win32AppCollisions)."
            foreach ($p in $PackagesToProcess) {
                Write-WrappStep -Package $p.AppName -Step 'Collision' -Kind 'Success' -TenantId $ResolvedTenantId
            }
        }
        else {
        Invoke-TokenRefreshIfNeeded -TenantID $ResolvedTenantId -ClientID $EffectiveClientID
        Write-Log "Checking for app name collisions..."

        $CollisionParams = @{ PackageList = $PackagesToProcess }
        if ($ScriptSection.TerminateOnCollision) {
            $CollisionParams['TerminateOnCollision'] = $true
        }
        # The tenant-narrowed set BEFORE collision filtering, so we can emit a
        # per-package collision outcome (B') for every package that was checked.
        $PreCollision = @($PackagesToProcess)
        $CollisionResults = Test-Win32AppCollisions @CollisionParams
        $Result.Collisions = $CollisionResults.Collisions

        if (-not $Validate) {
            if (-not $CollisionResults) {
                throw "Collision detection failed or returned null."
            }

            if ($CollisionResults.Collisions.Count -gt 0) {
                foreach ($c in $CollisionResults.Collisions) {
                    Write-Log "Collision: $($c.ExistingAppName) (v$($c.Version)) by $($c.Publisher)" -Type 2
                }
            }

            # B': typed per-package collision outcome. Emitted BEFORE the
            # AllBlocked throw so the UI marks each collided package Failed even
            # when the whole tenant pass then aborts.
            $ValidNames = @($CollisionResults.Valid | ForEach-Object { $_.AppName })
            foreach ($p in $PreCollision) {
                if ($p.AppName -in $ValidNames) {
                    Write-WrappStep -Package $p.AppName -Step 'Collision' -Kind 'Success' -TenantId $ResolvedTenantId
                }
                else {
                    Write-WrappStep -Package $p.AppName -Step 'Collision' -Kind 'Fail' -TenantId $ResolvedTenantId -ErrorMessage 'Already exists in Intune'
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
        } # end of collision check (skipped when -SkipCollisionCheck)

        # ==================================================================
        # Phase 10: Create .intunewin package
        # ==================================================================
        # Source path: use DomainConfig paths (CLI) or fall back to PackagePath (GUI bundle)
        if ($DomainConfig -and $DomainConfig.isDistPath) {
            $DistSourcePath = Join-Path -Path (Join-Path -Path $DomainConfig.isDistPath -ChildPath $DomainConfig.AppFolder) -ChildPath $App.Version
            if (Test-Path -Path $DistSourcePath -ErrorAction SilentlyContinue) {
                $SourcePath = $DistSourcePath
            }
            else {
                $SourcePath = $PackagePath
                Write-Log "Domain source path not reachable ($DistSourcePath) - using bundle folder: $SourcePath" -Type 2
            }
        }
        else {
            $SourcePath = $PackagePath
            Write-Log "No Domain config - using bundle folder as source: $SourcePath"
        }
        $SetupFile = 'Script\InstallScript.ps1'
        $PackageName = "$($App.Name -replace ' ', '_')_$($App.Version).intunewin"
        # Output path: use TenantConfig.IntuneWinPath (CLI) or fall back to PackagePath\Output
        if ($TenantConfig.IntuneWinPath) {
            $IntuneWinOutputPath = Join-Path -Path $TenantConfig.IntuneWinPath -ChildPath "$($App.Company)\$($App.Name)\$($App.Version)"
            # Test parent path reachability (the full path may not exist yet)
            $IntuneWinRoot = Split-Path -Path $TenantConfig.IntuneWinPath -Qualifier -ErrorAction SilentlyContinue
            if (-not $IntuneWinRoot) { $IntuneWinRoot = $TenantConfig.IntuneWinPath }
            if (Test-Path -Path $IntuneWinRoot -ErrorAction SilentlyContinue) {
                $OutputPath = $IntuneWinOutputPath
            }
            else {
                $AppNameFolder = (Get-Item "FileSystem::$PackagePath").Parent.FullName
                $OutputPath = Join-Path -Path $AppNameFolder -ChildPath 'Output'
                Write-Log "IntuneWinPath not reachable ($($TenantConfig.IntuneWinPath)) - using local output: $OutputPath" -Type 2
            }
        }
        else {
            # Output to app name folder (parent of version folder) to avoid
            # including previous .intunewin files in the next wrap cycle.
            $AppNameFolder = (Get-Item "FileSystem::$PackagePath").Parent.FullName
            $OutputPath = Join-Path -Path $AppNameFolder -ChildPath 'Output'
            Write-Log "No IntuneWinPath configured - using: $OutputPath"
        }

        # Clean any existing .intunewin before wrapping to avoid stale content.
        # Wrapping happens once per run; all packages share the same .intunewin.
        $ExistingIntuneWin = Join-Path -Path $OutputPath -ChildPath $PackageName
        if (Test-Path -Path $ExistingIntuneWin) {
            Remove-Item -Path $ExistingIntuneWin -Force
            Write-Log "Removed existing package: $ExistingIntuneWin"
        }

        $WrapParams = @{
            SourcePath  = $SourcePath
            SetupFile   = $SetupFile
            OutputPath  = $OutputPath
            PackageName = $PackageName
            Validate    = $Validate
        }
        # B': wrapping is one shared operation for every package in this tenant
        # pass, so its step events broadcast (Package = '*'). New-IntuneWin32Package
        # interleaves Progress events with its final result object on the output
        # stream -- re-emit the events upward (live) and capture the result.
        Write-WrappStep -Package '*' -Step 'Wrapping' -Kind 'Start' -TenantId $ResolvedTenantId
        $PackageResult = $null
        New-IntuneWin32Package @WrapParams | ForEach-Object {
            if ($_ -and $_.PSObject.Properties['_Type'] -and $_._Type -eq 'WrappStep') { $_ }
            else { $PackageResult = $_ }
        }
        # In -Validate mode New-IntuneWin32Package intentionally produces no
        # .intunewin (it only logs what it would do), so a null result there is
        # EXPECTED, not a wrapping failure. Use a sentinel path so the run
        # continues into the per-package validate branches (which also no-op)
        # instead of tripping the "package creation failed" fatal below.
        $IntuneWinFilePath = if ($PackageResult) { $PackageResult.Path }
                             elseif ($Validate)  { '(validate-mode: no package produced)' }
                             else                { $null }

        if (-not $IntuneWinFilePath) {
            Write-Log "Package creation failed - no .intunewin file produced. Check IntuneWinAppUtil output above for details." -Type 3
            Write-Log "  SourcePath: $SourcePath" -Type 3
            Write-Log "  OutputPath: $OutputPath" -Type 3
            $Result.Success = $false
            # Wrapping produced nothing -- fail every package in this tenant pass.
            Write-WrappStep -Package '*' -Step 'Fatal' -Kind 'Fail' -TenantId $ResolvedTenantId -ErrorMessage 'Package creation failed - no .intunewin produced'
            Write-WrappStep -Package '*' -Step 'RunComplete' -Kind 'Fail' -TenantId $ResolvedTenantId
            return $Result
        }

        if (-not $Validate) {
            Write-WrappStep -Package '*' -Step 'Wrapping' -Kind 'Success' -TenantId $ResolvedTenantId -Detail $PackageResult.SizeFormatted
        }

        # ==================================================================
        # Phase 11: Per-package app creation/update (UpdateMode routing)
        # ==================================================================
        foreach ($Package in $PackagesToProcess) {
            $updateMode = Get-ConfigValue $Package 'UpdateMode' 'Create'
            Write-WrappStep -Package $Package.AppName -Step 'AppCreation' -Kind 'Start' -TenantId $ResolvedTenantId

            try {
                switch ($updateMode) {
                    'Create' {
                        $CreateParams = @{
                            Package      = $Package
                            App          = $App
                            Config       = $Config
                            TenantConfig = $TenantConfig
                            IntuneWinPath = $IntuneWinFilePath
                            ScriptPath   = $ScriptPath
                            Creator      = $Creator
                            TenantID     = $ResolvedTenantId
                            ClientID     = $EffectiveClientID
                            Validate     = $Validate
                        }
                        # B': Add-IntuneWin32AppFromConfig interleaves upload
                        # sub-step Progress events with its final app object.
                        # Re-emit the events upward (live) and capture the app.
                        $AppResult = $null
                        Add-IntuneWin32AppFromConfig @CreateParams | ForEach-Object {
                            if ($_ -and $_.PSObject.Properties['_Type'] -and $_._Type -eq 'WrappStep') { $_ }
                            else { $AppResult = $_ }
                        }

                        if ($AppResult -and $AppResult.Id) {
                            $Result.DeployedApps[$Package.AppName] = $AppResult

                            # Emit encryption keys + package metadata for the GUI key store
                            try {
                                $MetaData = Get-IntuneWin32AppMetaData -FilePath $IntuneWinFilePath
                                if ($MetaData -and $MetaData.ApplicationInfo.EncryptionInfo) {
                                    $EncInfo = $MetaData.ApplicationInfo.EncryptionInfo
                                    $AppInfo = $MetaData.ApplicationInfo
                                    [PSCustomObject]@{
                                        _Type                  = 'EncryptionKeys'
                                        AppId                  = $AppResult.Id
                                        DisplayName            = $Package.AppName
                                        TenantId               = $ResolvedTenantId
                                        EncryptionKey          = $EncInfo.EncryptionKey
                                        InitializationVector   = $EncInfo.initializationVector
                                        MacKey                 = $EncInfo.macKey
                                        Mac                    = $EncInfo.mac
                                        ProfileIdentifier      = 'ProfileVersion1'
                                        FileDigest             = $EncInfo.fileDigest
                                        FileDigestAlgorithm    = $EncInfo.fileDigestAlgorithm
                                        PackageName            = $AppInfo.Name
                                        SetupFile              = $AppInfo.SetupFile
                                        InnerFileName          = $AppInfo.FileName
                                        UnencryptedContentSize = if ($AppInfo.UnencryptedContentSize) { [long]$AppInfo.UnencryptedContentSize } else { 0 }
                                    }
                                    Write-Log "Emitted encryption keys for '$($Package.AppName)' (ID: $($AppResult.Id))"
                                }
                            } catch {
                                Write-Log "Could not extract encryption keys: $_" -Type 2
                            }
                        }
                        else {
                            Write-Log "Skipping assignment for '$($Package.AppName)' - app was not deployed." -Type 2
                            $Result.Errors.Add("App creation failed for '$($Package.AppName)' - returned null from Graph API")
                        }
                    }
                    { $_ -in @('Update', 'UpdateContent') } {
                        $UpdateParams = @{
                            Package      = $Package
                            App          = $App
                            Config       = $Config
                            TenantConfig = $TenantConfig
                            IntuneWinPath = $IntuneWinFilePath
                            ScriptPath   = $ScriptPath
                            Creator      = $Creator
                            TenantID     = $ResolvedTenantId
                            ClientID     = $EffectiveClientID
                            Validate     = $Validate
                        }
                        $AppResult = Update-IntuneWin32AppFromConfig @UpdateParams

                        if ($AppResult) {
                            $Result.DeployedApps[$Package.AppName] = $AppResult
                        }
                        elseif ($Package.ExistingAppID) {
                            # For Update modes, store the ExistingAppID so assignments can still work
                            $Result.DeployedApps[$Package.AppName] = [PSCustomObject]@{
                                Id = $Package.ExistingAppID
                            }
                        }
                    }
                    default {
                        Write-Log "Unknown UpdateMode '$updateMode' for '$($Package.AppName)'" -Type 3
                        $Result.Errors.Add("Unknown UpdateMode '$updateMode' for '$($Package.AppName)'")
                    }
                }
            }
            catch {
                Write-Log "Failed to process app '$($Package.AppName)' (mode: $updateMode): $_" -Type 3
                $Result.Errors.Add("App processing failed for '$($Package.AppName)': $_")
            }

            # B': structured outcome for this package's app-creation step. Single
            # point covers every branch above (create/update/success/fail/throw).
            if ($Result.DeployedApps.ContainsKey($Package.AppName)) {
                $DeployedId = $Result.DeployedApps[$Package.AppName].Id
                Write-WrappStep -Package $Package.AppName -Step 'AppCreation' -Kind 'Success' -TenantId $ResolvedTenantId -Detail "$DeployedId"
            }
            else {
                Write-WrappStep -Package $Package.AppName -Step 'AppCreation' -Kind 'Fail' -TenantId $ResolvedTenantId -ErrorMessage 'App was not created in Intune'
            }
        }

        # ==================================================================
        # Phase 11.5: Dependencies and Supersedence
        # ==================================================================
        foreach ($Package in $PackagesToProcess) {
            $AppName = $Package.AppName
            $AppId = $null

            if ($Result.DeployedApps.ContainsKey($AppName)) {
                $AppId = $Result.DeployedApps[$AppName].Id
            }
            elseif ($Validate) {
                $AppId = '00000000-0000-0000-0000-000000000000'
            }
            else {
                continue
            }

            # Dependencies
            if ($Package.Dependencies -and @($Package.Dependencies).Count -gt 0) {
                Write-Log "Processing dependencies for '$AppName'..."
                Write-WrappStep -Package $AppName -Step 'Dependencies' -Kind 'Start' -TenantId $ResolvedTenantId
                if (-not $Validate) {
                    Invoke-TokenRefreshIfNeeded -TenantID $ResolvedTenantId -ClientID $EffectiveClientID
                }

                try {
                    $DepParams = @{
                        AppId        = $AppId
                        Dependencies = $Package.Dependencies
                        DeployedApps = $Result.DeployedApps
                        Validate     = $Validate
                    }
                    $DepResult = Set-Win32AppDependencyFromConfig @DepParams

                    foreach ($e in $DepResult.Errors) {
                        $Result.Errors.Add("Dependency error for '${AppName}': $e")
                    }

                    if (@($DepResult.Errors).Count -gt 0) {
                        Write-WrappStep -Package $AppName -Step 'Dependencies' -Kind 'Fail' -TenantId $ResolvedTenantId -ErrorMessage (@($DepResult.Errors) -join '; ')
                    }
                    else {
                        Write-WrappStep -Package $AppName -Step 'Dependencies' -Kind 'Success' -TenantId $ResolvedTenantId
                    }
                }
                catch {
                    Write-Log "Dependency processing failed for '$AppName': $_" -Type 3
                    $Result.Errors.Add("Dependency failed for '$AppName': $_")
                    Write-WrappStep -Package $AppName -Step 'Dependencies' -Kind 'Fail' -TenantId $ResolvedTenantId -ErrorMessage "$_"
                }
            }
            else {
                # No dependencies -- report the step Skipped so the progress row
                # reflects a complete, accounted-for step (not a stuck Pending).
                Write-WrappStep -Package $AppName -Step 'Dependencies' -Kind 'Skip' -TenantId $ResolvedTenantId
            }

            # Supersedence
            if ($Package.Supersedence -and @($Package.Supersedence).Count -gt 0) {
                Write-Log "Processing supersedence for '$AppName'..."
                if (-not $Validate) {
                    Invoke-TokenRefreshIfNeeded -TenantID $ResolvedTenantId -ClientID $EffectiveClientID
                }

                try {
                    $SupParams = @{
                        AppId        = $AppId
                        Supersedence = $Package.Supersedence
                        DeployedApps = $Result.DeployedApps
                        Validate     = $Validate
                    }
                    $SupResult = Set-Win32AppSupersedenceFromConfig @SupParams

                    foreach ($e in $SupResult.Errors) {
                        $Result.Errors.Add("Supersedence error for '${AppName}': $e")
                    }
                }
                catch {
                    Write-Log "Supersedence processing failed for '$AppName': $_" -Type 3
                    $Result.Errors.Add("Supersedence failed for '$AppName': $_")
                }
            }
        }

        # ==================================================================
        # Phase 12: Assignments
        #
        # Canonical schema: Script.IntunePackager.Packages[].Assignments[]
        # Legacy fallback:  IntuneTenant.<TenantId>.Assignments[] filtered by AppName
        #
        # The GUI always writes the new per-package format. Legacy bundles with
        # tenant-level assignments still work -- a warning steers users to
        # re-save through the GUI, which normalises to the new format.
        # ==================================================================
        $AnyAssignmentsSeen = $false

        foreach ($Package in $PackagesToProcess) {
            $AppName = $Package.AppName
            $AppId = $null

            if ($Result.DeployedApps.ContainsKey($AppName)) {
                $AppId = $Result.DeployedApps[$AppName].ID
            }
            elseif ($Validate) {
                $AppId = '00000000-0000-0000-0000-000000000000'
                Write-Log "[VALIDATE] Using dummy AppId for '$AppName'"
            }
            else {
                Write-Log "Skipping assignment for '$AppName' - app was not deployed." -Type 2
                continue
            }

            # Prefer per-package assignments; fall back to tenant-level by AppName.
            $PkgAssignments = @()
            if ($Package.PSObject.Properties['Assignments'] -and $Package.Assignments) {
                $PkgAssignments = @($Package.Assignments)
            }
            elseif ($TenantConfig.Assignments) {
                $legacy = @($TenantConfig.Assignments | Where-Object { $_.AppName -eq $AppName })
                if ($legacy.Count -gt 0) {
                    Write-Log "Using legacy tenant-level assignments for '$AppName'. Re-save the bundle through the GUI to migrate to the per-package format." -Type 2
                    $PkgAssignments = $legacy
                }
            }

            if ($PkgAssignments.Count -eq 0) {
                Write-Log "No assignments defined for '$AppName'."
                Write-WrappStep -Package $AppName -Step 'Assignment' -Kind 'Skip' -TenantId $ResolvedTenantId
                continue
            }
            $AnyAssignmentsSeen = $true
            Write-WrappStep -Package $AppName -Step 'Assignment' -Kind 'Start' -TenantId $ResolvedTenantId

            if (-not $Validate) {
                Invoke-TokenRefreshIfNeeded -TenantID $ResolvedTenantId -ClientID $EffectiveClientID

                Write-Log "Waiting for '$AppName' to become available in Intune..."
                $WaitParams = @{
                    AppId           = $AppId
                    TimeoutSeconds  = $script:ModuleDefaults.AppAvailabilityTimeoutSeconds
                    IntervalSeconds = $script:ModuleDefaults.AppAvailabilityIntervalSeconds
                }
                $LiveApp = Wait-IntuneAppAvailability @WaitParams

                if (-not $LiveApp) {
                    Write-Log "App '$AppName' not available in time. Skipping assignments." -Type 2
                    $Result.Errors.Add("Assignment skipped for '$AppName': app not available in time.")
                    Write-WrappStep -Package $AppName -Step 'Assignment' -Kind 'Fail' -TenantId $ResolvedTenantId -ErrorMessage 'App not available in time'
                    continue
                }

                # Verify GUID match
                try {
                    $NotesJson = $LiveApp.Notes | ConvertFrom-Json
                    if ($NotesJson.Guid -ne $App.GUID) {
                        Write-Log "GUID mismatch for '$AppName'. Expected '$($App.GUID)', found '$($NotesJson.Guid)'." -Type 2
                        $Result.Errors.Add("Assignment skipped for '$AppName': GUID mismatch.")
                        Write-WrappStep -Package $AppName -Step 'Assignment' -Kind 'Fail' -TenantId $ResolvedTenantId -ErrorMessage 'GUID mismatch'
                        continue
                    }
                    Write-Log "GUID match confirmed for '$AppName'."
                }
                catch {
                    Write-Log "Unable to parse Notes on '$AppName'. Skipping assignment." -Type 2
                    $Result.Errors.Add("Assignment skipped for '$AppName': could not parse Notes.")
                    Write-WrappStep -Package $AppName -Step 'Assignment' -Kind 'Fail' -TenantId $ResolvedTenantId -ErrorMessage 'Could not parse app Notes'
                    continue
                }
            }

            # Convert PSObject assignments to hashtables for Set-Win32AppAssignment
            $AssignmentHashtables = @()
            foreach ($a in $PkgAssignments) {
                $ht = @{}
                $a.PSObject.Properties | ForEach-Object { $ht[$_.Name] = $_.Value }
                $AssignmentHashtables += $ht
            }

            # Forward every DefaultAssignmentProperties key verbatim. The restart-
            # related keys are applied conditionally inside Set-Win32AppAssignment
            # (only when EnableRestartGracePeriod is $true on a given assignment).
            $AssignmentDefaults = @{}
            foreach ($k in $script:ModuleDefaults.DefaultAssignmentProperties.Keys) {
                $AssignmentDefaults[$k] = $script:ModuleDefaults.DefaultAssignmentProperties[$k]
            }

            try {
                $AssignParams = @{
                    AppId              = $AppId
                    Assignments        = $AssignmentHashtables
                    AssignmentDefaults = $AssignmentDefaults
                    Validate           = $Validate
                }
                $AssignResult = Set-Win32AppAssignment @AssignParams

                $appliedCount = $AssignResult.Applied.Count
                $errorCount   = $AssignResult.Errors.Count

                if ($errorCount -gt 0) {
                    foreach ($e in $AssignResult.Errors) {
                        Write-Log "  Assignment error: $e" -Type 3
                        $Result.Errors.Add("Assignment error for '$AppName': $e")
                    }
                }
                foreach ($s in $AssignResult.Skipped) {
                    Write-Log "  Assignment skipped: $s" -Type 2
                }
                foreach ($a in $AssignResult.Applied) {
                    Write-Log "  Assignment applied: $a"
                }

                # Emit structured result line for PhaseDetector
                if ($errorCount -gt 0) {
                    Write-Log "Assignment result for '$AppName': $appliedCount applied, $errorCount failed" -Type 2
                    Write-WrappStep -Package $AppName -Step 'Assignment' -Kind 'Fail' -TenantId $ResolvedTenantId -Detail "$appliedCount applied, $errorCount failed" -ErrorMessage "$errorCount assignment(s) failed"
                }
                else {
                    Write-Log "Assignment result for '$AppName': $appliedCount applied, 0 failed"
                    Write-WrappStep -Package $AppName -Step 'Assignment' -Kind 'Success' -TenantId $ResolvedTenantId -Detail "$appliedCount applied, 0 failed"
                }
            }
            catch {
                Write-Log "Assignment failed for '$AppName': $_" -Type 3
                Write-Log "Assignment result for '$AppName': 0 applied, 1 failed" -Type 3
                $Result.Errors.Add("Assignment failed for '$AppName': $_")
                Write-WrappStep -Package $AppName -Step 'Assignment' -Kind 'Fail' -TenantId $ResolvedTenantId -ErrorMessage "$_"
            }
        }

        if (-not $AnyAssignmentsSeen) {
            Write-Log "No assignments defined in configuration."
        }

        # ==================================================================
        # Phase 13: Result
        # ==================================================================
        if ($Result.Errors.Count -gt 0) {
            $Result.Success = $false
            Write-Log "Wrapp.Packager (Intune) completed with $($Result.Errors.Count) error(s)." -Type 2
        }
        else {
            $Result.Success = $true
            Write-Log "Wrapp.Packager (Intune) completed successfully."
        }
    }
    catch {
        Write-Log "Fatal error: $_" -Type 3
        $Result.Errors.Add("Fatal: $_")
        # B': typed fatal signal. Package '*' = broadcast to every package still
        # running/pending in THIS tenant pass (the .NET side scopes it). Fails
        # them explicitly, so a pre-package fatal (auth/preflight/AllBlocked)
        # can't be finalized as Succeeded by the RunComplete signal below.
        Write-WrappStep -Package '*' -Step 'Fatal' -Kind 'Fail' -TenantId $ResolvedTenantId -ErrorMessage "$_"
    }

    # B': typed completion signal. Fires on both the success and fatal paths;
    # the .NET side finalizes this tenant pass's packages (Fatal above has
    # already marked any that failed). Replaces the "Wrapp.Packager (Intune)
    # completed" log-scrape as the finalization trigger for the Intune path.
    $RunCompleteParams = @{
        Package  = '*'
        Step     = 'RunComplete'
        Kind     = if ($Result.Success) { 'Success' } else { 'Fail' }
        TenantId = $ResolvedTenantId
    }
    Write-WrappStep @RunCompleteParams

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
