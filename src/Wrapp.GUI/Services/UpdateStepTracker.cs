namespace Wrapp.Services;

/// <summary>The stages of the splash-level update flow, in execution order.
/// Open work is first priority: sibling windows close (each through its own
/// save prompts) BEFORE the download/rebuild starts, so the rebuild's CPU
/// burst can never touch a window someone is working in.</summary>
public enum UpdateFlowStep
{
    CheckingFeed,
    WaitingForWindows,
    Downloading,
    Rebuilding,
    Applying,
    Restarting,
    Failed,
}

/// <summary>
/// Phase D (update-flow-and-token-polling-plan): turns Velopack's bare
/// 0–100 progress callback into honest step-based progress. Velopack holds
/// one number through its whole delta-rebuild stage (minutes of CPU — the
/// "stuck at 70%" field reports), so: while the percent moves we are
/// <see cref="UpdateFlowStep.Downloading"/> with a real bar; once it stalls
/// past <see cref="StallThresholdMs"/> we are
/// <see cref="UpdateFlowStep.Rebuilding"/> with an indeterminate bar. No
/// more lying percentages. Pure — callers pass timestamps — so the mapping
/// is unit-testable.
/// </summary>
public sealed class UpdateStepTracker
{
    public const int StallThresholdMs = 5000;

    /// <summary>
    /// Velopack budgets its single 0-100 callback across the WHOLE operation:
    /// ~70 points for the network download, the rest for the local rebuild
    /// (the field-observed "always parks at 70%"). The rebuild renders as its
    /// own indeterminate step here, so the download's share is renormalized
    /// to a full bar: raw 0-70 → shown 0-99.
    /// </summary>
    public const double DownloadWeight = 0.70;

    private long _lastMoveMs;
    private int _lastPercent = -1;

    public UpdateFlowStep Step { get; private set; } = UpdateFlowStep.CheckingFeed;

    /// <summary>Last raw percent Velopack reported (0 before any report).</summary>
    public int Percent => Math.Max(_lastPercent, 0);

    /// <summary>
    /// Renormalized percent for display: the download portion stretched to a
    /// full bar, capped at 99 — the bar never claims completion while bytes
    /// are still moving; "done" is the step flip, not the number.
    /// </summary>
    public int DisplayPercent => Math.Min(99, (int)Math.Round(Percent / DownloadWeight));

    /// <summary>Only <see cref="UpdateFlowStep.Downloading"/> has a truthful percent.</summary>
    public bool IsIndeterminate => Step != UpdateFlowStep.Downloading;

    /// <summary>Explicit transition for the non-callback stages.</summary>
    public void Begin(UpdateFlowStep step) => Step = step;

    /// <summary>
    /// Feed a Velopack progress callback. A moving percent (re-)enters
    /// Downloading; returns true when anything display-worthy changed.
    /// </summary>
    public bool ReportPercent(int percent, long nowMs)
    {
        if (percent == _lastPercent) return false;
        _lastPercent = percent;
        _lastMoveMs = nowMs;
        var changed = Step != UpdateFlowStep.Downloading;
        Step = UpdateFlowStep.Downloading;
        return true;
    }

    /// <summary>
    /// Poll for the stall transition (call from a ~1s ticker). True when the
    /// step just flipped to Rebuilding.
    /// </summary>
    public bool EvaluateStall(long nowMs)
    {
        if (Step != UpdateFlowStep.Downloading || _lastPercent < 0) return false;
        if (nowMs - _lastMoveMs <= StallThresholdMs) return false;
        Step = UpdateFlowStep.Rebuilding;
        return true;
    }

    /// <summary>Operator-facing label for a step.</summary>
    public static string Label(UpdateFlowStep step) => step switch
    {
        UpdateFlowStep.CheckingFeed      => "Checking update feed",
        UpdateFlowStep.Downloading       => "Downloading",
        UpdateFlowStep.Rebuilding        => "Rebuilding package (takes a few minutes when several versions are skipped)",
        UpdateFlowStep.WaitingForWindows => "Waiting for other Wrapp windows to close",
        UpdateFlowStep.Applying          => "Applying update",
        UpdateFlowStep.Restarting        => "Restarting Wrapp",
        UpdateFlowStep.Failed            => "Update failed",
        _                                => string.Empty,
    };
}
