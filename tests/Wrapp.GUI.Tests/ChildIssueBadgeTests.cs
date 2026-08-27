using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Media;
using Wrapp.Models;
using Wrapp.ViewModels;

namespace Wrapp.Tests;

/// <summary>
/// The assignment/deployment warning-error split and its end-to-end badge
/// pipeline: children carry WarningCount (incomplete or duplicate targeting)
/// separate from ErrorCount; packages aggregate child issues live (no dialog
/// close required); the bundle-wide duplicate-target scan flags twins only
/// while both packages are enabled.
/// </summary>
public class ChildIssueBadgeTests
{
    // ------------------------------------------------------------------
    // Child-level classification
    // ------------------------------------------------------------------

    [Fact]
    public void Assignment_MissingGroup_IsWarningNotError()
    {
        var a = new AssignmentEntry();               // Type=Group, GroupID empty
        Assert.Equal(0, a.ErrorCount);
        Assert.Equal(1, a.WarningCount);             // group id missing
        Assert.True(a.HasValidationWarning);

        a.GroupID = "11111111-1111-1111-1111-111111111111";
        Assert.Equal(0, a.WarningCount);
        Assert.False(a.HasValidationWarning);
    }

    [Fact]
    public void Deployment_MissingCollection_IsWarningNotError()
    {
        var d = new SCCMDeploymentEntry();
        Assert.Equal(0, d.ErrorCount);
        Assert.Equal(1, d.WarningCount);
        d.Collection = "All Workstations";
        Assert.Equal(0, d.WarningCount);
    }

    [Fact]
    public void DuplicateTargetFlag_CountsAsWarning_AndNotifies()
    {
        var a = new AssignmentEntry { GroupID = "g-1" };
        var raised = false;
        a.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AssignmentEntry.WarningCount)) raised = true;
        };
        a.HasDuplicateTarget = true;
        Assert.True(raised);
        Assert.Equal(1, a.WarningCount);
        Assert.True(a.HasValidationWarning);
    }

    // ------------------------------------------------------------------
    // Package-level live aggregation
    // ------------------------------------------------------------------

    [Fact]
    public void Package_AggregatesAssignmentWarnings_Live()
    {
        var pkg = new IntunePackageEntry
        {
            AppName = "7-Zip", InstallCommand = "i", UninstallCommand = "u",
            TenantId = "t", InformationURL = "", PrivacyURL = "",
        };
        Assert.Equal(0, pkg.WarningCount);

        var raised = false;
        pkg.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IntunePackageEntry.WarningCount)) raised = true;
        };

        pkg.Assignments.Add(new AssignmentEntry());  // incomplete → 1 warning
        Assert.True(raised);
        Assert.Equal(1, pkg.WarningCount);

        // Editing the child (as the open dialog does) updates the package
        // immediately — the old pipeline refreshed only on dialog close.
        raised = false;
        pkg.Assignments[0].GroupID = "g-1";
        Assert.True(raised);
        Assert.Equal(0, pkg.WarningCount);
    }

    [Fact]
    public void DisabledPackage_SilencesChildIssuesToo()
    {
        var pkg = new IntunePackageEntry();
        pkg.Assignments.Add(new AssignmentEntry()); // 1 child warning
        Assert.True(pkg.WarningCount > 0);

        pkg.IsEnabled = false;
        Assert.Equal(0, pkg.WarningCount);
        Assert.Equal(0, pkg.ErrorCount);
    }

    [Fact]
    public void SccmPackage_AggregatesDeploymentWarnings()
    {
        var pkg = new SCCMPackageEntry { AppName = "x", Name = "n", InstallCommand = "i", SiteCode = "ABC" };
        pkg.Deployments.Add(new SCCMDeploymentEntry());
        Assert.Equal(1, pkg.WarningCount);
        pkg.Deployments[0].Collection = "All Workstations";
        Assert.Equal(0, pkg.WarningCount);
    }

    // ------------------------------------------------------------------
    // Bundle-wide duplicate-target scan
    // ------------------------------------------------------------------

    private sealed class TestVm : PackageViewModelBase
    {
        public readonly ObservableCollection<IntunePackageEntry> Packages = new();
        protected override string PlatformLabel => "Test";
        protected override IList<IPackageEntry> GetPackageEntries()
            => Packages.Cast<IPackageEntry>().ToList();
        protected override INotifyCollectionChanged GetPackageCollection() => Packages;
        protected override IPackageEntry? GetSelectedEntry() => null;
        protected override void OnValidationChanged() { }
        protected override GeneralViewModel GeneralVm => null!;
        protected override IEnumerable<ITargetedChild> GetChildTargets(IPackageEntry package)
            => package is IntunePackageEntry p ? p.Assignments : Enumerable.Empty<ITargetedChild>();
        protected override ImageSource? SelectedPackageIconSourceProp { get; set; }
        protected override bool IconPathMissingProp { get; set; }
        protected override string IconPathMissingTooltipProp { get; set; } = string.Empty;
    }

    private static IntunePackageEntry PkgWithAssignment(string name, string groupId, bool enabled = true)
    {
        var pkg = new IntunePackageEntry { AppName = name, IsEnabled = enabled };
        pkg.Assignments.Add(new AssignmentEntry { GroupID = groupId });
        return pkg;
    }

    [Fact]
    public void SameGroupAcrossTwoEnabledPackages_FlagsBoth()
    {
        var vm = new TestVm();
        var a = PkgWithAssignment("A", "g-shared");
        var b = PkgWithAssignment("B", "G-SHARED");   // case-insensitive
        vm.Packages.Add(a);
        vm.Packages.Add(b);

        vm.ValidateChildTargets();

        Assert.True(a.Assignments[0].HasDuplicateTarget);
        Assert.True(b.Assignments[0].HasDuplicateTarget);
    }

    [Fact]
    public void SameGroupTwiceInOnePackage_Flags()
    {
        var vm = new TestVm();
        var pkg = PkgWithAssignment("A", "g-1");
        pkg.Assignments.Add(new AssignmentEntry { GroupID = "g-1" });
        vm.Packages.Add(pkg);

        vm.ValidateChildTargets();

        Assert.All(pkg.Assignments, a => Assert.True(a.HasDuplicateTarget));
    }

    [Fact]
    public void DisabledTwin_DoesNotCollide_AndClearsOnScan()
    {
        var vm = new TestVm();
        var active   = PkgWithAssignment("A", "g-shared");
        var disabled = PkgWithAssignment("B", "g-shared", enabled: false);
        // Pre-set a stale flag on the disabled package's child: the scan clears it.
        disabled.Assignments[0].HasDuplicateTarget = true;
        vm.Packages.Add(active);
        vm.Packages.Add(disabled);

        vm.ValidateChildTargets();

        Assert.False(active.Assignments[0].HasDuplicateTarget);
        Assert.False(disabled.Assignments[0].HasDuplicateTarget);
    }

    [Fact]
    public void GroupIdEdit_Rewires_ThroughWiredEvents()
    {
        var vm = new TestVm();
        var a = PkgWithAssignment("A", "g-1");
        var b = PkgWithAssignment("B", "g-2");
        vm.Packages.Add(a);
        vm.Packages.Add(b);
        vm.WirePackageNameEvents();
        vm.ValidatePackageNames();
        Assert.False(a.Assignments[0].HasDuplicateTarget);

        // Editing a GroupID re-runs the scan via the ChildTargets notification.
        b.Assignments[0].GroupID = "g-1";

        Assert.True(a.Assignments[0].HasDuplicateTarget);
        Assert.True(b.Assignments[0].HasDuplicateTarget);
    }
}
