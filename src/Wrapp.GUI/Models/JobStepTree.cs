using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Wrapp.Services;

namespace Wrapp.Models;

/// <summary>
/// Reusable, observable, hierarchical step list for any multi-part
/// background job. Attach one to <see cref="BackgroundJob.Context"/> and
/// the popup's expanded card renders it as a tree via
/// <c>JobStepTreeRenderer</c>.
///
/// <para>Live updates: a worker thread mutates <see cref="JobStep.State"/>
/// / <see cref="JobStep.StatusMessage"/> and the binding refreshes
/// automatically because each step is <see cref="ObservableObject"/>.
/// <see cref="JobStep.SubSteps"/> is recursive so nested phases (e.g., the
/// three-source key cascade inside Import-to-Wrapp's "Decrypt" step) render
/// as indented children.</para>
/// </summary>
public partial class JobStepTree : ObservableObject
{
    public ObservableCollection<JobStep> Steps { get; } = new();

    /// <summary>Convenience: add a step at the top level and return it so
    /// callers can stash the reference for later Start/Finish calls.</summary>
    public JobStep Add(string title)
    {
        var step = new JobStep { Title = title };
        Steps.Add(step);
        return step;
    }

    /// <summary>
    /// Announces the duration of every running step (nested ones too) whose text has changed, and returns how
    /// long until one changes next (null when none is running). The job tracker's clock calls it.
    /// Back-ported from AlwaysBlue via Unwrapped.
    /// </summary>
    public TimeSpan? RefreshRunning() => JobStep.RefreshRunning(Steps);
}

/// <summary>
/// A single step inside a <see cref="JobStepTree"/>. Lifecycle:
/// Pending (initial) → <see cref="Start"/> → Running →
/// <see cref="Finish"/> → Succeeded / Failed / Skipped.
/// </summary>
public partial class JobStep : ObservableObject
{
    public string Title { get; init; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationDisplay))]
    private StepState _state = StepState.Pending;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationDisplay))]
    private DateTimeOffset? _startedAt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationDisplay))]
    private DateTimeOffset? _completedAt;

    public ObservableCollection<JobStep> SubSteps { get; } = new();

    // Back-ported from AlwaysBlue via Unwrapped: monotonic readings for the duration (the job's clock); StartedAt
    // and CompletedAt stay wall-clock times for display and saving. A step whose times were set without
    // Start/Finish falls back to those.
    private long? _startTicks;
    private long? _endTicks;
    private string? _shownDuration;

    /// <summary>Time since the step started, frozen once it finishes (zero before it starts).</summary>
    [JsonIgnore]
    public TimeSpan Duration
    {
        get
        {
            if (_startTicks is { } start && (_endTicks is not null || CompletedAt is null)) return SystemClock.Since(start, _endTicks);
            if (StartedAt is null) return TimeSpan.Zero;
            return (CompletedAt ?? SystemClock.UtcOffsetNow) - StartedAt.Value;
        }
    }

    /// <summary>The step's time as text ("12s", "1m 05s" while it runs; "0.4s" once it has finished; "" before it starts).</summary>
    [JsonIgnore]
    public string DurationDisplay
    {
        get
        {
            if (StartedAt is null) return "";
            return CompletedAt is null ? DurationText.Running(Duration) : DurationText.Finished(Duration);
        }
    }

    /// <summary>Stamp StartedAt and flip the state to Running.</summary>
    public void Start(string? msg = null)
    {
        _startTicks = SystemClock.Timestamp;
        _endTicks = null;
        CompletedAt = null;
        StartedAt = SystemClock.UtcOffsetNow;
        State = StepState.Running;
        if (msg is not null) StatusMessage = msg;
    }

    /// <summary>Stamp CompletedAt and flip to a terminal state
    /// (Succeeded / Failed / Skipped).</summary>
    public void Finish(StepState terminalState, string? msg = null)
    {
        _endTicks = SystemClock.Timestamp;
        CompletedAt = SystemClock.UtcOffsetNow;
        State = terminalState;
        if (msg is not null) StatusMessage = msg;
    }

    /// <summary>
    /// Announces DurationDisplay when a running step's text has changed, here and in its sub-steps.
    /// Returns how long until one of them changes next, or null when none is running.
    /// </summary>
    internal static TimeSpan? RefreshRunning(IEnumerable<JobStep> steps)
    {
        TimeSpan? next = null;
        foreach (var step in steps)
        {
            if (step.State == StepState.Running && step.StartedAt is not null && step.CompletedAt is null)
            {
                var text = step.DurationDisplay;
                if (text != step._shownDuration)
                {
                    step._shownDuration = text;
                    step.OnPropertyChanged(nameof(DurationDisplay));
                }
                var until = DurationText.UntilChange(step.Duration);
                if (next is null || until < next) next = until;
            }
            if (step.SubSteps.Count > 0 && RefreshRunning(step.SubSteps) is { } child && (next is null || child < next)) next = child;
        }
        return next;
    }

    /// <summary>Convenience - add a nested child step.</summary>
    public JobStep AddSubStep(string title)
    {
        var step = new JobStep { Title = title };
        SubSteps.Add(step);
        return step;
    }
}
