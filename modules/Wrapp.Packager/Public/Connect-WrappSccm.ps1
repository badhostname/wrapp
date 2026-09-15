function Connect-WrappSccm {
    <#
    .SYNOPSIS
        Imports the ConfigurationManager module and detects the SCCM site.

    .DESCRIPTION
        Loads the ConfigurationManager PowerShell module from the SCCM
        Configuration Manager Console installation path. Detects the
        local SCCM site code via the CMSITE PSDrive provider.

    .PARAMETER Validate
        If specified, skips actual module import and returns a placeholder.

    .OUTPUTS
        [PSCustomObject] with:
            SiteCode  [string] - The SCCM site code (e.g., 'CB1')
            SiteDrive [string] - The PSDrive path (e.g., 'CB1:')
    #>
    [CmdletBinding()]
    param(
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
        throw "SMS_ADMIN_UI_PATH environment variable not set. Is the SCCM Configuration Manager Console installed?"
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

    $SiteCode = Get-PSDrive -PSProvider CMSITE -ErrorAction SilentlyContinue
    if (-not $SiteCode) {
        throw "No SCCM site detected. Ensure the ConfigurationManager Console is properly connected."
    }

    $SiteName = $SiteCode.Name
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
