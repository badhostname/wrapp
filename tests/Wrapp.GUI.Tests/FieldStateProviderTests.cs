using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Wrapp.Helpers;
using Wrapp.Models;

namespace Wrapp.Tests;

public partial class FieldStateProviderTests
{
    private partial class TestVm : ObservableObject
    {
        [ObservableProperty] private string _intent = "available";
        [ObservableProperty] private string _type = "AllDevices";
        [ObservableProperty] private bool _enableRestartGracePeriod;
        [ObservableProperty] private string _filterName = string.Empty;
    }

    [Fact]
    public void StringValue_TriggersDisable()
    {
        var vm = new TestVm();
        var provider = new FieldStateProvider();
        var rules = new[]
        {
            new FieldRule("DeadlineTime", "Intent", ["available", "uninstall"], "Only required intent"),
        };

        provider.Bind(vm, rules);

        Assert.False(provider.FieldStates["DeadlineTime"].IsEnabled);
        Assert.Equal("Only required intent", provider.FieldStates["DeadlineTime"].DisabledReason);

        vm.Intent = "required";
        Assert.True(provider.FieldStates["DeadlineTime"].IsEnabled);
        Assert.Null(provider.FieldStates["DeadlineTime"].DisabledReason);
    }

    [Fact]
    public void BoolValue_MatchesViaToString()
    {
        var vm = new TestVm();
        var provider = new FieldStateProvider();
        var rules = new[]
        {
            new FieldRule("RestartGracePeriodInMinutes", "EnableRestartGracePeriod",
                ["False"], "Requires grace period checked"),
        };

        provider.Bind(vm, rules);

        Assert.False(provider.FieldStates["RestartGracePeriodInMinutes"].IsEnabled);

        vm.EnableRestartGracePeriod = true;
        Assert.True(provider.FieldStates["RestartGracePeriodInMinutes"].IsEnabled);
    }

    [Fact]
    public void EmptyString_MatchesEmptySentinel()
    {
        var vm = new TestVm();
        var provider = new FieldStateProvider();
        var rules = new[]
        {
            new FieldRule("FilterMode", "FilterName", [""], "Enter filter name first"),
        };

        provider.Bind(vm, rules);

        Assert.False(provider.FieldStates["FilterMode"].IsEnabled);

        vm.FilterName = "MyFilter";
        Assert.True(provider.FieldStates["FilterMode"].IsEnabled);

        vm.FilterName = "   ";
        Assert.False(provider.FieldStates["FilterMode"].IsEnabled);
    }

    [Fact]
    public void HideWhenDisabled_CollapsesVisibility()
    {
        var vm = new TestVm();
        var provider = new FieldStateProvider();
        var rules = new[]
        {
            new FieldRule("ExistingAppID", "Intent", ["available"], "...",
                HideWhenDisabled: true),
        };

        provider.Bind(vm, rules);

        Assert.False(provider.FieldStates["ExistingAppID"].IsEnabled);
        Assert.False(provider.FieldStates["ExistingAppID"].IsVisible);

        vm.Intent = "required";
        Assert.True(provider.FieldStates["ExistingAppID"].IsVisible);
    }

    [Fact]
    public void RequiredWhenEnabled_FlipsRequired()
    {
        var vm = new TestVm();
        var provider = new FieldStateProvider();
        var rules = new[]
        {
            new FieldRule("ExistingAppID", "Intent", ["available"], "...",
                RequiredWhenEnabled: true),
        };

        provider.Bind(vm, rules);

        Assert.False(provider.FieldStates["ExistingAppID"].IsRequired);

        vm.Intent = "required";
        Assert.True(provider.FieldStates["ExistingAppID"].IsRequired);
    }

    [Fact]
    public void TooltipTemplate_FormatsSourceValue()
    {
        var vm = new TestVm { Type = "AllDevices" };
        var provider = new FieldStateProvider();
        var rules = new[]
        {
            new FieldRule("GroupID", "Type", ["AllDevices", "AllUsers"], "Not required for {0}"),
        };

        provider.Bind(vm, rules);

        Assert.Equal("Not required for AllDevices", provider.FieldStates["GroupID"].DisabledReason);
    }

    [Fact]
    public void MultipleRules_LastTriggeredWinsForTooltip()
    {
        var vm = new TestVm { Intent = "uninstall", FilterName = "x" };
        var provider = new FieldStateProvider();
        // GroupMode rule comes after Intent rule; if both trigger, GroupMode tooltip should win.
        var rules = new[]
        {
            new FieldRule("AvailableTime", "Intent", ["uninstall"], "From Intent"),
            new FieldRule("AvailableTime", "FilterName", ["x"], "From FilterName"),
        };

        provider.Bind(vm, rules);

        Assert.False(provider.FieldStates["AvailableTime"].IsEnabled);
        Assert.Equal("From FilterName", provider.FieldStates["AvailableTime"].DisabledReason);
    }

    [Fact]
    public void Unbind_StopsListening()
    {
        var vm = new TestVm();
        var provider = new FieldStateProvider();
        var rules = new[]
        {
            new FieldRule("DeadlineTime", "Intent", ["available"], "..."),
        };

        provider.Bind(vm, rules);
        Assert.False(provider.FieldStates["DeadlineTime"].IsEnabled);

        provider.Unbind();
        vm.Intent = "required";
        // State stays where it was at unbind time.
        Assert.False(provider.FieldStates["DeadlineTime"].IsEnabled);
    }

    [Fact]
    public void Rebind_SwapsSourceCleanly()
    {
        var vm1 = new TestVm { Intent = "available" };
        var vm2 = new TestVm { Intent = "required" };
        var provider = new FieldStateProvider();
        var rules = new[]
        {
            new FieldRule("DeadlineTime", "Intent", ["available"], "..."),
        };

        provider.Bind(vm1, rules);
        Assert.False(provider.FieldStates["DeadlineTime"].IsEnabled);

        provider.Bind(vm2, rules);
        Assert.True(provider.FieldStates["DeadlineTime"].IsEnabled);

        // Old source no longer drives state.
        vm1.Intent = "required";
        Assert.True(provider.FieldStates["DeadlineTime"].IsEnabled);
    }

    [Fact]
    public void AssignmentEntry_RealRules_Cascade()
    {
        var entry = new AssignmentEntry();
        // Defaults: Intent=available, Type=Group, GroupMode=include
        Assert.True(entry.FieldStates["GroupID"].IsEnabled);
        Assert.True(entry.FieldStates["AvailableTime"].IsEnabled);
        Assert.False(entry.FieldStates["DeadlineTime"].IsEnabled);

        entry.Intent = "required";
        Assert.True(entry.FieldStates["DeadlineTime"].IsEnabled);
        Assert.False(entry.FieldStates["AutoUpdateSupersededApps"].IsEnabled);

        entry.GroupMode = "exclude";
        Assert.False(entry.FieldStates["DeadlineTime"].IsEnabled);
        Assert.False(entry.FieldStates["Notification"].IsEnabled);

        entry.GroupMode = "include";
        entry.EnableRestartGracePeriod = true;
        Assert.True(entry.FieldStates["RestartGracePeriodInMinutes"].IsEnabled);

        entry.EnableRestartGracePeriod = false;
        Assert.False(entry.FieldStates["RestartGracePeriodInMinutes"].IsEnabled);
    }

    [Fact]
    public void AssignmentEntry_FilterNameDrivesFilterMode()
    {
        var entry = new AssignmentEntry { FilterName = string.Empty };
        Assert.False(entry.FieldStates["FilterMode"].IsEnabled);

        entry.FilterName = "MyFilter";
        Assert.True(entry.FieldStates["FilterMode"].IsEnabled);
    }
}
