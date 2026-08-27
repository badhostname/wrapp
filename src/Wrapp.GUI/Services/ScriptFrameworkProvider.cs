using System.IO;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Provides framework-specific values for bundle creation, script editing,
/// and install/uninstall command construction. All framework-dependent strings
/// are centralized here to avoid scattering them across ViewModels and Services.
/// </summary>
public static class ScriptFrameworkProvider
{
    // ------------------------------------------------------------------
    // Script files written into the bundle
    // ------------------------------------------------------------------

    /// <summary>Scripts to write into Script/ during bundle creation (template-based).</summary>
    public static string[] GetBundleScripts(ScriptFramework fw) => fw switch
    {
        ScriptFramework.PSADT => new[] { "DetectScript.ps1" },
        _ => new[] { "InstallScript.ps1", "UninstallScript.ps1", "DetectScript.ps1", "Appease.ps1" }
    };

    // ------------------------------------------------------------------
    // Shortcut .lnk files in Shortcuts/
    // ------------------------------------------------------------------

    // powershell.exe is always at this path on any supported Windows version;
    // hardcoding it keeps the shortcut portable since the .lnk has no way to
    // expand env vars in TargetPath at launch time.
    private const string PowerShellPath =
        @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";

    /// <summary>
    /// Returns (lnkFileName, targetPath, arguments) tuples describing the
    /// Install/Uninstall .lnk shortcuts to place in the bundle's Shortcuts/
    /// folder. Arguments use relative paths (..\Script\... or
    /// ..\Invoke-AppDeployToolkit.exe) that resolve against the .lnk's own
    /// folder at launch time, since WorkingDirectory is left empty by the
    /// writer to keep the shortcut portable.
    /// </summary>
    public static (string LnkName, string TargetPath, string Arguments)[] GetShortcuts(ScriptFramework fw) => fw switch
    {
        // PSADT: route through powershell.exe so the .lnk's TargetPath can be
        // an absolute, fixed system path while the actual Invoke-AppDeployToolkit.exe
        // is referenced by a relative path inside the -Command expression,
        // which PowerShell resolves against its working directory (the .lnk's
        // folder) at launch time.
        ScriptFramework.PSADT => new[]
        {
            ("Install.lnk",
             PowerShellPath,
             @"-ExecutionPolicy Bypass -Command ""& '..\Invoke-AppDeployToolkit.exe' -DeploymentType Install -DeployMode Silent"""),
            ("Uninstall.lnk",
             PowerShellPath,
             @"-ExecutionPolicy Bypass -Command ""& '..\Invoke-AppDeployToolkit.exe' -DeploymentType Uninstall -DeployMode Silent"""),
        },
        _ => new[]
        {
            ("Install.lnk",
             PowerShellPath,
             @"-ExecutionPolicy Bypass -File ""..\Script\InstallScript.ps1"""),
            ("Uninstall.lnk",
             PowerShellPath,
             @"-ExecutionPolicy Bypass -File ""..\Script\UninstallScript.ps1"""),
        }
    };

    // ------------------------------------------------------------------
    // Install / Uninstall command lines per platform
    // ------------------------------------------------------------------

    public static string GetIntuneInstallCommand(ScriptFramework fw) => fw switch
    {
        ScriptFramework.PSADT =>
            @"Invoke-AppDeployToolkit.exe -DeploymentType Install -DeployMode Silent",
        _ =>
            @"%SystemRoot%\sysnative\WindowsPowerShell\v1.0\powershell.exe -ExecutionPolicy Bypass -file ""Script\InstallScript.ps1"""
    };

    public static string GetIntuneUninstallCommand(ScriptFramework fw) => fw switch
    {
        ScriptFramework.PSADT =>
            @"Invoke-AppDeployToolkit.exe -DeploymentType Uninstall -DeployMode Silent",
        _ =>
            @"%SystemRoot%\sysnative\WindowsPowerShell\v1.0\powershell.exe -ExecutionPolicy Bypass -file ""Script\UninstallScript.ps1"""
    };

    public static string GetSccmInstallCommand(ScriptFramework fw) => fw switch
    {
        ScriptFramework.PSADT =>
            @"Invoke-AppDeployToolkit.exe -DeploymentType Install -DeployMode Silent",
        _ =>
            @"Powershell.exe -executionpolicy bypass -file ""Script\InstallScript.ps1"""
    };

    public static string GetSccmUninstallCommand(ScriptFramework fw) => fw switch
    {
        ScriptFramework.PSADT =>
            @"Invoke-AppDeployToolkit.exe -DeploymentType Uninstall -DeployMode Silent",
        _ =>
            @"Powershell.exe -executionpolicy bypass -file ""Script\UninstallScript.ps1"""
    };

    // ------------------------------------------------------------------
    // Scripts editor tabs
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns (modelId, displayLabel, fileName) tuples for the scripts editor tabs.
    /// </summary>
    public static (string Id, string Label, string FileName)[] GetEditorTabs(ScriptFramework fw) => fw switch
    {
        ScriptFramework.PSADT => new[]
        {
            ("detect", "Detect", "DetectScript.ps1"),
            ("deploy", "Deploy", "Invoke-AppDeployToolkit.ps1"),
        },
        _ => new[]
        {
            ("detect", "Detect", "DetectScript.ps1"),
            ("install", "Install", "InstallScript.ps1"),
            ("uninstall", "Uninstall", "UninstallScript.ps1"),
        }
    };

    /// <summary>
    /// Maps a modelId to its filename for the given framework.
    /// Returns null if the modelId is not valid for the framework.
    /// </summary>
    public static string? ModelIdToFileName(ScriptFramework fw, string modelId)
    {
        foreach (var tab in GetEditorTabs(fw))
        {
            if (string.Equals(tab.Id, modelId, StringComparison.OrdinalIgnoreCase))
                return tab.FileName;
        }
        return null;
    }

    /// <summary>
    /// Script template categories available for the template ComboBox.
    /// Returns null if no templates apply for the given tab.
    /// </summary>
    public static string? GetTemplateCategory(ScriptFramework fw, string modelId) => fw switch
    {
        ScriptFramework.PSADT => modelId == "deploy" ? "PSADT" : null,
        _ => modelId switch
        {
            "install" => "Install",
            "uninstall" => "Uninstall",
            _ => null
        }
    };

    // ------------------------------------------------------------------
    // Bundle folder names
    // ------------------------------------------------------------------

    /// <summary>Folder name where installers/binaries are placed.</summary>
    public static string GetBinaryFolderName(ScriptFramework fw) => fw switch
    {
        ScriptFramework.PSADT => "Files",
        _ => "B"
    };

    /// <summary>
    /// Name of the bundled template folder in the output directory
    /// (e.g. "appease-template" or "psadt-template").
    /// </summary>
    public static string GetTemplateFolderName(ScriptFramework fw) => fw switch
    {
        ScriptFramework.PSADT => "psadt-template",
        _ => "appease-template"
    };

    // ------------------------------------------------------------------
    // Detection from folder structure
    // ------------------------------------------------------------------

    /// <summary>
    /// Detects the script framework from a decrypted bundle folder by checking
    /// Config.json, file signatures, and directory structure.
    /// </summary>
    public static ScriptFramework DetectFromFolder(string folderPath)
    {
        // Priority 1: Config.json ScriptFramework field
        var configPath = BundlePaths.ConfigJson(folderPath);
        if (!File.Exists(configPath))
            configPath = Path.Combine(folderPath, BundlePaths.ConfigFileName);
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("App", out var app)
                    && app.TryGetProperty("ScriptFramework", out var fw))
                {
                    var parsed = Parse(fw.GetString());
                    AppLogger.Info($"ScriptFrameworkProvider: detected '{parsed}' from Config.json");
                    return parsed;
                }
            }
            catch { /* fall through to file-based detection */ }
        }

        // Priority 2: PSADT root-level files
        if (File.Exists(Path.Combine(folderPath, "Invoke-AppDeployToolkit.ps1"))
            || File.Exists(Path.Combine(folderPath, "Invoke-AppDeployToolkit.exe")))
        {
            AppLogger.Info("ScriptFrameworkProvider: detected PSADT from Invoke-AppDeployToolkit files");
            return ScriptFramework.PSADT;
        }

        // Priority 3: Appease script files
        var scriptDir = BundlePaths.ScriptDir(folderPath);
        if (Directory.Exists(scriptDir)
            && (File.Exists(Path.Combine(scriptDir, "InstallScript.ps1"))
                || File.Exists(Path.Combine(scriptDir, "Appease.ps1"))))
        {
            AppLogger.Info("ScriptFrameworkProvider: detected Appease from script files");
            return ScriptFramework.Appease;
        }

        // Fallback
        AppLogger.Info("ScriptFrameworkProvider: no framework detected, defaulting to Appease");
        return ScriptFramework.Appease;
    }

    // ------------------------------------------------------------------
    // Parsing
    // ------------------------------------------------------------------

    /// <summary>Parses a string to ScriptFramework enum, defaulting to Appease.</summary>
    public static ScriptFramework Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ScriptFramework.Appease;
        if (Enum.TryParse<ScriptFramework>(value, ignoreCase: true, out var fw)) return fw;
        return ScriptFramework.Appease;
    }
}
