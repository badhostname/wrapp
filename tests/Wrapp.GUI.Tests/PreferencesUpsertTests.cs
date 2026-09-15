using Wrapp.Models;
using Wrapp.ViewModels;

namespace Wrapp.Tests;

/// <summary>
/// Bundle → preferences sync. The bundle's tenant list (Config.json) and the
/// technician's saved tenants (settings.json) are separate by design, so the
/// bridge must be non-destructive: add what's missing, complete what's blank,
/// overwrite nothing.
/// </summary>
public class PreferencesUpsertTests
{
    private static PreferencesViewModel Vm() => new(new AppSettings());

    [Fact]
    public void UpsertTenant_AddsAnUnknownKey()
    {
        var vm = Vm();
        var before = vm.IntuneTenants.Count;

        Assert.True(vm.UpsertTenant(new IntuneTenantEntry
        {
            Key = "11111111-1111-1111-1111-111111111111",
            Name = "Contoso Prod",
        }));

        Assert.Equal(before + 1, vm.IntuneTenants.Count);
        Assert.Contains(vm.IntuneTenants, t => t.Name == "Contoso Prod");
    }

    [Fact]
    public void UpsertTenant_NeverOverwritesAValueTheTechnicianSet()
    {
        var vm = Vm();
        vm.IntuneTenants.Clear();
        vm.IntuneTenants.Add(new IntuneTenantEntry
        {
            Key = "11111111-1111-1111-1111-111111111111",
            Name = "My name for it",
            Comment = "",
        });

        var changed = vm.UpsertTenant(new IntuneTenantEntry
        {
            Key = "11111111-1111-1111-1111-111111111111",
            Name = "Discovered name",
            Comment = "from sign-in",
        });

        var entry = vm.IntuneTenants.Single();
        Assert.True(changed);                       // the blank comment was filled
        Assert.Equal("My name for it", entry.Name); // ...but the name was kept
        Assert.Equal("from sign-in", entry.Comment);
    }

    [Fact]
    public void UpsertTenant_MatchesKeyCaseInsensitively_AndReportsNoChange()
    {
        var vm = Vm();
        vm.IntuneTenants.Clear();
        vm.IntuneTenants.Add(new IntuneTenantEntry { Key = "ABCDEF00-0000-0000-0000-000000000000", Name = "T" });

        Assert.False(vm.UpsertTenant(new IntuneTenantEntry
        {
            Key = "abcdef00-0000-0000-0000-000000000000",
            Name = "T",
        }));
        Assert.Single(vm.IntuneTenants);
    }

    [Fact]
    public void UpsertTenant_IgnoresAKeylessEntry()
    {
        var vm = Vm();
        Assert.False(vm.UpsertTenant(new IntuneTenantEntry { Key = "", Name = "nameless" }));
    }

    [Fact]
    public void UpsertSite_AddsAndMergesDeploymentGroups()
    {
        var vm = Vm();
        vm.SCCMSites.Clear();

        var incoming = new SCCMSiteEntry { Key = "ABC", Comment = "Prod" };
        incoming.DeploymentGroups.Add("Group A");
        Assert.True(vm.UpsertSite(incoming));

        var more = new SCCMSiteEntry { Key = "abc" };
        more.DeploymentGroups.Add("Group A");   // already there
        more.DeploymentGroups.Add("Group B");   // new
        Assert.True(vm.UpsertSite(more));

        var site = vm.SCCMSites.Single();
        Assert.Equal("Prod", site.Comment);
        Assert.Equal(new[] { "Group A", "Group B" }, site.DeploymentGroups);
    }
}
