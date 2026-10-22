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

    /// <summary>Human-readable elapsed time ("<1s", "2.3s", or "" before start).</summary>
    [JsonIgnore]
    public string DurationDisplay
    {
        get
        {
            if (StartedAt is null) return "";
            var end = CompletedAt ?? SystemClock.UtcOffsetNow;
            var ts = end - StartedAt.Value;
            return ts.TotalSeconds < 1 ? "<1s" : $"{ts.TotalSeconds:F1}s";
        }
    }

    /// <summary>Stamp StartedAt and flip the state to Running.</summary>
    public void Start(string? msg = null)
    {
        StartedAt = SystemClock.UtcOffsetNow;
        State = StepState.Running;
        if (msg is not null) StatusMessage = msg;
    }

    /// <summary>Stamp CompletedAt and flip to a terminal state
    /// (Succeeded / Failed / Skipped).</summary>
    public void Finish(StepState terminalState, string? msg = null)
    {
        CompletedAt = SystemClock.UtcOffsetNow;
        State = terminalState;
        if (msg is not null) StatusMessage = msg;
    }

    /// <summary>Convenience - add a nested child step.</summary>
    public JobStep AddSubStep(string title)
    {
        var step = new JobStep { Title = title };
        SubSteps.Add(step);
        return step;
    }
}
