using System.Management.Automation;
using Wrapp.Models;

namespace Wrapp.Tests;

/// <summary>
/// Tests for <see cref="StepEvent.FromPSObject"/> — the parser that turns a
/// <c>Write-WrappStep</c> output object into a typed step event (Workstream B′).
/// </summary>
public class StepEventTests
{
    private static PSObject Obj(params (string Name, object? Value)[] props)
    {
        var o = new PSObject();
        foreach (var (name, value) in props)
            o.Properties.Add(new PSNoteProperty(name, value));
        return o;
    }

    [Fact]
    public void FromPSObject_ParsesWrappStep()
    {
        var evt = StepEvent.FromPSObject(Obj(
            ("_Type", "WrappStep"),
            ("Package", "7-Zip"),
            ("Step", "AppCreation"),
            ("Kind", "Success"),
            ("TenantId", "bfb009f8"),
            ("Error", null)));

        Assert.NotNull(evt);
        Assert.Equal("7-Zip", evt!.Package);
        Assert.Equal("AppCreation", evt.Step);
        Assert.Equal("Success", evt.Kind);
        Assert.Equal("bfb009f8", evt.TenantId);
        Assert.Null(evt.Error);
    }

    [Fact]
    public void FromPSObject_CarriesErrorOnFail()
    {
        var evt = StepEvent.FromPSObject(Obj(
            ("_Type", "WrappStep"),
            ("Package", "7-Zip Prod"),
            ("Step", "AppCreation"),
            ("Kind", "Fail"),
            ("Error", "Graph 409")));

        Assert.NotNull(evt);
        Assert.Equal("Fail", evt!.Kind);
        Assert.Equal("Graph 409", evt.Error);
    }

    [Fact]
    public void FromPSObject_CarriesDetailSummary()
    {
        var evt = StepEvent.FromPSObject(Obj(
            ("_Type", "WrappStep"),
            ("Package", "7-Zip"),
            ("Step", "Assignment"),
            ("Kind", "Success"),
            ("Detail", "3 applied, 0 failed")));

        Assert.NotNull(evt);
        Assert.Equal("Assignment", evt!.Step);
        Assert.Equal("3 applied, 0 failed", evt.Detail);
        Assert.Null(evt.Error);
    }

    [Fact]
    public void FromPSObject_ParsesProgressPercent()
    {
        var evt = StepEvent.FromPSObject(Obj(
            ("_Type", "WrappStep"),
            ("Package", "7-Zip"),
            ("Step", "AppCreation"),
            ("Kind", "Progress"),
            ("Detail", "Uploading 0%"),
            ("Percent", 25)));

        Assert.NotNull(evt);
        Assert.Equal(25, evt!.Percent);
    }

    [Fact]
    public void FromPSObject_NormalizesUnsetPercentToNull()
    {
        // The module emits -1 for "percent not set" (non-Progress kinds).
        var evt = StepEvent.FromPSObject(Obj(
            ("_Type", "WrappStep"),
            ("Package", "7-Zip"),
            ("Step", "AppCreation"),
            ("Kind", "Success"),
            ("Percent", -1)));

        Assert.NotNull(evt);
        Assert.Null(evt!.Percent);
    }

    [Fact]
    public void FromPSObject_IgnoresEncryptionKeysObject()
        => Assert.Null(StepEvent.FromPSObject(Obj(("_Type", "EncryptionKeys"), ("AppId", "x"))));

    [Fact]
    public void FromPSObject_IgnoresObjectWithoutType()
        => Assert.Null(StepEvent.FromPSObject(Obj(("Success", true))));
}
