using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Media;
using Wrapp.Models;
using Wrapp.ViewModels;

namespace Wrapp.Tests;

/// <summary>
/// Disabled packages are silent on the error side too: ErrorCount is 0 while
/// IsEnabled is false (same rule WarningCount always had), and duplicate-name
/// detection only considers enabled packages — a disabled package neither
/// claims a name nor gets flagged itself.
/// </summary>
public class DisabledPackageErrorCountTests
{
    // ------------------------------------------------------------------
    // Entry-level: ErrorCount gating
    // ------------------------------------------------------------------

    [Fact]
    public void IntuneErrorCount_SilentWhileDisabled_ReturnsOnEnable()
    {
        var pkg = new IntunePackageEntry();          // empty fields → errors
        Assert.True(pkg.ErrorCount > 0);

        pkg.IsEnabled = false;
        Assert.Equal(0, pkg.ErrorCount);

        pkg.IsEnabled = true;
        Assert.True(pkg.ErrorCount > 0);             // nothing was fixed
    }

    [Fact]
    public void SccmErrorCount_SilentWhileDisabled()
    {
        var pkg = new SCCMPackageEntry();
        Assert.True(pkg.ErrorCount > 0);
        pkg.IsEnabled = false;
        Assert.Equal(0, pkg.ErrorCount);
    }

    [Fact]
    public void IsEnabledToggle_RaisesErrorCountChange()
    {
        var pkg = new IntunePackageEntry();
        var raised = false;
        pkg.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IntunePackageEntry.ErrorCount)) raised = true;
        };
        pkg.IsEnabled = false;
        Assert.True(raised);
    }

    // ------------------------------------------------------------------
    // Duplicate detection: enabled packages only
    // ------------------------------------------------------------------

    private sealed class TestVm : PackageViewModelBase
    {
        public readonly ObservableCollection<IntunePackageEntry> Packages = new();
        public int ValidationChangedCalls;

        protected override string PlatformLabel => "Test";
        protected override IList<IPackageEntry> GetPackageEntries()
            => Packages.Cast<IPackageEntry>().ToList();
        protected override INotifyCollectionChanged GetPackageCollection() => Packages;
        protected override IPackageEntry? GetSelectedEntry() => null;
        protected override void OnValidationChanged() => ValidationChangedCalls++;
        protected override IEnumerable<ITargetedChild> GetChildTargets(IPackageEntry package)
            => package is IntunePackageEntry p ? p.Assignments : Enumerable.Empty<ITargetedChild>();
        protected override GeneralViewModel GeneralVm => null!;
        protected override ImageSource? SelectedPackageIconSourceProp { get; set; }
        protected override bool IconPathMissingProp { get; set; }
        protected override string IconPathMissingTooltipProp { get; set; } = string.Empty;
    }

    [Fact]
    public void DuplicateName_WithDisabledTwin_FlagsNeither()
    {
        var vm = new TestVm();
        var active   = new IntunePackageEntry { AppName = "7-Zip" };
        var disabled = new IntunePackageEntry { AppName = "7-Zip", IsEnabled = false };
        vm.Packages.Add(active);
        vm.Packages.Add(disabled);

        vm.ValidatePackageNames();

        // The disabled twin never reaches the endpoint — no collision exists.
        Assert.False(active.HasDuplicateName);
        Assert.False(disabled.HasDuplicateName);
    }

    [Fact]
    public void DuplicateName_ReappearsWhenTwinIsReEnabled()
    {
        var vm = new TestVm();
        var a = new IntunePackageEntry { AppName = "7-Zip" };
        var b = new IntunePackageEntry { AppName = "7-Zip", IsEnabled = false };
        vm.Packages.Add(a);
        vm.Packages.Add(b);
        vm.WirePackageNameEvents();
        vm.ValidatePackageNames();
        Assert.False(a.HasDuplicateName);

        b.IsEnabled = true;                          // toggle re-validates via wiring

        Assert.True(a.HasDuplicateName);
        Assert.True(b.HasDuplicateName);
        Assert.True(a.ErrorCount > 0);
    }

    [Fact]
    public void TwoEnabledDuplicates_StillBothFlagged()
    {
        var vm = new TestVm();
        var a = new IntunePackageEntry { AppName = "7-Zip" };
        var b = new IntunePackageEntry { AppName = "7-zip" };   // case-insensitive
        vm.Packages.Add(a);
        vm.Packages.Add(b);

        vm.ValidatePackageNames();

        Assert.True(a.HasDuplicateName);
        Assert.True(b.HasDuplicateName);
    }
}
