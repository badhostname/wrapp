using Wrapp.Models;
using Wrapp.Services;
using Wrapp.ViewModels;

namespace Wrapp.Tests;

/// <summary>
/// Workstream P polish: placeholder rows with duplicate / reserved / invalid
/// names are ERRORS — flagged per-row (<see cref="PlaceholderRowVm.IsDuplicate"/>,
/// the Detection duplicate-symbol pattern), counted into
/// <see cref="PlaceholdersViewModel.ErrorCount"/> (nav + tab badges), and they
/// block the whole Settings save. The block decision is
/// <c>ErrorCount &gt; 0</c> plus <see cref="PlaceholdersViewModel.BuildBlockingErrorMessage"/>,
/// both testable here without UI.
/// </summary>
public class PlaceholdersViewModelTests : IDisposable
{
    public PlaceholdersViewModelTests()
    {
        PlaceholderService.CustomsSource = () => Array.Empty<(string, string?, bool)>();
    }

    public void Dispose()
    {
        PlaceholderService.CustomsSource = () => Array.Empty<(string, string?, bool)>();
    }

    private static PlaceholdersViewModel BuildVm(params PlaceholderEntry[] entries)
    {
        var settings = new AppSettings();
        settings.Placeholders.AddRange(entries);
        return new PlaceholdersViewModel(settings);
    }

    private static PlaceholderRowVm Custom(PlaceholdersViewModel vm, string name)
        => vm.Rows.Single(r => !r.IsBuiltIn && r.Name == name);

    [Fact]
    public void ValidRows_ProduceNoErrors()
    {
        var vm = BuildVm(
            new PlaceholderEntry { Name = "pilotgroup", Value = "gid-1" },
            new PlaceholderEntry { Name = "share-path", Value = @"\\srv\share" });

        Assert.Equal(0, vm.ErrorCount);
        Assert.False(vm.HasNameWarning);
        Assert.All(vm.Rows.Where(r => !r.IsBuiltIn), r => Assert.False(r.IsDuplicate));
    }

    [Fact]
    public void DuplicateNames_FlagEveryOffendingRow_CaseInsensitive()
    {
        var vm = BuildVm(
            new PlaceholderEntry { Name = "grp", Value = "a" },
            new PlaceholderEntry { Name = "GRP", Value = "b" },
            new PlaceholderEntry { Name = "fine", Value = "c" });

        Assert.Equal(2, vm.ErrorCount);
        Assert.True(Custom(vm, "grp").IsDuplicate);
        Assert.True(Custom(vm, "GRP").IsDuplicate);
        Assert.False(Custom(vm, "fine").IsDuplicate);
        Assert.True(vm.HasNameWarning);
        Assert.Contains("duplicated", vm.NameWarning);
    }

    [Fact]
    public void ReservedAndInvalidNames_AreErrors()
    {
        var vm = BuildVm(
            new PlaceholderEntry { Name = "Name", Value = "shadow" },     // reserved
            new PlaceholderEntry { Name = "has space", Value = "bad" },   // invalid chars
            new PlaceholderEntry { Name = "", Value = "anon" });          // empty

        Assert.Equal(3, vm.ErrorCount);
        Assert.Contains("built-in", vm.NameWarning);
        Assert.Contains("not a valid name", vm.NameWarning);
        Assert.Contains("no name", vm.NameWarning);
    }

    [Fact]
    public void ErrorsRecompute_OnNameEdits_WithChangeNotification()
    {
        var vm = BuildVm(
            new PlaceholderEntry { Name = "one", Value = "a" },
            new PlaceholderEntry { Name = "two", Value = "b" });
        Assert.Equal(0, vm.ErrorCount);

        var raised = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaceholdersViewModel.ErrorCount)) raised++;
        };

        var rowA = vm.Rows.Single(r => !r.IsBuiltIn && r.Value == "a");
        var rowB = vm.Rows.Single(r => !r.IsBuiltIn && r.Value == "b");

        // Rename into a collision → both rows flagged, notification raised
        // (the badge relay chain rides these PropertyChanged events).
        rowB.Name = "one";
        Assert.Equal(2, vm.ErrorCount);
        Assert.True(rowA.IsDuplicate);
        Assert.True(rowB.IsDuplicate);
        Assert.True(raised > 0);

        // Fix it again → errors clear.
        rowB.Name = "two";
        Assert.Equal(0, vm.ErrorCount);
        Assert.False(vm.HasNameWarning);
    }

    [Fact]
    public void BuiltInRows_NeverCountAsErrors()
    {
        // A custom row named like a built-in flags ONLY the custom row; the
        // twelve built-in reference rows stay clean.
        var vm = BuildVm(new PlaceholderEntry { Name = "Company", Value = "x" });
        Assert.Equal(1, vm.ErrorCount);
        Assert.All(vm.Rows.Where(r => r.IsBuiltIn), r => Assert.False(r.IsDuplicate));
    }

    [Fact]
    public void AddPlaceholderTwice_IsAnErrorUntilRenamed()
    {
        var vm = BuildVm();
        vm.AddPlaceholderCommand.Execute(null);
        vm.AddPlaceholderCommand.Execute(null); // both are "new-placeholder"

        Assert.Equal(2, vm.ErrorCount);

        vm.Rows.Last(r => !r.IsBuiltIn).Name = "renamed";
        Assert.Equal(0, vm.ErrorCount);
    }

    [Fact]
    public void RemovingOffendingRows_ClearsErrors()
    {
        var vm = BuildVm(
            new PlaceholderEntry { Name = "dup", Value = "a" },
            new PlaceholderEntry { Name = "dup", Value = "b" });
        Assert.Equal(2, vm.ErrorCount);

        var second = vm.Rows.Last(r => !r.IsBuiltIn);
        second.IsSelected = true;
        vm.RemoveSelectedPlaceholdersCommand.Execute(null);

        Assert.Equal(0, vm.ErrorCount);
        Assert.False(vm.HasNameWarning);
    }

    [Fact]
    public void BlockingMessage_NamesTheOffenders_AndStatesTheRules()
    {
        var vm = BuildVm(
            new PlaceholderEntry { Name = "dup", Value = "a" },
            new PlaceholderEntry { Name = "dup", Value = "b" },
            new PlaceholderEntry { Name = "Version", Value = "x" });

        var msg = vm.BuildBlockingErrorMessage();
        Assert.Contains("\"dup\" is duplicated", msg);
        Assert.Contains("\"Version\" is a built-in name", msg);
        Assert.Contains("unique", msg);
        Assert.Contains("Settings > Placeholders", msg);
    }
}
