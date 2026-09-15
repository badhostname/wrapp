using Wrapp.Models;

namespace Wrapp.Tests;

/// <summary>
/// The enable flag (persistent operator intent) and the warning count
/// (non-blocking issues) are deliberately separate concepts: a package can be
/// complete but disabled, or enabled but not yet targeted. These tests pin the
/// rules that keep the amber badge meaningful.
/// </summary>
public class PackageEnableWarningTests
{
    private static IntunePackageEntry ValidIntune() => new()
    {
        AppName = "Demo",
        InstallCommand = "install.cmd",
        UninstallCommand = "uninstall.cmd",
        MaximumInstallationTimeInMinutes = 60,
    };

    [Fact]
    public void Packages_AreEnabledByDefault()
    {
        Assert.True(new IntunePackageEntry().IsEnabled);
        Assert.True(new SCCMPackageEntry().IsEnabled);
    }

    [Fact]
    public void UntargetedButValidPackage_RaisesOneWarning_NotAnError()
    {
        var pkg = ValidIntune();          // TenantId left empty
        Assert.Equal(0, pkg.ErrorCount);
        Assert.Equal(1, pkg.WarningCount);
    }

    [Fact]
    public void IncompletePackage_ReportsErrorsAndTargetingWarningTogether()
    {
        // Both badges report at once: an outstanding targeting warning must
        // stay visible while validation errors exist (previously the warning
        // was suppressed, so the amber badge vanished when the red appeared).
        var pkg = new IntunePackageEntry { AppName = "", MaximumInstallationTimeInMinutes = 60 };
        Assert.True(pkg.ErrorCount > 0);
        Assert.Equal(1, pkg.WarningCount);
    }

    [Fact]
    public void FixingAnError_LeavesTheTargetingWarningStanding()
    {
        var pkg = new IntunePackageEntry
        {
            AppName = "", InstallCommand = "i", UninstallCommand = "u",
            MaximumInstallationTimeInMinutes = 60,
        };
        Assert.True(pkg.ErrorCount > 0);
        Assert.Equal(1, pkg.WarningCount);

        pkg.AppName = "Now valid";
        Assert.Equal(0, pkg.ErrorCount);
        Assert.Equal(1, pkg.WarningCount);   // still untargeted
    }

    [Fact]
    public void DisabledPackage_IsSilent()
    {
        var pkg = ValidIntune();
        Assert.Equal(1, pkg.WarningCount);   // untargeted while enabled

        pkg.IsEnabled = false;
        Assert.Equal(0, pkg.WarningCount);   // you already said you don't want it
    }

    [Fact]
    public void TargetedPackage_RaisesNoWarning()
    {
        var pkg = ValidIntune();
        pkg.TenantId = "00000000-0000-0000-0000-000000000001";
        Assert.Equal(0, pkg.WarningCount);
    }

    [Fact]
    public void InvalidUrls_WarnWithoutBlocking()
    {
        var pkg = ValidIntune();
        pkg.TenantId = "00000000-0000-0000-0000-000000000001";
        pkg.InformationURL = "not-a-url";
        Assert.Equal(0, pkg.ErrorCount);
        Assert.Equal(1, pkg.WarningCount);
    }

    [Fact]
    public void SccmPackage_WarnsWhenValidButUntargeted()
    {
        var pkg = new SCCMPackageEntry
        {
            AppName = "Demo",
            Name = "Demo - Script",
            InstallCommand = "install.cmd",
        };
        Assert.Equal(0, pkg.ErrorCount);
        Assert.Equal(1, pkg.WarningCount);

        pkg.SiteCode = "ABC";
        Assert.Equal(0, pkg.WarningCount);
    }

    [Fact]
    public void WarningCount_RaisesChangeNotification()
    {
        // The nav badge only updates if the computed property notifies.
        var pkg = ValidIntune();
        var raised = false;
        pkg.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IntunePackageEntry.WarningCount)) raised = true;
        };
        pkg.TenantId = "00000000-0000-0000-0000-000000000001";
        Assert.True(raised);
    }
}
