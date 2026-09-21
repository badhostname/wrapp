using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// The PowerShell preamble every SCCM inventory script starts with. It is a
/// string, so these tests pin the shape that matters: the site drive is built
/// against the SMS Provider the console knows about, never blindly against
/// the local machine, and every way in fails with a message that names the
/// remedy instead of PowerShell's "Cannot find drive".
/// </summary>
public class SccmSitePreambleTests
{
    private static readonly string Preamble = AppInventoryService.SccmSitePreamble("CB1");

    [Fact]
    public void Preamble_ResolvesTheProviderServerBeforeCreatingTheDrive()
    {
        // Console MRU first (per-user), then the server the console installer recorded.
        Assert.Contains(@"HKCU:\Software\Microsoft\ConfigMgr10\AdminUI\MRU", Preamble);
        Assert.Contains(@"HKLM:\SOFTWARE\Microsoft\ConfigMgr10\AdminUI\Connection", Preamble);
        Assert.Contains("New-PSDrive -Name $siteCode -PSProvider CMSite -Root $server -ErrorAction Stop", Preamble);
        Assert.DoesNotContain("-Root '.'", Preamble);
    }

    [Fact]
    public void Preamble_ReusesADriveTheModuleAlreadyCreated()
    {
        var create = Preamble.IndexOf("New-PSDrive", StringComparison.Ordinal);
        var check = Preamble.IndexOf("Get-PSDrive -Name $siteCode -PSProvider CMSite", StringComparison.Ordinal);
        Assert.True(check >= 0 && check < create, "the module's own drive must be checked before a new one is built");
    }

    [Fact]
    public void Preamble_StopsWithARemedyAtEveryFailure()
    {
        Assert.Contains("SMS_ADMIN_UI_PATH is not set", Preamble);
        Assert.Contains("restart Wrapp", Preamble);
        Assert.Contains("ConfigurationManager module not found", Preamble);
        Assert.Contains("Could not connect to ConfigMgr site $siteCode through", Preamble);
        Assert.Contains("Open the Configuration Manager console as this Windows account", Preamble);
        Assert.Contains("Push-Location $siteDrive -ErrorAction Stop", Preamble);
    }

    [Fact]
    public void Preamble_EscapesTheSiteCodeAsAPowerShellLiteral()
    {
        Assert.Contains("$siteCode  = 'CB1'", Preamble);
        Assert.Contains("$siteCode  = 'A''B'", AppInventoryService.SccmSitePreamble("A'B"));
    }

    // ── The Run view's per-site probe ────────────────────────────────

    [Fact]
    public void SiteProbe_ConnectsThroughThePreambleAndAsksTheProviderForThatSite()
    {
        var probe = PowerShellService.SccmSiteProbeScript("CB1");
        Assert.StartsWith(Preamble, probe);
        Assert.Contains("Get-CMSite -SiteCode 'CB1' -ErrorAction Stop", probe);
        Assert.Contains("Connected = [bool]$site", probe);
        Assert.Contains("Pop-Location", probe);
        Assert.Contains("Get-CMSite -SiteCode 'A''B'", PowerShellService.SccmSiteProbeScript("A'B"));
    }

}
