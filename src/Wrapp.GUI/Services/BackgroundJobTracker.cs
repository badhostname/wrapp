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
    /// Drives live-updates of <see cref="BackgroundJob.ElapsedDisplay"/> for
    /// every active job, so the pop-up's per-job stopwatch ticks in real
    /// time (not only when Status / Progress / IsCompleted change). Runs
    /// on the UI dispatcher so `OnPropertyChanged` fires on the right
    /// thread; auto-stops when no jobs are active so it costs zero CPU
    /// while idle. 500 ms cadence matches the one-decimal precision of
    /// <c>ElapsedDisplay</c> without overworking the binding system.
    /// </summary>
    private readonly DispatcherTimer _elapsedTicker;

    public BackgroundJobTracker()
    {
        _elapsedTicker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _elapsedTicker.Tick += (_, _) =>
        {
            bool anyActive = false;
            foreach (var j in Jobs)
            {
                if (j.IsCompleted) continue;
                anyActive = true;
                j.RefreshElapsed();
            }
            if (!anyActive) _elapsedTicker.Stop();
        };
    }

    /// <summary>
    /// Ensures the elapsed-display ticker is running if at least one job
    /// is active. Called whenever Jobs collection membership or completion
    /// state changes. Idempotent - starting a running timer is a no-op.
    /// </summary>
    private void EnsureTickerState()
    {
        if (Jobs.Any(j => !j.IsCompleted))
        {
            if (!_elapsedTicker.IsEnabled) _elapsedTicker.Start();
        }
        else if (_elapsedTicker.IsEnabled)
        {
            _elapsedTicker.Stop();
        }
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
    {
        job.Timer.Stop();
        job.Status = finalStatus ?? $"{job.Title} -- done";
        job.ResultSummary = resultSummary;
        job.CompletedAt = SystemClock.OffsetNow;
        job.IsCompleted = true;
        // Push the final (now-frozen) ElapsedDisplay to the UI BEFORE
        // the ticker shuts down. Without this, the binding keeps the
        // last value the 500 ms ticker broadcast -- which is always
        // slightly stale -- and the true final elapsed only shows up
        // when the user closes and reopens the popup.
        job.RefreshElapsed();
        AppLogger.Info($"Job completed: {job.Title} ({job.ElapsedDisplay})");
        DisposeJobCts(job);
        EnsureTickerState();
    }

    /// <summary>Marks a job as failed.</summary>
    public void Fail(BackgroundJob job, string errorMessage)
    {
        job.Timer.Stop();
        job.Status = $"{job.Title} -- {errorMessage}";
        job.CompletedAt = SystemClock.OffsetNow;
        job.IsFaulted = true;
        job.IsCompleted = true;
        // See Complete() -- push the final elapsed value before the
        // ticker stops so the UI freezes on the truth, not the last tick.
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
