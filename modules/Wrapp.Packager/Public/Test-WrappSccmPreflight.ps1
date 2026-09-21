function Test-WrappSccmPreflight {
    <#
    .SYNOPSIS
        Post-auth SCCM environment validation before any write operations.

    .DESCRIPTION
        Validates that the SCCM environment is ready for packaging:
        - Site connectivity
        - Distribution Point Groups exist
        - Deployment collections exist
        - Dependency target applications exist
        - AppFolder paths are valid
        - Icon files are accessible

        Returns three parallel outputs for maximum compatibility:
          Errors   [string[]] - Fatal issues (existing callers use this)
          Warnings [string[]] - Non-fatal advisories (existing callers use this)
          Issues   [object[]] - Structured issue objects for the WPF GUI.
                                Each object has: FieldPath, Severity, Code,
                                Message, AttemptedValue, AllowedValues, Guidance.

    .PARAMETER SCCMSiteConfig
        The SCCMSite configuration for the detected site code.

    .PARAMETER SiteDrive
        The SCCM site PSDrive path (e.g., 'CB1:').

    .PARAMETER Packages
        Array of package objects from Config.Script.SCCMPackager.Packages.

    .PARAMETER App
        The Config.App object (for dependency checks).

    .PARAMETER Validate
        If specified, returns placeholder results without SCCM queries.

    .OUTPUTS
        [PSCustomObject] with:
            IsValid    [bool]
            Errors     [string[]]
            Warnings   [string[]]
            Issues     [object[]]
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        $SCCMSiteConfig,

        [Parameter(Mandatory = $true)]
        [string]$SiteDrive,

        [Parameter(Mandatory = $true)]
        [array]$Packages,

        [Parameter(Mandatory = $true)]
        $App,

        [switch]$Validate
    )

    $Errors   = [System.Collections.Generic.List[string]]::new()
    $Warnings = [System.Collections.Generic.List[string]]::new()
    $Issues   = [System.Collections.Generic.List[object]]::new()

    # Helper: add an Error to both flat list and Issues
    function Add-Error {
        param([string]$FieldPath, [string]$Code, [string]$Msg, $AttemptedValue = $null, [string]$Guidance = '')
        $Errors.Add($Msg)
        $Issues.Add((New-ValidationIssue -FieldPath $FieldPath -Severity 'Error' -Code $Code -Message $Msg -AttemptedValue $AttemptedValue -Guidance $Guidance))
    }

    # Helper: add a Warning to both flat list and Issues
    function Add-Warning {
        param([string]$FieldPath, [string]$Code, [string]$Msg, $AttemptedValue = $null, [string]$Guidance = '')
        $Warnings.Add($Msg)
        $Issues.Add((New-ValidationIssue -FieldPath $FieldPath -Severity 'Warning' -Code $Code -Message $Msg -AttemptedValue $AttemptedValue -Guidance $Guidance))
    }

    if ($Validate) {
        Write-Log "[VALIDATE] Skipping SCCM preflight checks."
        return [PSCustomObject]@{
            IsValid  = $true
            Errors   = @()
            Warnings = @()
            Issues   = @()
        }
    }

    $OriginalLocation = Get-Location

    try {
        Set-Location -Path $SiteDrive

        # 1. Site connectivity test
        Write-Log "Preflight: Testing SCCM site connectivity..."
        try {
            $SiteInfo = Get-CMSite -ErrorAction Stop
            if (-not $SiteInfo) {
                Add-Error 'SCCM.Site.Connectivity' 'ENTITY_NOT_FOUND' "SCCM site query returned no results" -Guidance 'The ConfigurationManager module connected to the PSDrive but Get-CMSite returned nothing. Verify the site server is online and the SCCM console can connect to it. Check the SMS_ADMIN_UI_PATH environment variable points to the correct console installation.'
            }
            else {
                Write-Log "Preflight: Site '$($SiteInfo.SiteCode)' - '$($SiteInfo.SiteName)' is accessible."
            }
        }
        catch {
            Add-Error 'SCCM.Site.Connectivity' 'ENTITY_NOT_FOUND' "SCCM site connectivity failed: $_" -Guidance 'Ensure the SCCM console is installed and the SMS_ADMIN_UI_PATH environment variable is set. The account running the packager must have at least Read access to the SCCM site.'
        }

        # 2. Distribution Point Groups exist
        Write-Log "Preflight: Validating Distribution Point Groups..."
        if ($SCCMSiteConfig.DeploymentGroups) {
            foreach ($dpg in $SCCMSiteConfig.DeploymentGroups) {
                try {
                    $dpgObj = Get-CMDistributionPointGroup -Name $dpg -ErrorAction SilentlyContinue
                    if (-not $dpgObj) {
                        $p = @{
                            FieldPath      = "Config.SCCMSite.DeploymentGroups[$dpg]"
                            Code           = 'ENTITY_NOT_FOUND'
                            Msg            = "Distribution Point Group '$dpg' not found in SCCM"
                            AttemptedValue = $dpg
                            Guidance       = 'The distribution point group name must match exactly what is configured in the SCCM console under Administration > Distribution Point Groups. The name is case-sensitive. Create the group first, or correct the name in Config.json.'
                        }
                        Add-Error @p
                    }
                    else {
                        Write-Log "Preflight: DP Group '$dpg' found."
                    }
                }
                catch {
                    $p = @{
                        FieldPath      = "Config.SCCMSite.DeploymentGroups[$dpg]"
                        Code           = 'ENTITY_NOT_FOUND'
                        Msg            = "Failed to query DP Group '$dpg': $_"
                        AttemptedValue = $dpg
                        Guidance       = 'A query error occurred when checking for the distribution point group. Verify the SCCM console connection and that the account has sufficient permissions to read distribution point group information.'
                    }
                    Add-Error @p
                }
            }
        }

        # 3. Deployment collections exist
        Write-Log "Preflight: Validating deployment collections..."
        if ($SCCMSiteConfig.Deployments) {
            $checkedCollections = @{}
            foreach ($dep in $SCCMSiteConfig.Deployments) {
                $collName = $dep.Collection
                if (-not $collName -or $checkedCollections.ContainsKey($collName)) { continue }

                try {
                    $coll = Get-CMCollection -Name $collName -ErrorAction SilentlyContinue
                    if (-not $coll) {
                        $p = @{
                            FieldPath      = "Config.SCCMSite.Deployments[$($dep.AppName)].Collection"
                            Code           = 'ENTITY_NOT_FOUND'
                            Msg            = "Collection '$collName' not found in SCCM"
                            AttemptedValue = $collName
                            Guidance       = 'The collection name must match exactly what exists in SCCM. Collection names are case-sensitive. Create the collection in SCCM Assets and Compliance > Device Collections before running the packager.'
                        }
                        Add-Error @p
                    }
                    else {
                        Write-Log "Preflight: Collection '$collName' found."
                    }
                    $checkedCollections[$collName] = $true
                }
                catch {
                    $p = @{
                        FieldPath      = "Config.SCCMSite.Deployments[$($dep.AppName)].Collection"
                        Code           = 'ENTITY_NOT_FOUND'
                        Msg            = "Failed to query collection '$collName': $_"
                        AttemptedValue = $collName
                        Guidance       = 'A query error occurred when checking for the collection. Verify the SCCM console connection and that the account has permissions to read collection information.'
                    }
                    Add-Error @p
                    $checkedCollections[$collName] = $true
                }
            }
        }

        # 4. Dependency target applications exist
        if ($App.Dependencies -and @($App.Dependencies).Count -gt 0) {
            Write-Log "Preflight: Validating dependency targets..."
            foreach ($dep in $App.Dependencies) {
                $sccmAppName = $dep.SCCMAppName
                if (-not $sccmAppName) { continue }

                try {
                    $depApp = Get-CMApplication -Name $sccmAppName -ErrorAction SilentlyContinue
                    if (-not $depApp) {
                        $p = @{
                            FieldPath      = "Config.App.Dependencies[$sccmAppName].SCCMAppName"
                            Code           = 'ENTITY_NOT_FOUND'
                            Msg            = "Dependency target '$sccmAppName' not found in SCCM"
                            AttemptedValue = $sccmAppName
                            Guidance       = 'The dependency application must already exist in SCCM before the dependent app is created. Create the dependency app first, or verify the SCCMAppName matches the SCCM application name exactly.'
                        }
                        Add-Error @p
                    }
                    else {
                        Write-Log "Preflight: Dependency target '$sccmAppName' found."
                    }
                }
                catch {
                    $p = @{
                        FieldPath      = "Config.App.Dependencies[$sccmAppName].SCCMAppName"
                        Code           = 'ENTITY_NOT_FOUND'
                        Msg            = "Failed to query dependency '$sccmAppName': $_"
                        AttemptedValue = $sccmAppName
                        Guidance       = 'A query error occurred when checking for the dependency application. Verify the SCCM console connection and account permissions.'
                    }
                    Add-Error @p
                }
            }
        }

        # 5. AppFolder path exists (if specified)
        if ($SCCMSiteConfig.AppFolder) {
            Write-Log "Preflight: Validating AppFolder path..."
            try {
                $folderExists = Test-Path -Path $SCCMSiteConfig.AppFolder -ErrorAction SilentlyContinue
                if (-not $folderExists) {
                    $p = @{
                        FieldPath      = 'Config.SCCMSite.AppFolder'
                        Code           = 'ENTITY_NOT_FOUND'
                        Msg            = "AppFolder '$($SCCMSiteConfig.AppFolder)' does not exist in SCCM console (will skip move)"
                        AttemptedValue = $SCCMSiteConfig.AppFolder
                        Guidance       = 'The AppFolder path is used to organize applications in the SCCM console folder structure. Create the folder in the SCCM console under Software Library > Application Management > Applications, or remove the AppFolder setting from Config.json to skip folder organization.'
                    }
                    Add-Warning @p
                }
                else {
                    Write-Log "Preflight: AppFolder '$($SCCMSiteConfig.AppFolder)' exists."
                }
            }
            catch {
                $p = @{
                    FieldPath      = 'Config.SCCMSite.AppFolder'
                    Code           = 'ENTITY_NOT_FOUND'
                    Msg            = "Could not validate AppFolder '$($SCCMSiteConfig.AppFolder)': $_"
                    AttemptedValue = $SCCMSiteConfig.AppFolder
                    Guidance       = 'AppFolder validation failed with an error. The packager will attempt to move the app to the folder at creation time. If the folder does not exist, the move step will be skipped with a warning.'
                }
                Add-Warning @p
            }
        }
    }
    finally {
        Set-Location -Path $OriginalLocation
    }

    # 6. Icon files are accessible (filesystem check, not SCCM PSDrive)
    if ($SCCMSiteConfig.IconFolder) {
        Write-Log "Preflight: Validating icon files..."
        foreach ($pkg in $Packages) {
            if (-not $pkg.Icon) { continue }
            $iconPath = Join-Path -Path $SCCMSiteConfig.IconFolder -ChildPath $pkg.Icon
            if (-not (Test-Path -Path "FileSystem::$iconPath")) {
                $p = @{
                    FieldPath      = "Config.SCCMSite.IconFolder[$($pkg.Icon)]"
                    Code           = 'ENTITY_NOT_FOUND'
                    Msg            = "Icon file not found: $iconPath"
                    AttemptedValue = $iconPath
                    Guidance       = 'The icon file must be accessible from the machine running the packager. Verify the IconFolder path and the icon file name are correct. Supported formats are PNG and JPG. The app will be created without an icon if the file is missing.'
                }
                Add-Warning @p
            }
            else {
                Write-Log "Preflight: Icon '$($pkg.Icon)' found."
            }
        }
    }

    return [PSCustomObject]@{
        IsValid  = ($Errors.Count -eq 0)
        Errors   = $Errors.ToArray()
        Warnings = $Warnings.ToArray()
        Issues   = $Issues.ToArray()
    }
}
