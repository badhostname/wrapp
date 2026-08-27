using Wrapp.Services;

namespace Wrapp.Models;

/// <summary>
/// Lightweight wrapper around a (<see cref="BackgroundJobTracker"/>,
/// <see cref="BackgroundJob"/>) pair so call sites don&#x2019;t repeat the
/// <c>if (job is not null) _jobTracker?.Complete(...)</c> dance.
///
/// Phase 13 (D-2). The existing
/// <see cref="BackgroundJobTracker.Register(string, string, object?)"/> +
/// <see cref="BackgroundJobTracker.Complete(BackgroundJob, string?)"/> /
/// <see cref="BackgroundJobTracker.Fail(BackgroundJob, string)"/> API stays
/// in place &#x2014; many call sites still use it directly. New (and migrated)
/// call sites can call <see cref="BackgroundJobTracker.BeginJob"/>, get a
/// <see cref="JobHandle"/>, and call <see cref="Complete"/> / <see cref="Fail"/>
/// on it without the null guard. When the tracker is itself null
/// (test seams, headless contexts), the handle becomes a no-op &#x2014;
/// <see cref="IsActive"/> stays false and Complete/Fail return silently.
/// </summary>
public readonly struct JobHandle
{
    private readonly BackgroundJobTracker? _tracker;

    /// <summary>The underlying job, or <c>null</c> when the tracker is null.</summary>
    public BackgroundJob? Job { get; }

    /// <summary>True when a real job is being tracked. Use this to gate per-job UI updates.</summary>
    public bool IsActive => Job is not null;

    internal JobHandle(BackgroundJobTracker? tracker, BackgroundJob? job)
    {
        _tracker = tracker;
        Job = job;
    }

    /// <summary>
    /// Marks the job as successfully completed. No-op when the handle is
    /// inactive (tracker was null at <see cref="BackgroundJobTracker.BeginJob"/>).
    /// </summary>
    public void Complete(string? finalStatus = null)
    {
        if (_tracker is not null && Job is not null)
            _tracker.Complete(Job, finalStatus);
    }

    /// <summary>
    /// Marks the job as successfully completed with a structured summary.
    /// No-op when the handle is inactive.
    /// </summary>
    public void Complete(string? finalStatus, object? resultSummary)
    {
        if (_tracker is not null && Job is not null)
            _tracker.Complete(Job, finalStatus, resultSummary);
    }

    /// <summary>Marks the job as failed. No-op when the handle is inactive.</summary>
    public void Fail(string errorMessage)
    {
        if (_tracker is not null && Job is not null)
            _tracker.Fail(Job, errorMessage);
    }

    /// <summary>
    /// Sets the live status string on the job. No-op when the handle is
    /// inactive. Use this instead of mutating <c>Job.Status</c> directly so
    /// future tracker-side logic (e.g. timestamping or rate-limiting status
    /// updates) gets the call.
    /// </summary>
    public void SetStatus(string status)
    {
        if (Job is not null)
            Job.Status = status;
    }

    /// <summary>Sets the determinate progress percentage. No-op when inactive.</summary>
    public void SetProgress(int percent)
    {
        if (Job is not null)
            Job.Progress = percent;
    }

    /// <summary>
    /// Adds or updates a labeled fact on the job's detail card (counts,
    /// paths, statistics). Attaches a <see cref="JobDetails"/> context on
    /// first use; never clobbers a typed context (a run's tree, a step
    /// tree). Facts are ADDED on the caller's thread — first-time calls
    /// belong on the dispatcher; value updates are thread-safe.
    /// </summary>
    public void SetDetail(string label, string value)
        => DetailsOrNull()?.Set(label, value);

    /// <summary>
    /// Stamps a structured error payload (short code + raw body) onto the
    /// job's detail card — e.g. an HTTP status and the raw response of a
    /// failed query. Pair with <see cref="Fail"/> for the status line.
    /// </summary>
    public void SetError(string? code, string? body)
    {
        var details = DetailsOrNull();
        if (details is null) return;
        details.ErrorCode = code;
        details.ErrorBody = body;
    }

    private JobDetails? DetailsOrNull()
    {
        if (Job is null) return null;
        if (Job.Context is JobDetails existing) return existing;
        if (Job.Context is not null) return null;   // typed context owns the card
        var created = new JobDetails();
        Job.Context = created;
        return created;
    }
}
