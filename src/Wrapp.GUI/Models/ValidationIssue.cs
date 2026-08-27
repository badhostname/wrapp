namespace Wrapp.Models;

/// <summary>
/// C# mirror of Wrapp.Packager's New-ValidationIssue output object.
/// Every structured issue returned by Test-WrappConfig, Test-WrappIntunePreflight,
/// or Test-WrappSccmPreflight is deserialized into this record.
/// </summary>
public record ValidationIssue(
    string FieldPath,
    string Severity,        // "Error" | "Warning"
    string Code,
    string Message,
    string? AttemptedValue,
    string[] AllowedValues,
    string Guidance
)
{
    public bool IsError => Severity == "Error";
    public bool IsWarning => Severity == "Warning";

    /// <summary>
    /// Returns the top-level config section this issue belongs to, for nav badge routing.
    /// Derived from the FieldPath prefix (e.g. "Config.Script.IntunePackager..." -> "Packages").
    /// </summary>
    public NavigationSection? RoutingSection => FieldPath switch
    {
        var p when p.StartsWith("Config.App") => NavigationSection.General,
        var p when p.StartsWith("Config.Script.IntunePackager") => NavigationSection.Intune,
        var p when p.StartsWith("Config.Script.SCCMPackager") => NavigationSection.SCCM,
        var p when p.StartsWith("Config.IntuneTenant") => NavigationSection.Intune,
        var p when p.StartsWith("Config.SCCMSite") => NavigationSection.SCCM,
        var p when p.StartsWith("Config.Script.Detect") => NavigationSection.Detection,
        var p when p.StartsWith("Win32AppParameters") => NavigationSection.Intune,
        _ => null
    };
}
