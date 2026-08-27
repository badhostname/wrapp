using System.Collections.ObjectModel;
using Wrapp.ViewModels;

namespace Wrapp.Tests;

/// <summary>
/// Tests for <see cref="PackageViewModelBase.SyncTargetItems"/> - the
/// non-destructive tenant/site picker sync. The core guarantee is that it
/// never rebuilds from scratch: existing items keep their instance identity so
/// the target ComboBox's SelectedValue survives, which is what fixes the
/// "target tenant cleared when navigating between packages" bug.
/// </summary>
public class SyncTargetItemsTests
{
    private static (string, string)[] Desired(params string[] keys)
        => keys.Select(k => (k, $"{k}-name")).ToArray();

    [Fact]
    public void EmptyCollection_AddsAllDesired()
    {
        var col = new ObservableCollection<TargetCheckItem>();
        PackageViewModelBase.SyncTargetItems(col, Desired("a", "b"));

        Assert.Equal(new[] { "a", "b" }, col.Select(x => x.Key));
    }

    [Fact]
    public void UnchangedDesired_PreservesExistingInstances()
    {
        var col = new ObservableCollection<TargetCheckItem>();
        PackageViewModelBase.SyncTargetItems(col, Desired("a", "b"));
        var a = col[0];
        var b = col[1];

        // Sync again with the same desired set (the navigation case).
        PackageViewModelBase.SyncTargetItems(col, Desired("a", "b"));

        // Same instances kept -> a bound ComboBox's SelectedValue is not disturbed.
        Assert.Same(a, col[0]);
        Assert.Same(b, col[1]);
        Assert.Equal(2, col.Count);
    }

    [Fact]
    public void RemovedFromDesired_IsRemoved_OthersPreserved()
    {
        var col = new ObservableCollection<TargetCheckItem>();
        PackageViewModelBase.SyncTargetItems(col, Desired("a", "b", "c"));
        var a = col[0];
        var c = col[2];

        PackageViewModelBase.SyncTargetItems(col, Desired("a", "c")); // b removed

        Assert.Equal(new[] { "a", "c" }, col.Select(x => x.Key));
        Assert.Same(a, col.First(x => x.Key == "a"));
        Assert.Same(c, col.First(x => x.Key == "c"));
    }

    [Fact]
    public void AddedToDesired_IsAppended_ExistingPreserved()
    {
        var col = new ObservableCollection<TargetCheckItem>();
        PackageViewModelBase.SyncTargetItems(col, Desired("a"));
        var a = col[0];

        PackageViewModelBase.SyncTargetItems(col, Desired("a", "b"));

        Assert.Equal(new[] { "a", "b" }, col.Select(x => x.Key));
        Assert.Same(a, col[0]);
    }

    [Fact]
    public void ChangedDisplayName_UpdatedInPlace_SameInstance()
    {
        var col = new ObservableCollection<TargetCheckItem>();
        PackageViewModelBase.SyncTargetItems(col, new[] { ("a", "old") });
        var a = col[0];

        PackageViewModelBase.SyncTargetItems(col, new[] { ("a", "new") });

        Assert.Same(a, col[0]);               // instance preserved (selection survives)
        Assert.Equal("new", col[0].DisplayName); // display refreshed in place
    }

    [Fact]
    public void KeyMatch_IsCaseInsensitive()
    {
        var col = new ObservableCollection<TargetCheckItem>();
        PackageViewModelBase.SyncTargetItems(col, new[] { ("ABC", "x") });
        var item = col[0];

        // Same key, different case, different display - should update in place,
        // not duplicate (tenant GUIDs are compared case-insensitively).
        PackageViewModelBase.SyncTargetItems(col, new[] { ("abc", "y") });

        Assert.Single(col);
        Assert.Same(item, col[0]);
    }
}
