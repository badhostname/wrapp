namespace Wrapp.Models;

/// <summary>
/// Identifies the script framework used by the bundle.
/// Controls install/uninstall commands, bundle structure, and editor tabs.
/// </summary>
public enum ScriptFramework
{
    /// <summary>
    /// Appease framework: separate InstallScript.ps1, UninstallScript.ps1,
    /// DetectScript.ps1, and Appease.ps1 helper module.
    /// </summary>
    Appease,

    /// <summary>
    /// PowerShell App Deployment Toolkit v4: single Invoke-AppDeployToolkit.ps1
    /// with -DeploymentType Install/Uninstall/Repair parameter.
    /// </summary>
    PSADT
}
