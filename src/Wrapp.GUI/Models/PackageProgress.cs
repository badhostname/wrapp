using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wrapp.Models;

/// <summary>
/// Tracks per-package progress through the orchestrator phases.
/// One instance per package entry in the config.
/// </summary>
public partial class PackageProgress : ObservableObject
{
    [ObservableProperty] private string _packageName = string.Empty;
    [ObservableProperty] private string _target = string.Empty;
    [ObservableProperty] private PackageOutcome _outcome = PackageOutcome.Pending;
    [ObservableProperty] private string _currentStepName = string.Empty;
    [ObservableProperty] private string _detailText = string.Empty;
    [ObservableProperty] private string _failureReason = string.Empty;
    [ObservableProperty] private int _completedSteps;
    [ObservableProperty] private int _totalSteps;
    [ObservableProperty] private double _progressPercent;

    /// <summary>
    /// Sub-step percentage within the current step (0-100).
    /// Used to interpolate the progress bar during long operations like upload.
    /// </summary>
    [ObservableProperty] private int _subStepPercent;

    public ObservableCollection<StepStatus> Steps { get; } = new();
}

public partial class StepStatus : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private StepState _state = StepState.Pending;
    [ObservableProperty] private string _errorDetail = string.Empty;
}

public enum StepState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped
}

public enum PackageOutcome
{
    Pending,
    Running,
    Succeeded,
    PartialSuccess,
    Failed,
    Skipped,
    Cancelled
}
