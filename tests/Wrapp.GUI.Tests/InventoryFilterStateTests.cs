using Wrapp.Models;

namespace Wrapp.Tests;

public class InventoryFilterStateTests
{
    [Fact]
    public void IsActive_DefaultFalse()
    {
        var state = new InventoryFilterState();
        Assert.False(state.IsActive);
    }

    [Fact]
    public void IsActive_TrueWhenIntentSet()
    {
        var state = new InventoryFilterState { IntentRequired = true };
        Assert.True(state.IsActive);
    }

    [Fact]
    public void ActiveCount_CountsMultipleFilters()
    {
        var state = new InventoryFilterState
        {
            IntentRequired = true,
            ArchX64 = true,
            HasDependencies = true,
        };
        Assert.Equal(3, state.ActiveCount);
    }

    [Fact]
    public void Reset_ClearsAllFilters()
    {
        var state = new InventoryFilterState
        {
            IntentRequired = true,
            IntentAvailable = true,
            ArchX64 = true,
            MinSizeMB = 100,
            HasDependencies = true,
        };
        state.Reset();
        Assert.False(state.IsActive);
        Assert.Equal(0, state.ActiveCount);
        Assert.Equal(0, state.MinSizeMB);
    }

    [Fact]
    public void IsActive_MinOSFilter_NonEmpty()
    {
        var state = new InventoryFilterState { MinOSFilter = "10.0.19041" };
        Assert.True(state.IsActive);
    }

    [Fact]
    public void IsActive_SizeFilters()
    {
        var state = new InventoryFilterState { MinSizeMB = 10 };
        Assert.True(state.IsActive);
        Assert.Equal(1, state.ActiveCount);

        state.MaxSizeMB = 500;
        Assert.Equal(2, state.ActiveCount);
    }
}
