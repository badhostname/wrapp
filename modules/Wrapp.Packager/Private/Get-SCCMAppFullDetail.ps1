function Get-SCCMAppFullDetail {
    <#
    .SYNOPSIS
        Returns full detail for a single SCCM application including deployment type,
        commands, content location, and deployments.
    .PARAMETER AppName
        The localized display name of the application.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$AppName
    )

    $app = Get-CMApplication -Name $AppName -ErrorAction Stop
    $dt  = Get-CMDeploymentType -ApplicationName $AppName -ErrorAction SilentlyContinue |
           Select-Object -First 1
    $deployments = Get-CMApplicationDeployment -Name $AppName -ErrorAction SilentlyContinue

    $installCmd      = ''
    $uninstallCmd    = ''
    $contentLocation = ''
    $dtName          = ''

    if ($dt) {
        $dtName = $dt.LocalizedDisplayName
        try {
            $xml       = [xml]$dt.SDMPackageXML
            $installer = $xml.AppMgmtDigest.DeploymentType.Installer

            $installCmd = $installer.InstallAction.Args.Arg |
                Where-Object { $_.Name -eq 'InstallCommandLine' } |
                Select-Object -ExpandProperty '#text' -ErrorAction SilentlyContinue

            $uninstallCmd = $installer.UninstallAction.Args.Arg |
                Where-Object { $_.Name -eq 'InstallCommandLine' } |
                Select-Object -ExpandProperty '#text' -ErrorAction SilentlyContinue

            $contentLocation = $installer.Contents.Content.Location
        }
        catch {
            Write-Verbose "Failed to parse deployment type XML: $_"
        }
    }

    [PSCustomObject]@{
        Platform           = 'SCCM'
        Id                 = $app.CI_ID.ToString()
        DisplayName        = $app.LocalizedDisplayName
        Publisher          = $app.Manufacturer
        Version            = $app.SoftwareVersion
        Description        = $app.LocalizedDescription
        InstallCommand     = $installCmd
        UninstallCommand   = $uninstallCmd
        ContentLocation    = $contentLocation
        DeploymentTypeName = $dtName
        Assignments        = @($deployments | ForEach-Object {
            [PSCustomObject]@{
                Intent       = $_.DesiredConfigType
                TargetType   = 'Collection'
                GroupName    = $_.CollectionName
                Notification = $_.UserUIExperience
            }
        })
        Dependencies       = @()
        Supersedence       = @()
    }
}
