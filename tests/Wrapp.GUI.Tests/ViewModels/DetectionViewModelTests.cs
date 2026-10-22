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

    // ------------------------------------------------------------------
    // Warnings: the amber badge, the header text and the highlighted fields
    // ------------------------------------------------------------------

    [Fact]
    public void A_New_Bundle_Shows_No_Warnings()
    {
        var vm = BuildVm();
        Assert.Equal(0, vm.WarningCount);
        Assert.False(vm.HasExpressionWarning);
        Assert.False(vm.IsTestTableMissing);
    }

    [Fact]
    public void Adding_A_Test_Asks_For_The_Default_Expression_Until_It_Is_Typed()
    {
        var vm = BuildVm();
        vm.AddTestCommand.Execute(null); // A

        Assert.Equal(1, vm.WarningCount);
        Assert.True(vm.IsDefaultExpressionMissing);
        Assert.True(vm.HasExpressionWarning);
        Assert.Equal("An expression must be entered", vm.ExpressionWarning);

        vm.Detect.Expression_Default = "A";

        Assert.Equal(0, vm.WarningCount);
        Assert.False(vm.IsDefaultExpressionMissing);
        Assert.False(vm.HasExpressionWarning);
    }

    [Fact]
    public void A_Blank_Named_Expression_Is_Highlighted_On_Its_Own_Row()
    {
        var vm = BuildVm();
        vm.AddTestCommand.Execute(null);
        vm.Detect.Expression_Default = "A";
        vm.AddExpressionCommand.Execute(null);

        var row = Assert.Single(vm.Detect.Expressions);
        Assert.True(row.IsExpressionMissing);
        Assert.False(vm.IsDefaultExpressionMissing);
        Assert.Equal(1, vm.WarningCount);

        row.Expression = "A";
        Assert.False(row.IsExpressionMissing);
        Assert.Equal(0, vm.WarningCount);
    }

    [Fact]
    public void An_Expression_Without_Tests_Highlights_The_Table()
    {
        var vm = BuildVm();
        vm.Detect.Expression_Default = "A -and B";

        Assert.True(vm.IsTestTableMissing);
        Assert.Equal(1, vm.WarningCount);

        vm.AddTestCommand.Execute(null); // A
        vm.AddTestCommand.Execute(null); // B
        Assert.False(vm.IsTestTableMissing);
        Assert.Equal(0, vm.WarningCount);
    }

    [Fact]
    public void A_Symbol_No_Expression_Uses_Is_Named_And_Highlighted()
    {
        var vm = BuildVm();
        vm.AddTestCommand.Execute(null); // A
        vm.AddTestCommand.Execute(null); // B
        vm.Detect.Expression_Default = "A";

        Assert.True(vm.Detect.Tests[1].IsSymbolUnused);
        Assert.False(vm.Detect.Tests[0].IsSymbolUnused);
        Assert.True(vm.HasSymbolUnusedWarning);
        Assert.Equal("Not used in any expression: B", vm.SymbolUnusedWarning);
        Assert.Equal(1, vm.WarningCount);

        vm.Detect.Expression_Default = "A -or B";
        Assert.False(vm.Detect.Tests[1].IsSymbolUnused);
        Assert.False(vm.HasSymbolUnusedWarning);
    }

    [Fact]
    public void A_Test_Without_A_Symbol_Highlights_Its_Symbol_Cell()
    {
        var vm = BuildVm();
        vm.AddTestCommand.Execute(null); // A
        vm.Detect.Expression_Default = "A";

        vm.Detect.Tests[0].Symbol = "";

        Assert.True(vm.Detect.Tests[0].IsSymbolMissing);
        Assert.False(vm.Detect.Tests[0].IsSymbolUnused);
        Assert.Equal("A test needs a symbol", vm.SymbolMissingWarning);
        Assert.Equal(1, vm.WarningCount);
    }

    [Fact]
    public void Warning_Count_Changes_Are_Announced_For_The_Badge()
    {
        var vm = BuildVm();
        var raised = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DetectionViewModel.WarningCount)) raised++;
        };

        vm.AddTestCommand.Execute(null);   // 0 -> 1 (blank Default)
        vm.Detect.Expression_Default = "A"; // 1 -> 0
        Assert.Equal(2, raised);
    }

    [Fact]
    public void The_Path_Lock_Follows_The_Command_Both_Ways()
    {
        // The Path cell's lock outline and icon bind IsPathLockedByCommand; without
        // the change notification they kept whatever state the row was drawn with.
        var test = new DetectionTest();
        var raised = 0;
        test.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DetectionTest.IsPathLockedByCommand)) raised++;
        };

        test.Command = "Get-Service Spooler";
        Assert.True(test.IsPathLockedByCommand);
        test.Command = "";
        Assert.False(test.IsPathLockedByCommand);
        test.IsPathLocked = true;

        Assert.Equal(3, raised);
    }

    [Fact]
    public void A_Replaced_Test_Collection_Is_Followed()
    {
        var vm = BuildVm();
        vm.Detect.Expression_Default = "A";
        Assert.True(vm.IsTestTableMissing);

        vm.Detect.Tests = new System.Collections.ObjectModel.ObservableCollection<DetectionTest>();
        vm.AddTestCommand.Execute(null); // lands in the new collection

        Assert.False(vm.IsTestTableMissing);
        Assert.Equal(0, vm.WarningCount);
    }
}
