# EXE Silent Install/Uninstall via PSADT v4
# Installs an EXE with silent switches. Uninstalls by registry lookup.
# Place the .exe installer in the Files/ folder.
# Adjust the silent parameters for your specific installer.

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
    # $uninstallKey = Get-ADTApplication -Name '{{Name}}'
    # if ($uninstallKey) { Start-ADTProcess -FilePath $uninstallKey.UninstallString -ArgumentList '/S' -WaitForMsiExec }

    $adtSession.InstallPhase = $adtSession.DeploymentType

    ## Install the EXE silently
    ## Common silent switches: /S, /silent, /quiet, /verysilent, --silent
    Start-ADTProcess -FilePath "$($adtSession.DirFiles)\{{EXEFile}}" -ArgumentList '/S'

    $adtSession.InstallPhase = "Post-$($adtSession.DeploymentType)"

    ## Post-Install tasks (registry keys, file copies, shortcuts, etc.)
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

    ## Uninstall by looking up the product in the registry
    $app = Get-ADTApplication -Name '{{Name}}'
    if ($app)
    {
        ## EXE-based uninstaller: parse the UninstallString and add silent switch
        Start-ADTProcess -FilePath $app.UninstallString -ArgumentList '/S'
    }

    $adtSession.InstallPhase = "Post-$($adtSession.DeploymentType)"
}

function Repair-ADTDeployment
{
    [CmdletBinding()]
    param ()

    $adtSession.InstallPhase = "Pre-$($adtSession.DeploymentType)"
    Show-ADTInstallationProgress

    $adtSession.InstallPhase = $adtSession.DeploymentType

    ## Repair: re-run the installer with repair/overwrite flags
    Start-ADTProcess -FilePath "$($adtSession.DirFiles)\{{EXEFile}}" -ArgumentList '/S'

    $adtSession.InstallPhase = "Post-$($adtSession.DeploymentType)"
}
