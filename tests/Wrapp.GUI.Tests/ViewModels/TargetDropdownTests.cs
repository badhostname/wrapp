using Wrapp.Models;
using Wrapp.Services;
using Wrapp.ViewModels;

namespace Wrapp.Tests;

/// <summary>
/// The Intune and SCCM package target dropdowns follow the bundle's tenant
/// and site lists live. They used to rebuild only when a different package
/// was selected, so a tenant removed from the bundle (or dropped by a sync
/// from Preferences) stayed offered on the package that was open.
/// </summary>
public class TargetDropdownTests
{
    private static void Sta(Action body)
    {
        Exception? failure = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { failure = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    private static (GeneralViewModel General, IntuneViewModel Intune, SCCMViewModel Sccm) Build()
    {
        var settings = new AppSettings();
        var general = new GeneralViewModel(settings);
        var main = new MainViewModel(new PowerShellService(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WrappTests_NoModule", "Wrapp.Packager.psd1")), new BackgroundJobTracker());
        var tenants = new TenantsViewModel(general, main);
        return (general, new IntuneViewModel(general, main, tenants, settings), new SCCMViewModel(general, main, tenants, settings));
    }

    [Fact]
    public void The_tenant_dropdown_follows_adds_removes_and_renames_while_a_package_stays_selected()
    {
        Sta(() =>
        {
            var (general, intune, _) = Build();
            var tenants = general.FullConfig.IntuneTenants;
            tenants.Add(new IntuneTenantEntry { Key = TenantA, Name = "Production" });
            var pkg = new IntunePackageEntry { AppName = "Snagit", TenantId = TenantA };
            general.FullConfig.Script.IntunePackager.Packages.Add(pkg);
            intune.SelectedPackage = pkg;
            Assert.Equal(new[] { "Production" }, intune.AvailableTenants.Select(t => t.DisplayName));

            tenants.Add(new IntuneTenantEntry { Key = TenantB, Name = "Test" });
            Assert.Equal(new[] { "Production", "Test" }, intune.AvailableTenants.Select(t => t.DisplayName));

            tenants[1].Name = "Pilot";
            Assert.Contains(intune.AvailableTenants, t => t.Key == TenantB && t.DisplayName == "Pilot");

            tenants.RemoveAt(0);
            Assert.Equal(new[] { TenantB }, intune.AvailableTenants.Select(t => t.Key));

            // A sync from Preferences clears and refills the bundle's list.
            tenants.Clear();
            Assert.Empty(intune.AvailableTenants);
        });
    }

    [Fact]
    public void The_site_dropdown_follows_the_bundles_sites_while_a_package_stays_selected()
    {
        Sta(() =>
        {
            var (general, _, sccm) = Build();
            var sites = general.FullConfig.SccmSites;
            sites.Add(new SCCMSiteEntry { Key = "CB1", Comment = "Primary" });
            var pkg = new SCCMPackageEntry { AppName = "Snagit", SiteCode = "CB1" };
            general.FullConfig.Script.SCCMPackager.Packages.Add(pkg);
            sccm.SelectedPackage = pkg;
            Assert.Equal(new[] { "CB1 - Primary" }, sccm.AvailableSites.Select(s => s.DisplayName));

            sites.Add(new SCCMSiteEntry { Key = "CB2" });
            sites[0].Comment = "Main";
            Assert.Equal(new[] { "CB1 - Main", "CB2" }, sccm.AvailableSites.Select(s => s.DisplayName));

            sites.RemoveAt(0);
            Assert.Equal(new[] { "CB2" }, sccm.AvailableSites.Select(s => s.Key));
        });
    }
}
