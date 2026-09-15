using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Covers the reusable <see cref="JobStepTree"/> primitive introduced in
/// 0.6.0.0180 for multi-part background job details. Guards the
/// lifecycle semantics (Pending → Running → terminal) and the observable
/// contract the UI renderer binds against.
/// </summary>
public class JobStepTreeTests
{
    // ── Default state ───────────────────────────────────────────────

    [Fact]
    public void NewJobStep_DefaultsToPending_WithNullTimes()
    {
        var s = new JobStep { Title = "Foo" };
        Assert.Equal(StepState.Pending, s.State);
        Assert.Null(s.StartedAt);
        Assert.Null(s.CompletedAt);
        Assert.Equal("", s.DurationDisplay);
        Assert.Equal("", s.StatusMessage);
    }

    [Fact]
    public void NewJobStepTree_HasEmptyStepsCollection()
    {
        var t = new JobStepTree();
        Assert.Empty(t.Steps);
    }

    // ── Lifecycle ───────────────────────────────────────────────────

    [Fact]
    public void Start_FlipsToRunningAndStampsTime()
    {
        var s = new JobStep { Title = "Foo" };
        s.Start("starting...");

        Assert.Equal(StepState.Running, s.State);
        Assert.NotNull(s.StartedAt);
        Assert.Null(s.CompletedAt);
        Assert.Equal("starting...", s.StatusMessage);
    }

    [Theory]
    [InlineData(StepState.Succeeded)]
    [InlineData(StepState.Failed)]
    [InlineData(StepState.Skipped)]
    public void Finish_SetsTerminalStateAndStampsCompleted(StepState terminal)
    {
        var s = new JobStep { Title = "Foo" };
        s.Start();
        s.Finish(terminal, "done");

        Assert.Equal(terminal, s.State);
        Assert.NotNull(s.CompletedAt);
        Assert.Equal("done", s.StatusMessage);
    }

    [Fact]
    public void Finish_WithoutPriorStart_StillStampsCompleted()
    {
        // Skipping a step that never ran -- Finish still works.
        var s = new JobStep { Title = "Foo" };
        s.Finish(StepState.Skipped);

        Assert.Equal(StepState.Skipped, s.State);
        Assert.NotNull(s.CompletedAt);
        Assert.Null(s.StartedAt);  // never started -- stays null
    }

    // ── DurationDisplay ─────────────────────────────────────────────

    [Fact]
    public void Duration_BeforeStart_IsEmpty()
    {
        var s = new JobStep { Title = "Foo" };
        Assert.Equal("", s.DurationDisplay);
    }

    [Fact]
    public void Duration_AfterFinish_FormatsUnderOneSecondAsLessThan1s()
    {
        var s = new JobStep { Title = "Foo" };
        s.Start();
        s.Finish(StepState.Succeeded);
        // StartedAt and CompletedAt are set in the same tick, so duration
        // is effectively zero seconds which renders as "<1s".
        Assert.Equal("<1s", s.DurationDisplay);
    }

    [Fact]
    public void Duration_FreezesAfterFinish_DoesNotGrow()
    {
        // Phase-1 rewrite: uses SystemClock.Override + an inline fake time
        // provider to advance wall-clock deterministically instead of
        // Thread.Sleep. This test now runs in microseconds and can't flake
        // under load.
        var fake = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-22T10:00:00Z"));
        using (SystemClock.Override(fake))
        {
            var s = new JobStep { Title = "Foo" };
            s.Start();
            fake.Advance(TimeSpan.FromSeconds(3));
            s.Finish(StepState.Succeeded);
            var snapshot1 = s.DurationDisplay;

            // Simulate time passing AFTER the step finished - duration
            // must be frozen at the Finish point, not still growing.
            fake.Advance(TimeSpan.FromMinutes(1));
            var snapshot2 = s.DurationDisplay;

            Assert.Equal(snapshot1, snapshot2);
        }
    }

    // ── Observability ───────────────────────────────────────────────

    [Fact]
    public void PropertyChanged_FiresOnLifecycleMutations()
    {
        var s = new JobStep { Title = "Foo" };
        var seen = new HashSet<string>();
        s.PropertyChanged += (_, e) => { if (e.PropertyName is not null) seen.Add(e.PropertyName); };

        s.Start("go");
        s.Finish(StepState.Succeeded, "yay");

        Assert.Contains(nameof(JobStep.State),         seen);
        Assert.Contains(nameof(JobStep.StatusMessage), seen);
        Assert.Contains(nameof(JobStep.StartedAt),     seen);
        Assert.Contains(nameof(JobStep.CompletedAt),   seen);
        // DurationDisplay is computed; it must also raise so the binding refreshes.
        Assert.Contains(nameof(JobStep.DurationDisplay), seen);
    }

    // ── Sub-steps (recursion) ───────────────────────────────────────

    [Fact]
    public void SubSteps_IndependentLifecycle_FromParent()
    {
        var parent = new JobStep { Title = "Parent" };
        var child  = parent.AddSubStep("Child");

        parent.Start();
        child.Start();
        child.Finish(StepState.Succeeded);

        // Child is done; parent is still Running.
        Assert.Equal(StepState.Succeeded, child.State);
        Assert.Equal(StepState.Running,   parent.State);
    }

    [Fact]
    public void AddSubStep_ReturnsSameInstanceAsCollectionEntry()
    {
        var parent = new JobStep { Title = "P" };
        var returned = parent.AddSubStep("C");
        Assert.Same(returned, parent.SubSteps[0]);
    }

    [Fact]
    public void Tree_Add_ReturnsSameInstanceAsStepsEntry()
    {
        var tree = new JobStepTree();
        var returned = tree.Add("S1");
        Assert.Same(returned, tree.Steps[0]);
    }
}
