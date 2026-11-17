function Connect-WrappSccm {
    <#
    .SYNOPSIS
        Imports the ConfigurationManager module and lands on the SCCM site drive.

    .DESCRIPTION
        Loads the ConfigurationManager PowerShell module from the SCCM
        Configuration Manager Console installation path, then finds or
        creates the site's CMSite PSDrive.

        The module creates a site drive on import only when the console has
        completed a connection to that site under the current Windows
        account. When no drive appears and a site code is known, the drive
        is built against the SMS Provider server the console recorded (see
        Resolve-WrappSccmProviderServer), so a packaging run works from a
        console-only workstation on an account that has never opened the
        console. Without a site code there is nothing to build, and the
        error says so.

    .PARAMETER SiteCode
        The site to connect to. Optional: when omitted, the drive the module
        created is used, whichever site it is for.

    .PARAMETER Validate
        If specified, skips actual module import and returns a placeholder.

    .OUTPUTS
        [PSCustomObject] with:
            SiteCode  [string] - The SCCM site code (e.g., 'CB1')
            SiteDrive [string] - The PSDrive path (e.g., 'CB1:')
    #>
    [CmdletBinding()]
    param(
        [string]$SiteCode,

        [switch]$Validate
    )

    if ($Validate) {
        Write-Log "[VALIDATE] Skipping ConfigurationManager module import."
        return [PSCustomObject]@{
            SiteCode  = 'VALIDATE'
            SiteDrive = 'VALIDATE:'
        }
    }

    $AdminUIPath = $env:SMS_ADMIN_UI_PATH
    if (-not $AdminUIPath) {
        throw "SMS_ADMIN_UI_PATH environment variable not set. Is the SCCM Configuration Manager Console installed? If it was installed while Wrapp was running, restart Wrapp so it sees the variable."
    }

    $ModulePath = Join-Path -Path (Split-Path -Path $AdminUIPath) -ChildPath 'ConfigurationManager.psd1'
    if (-not (Test-Path -Path $ModulePath)) {
        throw "ConfigurationManager module not found at: $ModulePath"
    }

    Write-Log "Importing ConfigurationManager module from: $ModulePath"

    try {
        Import-Module -Name $ModulePath -ErrorAction Stop
    }
    catch {
        Write-Log "Failed to import ConfigurationManager module: $_" -Type 3
        throw "Failed to import ConfigurationManager module: $_"
    }

    $Drives = @(Get-PSDrive -PSProvider CMSITE -ErrorAction SilentlyContinue)
    $Drive = if ($SiteCode) {
        $Drives | Where-Object { $_.Name -eq $SiteCode } | Select-Object -First 1
    }
    else {
        $Drives | Select-Object -First 1
    }

    if (-not $Drive) {
        if (-not $SiteCode) {
            throw "No SCCM site detected. Open the Configuration Manager console as this Windows account and connect to the site once, or pass -SiteCode so the drive can be built directly."
        }

        $Server = Resolve-WrappSccmProviderServer
        $Where = if ($Server -eq '.') { 'this machine (no console connection is recorded for this Windows account)' } else { "'$Server'" }
        Write-Log "No $SiteCode drive from the console; connecting to site $SiteCode through $Where"
        try {
            $Drive = New-PSDrive -Name $SiteCode -PSProvider CMSITE -Root $Server -ErrorAction Stop
        }
        catch {
            Write-Log "Could not connect to ConfigMgr site ${SiteCode}: $_" -Type 3
            throw "Could not connect to ConfigMgr site $SiteCode through $Where - $($_.Exception.Message) Open the Configuration Manager console as this Windows account, connect to site $SiteCode once, and check that the account is an administrative user in that site."
        }
    }

    $SiteName = $Drive.Name
    $SiteDrivePath = "${SiteName}:"

    # Record the site code at module scope so the
    # outer runspace can Remove-PSDrive on it in the finally cleanup. Without
    # this, a pooled runspace would inherit admin authority on the SCCM site
    # for the next dispatch.
    $script:SccmSiteCode = $SiteName

    Write-Log "Connected to SCCM site: $SiteName ($SiteDrivePath)"

    return [PSCustomObject]@{
        SiteCode  = $SiteName
        SiteDrive = $SiteDrivePath
    }
}
