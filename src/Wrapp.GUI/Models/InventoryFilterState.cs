using CommunityToolkit.Mvvm.ComponentModel;

namespace Wrapp.Models;

/// <summary>
/// Multi-criteria filter state for the Inventory view.
/// All changes fire PropertyChanged so the ViewModel can re-apply filters.
/// </summary>
public partial class InventoryFilterState : ObservableObject
{
    // -----------------------------------------------------------------------
    // Assignment filters
    // -----------------------------------------------------------------------

    [ObservableProperty] private bool _intentRequired;
    [ObservableProperty] private bool _intentAvailable;
    [ObservableProperty] private bool _intentUninstall;

    [ObservableProperty] private bool _hasAssignments;
    [ObservableProperty] private bool _noAssignments;

    // -----------------------------------------------------------------------
    // Requirements filters
    // -----------------------------------------------------------------------

    [ObservableProperty] private bool _archX64;
    [ObservableProperty] private bool _archX86;
    [ObservableProperty] private bool _archArm;

    [ObservableProperty] private string _minOSFilter = "";

    // -----------------------------------------------------------------------
    // Content filters
    // -----------------------------------------------------------------------

    /// <summary>Minimum .intunewin size in MB (0 = no filter).</summary>
    [ObservableProperty] private double _minSizeMB;

    /// <summary>Maximum .intunewin size in MB (0 = no filter).</summary>
    [ObservableProperty] private double _maxSizeMB;

    // -----------------------------------------------------------------------
    // Relationship filters
    // -----------------------------------------------------------------------

    [ObservableProperty] private bool _hasDependencies;
    [ObservableProperty] private bool _hasSupersedence;

    /// <summary>True when any filter is active (used for badge indicator).</summary>
    public bool IsActive =>
        IntentRequired || IntentAvailable || IntentUninstall
        || HasAssignments || NoAssignments
        || ArchX64 || ArchX86 || ArchArm
        || !string.IsNullOrEmpty(MinOSFilter)
        || MinSizeMB > 0 || MaxSizeMB > 0
        || HasDependencies || HasSupersedence;

    /// <summary>Count of active filter criteria.</summary>
    public int ActiveCount
    {
        get
        {
            int c = 0;
            if (IntentRequired) c++;
            if (IntentAvailable) c++;
            if (IntentUninstall) c++;
            if (HasAssignments) c++;
            if (NoAssignments) c++;
            if (ArchX64) c++;
            if (ArchX86) c++;
            if (ArchArm) c++;
            if (!string.IsNullOrEmpty(MinOSFilter)) c++;
            if (MinSizeMB > 0) c++;
            if (MaxSizeMB > 0) c++;
            if (HasDependencies) c++;
            if (HasSupersedence) c++;
            return c;
        }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not nameof(IsActive) and not nameof(ActiveCount))
        {
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(ActiveCount));
        }
    }

    public void Reset()
    {
        IntentRequired = false;
        IntentAvailable = false;
        IntentUninstall = false;
        HasAssignments = false;
        NoAssignments = false;
        ArchX64 = false;
        ArchX86 = false;
        ArchArm = false;
        MinOSFilter = "";
        MinSizeMB = 0;
        MaxSizeMB = 0;
        HasDependencies = false;
        HasSupersedence = false;
    }
}
