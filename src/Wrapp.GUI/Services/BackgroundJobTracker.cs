using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Shared singleton that tracks all background operations across the app.
/// ViewModels register jobs here; the MainWindow status bar binds to the
/// computed properties for aggregate progress visibility.
/// </summary>
public partial class BackgroundJobTracker : ObservableObject
{
    public ObservableCollection<BackgroundJob> Jobs { get; } = new();

    public bool HasActiveJobs => Jobs.Any(j => !j.IsCompleted);
    public int ActiveCount => Jobs.Count(j => !j.IsCompleted);

    /// <summary>
    /// Average progress across active jobs that report determinate progress.
    /// Returns -1 if all active jobs are indeterminate.
    /// </summary>
    public int AggregateProgress
    {
        get
        {
            var withProgress = Jobs.Where(j => !j.IsCompleted && j.Progress >= 0).ToList();
            return withProgress.Count > 0 ? (int)withProgress.Average(j => j.Progress) : -1;
        }
    }

    /// <summary>True when at least one active job reports determinate progress.</summary>
    public bool HasDeterminateProgress => Jobs.Any(j => !j.IsCompleted && j.Progress >= 0);

    /// <summary>True when CancelAll has been called during app shutdown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryStatus))]
    private bool _isShuttingDown;

    /// <summary>
    /// Aggregate status for the main status bar.
    /// During shutdown, shows cancellation message.
    /// </summary>
    public string SummaryStatus
    {
        get
        {
            if (IsShuttingDown)
                return "Background jobs will be cancelled";

            var active = Jobs.Where(j => !j.IsCompleted).ToList();
            return active.Count switch
            {
                0 => "",
                1 => active[0].Status,
                _ => $"{active.Count} operations in progress"
            };
        }
    }

    /// <summary>
    /// Upper bound on retained completed jobs across all bundles. Keeps a long
    /// session from ballooning memory while still preserving meaningful history.
    /// Per-bundle filtering happens in the UI; this is the hard global cap.
    /// </summary>
    private const int MaxCompletedHistory = 200;

    /// <summary>
    /// Back-ported from AlwaysBlue via Unwrapped: drives the elapsed time of every running job and running step,
    /// and the "finished 2 min ago" of finished cards. It runs only while job cards are on screen
    /// (<see cref="Watch"/>: the jobs pop-up) and something on them can change, so it costs nothing otherwise.
    /// Each tick is timed to the moment the next text changes (the next whole second of the clock that changes
    /// soonest), so digits move exactly on the second, and a label is touched only when its text changes.
    /// Normal priority: a busy UI no longer starves it the way Background priority did. The old 500 ms ticker
    /// showed tenths, so its seconds jumped by half, and its steps never ticked at all.
    /// </summary>
    private readonly DispatcherTimer _clock;
    private int _watchers;
    private DateTimeOffset _agoRefreshedAt = DateTimeOffset.MinValue;

    /// <summary>How often "finished 2 min ago" is brought up to date.</summary>
    internal static readonly TimeSpan AgoEvery = TimeSpan.FromSeconds(30);

    /// <summary>The clock never ticks faster than this, however many clocks are on screen.</summary>
    internal static readonly TimeSpan MinTick = TimeSpan.FromMilliseconds(100);

    public BackgroundJobTracker()
    {
        _clock = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => EnsureTickerState();
    }

    /// <summary>True while the clock is running (tests read it).</summary>
    internal bool ClockRunning => _clock.IsEnabled;

    /// <summary>
    /// Job cards are on screen until the returned scope is disposed, so their clocks tick. Opening a view brings
    /// every clock up to date at once. <c>helpers:JobClock.Tracker</c> calls it for an element.
    /// </summary>
    public IDisposable Watch()
    {
        Models.DispatcherHelper.RunOnUi(() =>
        {
            _watchers++;
            _agoRefreshedAt = DateTimeOffset.MinValue;
            EnsureTickerState();
        });
        return new WatchScope(this);
    }

    private sealed class WatchScope(BackgroundJobTracker owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Models.DispatcherHelper.RunOnUi(() =>
            {
                owner._watchers--;
                owner.EnsureTickerState();
            });
        }
    }

    /// <summary>
    /// Brings every clock on the cards up to date and returns how long until one of them changes next (null when
    /// none will). Finished cards' "ago" is refreshed every <see cref="AgoEvery"/>.
    /// </summary>
    internal TimeSpan? RefreshClocks()
    {
        TimeSpan? next = null;
        var anyFinished = false;
        foreach (var j in Jobs)
        {
            if (j.IsCompleted) { anyFinished = true; continue; }
            if (j.RefreshClocks() is { } until && (next is null || until < next)) next = until;
        }
        if (anyFinished)
        {
            var now = SystemClock.UtcOffsetNow;
            if (now - _agoRefreshedAt >= AgoEvery)
            {
                foreach (var j in Jobs) if (j.IsCompleted) j.RefreshAgo();
                _agoRefreshedAt = now;
            }
            var untilAgo = AgoEvery - (now - _agoRefreshedAt);
            if (next is null || untilAgo < next) next = untilAgo;
        }
        return next;
    }

    /// <summary>
    /// Brings the clocks up to date and runs the timer while cards are watched and one of their clocks can
    /// change, timed to the next change; stops it otherwise. Called on watch, register, complete, fail, and tick.
    /// </summary>
    private void EnsureTickerState()
    {
        var next = _watchers > 0 ? RefreshClocks() : null;
        if (next is null)
        {
            if (_clock.IsEnabled) _clock.Stop();
            return;
        }
        // A little past the change, so the clock that changes is read after its second has turned.
        var interval = next.Value + TimeSpan.FromMilliseconds(15);
        _clock.Interval = interval < MinTick ? MinTick : interval;
        if (!_clock.IsEnabled) _clock.Start();
    }

    /// <summary>Creates and registers a new background job.</summary>
    public BackgroundJob Register(string title)
        => Register(title, bundleRoot: "", context: null);

    /// <summary>
    /// Creates and registers a job and returns a
    /// <see cref="JobHandle"/> so the caller can Complete / Fail / SetStatus
    /// it without the <c>if (job is not null) _jobTracker?.Complete(...)</c>
    /// dance. The pre-existing <see cref="Register(string, string, object?)"/>
    /// API stays in place so existing call sites are not forced to migrate.
    /// </summary>
    public JobHandle BeginJob(string title, string? bundleRoot = null, object? context = null)
    {
        var job = Register(title, bundleRoot ?? string.Empty, context);
        return new JobHandle(this, job);
    }

    /// <summary>
    /// Creates and registers a new background job with bundle scope and optional
    /// structured context. The context is an opaque POCO rendered by the pop-up
    /// via a type-specific DataTemplate (see Services/DeploymentPlanRenderer).
    /// </summary>
    public BackgroundJob Register(string title, string bundleRoot, object? context = null)
    {
        // Back-ported from Unwrapped: Jobs is UI-bound (the popup's list) and
        // the elapsed DispatcherTimer is thread-affine, so registration and
        // completion marshal to the dispatcher centrally - no call site on a
        // ConfigureAwait(false) continuation can get it wrong.
        return Models.DispatcherHelper.RunOnUi(() => RegisterCore(title, bundleRoot, context));
    }

    private BackgroundJob RegisterCore(string title, string bundleRoot, object? context)
    {
        TrimHistory();

        var job = new BackgroundJob
        {
            Title         = title,
            Status        = title,
            BundleRootDir = bundleRoot ?? "",
        };
        job.Context = context;
        job.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "Status" or "IsCompleted" or "Progress")
            {
                OnPropertyChanged(nameof(HasActiveJobs));
                OnPropertyChanged(nameof(ActiveCount));
                OnPropertyChanged(nameof(SummaryStatus));
                OnPropertyChanged(nameof(AggregateProgress));
                OnPropertyChanged(nameof(HasDeterminateProgress));
            }
        };
        Jobs.Add(job);
        AppLogger.Info($"Job started: {title}");

        OnPropertyChanged(nameof(HasActiveJobs));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(SummaryStatus));
        EnsureTickerState();
        return job;
    }

    /// <summary>Marks a job as successfully completed.</summary>
    public void Complete(BackgroundJob job, string? finalStatus = null)
        => Complete(job, finalStatus, resultSummary: null);

    /// <summary>
    /// Marks a job as successfully completed, optionally stamping a structured
    /// summary (rendered in the pop-up's expanded card).
    /// </summary>
    public void Complete(BackgroundJob job, string? finalStatus, object? resultSummary)
        => Models.DispatcherHelper.RunOnUi(() => CompleteCore(job, finalStatus, resultSummary));

    private void CompleteCore(BackgroundJob job, string? finalStatus, object? resultSummary)
    {
        job.StopClock();
        job.Status = finalStatus ?? $"{job.Title} -- done";
        job.ResultSummary = resultSummary;
        job.CompletedAt = SystemClock.OffsetNow;
        job.IsCompleted = true;
        // Push the final (now-frozen) ElapsedDisplay to the UI, whether or
        // not the clock is running: the finished text (tenths) differs from
        // the running one (whole seconds), so the card freezes on the truth.
        job.RefreshElapsed();
        AppLogger.Info($"Job completed: {job.Title} ({job.ElapsedDisplay})");
        DisposeJobCts(job);
        EnsureTickerState();
    }

    /// <summary>Marks a job as failed.</summary>
    public void Fail(BackgroundJob job, string errorMessage)
        => Models.DispatcherHelper.RunOnUi(() => FailCore(job, errorMessage));

    private void FailCore(BackgroundJob job, string errorMessage)
    {
        job.StopClock();
        job.Status = $"{job.Title} -- {errorMessage}";
        job.CompletedAt = SystemClock.OffsetNow;
        job.IsFaulted = true;
        job.IsCompleted = true;
        // See Complete() -- push the final elapsed value so the UI freezes
        // on the truth, not the last tick.
        job.RefreshElapsed();
        AppLogger.Warn($"Job failed: {job.Title} -- {errorMessage} ({job.ElapsedDisplay})");
        DisposeJobCts(job);
        EnsureTickerState();
    }

    /// <summary>Removes all completed jobs. Active jobs are preserved.</summary>
    public void ClearCompleted()
    {
        var stale = Jobs.Where(j => j.IsCompleted).ToList();
        foreach (var j in stale) Jobs.Remove(j);
    }

    /// <summary>
    /// Disposes a completed job's CancellationTokenSource. A linked token chain
    /// amplifies the cost of leaking these, so we free them as soon as the job
    /// reaches a terminal state.
    /// </summary>
    private static void DisposeJobCts(BackgroundJob job)
    {
        try { job.Cts.Dispose(); }
        catch { /* already disposed */ }
    }

    /// <summary>Cancels all active jobs and enters shutdown state.</summary>
    public void CancelAll()
    {
        IsShuttingDown = true;
        OnPropertyChanged(nameof(SummaryStatus));
        foreach (var job in Jobs.Where(j => !j.IsCompleted))
        {
            try
            {
                job.Cts.Cancel();
                job.Status = $"Cancelling: {job.Title}";
            }
            catch { /* already cancelled or disposed */ }
        }
        AppLogger.Info("Job tracker: cancelled all active jobs");
    }

    /// <summary>Reverts shutdown state (called when user cancels the close dialog).</summary>
    public void RevertShutdown()
    {
        IsShuttingDown = false;
        OnPropertyChanged(nameof(SummaryStatus));
    }

    /// <summary>Waits for all active jobs to complete (or timeout).</summary>
    public async Task WaitAllAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (HasActiveJobs && !cts.IsCancellationRequested)
                await Task.Delay(100, cts.Token);
        }
        catch (OperationCanceledException) { /* timeout reached */ }
    }

    /// <summary>
    /// Drops the oldest completed entries when the history exceeds the cap.
    /// Active jobs are never removed. Runs on each Register.
    /// </summary>
    private void TrimHistory()
    {
        var completed = Jobs.Where(j => j.IsCompleted).ToList();
        if (completed.Count <= MaxCompletedHistory) return;

        var toDrop = completed.Count - MaxCompletedHistory;
        foreach (var j in completed.OrderBy(j => j.CompletedAt ?? DateTimeOffset.MinValue).Take(toDrop))
            Jobs.Remove(j);
    }
}
