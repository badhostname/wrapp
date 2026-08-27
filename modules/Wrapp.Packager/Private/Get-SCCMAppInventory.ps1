function Get-SCCMAppInventory {
    <#
    .SYNOPSIS
        Returns a lightweight list of all SCCM applications for inventory display.
    .DESCRIPTION
        Wraps Get-CMApplication -Fast. Only works when the ConfigMgr console is
        installed and the site drive is available.
    #>
    [CmdletBinding()]
    param()

    $apps = Get-CMApplication -Fast -ErrorAction Stop

    foreach ($app in $apps) {
        [PSCustomObject]@{
            CI_ID           = $app.CI_ID
            Name            = $app.LocalizedDisplayName
            Manufacturer    = $app.Manufacturer
            SoftwareVersion = $app.SoftwareVersion
            DeploymentCount = $app.NumberOfDeployments
            DependencyCount = $app.NumberOfDependentDTs
            IsDeployed      = $app.IsDeployed
        }
    }
}
