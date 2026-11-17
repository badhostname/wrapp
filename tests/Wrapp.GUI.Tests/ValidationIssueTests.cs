using Wrapp.Models;

namespace Wrapp.Tests;

public class ValidationIssueTests
{
    // Helper to create an issue with a given FieldPath
    private static ValidationIssue Issue(string fieldPath, string severity = "Error") =>
        new(fieldPath, severity, "TEST_CODE", "Test message", null, Array.Empty<string>(), "Guidance");

    // -------------------------------------------------------------------
    // Severity flags
    // -------------------------------------------------------------------

    [Fact]
    public void IsError_True_WhenSeverityIsError()
    {
        var issue = Issue("Config.App.Name", "Error");
        Assert.True(issue.IsError);
        Assert.False(issue.IsWarning);
    }

    [Fact]
    public void IsWarning_True_WhenSeverityIsWarning()
    {
        var issue = Issue("Config.App.Name", "Warning");
        Assert.False(issue.IsError);
        Assert.True(issue.IsWarning);
    }

    // -------------------------------------------------------------------
    // RoutingSection - General (App paths)
    // -------------------------------------------------------------------

    [Theory]
    [InlineData("Config.App.Name")]
    [InlineData("Config.App.Company")]
    [InlineData("Config.App.EXEFile")]
    [InlineData("Config.App.GUID")]
    [InlineData("Config.App.DetectRunning[0].ExeFileName")]
    public void RoutingSection_AppPaths_MapToGeneral(string fieldPath)
    {
        var issue = Issue(fieldPath);
        Assert.Equal(NavigationSection.General, issue.RoutingSection);
    }

    // -------------------------------------------------------------------
    // RoutingSection - Intune / SCCM (Package + Tenant paths)
    // -------------------------------------------------------------------

    [Theory]
    [InlineData("Config.Script.IntunePackager.Packages[0].InstallExperience")]
    [InlineData("Config.Script.IntunePackager.Packages[1].AppName")]
    [InlineData("Win32AppParameters.InstallExperience")]
    [InlineData("Win32AppParameters.RestartBehavior")]
    [InlineData("Config.IntuneTenant.PROD.AuthFlow")]
    [InlineData("Config.IntuneTenant.PROD.ClientID")]
    public void RoutingSection_IntunePaths_MapToIntune(string fieldPath)
    {
        var issue = Issue(fieldPath);
        Assert.Equal(NavigationSection.Intune, issue.RoutingSection);
    }

    [Theory]
    [InlineData("Config.Script.SCCMPackager.Packages[0].InstallationBehaviorType")]
    [InlineData("Config.SCCMSite.SITE1.AppFolder")]
    [InlineData("Config.SCCMSite.SITE1.Deployments[0].Collection")]
    public void RoutingSection_SCCMPaths_MapToSCCM(string fieldPath)
    {
        var issue = Issue(fieldPath);
        Assert.Equal(NavigationSection.SCCM, issue.RoutingSection);
    }

    // -------------------------------------------------------------------
    // RoutingSection - Detection
    // -------------------------------------------------------------------

    [Theory]
    [InlineData("Config.Script.Detect.Expression_Default")]
    [InlineData("Config.Script.Detect.Tests[0].Operator")]
    [InlineData("Config.Script.Detect.Tests[1].Value")]
    public void RoutingSection_DetectPaths_MapToDetection(string fieldPath)
    {
        var issue = Issue(fieldPath);
        Assert.Equal(NavigationSection.Detection, issue.RoutingSection);
    }

    // -------------------------------------------------------------------
    // RoutingSection - unknown paths return null
    // -------------------------------------------------------------------

    [Theory]
    [InlineData("SomeUnknownPath")]
    [InlineData("Config.Script.Install.Tag")]
    [InlineData("Config.Script.Uninstall.Tag")]
    [InlineData("")]
    public void RoutingSection_UnknownPaths_ReturnNull(string fieldPath)
    {
        var issue = Issue(fieldPath);
        Assert.Null(issue.RoutingSection);
    }
}
