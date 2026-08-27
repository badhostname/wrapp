using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;

namespace Wrapp.Models;

/// <summary>
/// Shared interface for IntunePackageEntry and SCCMPackageEntry.
/// Enables generic handling in PackageViewModelBase for icon management,
/// name validation, dependency/supersedence CRUD, and error counting.
/// </summary>
public interface IPackageEntry : INotifyPropertyChanged
{
    /// <summary>
    /// PropertyChanged name a package raises when a child's TARGETING changed
    /// (assignment GroupID/Type, deployment Collection, membership). The view
    /// model layer listens for it to re-run duplicate-target validation across
    /// the bundle — counts alone can't signal "same group targeted twice".
    /// </summary>
    const string ChildTargetsProperty = "ChildTargets";

    /// <summary>Stable GUID for linking assignments/deployments to this package.</summary>
    string PackageId { get; set; }

    string AppName { get; set; }

    /// <summary>Operator intent: false excludes the package from runs and
    /// silences its error/warning badges (only the disabled state shows).</summary>
    bool IsEnabled { get; set; }

    bool HasDuplicateName { get; set; }
    ImageSource? ListIconSource { get; set; }
    int ErrorCount { get; }
    int WarningCount { get; }

    /// <summary>The icon path property (IconFile for Intune, Icon for SCCM).</summary>
    string IconPath { get; set; }

    ObservableCollection<DependencyEntry> Dependencies { get; }
    ObservableCollection<SupersedenceEntry> Supersedence { get; }
}

/// <summary>
/// A package child that targets a group/collection: Intune assignments and
/// SCCM deployments. Lets <c>PackageViewModelBase</c> run one duplicate-target
/// scan over both platforms.
/// </summary>
public interface ITargetedChild : INotifyPropertyChanged
{
    /// <summary>
    /// Normalized key duplicate detection groups by (group ID / collection
    /// name), or null when the child doesn't target anything yet.
    /// </summary>
    string? DuplicateTargetKey { get; }

    /// <summary>Set by the bundle-wide scan: another ENABLED package's child
    /// (or a sibling on the same package) targets the same key.</summary>
    bool HasDuplicateTarget { get; set; }

    int ErrorCount { get; }
    int WarningCount { get; }
}
