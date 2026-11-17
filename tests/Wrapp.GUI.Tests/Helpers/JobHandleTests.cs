using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Phase 13 (D-2). Covers the <see cref="JobHandle"/> wrapper and the
/// <see cref="BackgroundJobTracker.BeginJob"/> factory. Migration-target
/// call sites rely on:
///   1. <c>BeginJob</c> producing an active handle whose Complete / Fail
///      actually route through the tracker.
///   2. <c>default(JobHandle)</c> being a safe no-op so call sites can do
///      <c>_jobTracker?.BeginJob(...) ?? default</c>.
///   3. <c>SetStatus</c> / <c>SetProgress</c> mutating the underlying job
///      visibly via the property notification path.
/// </summary>
public class JobHandleTests
{
    [Fact]
    public void BeginJob_ProducesActiveHandle()
    {
        var tracker = new BackgroundJobTracker();
        var handle = tracker.BeginJob("test job");
        Assert.True(handle.IsActive);
        Assert.NotNull(handle.Job);
        Assert.Equal("test job", handle.Job!.Title);
    }

    [Fact]
    public void Complete_FlipsUnderlyingJobToCompleted()
    {
        var tracker = new BackgroundJobTracker();
        var handle = tracker.BeginJob("complete-target");
        handle.Complete("done");
        Assert.True(handle.Job!.IsCompleted);
        Assert.Equal("done", handle.Job.Status);
        Assert.False(handle.Job.IsFaulted);
    }

    [Fact]
    public void Complete_WithSummary_StampsResultSummaryOnJob()
    {
        var tracker = new BackgroundJobTracker();
        var handle = tracker.BeginJob("summary-target");
        var summary = new { Outcome = "ok", Items = 3 };
        handle.Complete("done", summary);
        Assert.True(handle.Job!.IsCompleted);
        Assert.Same(summary, handle.Job.ResultSummary);
    }

    [Fact]
    public void Fail_FlipsUnderlyingJobToFaulted()
    {
        var tracker = new BackgroundJobTracker();
        var handle = tracker.BeginJob("fail-target");
        handle.Fail("boom");
        Assert.True(handle.Job!.IsCompleted);
        Assert.True(handle.Job.IsFaulted);
        Assert.Contains("boom", handle.Job.Status);
    }

    [Fact]
    public void SetStatus_MutatesUnderlyingJob()
    {
        var tracker = new BackgroundJobTracker();
        var handle = tracker.BeginJob("status-target");
        handle.SetStatus("in flight");
        Assert.Equal("in flight", handle.Job!.Status);
    }

    [Fact]
    public void SetProgress_MutatesUnderlyingJob()
    {
        var tracker = new BackgroundJobTracker();
        var handle = tracker.BeginJob("progress-target");
        handle.SetProgress(42);
        Assert.Equal(42, handle.Job!.Progress);
    }

    [Fact]
    public void DefaultHandle_IsInactiveAndOperationsNoOp()
    {
        // The struct default must be safe for the
        // `_jobTracker?.BeginJob(...) ?? default` call-site pattern.
        var handle = default(JobHandle);
        Assert.False(handle.IsActive);
        Assert.Null(handle.Job);

        // These must not throw -- they back-door into Complete/Fail/SetStatus
        // when the tracker was null.
        handle.Complete("ignored");
        handle.Complete("ignored", new { });
        handle.Fail("ignored");
        handle.SetStatus("ignored");
        handle.SetProgress(99);
    }
}
