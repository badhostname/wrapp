using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// JobDetails facts model + JobHandle attachment semantics: facts upsert in
/// order, error payloads flag HasError, SetDetail lazily attaches a
/// JobDetails context, and a typed context (run tree, step tree) is never
/// clobbered by detail calls.
/// </summary>
public class JobDetailsTests
{
    [Fact]
    public void Set_Upserts_PreservingFirstAddOrder()
    {
        var d = new JobDetails();
        d.Set("Apps", "10");
        d.Set("Assigned", "4");
        d.Set("Apps", "12");   // update, not duplicate

        Assert.Equal(2, d.Facts.Count);
        Assert.Equal("Apps", d.Facts[0].Label);
        Assert.Equal("12", d.Facts[0].Value);
        Assert.Equal("Assigned", d.Facts[1].Label);
    }

    [Fact]
    public void HasError_FlagsOnEitherField()
    {
        var d = new JobDetails();
        Assert.False(d.HasError);
        d.ErrorCode = "HttpRequestException";
        Assert.True(d.HasError);

        var d2 = new JobDetails { ErrorBody = "raw body" };
        Assert.True(d2.HasError);
    }

    [Fact]
    public void JobHandle_SetDetail_AttachesDetailsContext()
    {
        var tracker = new BackgroundJobTracker();
        var handle = tracker.BeginJob("test");
        handle.SetDetail("From", @"C:\a");
        handle.SetError("Boom", "body");

        var details = Assert.IsType<JobDetails>(handle.Job!.Context);
        Assert.Equal(@"C:\a", details.Facts[0].Value);
        Assert.Equal("Boom", details.ErrorCode);
        Assert.Equal("body", details.ErrorBody);
    }

    [Fact]
    public void JobHandle_SetDetail_NeverClobbersTypedContext()
    {
        var tracker = new BackgroundJobTracker();
        var tree = new JobStepTree();
        var handle = tracker.BeginJob("run", context: tree);

        handle.SetDetail("ignored", "x");
        handle.SetError("ignored", "x");

        Assert.Same(tree, handle.Job!.Context);   // step tree untouched
    }

    [Fact]
    public void InactiveHandle_DetailCalls_AreNoOps()
    {
        JobHandle handle = default;
        handle.SetDetail("a", "b");   // must not throw
        handle.SetError("a", "b");
    }
}
