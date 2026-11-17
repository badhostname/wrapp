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
    /// in the pop-up's expanded card by a type-specific renderer. Observable -
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FinishedAgo))]
    [NotifyPropertyChangedFor(nameof(FinishedAtText))]
    private DateTimeOffset? _completedAt;

    /// <summary>How long ago the job ended ("just now", "5 min ago"), or "" while it runs. The tracker's clock refreshes it.</summary>
    public string FinishedAgo => CompletedAt is { } at ? DurationText.Ago(at, SystemClock.OffsetNow) : "";

    /// <summary>When the job ended, to the second (the hover text of <see cref="FinishedAgo"/>).</summary>
    public string FinishedAtText => CompletedAt is { } at ? "Finished " + at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) : "";

    /// <summary>Cancellation source owned by this job. Cancel via Cts.Cancel().</summary>
    public CancellationTokenSource Cts { get; } = new();

    /// <summary>Shortcut to Cts.Token for passing into async methods.</summary>
    public CancellationToken Token => Cts.Token;

    /// <summary>Pass this into service methods that accept IProgress&lt;string&gt;.</summary>
    public IProgress<string> StatusReporter { get; }

    /// <summary>Pass this into service methods that accept IProgress&lt;int&gt;.</summary>
    public IProgress<int> ProgressReporter { get; }

    // Back-ported from AlwaysBlue via Unwrapped: monotonic clock readings at creation and at the end (Complete or
    // Fail), not a Stopwatch, so the card and its steps read the same clock and tests can drive it.
    private readonly long _startedAt = SystemClock.Timestamp;
    private long? _endedAt;
    private string? _shownElapsed;
    private string? _shownAgo;

    /// <summary>Time since the job was created, frozen once it ends.</summary>
    public TimeSpan Elapsed => SystemClock.Since(_startedAt, _endedAt);

    /// <summary>Stops the clock (the tracker calls it on Complete and Fail).</summary>
    public void StopClock() => _endedAt ??= SystemClock.Timestamp;

    /// <summary>Elapsed time as text: "12s", "1m 05s" while it runs; "4.3s" once it has finished.</summary>
    public string ElapsedDisplay => _endedAt is null ? DurationText.Running(Elapsed) : DurationText.Finished(Elapsed);

    /// <summary>Announces ElapsedDisplay when its text has changed since it was last announced.</summary>
    public void RefreshElapsed() => RefreshClocks();

    /// <summary>
    /// Announces the elapsed text, and that of every running step in the job's step tree, when it has changed.
    /// Returns how long until one of them changes next (null when nothing here is running).
    /// </summary>
    public TimeSpan? RefreshClocks()
    {
        var text = ElapsedDisplay;
        if (text != _shownElapsed)
        {
            _shownElapsed = text;
            OnPropertyChanged(nameof(ElapsedDisplay));
        }
        if (_endedAt is not null) return null;
        TimeSpan? next = DurationText.UntilChange(Elapsed);
        if (Context is JobStepTree tree && tree.RefreshRunning() is { } step && step < next) next = step;
        return next;
    }

    /// <summary>Announces FinishedAgo when its text has changed.</summary>
    public void RefreshAgo()
    {
        var text = FinishedAgo;
        if (text == _shownAgo) return;
        _shownAgo = text;
        OnPropertyChanged(nameof(FinishedAgo));
    }

    public BackgroundJob()
    {
        StatusReporter   = UiProgress.ForStatus(s => Status = s);
        ProgressReporter = UiProgress.ForProgress(p => Progress = p);
    }
}
