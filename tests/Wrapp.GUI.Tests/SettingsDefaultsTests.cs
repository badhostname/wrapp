using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// The defaults POCOs must produce a VALID configuration, not a blank one.
/// AddPackage (Intune/SCCMViewModel) reads them directly, so type defaults
/// meant every new Intune package started with
/// MaximumInstallationTimeInMinutes = 0 (a validation error) and every SCCM
/// package with 0-minute runtimes (silent - SCCM has no runtime validator).
/// </summary>
public class SettingsDefaultsTests
{
    [Fact]
    public void FactorySettings_AreValidNotBlank()
    {
        var s = new AppSettings();

        Assert.Equal(ModuleDefaultsSeed.IntuneMaximumInstallationTimeInMinutes,
            s.IntunePackageDefaults.MaximumInstallationTimeInMinutes);
        Assert.InRange(s.IntunePackageDefaults.MaximumInstallationTimeInMinutes, 1, 1440);
        Assert.NotEmpty(s.IntunePackageDefaults.Architecture);
        Assert.NotEmpty(s.IntunePackageDefaults.InstallExperience);

        Assert.True(s.SccmPackageDefaults.EstimatedRuntimeMins > 0);
        Assert.True(s.SccmPackageDefaults.MaximumAllowedRuntimeMins > 0);
        Assert.NotEmpty(s.SccmPackageDefaults.InstallationBehaviorType);

        Assert.NotEmpty(s.IntuneAssignmentDefaults.Intent);
        Assert.NotEmpty(s.SccmDeploymentDefaults.DeployAction);
    }

    /// <summary>A package built from factory defaults must not be born invalid.</summary>
    [Fact]
    public void PackageBuiltFromFactoryDefaults_HasNoMaxInstallTimeError()
    {
        var defaults = new AppSettings().IntunePackageDefaults;
        var pkg = new IntunePackageEntry
        {
            AppName = "Demo",
            InstallCommand = "install.cmd",
            UninstallCommand = "uninstall.cmd",
            MaximumInstallationTimeInMinutes = defaults.MaximumInstallationTimeInMinutes,
        };

        Assert.False(pkg.IsMaxInstallTimeOutOfRange);
        Assert.Equal(0, pkg.ErrorCount);
    }

    // ---- repair of profiles written before the initializers existed ----

    private static AppSettings ZeroedProfile() => new()
    {
        IntunePackageDefaults = new IntunePackageDefaults
        {
            Architecture = "", MinimumSupportedWindowsRelease = "", InstallExperience = "",
            RestartBehavior = "", AzCopyWindowStyle = "", MaximumInstallationTimeInMinutes = 0,
        },
        SccmPackageDefaults = new SccmPackageDefaults
        {
            InstallationBehaviorType = "", LogonRequirementType = "", UserInteractionMode = "",
            RebootBehavior = "", SlowNetworkDeploymentMode = "",
            EstimatedRuntimeMins = 0, MaximumAllowedRuntimeMins = 0,
        },
    };

    [Fact]
    public void Repair_RestoresUnsetValues()
    {
        var s = ZeroedProfile();
        Assert.True(SettingsRepair.Apply(s));

        Assert.Equal(60, s.IntunePackageDefaults.MaximumInstallationTimeInMinutes);
        Assert.Equal("x64", s.IntunePackageDefaults.Architecture);
        Assert.Equal(15, s.SccmPackageDefaults.EstimatedRuntimeMins);
        Assert.Equal(120, s.SccmPackageDefaults.MaximumAllowedRuntimeMins);
    }

    [Fact]
    public void Repair_LeavesLegitimateEmptyValuesAlone()
    {
        var s = new AppSettings();
        s.IntuneAssignmentDefaults.DeadlineTime = "";
        s.IntuneAssignmentDefaults.FilterMode = "";
        s.SccmDeploymentDefaults.DeadlineDateTime = "";

        SettingsRepair.Apply(s);

        // "no deadline" / "no filter" are real choices, not unset values.
        Assert.Equal("", s.IntuneAssignmentDefaults.DeadlineTime);
        Assert.Equal("", s.IntuneAssignmentDefaults.FilterMode);
        Assert.Equal("", s.SccmDeploymentDefaults.DeadlineDateTime);
    }

    [Fact]
    public void Repair_IsIdempotentOnAFactoryProfile()
    {
        var s = new AppSettings();
        Assert.False(SettingsRepair.Apply(s));
    }

    /// <summary>
    /// Regression guard for the migration hazard: OrgDefaultsSeeder only applies
    /// a block while it equals factory. A profile written with zeros would stop
    /// matching once the POCOs gained initializers - repair restores the
    /// equality, so org provisioning still reaches those profiles.
    /// </summary>
    [Fact]
    public void RepairedProfile_StillReceivesOrgDefaults()
    {
        var s = ZeroedProfile();
        SettingsRepair.Apply(s);

        var org = new OrgDefaults
        {
            IntunePackageDefaults = new IntunePackageDefaults
            {
                Architecture = "arm64",
                MaximumInstallationTimeInMinutes = 90,
            },
        };

        Assert.True(OrgDefaultsSeeder.Apply(s, org));
        Assert.Equal(90, s.IntunePackageDefaults.MaximumInstallationTimeInMinutes);
        Assert.Equal("arm64", s.IntunePackageDefaults.Architecture);
    }
}
