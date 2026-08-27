# MSI Silent Install/Uninstall via PSADT v4
# Installs an MSI file silently. Uninstalls by product name lookup.
# Place the .msi file in the Files/ folder.

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

    ## Pre-Install: remove previous version if present
    # Uninstall-ADTApplication -Name '{{Name}}'

    $adtSession.InstallPhase = $adtSession.DeploymentType

    ## Install the MSI
    Start-ADTMsiProcess -Action Install -FilePath '{{MSIFile}}'

    $adtSession.InstallPhase = "Post-$($adtSession.DeploymentType)"

    ## Post-Install tasks (registry keys, file copies, etc.)
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

    ## Uninstall the MSI by product name
    Start-ADTMsiProcess -Action Uninstall -FilePath '{{MSIFile}}'

    $adtSession.InstallPhase = "Post-$($adtSession.DeploymentType)"
}

function Repair-ADTDeployment
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

    Start-ADTMsiProcess -Action Repair -FilePath '{{MSIFile}}'

    $adtSession.InstallPhase = "Post-$($adtSession.DeploymentType)"
}
