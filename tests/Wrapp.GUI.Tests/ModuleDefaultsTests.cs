using Wrapp.Models;

namespace Wrapp.Tests;

public class ModuleDefaultsTests
{
    // -------------------------------------------------------------------
    // ModuleDefaults.Empty - verify all arrays are non-null and non-empty
    // -------------------------------------------------------------------

    [Fact]
    public void Empty_ValidAuthFlows_ContainsFourFlows()
    {
        var d = ModuleDefaults.Empty;
        Assert.Contains(AuthFlow.Interactive,  d.ValidAuthFlows);
        Assert.Contains(AuthFlow.DeviceCode,   d.ValidAuthFlows);
        Assert.Contains(AuthFlow.ClientSecret, d.ValidAuthFlows);
        Assert.Contains(AuthFlow.ClientCert,   d.ValidAuthFlows);
        Assert.Equal(4, d.ValidAuthFlows.Length);
    }

    [Fact]
    public void Empty_ValidInstallExperience_ContainsSystemAndUser()
    {
        var d = ModuleDefaults.Empty;
        Assert.Contains("system", d.ValidInstallExperience);
        Assert.Contains("user",   d.ValidInstallExperience);
    }

    [Fact]
    public void Empty_ValidRestartBehavior_HasExpectedValues()
    {
        var d = ModuleDefaults.Empty;
        Assert.Contains("allow",             d.ValidRestartBehavior);
        Assert.Contains("basedOnReturnCode", d.ValidRestartBehavior);
        Assert.Contains("suppress",          d.ValidRestartBehavior);
        Assert.Contains("force",             d.ValidRestartBehavior);
    }

    [Fact]
    public void Empty_ValidArchitecture_ContainsX64()
    {
        var d = ModuleDefaults.Empty;
        Assert.Contains("x64", d.ValidArchitecture);
    }

    [Fact]
    public void Empty_ValidUpdateModes_HasThreeModes()
    {
        var d = ModuleDefaults.Empty;
        Assert.Contains(UpdateMode.Create,        d.ValidUpdateModes);
        Assert.Contains(UpdateMode.Update,        d.ValidUpdateModes);
        Assert.Contains(UpdateMode.UpdateContent, d.ValidUpdateModes);
    }

    [Fact]
    public void Empty_ValidSupersedenceTypes_ContainsReplaceAndUpdate()
    {
        var d = ModuleDefaults.Empty;
        Assert.Contains("Replace", d.ValidSupersedenceTypes);
        Assert.Contains("Update",  d.ValidSupersedenceTypes);
    }

    [Fact]
    public void Empty_AllArrays_NonNull()
    {
        var d = ModuleDefaults.Empty;
        Assert.NotNull(d.ValidAuthFlows);
        Assert.NotNull(d.ValidInstallExperience);
        Assert.NotNull(d.ValidRestartBehavior);
        Assert.NotNull(d.ValidArchitecture);
        Assert.NotNull(d.ValidMinOS);
        Assert.NotNull(d.ValidUpdateModes);
        Assert.NotNull(d.ValidDetectionTypes);
        Assert.NotNull(d.ValidRequirementTypes);
        Assert.NotNull(d.ValidReturnCodeTypes);
        Assert.NotNull(d.ValidDependencyTypes);
        Assert.NotNull(d.ValidSupersedenceTypes);
        Assert.NotNull(d.ValidAssignmentIntents);
        Assert.NotNull(d.ValidAssignmentTypes);
        Assert.NotNull(d.ValidFilterModes);
        Assert.NotNull(d.ValidInstallationBehaviorTypes);
        Assert.NotNull(d.ValidLogonRequirementTypes);
        Assert.NotNull(d.ValidUserNotifications);
        Assert.NotNull(d.ValidDeployActions);
        Assert.NotNull(d.ValidDeployPurposes);
    }

    [Fact]
    public void Empty_ValidMinOS_ContainsW11Entries()
    {
        var d = ModuleDefaults.Empty;
        Assert.Contains("W11_21H2", d.ValidMinOS);
        Assert.Contains("W11_22H2", d.ValidMinOS);
    }

    [Fact]
    public void Empty_ValidDeployPurposes_ContainsRequiredAndAvailable()
    {
        var d = ModuleDefaults.Empty;
        Assert.Contains("Required",  d.ValidDeployPurposes);
        Assert.Contains("Available", d.ValidDeployPurposes);
    }
}
