using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Phase D (update-flow-and-token-polling-plan): the honest-progress mapping.
/// Contract: a moving percent is Downloading with a real bar; a percent
/// frozen past the stall threshold is Rebuilding with an indeterminate bar
/// (the truthful rendering of Velopack's "stuck at 70%" delta-rebuild stage);
/// movement after a stall returns to Downloading.
/// </summary>
public class UpdateStepTrackerTests
{
    [Fact]
    public void InitialState_CheckingFeed_Indeterminate()
    {
        var t = new UpdateStepTracker();
        Assert.Equal(UpdateFlowStep.CheckingFeed, t.Step);
        Assert.True(t.IsIndeterminate);
        Assert.Equal(0, t.Percent);
    }

    [Fact]
    public void MovingPercent_IsDownloading_WithRealBar()
    {
        var t = new UpdateStepTracker();
        Assert.True(t.ReportPercent(10, 1000));

        Assert.Equal(UpdateFlowStep.Downloading, t.Step);
        Assert.False(t.IsIndeterminate);
        Assert.Equal(10, t.Percent);
    }

    [Fact]
    public void RepeatedPercent_ReportsNoChange()
    {
        var t = new UpdateStepTracker();
        t.ReportPercent(42, 1000);
        Assert.False(t.ReportPercent(42, 2000));
    }

    [Fact]
    public void StalledPercent_FlipsToRebuilding_Indeterminate()
    {
        var t = new UpdateStepTracker();
        t.ReportPercent(70, 1000);

        Assert.False(t.EvaluateStall(1000 + UpdateStepTracker.StallThresholdMs));      // at threshold: not yet
        Assert.True(t.EvaluateStall(1001 + UpdateStepTracker.StallThresholdMs));       // past it: flip

        Assert.Equal(UpdateFlowStep.Rebuilding, t.Step);
        Assert.True(t.IsIndeterminate);
    }

    [Fact]
    public void MovementAfterStall_ReturnsToDownloading()
    {
        var t = new UpdateStepTracker();
        t.ReportPercent(70, 1000);
        t.EvaluateStall(10_000);
        Assert.Equal(UpdateFlowStep.Rebuilding, t.Step);

        Assert.True(t.ReportPercent(71, 11_000));
        Assert.Equal(UpdateFlowStep.Downloading, t.Step);
        Assert.False(t.IsIndeterminate);
    }

    [Fact]
    public void StallCheck_OnlyAppliesWhileDownloading()
    {
        var t = new UpdateStepTracker();
        Assert.False(t.EvaluateStall(60_000));          // CheckingFeed: no percent yet

        t.ReportPercent(100, 1000);
        t.Begin(UpdateFlowStep.Applying);
        Assert.False(t.EvaluateStall(60_000));          // explicit stage: stall logic off
        Assert.Equal(UpdateFlowStep.Applying, t.Step);
    }

    [Fact]
    public void ExplicitStages_AreIndeterminate()
    {
        var t = new UpdateStepTracker();
        foreach (var step in new[]
        {
            UpdateFlowStep.WaitingForWindows, UpdateFlowStep.Applying, UpdateFlowStep.Restarting,
        })
        {
            t.Begin(step);
            Assert.Equal(step, t.Step);
            Assert.True(t.IsIndeterminate);
        }
    }

    [Fact]
    public void EveryStep_HasALabel()
    {
        foreach (UpdateFlowStep step in Enum.GetValues<UpdateFlowStep>())
            Assert.False(string.IsNullOrWhiteSpace(UpdateStepTracker.Label(step)));
    }

    // ------------------------------------------------------------------
    // DisplayPercent: the download's 0-70 raw share renders as a full bar
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(35, 50)]     // halfway through the download shows half a bar
    [InlineData(63, 90)]
    [InlineData(70, 99)]     // download complete: capped, never a premature 100
    [InlineData(85, 99)]     // raw past the download share still caps at 99
    public void DisplayPercent_RenormalizesDownloadShare(int raw, int shown)
    {
        var t = new UpdateStepTracker();
        t.ReportPercent(raw, 1000);
        Assert.Equal(shown, t.DisplayPercent);
    }

    // ------------------------------------------------------------------
    // Hash display formatting (SplashViewModel.FormatHash)
    // ------------------------------------------------------------------

    [Fact]
    public void FormatHash_Sha256_LabeledAndBalancedAcrossTwoLines()
    {
        var sha = new string('a', 32) + new string('b', 32);
        var lines = Wrapp.ViewModels.SplashViewModel.FormatHash(sha).Split('\n');

        Assert.Equal(3, lines.Length);
        Assert.Equal("SHA-256", lines[0]);
        Assert.Equal(4, lines[1].Split(' ').Length);    // 4 groups of 8 per line
        Assert.Equal(4, lines[2].Split(' ').Length);
        Assert.Equal(sha, string.Concat(lines[1..]).Replace(" ", ""));  // nothing elided
    }

    [Fact]
    public void FormatHash_Sha1_LabeledAndComplete()
    {
        var sha = new string('c', 40);
        var formatted = Wrapp.ViewModels.SplashViewModel.FormatHash(sha);
        Assert.StartsWith("SHA-1", formatted);
        Assert.Equal(sha, formatted.Split('\n', 2)[1].Replace(" ", "").Replace("\n", ""));
    }

    [Fact]
    public void FormatHash_Empty_IsEmpty()
        => Assert.Equal(string.Empty, Wrapp.ViewModels.SplashViewModel.FormatHash(null));

    [Fact]
    public void DisplayPercent_LeavesRawPercentUntouched()
    {
        // Stall detection keys off the RAW value; renormalization is
        // display-only.
        var t = new UpdateStepTracker();
        t.ReportPercent(70, 1000);
        Assert.Equal(70, t.Percent);
        Assert.True(t.EvaluateStall(1001 + UpdateStepTracker.StallThresholdMs));
        Assert.Equal(UpdateFlowStep.Rebuilding, t.Step);
    }
}
