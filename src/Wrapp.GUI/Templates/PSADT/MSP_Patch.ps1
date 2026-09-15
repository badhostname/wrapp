# MSP Patch Install via PSADT v4
# Applies an MSP patch file to an existing MSI installation.
# Place the .msp file in the Files/ folder.

$adtSession = @{
    AppVendor = '{{Company}}'
    AppName = '{{Name}}'
    AppVersion = '{{DotVersion}}'
    AppArch = ''
    AppLang = '{{Language}}'
    AppRevision = '01'
    AppSuccessExitCodes = @(0)
    AppRebootExitCodes = @(1641, 3010)
    AppProcessesToClose = @()
    AppScriptVersion = '1.0.0'
    AppScriptDate = '{{Date}}'
    AppScriptAuthor = '{{Author}}'
    RequireAdmin = $true
    InstallName = ''
    InstallTitle = ''
    DeployAppScriptFriendlyName = $MyInvocation.MyCommand.Name
    DeployAppScriptParameters = $PSBoundParameters
    DeployAppScriptVersion = '4.1.8'
}

function Install-ADTDeployment
{
    [CmdletBinding()]
    param ()

    $adtSession.InstallPhase = "Pre-$($adtSession.DeploymentType)"

    $saiwParams = @{
        CheckDiskSpace = $true
        PersistPrompt = $true
    }
    if ($adtSession.AppProcessesToClose.Count -gt 0)
    {
        $saiwParams.Add('CloseProcesses', $adtSession.AppProcessesToClose)
    }
    Show-ADTInstallationWelcome @saiwParams
    Show-ADTInstallationProgress

    $adtSession.InstallPhase = $adtSession.DeploymentType

    ## Apply the MSP patch
    Start-ADTMsiProcess -Action Patch -FilePath '{{MSIFile}}'

    $adtSession.InstallPhase = "Post-$($adtSession.DeploymentType)"
}

function Uninstall-ADTDeployment
{
    [CmdletBinding()]
    param ()

    $adtSession.InstallPhase = "Pre-$($adtSession.DeploymentType)"

    if ($adtSession.AppProcessesToClose.Count -gt 0)
    {
        Show-ADTInstallationWelcome -CloseProcesses $adtSession.AppProcessesToClose -CloseProcessesCountdown 60
    }
    Show-ADTInstallationProgress

    $adtSession.InstallPhase = $adtSession.DeploymentType

    ## Uninstall the base product (patch removal is typically not supported)
    ## Adjust the product name to match your base MSI application
    Uninstall-ADTApplication -Name '{{Name}}'

    $adtSession.InstallPhase = "Post-$($adtSession.DeploymentType)"
}

function Repair-ADTDeployment
{
    [CmdletBinding()]
    param ()

    $adtSession.InstallPhase = "Pre-$($adtSession.DeploymentType)"
    Show-ADTInstallationProgress

    $adtSession.InstallPhase = $adtSession.DeploymentType

    ## Re-apply the patch
    Start-ADTMsiProcess -Action Patch -FilePath '{{MSIFile}}'

    $adtSession.InstallPhase = "Post-$($adtSession.DeploymentType)"
}
