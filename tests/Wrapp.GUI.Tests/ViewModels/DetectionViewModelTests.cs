using Wrapp.Models;
using Wrapp.ViewModels;

namespace Wrapp.Tests;

/// <summary>
/// Workstream P polish: DetectionViewModel exposes ErrorCount (tests whose
/// Symbol collides with another test's) so the Detection nav item can show
/// the same red badge the Intune/SCCM items do. The count must track the
/// existing per-row IsSymbolDuplicate flags exactly.
/// </summary>
public class DetectionViewModelTests
{
    private static DetectionViewModel BuildVm()
        => new(new GeneralViewModel(new AppSettings()));

    [Fact]
    public void FreshTests_HaveUniqueSymbols_NoErrors()
    {
        var vm = BuildVm();
        vm.AddTestCommand.Execute(null); // A
        vm.AddTestCommand.Execute(null); // B
        Assert.Equal(0, vm.ErrorCount);
        Assert.False(vm.HasSymbolWarning);
    }

    [Fact]
    public void DuplicateSymbols_CountEveryCollidingTest()
    {
        var vm = BuildVm();
        vm.AddTestCommand.Execute(null);
        vm.AddTestCommand.Execute(null);
        vm.AddTestCommand.Execute(null);

        // Collide the second test with the first: both count, third stays clean.
        vm.Detect.Tests[1].Symbol = vm.Detect.Tests[0].Symbol;

        Assert.Equal(2, vm.ErrorCount);
        Assert.True(vm.Detect.Tests[0].IsSymbolDuplicate);
        Assert.True(vm.Detect.Tests[1].IsSymbolDuplicate);
        Assert.False(vm.Detect.Tests[2].IsSymbolDuplicate);
        Assert.True(vm.HasSymbolWarning);
    }

    [Fact]
    public void FixingTheCollision_ClearsTheCount_WithChangeNotification()
    {
        var vm = BuildVm();
        vm.AddTestCommand.Execute(null);
        vm.AddTestCommand.Execute(null);

        var raised = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DetectionViewModel.ErrorCount)) raised++;
        };

        vm.Detect.Tests[1].Symbol = vm.Detect.Tests[0].Symbol;
        Assert.Equal(2, vm.ErrorCount);

        vm.Detect.Tests[1].Symbol = "Z";
        Assert.Equal(0, vm.ErrorCount);
        Assert.True(raised >= 2); // once up, once back down - the badge relay rides these
    }

    [Fact]
    public void RemovingCollidingTests_ClearsTheCount()
    {
        var vm = BuildVm();
        vm.AddTestCommand.Execute(null);
        vm.AddTestCommand.Execute(null);
        vm.Detect.Tests[1].Symbol = vm.Detect.Tests[0].Symbol;
        Assert.Equal(2, vm.ErrorCount);

        vm.Detect.Tests[1].IsSelected = true;
        vm.RemoveSelectedTestsCommand.Execute(null);

        Assert.Equal(0, vm.ErrorCount);
    }
}
