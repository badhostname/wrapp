using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Wrapp.Services;

namespace Wrapp.Models;

/// <summary>
/// Represents a single background operation tracked by BackgroundJobTracker.
/// Each job has its own status, progress, cancellation token, and elapsed timer.
/// ViewModels create jobs via the tracker and pass the reporters into service methods.
/// </summary>
public partial class BackgroundJob : ObservableObject
{
    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Short description shown in the status bar (e.g. "Downloading 1Password.intunewin").</summary>
    public string Title { get; init; } = "";

    /// <summary>
    /// Bundle this job belongs to (its BundleRootDir, usually from GeneralViewModel).
    /// Empty for app-wide jobs (inventory fetch, cross-bundle operations). Used by
    /// the job-history pop-up to scope visible jobs to the active bundle.
    /// </summary>
    public string BundleRootDir { get; init; } = "";

    /// <summary>
    /// Optional structured context (a typed POCO like <c>PackagingRunContext</c>,
    /// <c>JobStepTree</c>, or the general-purpose <c>JobDetails</c>). Rendered
    /// in the pop-up's expanded card by a type-specific renderer. Observable —
    /// jobs may attach details AFTER creation as they learn things
    /// (<see cref="JobHandle.SetDetail"/>), and the card re-renders.
    /// </summary>
    [ObservableProperty] private object? _context;

    /// <summary>
    /// Optional structured summary stamped on Complete. Lets a completed card show
    /// "3 packages uploaded, 2 skipped, 1 failed" kind of detail instead of just "done".
    /// </summary>
    [ObservableProperty] private object? _resultSummary;

    /// <summary>Current status message (e.g. "Fetching details..." or "3/12 keys tested").</summary>
    [ObservableProperty] private string _status = "";

    /// <summary>Progress percentage (0-100). -1 = indeterminate.</summary>
    [ObservableProperty] private int _progress = -1;

    [ObservableProperty] private bool _isCompleted;
    [ObservableProperty] private bool _isFaulted;

    /// <summary>Set when the job reaches a terminal state (Complete or Fail).</summary>
    [ObservableProperty] private DateTimeOffset? _completedAt;

    /// <summary>Cancellation source owned by this job. Cancel via Cts.Cancel().</summary>
    public CancellationTokenSource Cts { get; } = new();

    /// <summary>Shortcut to Cts.Token for passing into async methods.</summary>
    public CancellationToken Token => Cts.Token;

    /// <summary>Pass this into service methods that accept IProgress&lt;string&gt;.</summary>
    public IProgress<string> StatusReporter { get; }

    /// <summary>Pass this into service methods that accept IProgress&lt;int&gt;.</summary>
    public IProgress<int> ProgressReporter { get; }

    /// <summary>Stopwatch started at job creation, stopped on completion.</summary>
    public Stopwatch Timer { get; } = Stopwatch.StartNew();

    /// <summary>Elapsed time as a human-readable string (e.g. "2.3s", "1m 15s").</summary>
    public string ElapsedDisplay
    {
        get
        {
            var ts = Timer.Elapsed;
            if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
            return $"{ts.TotalSeconds:F1}s";
        }
    }

    /// <summary>Notifies that ElapsedDisplay has changed (call from a timer tick).</summary>
    public void RefreshElapsed() => OnPropertyChanged(nameof(ElapsedDisplay));

    public BackgroundJob()
    {
        StatusReporter   = UiProgress.ForStatus(s => Status = s);
        ProgressReporter = UiProgress.ForProgress(p => Progress = p);
    }
}
