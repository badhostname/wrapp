using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Covers <see cref="PhaseDetector.ProcessLine"/>, the parser that turns raw
/// PowerShell log lines into the phase transitions the Run view renders.
/// Two things are easy to break here and invisible until a live run: (1) the
/// prefix/timestamp stripping that must run before matching, and (2) the
/// priority ordering + capture groups of ~40 regexes. These tests feed real
/// log-shaped strings and assert the emitted PhaseEvent.
/// </summary>
public class PhaseDetectorTests
{
    private static List<PhaseEvent> Capture(params string[] lines)
    {
        var det = new PhaseDetector();
        var events = new List<PhaseEvent>();
        det.PhaseChanged += e => events.Add(e);
        foreach (var l in lines) det.ProcessLine(l);
        return events;
    }

    private static PhaseEvent One(string line) => Assert.Single(Capture(line));

    // ── Priority + capture groups ──────────────────────────────────

    [Fact]
    public void FatalError_EmitsFatalWithMessage()
    {
        var e = One("Fatal error: token acquisition failed");
        Assert.Equal(PhaseId.Fatal, e.Phase);
        Assert.Equal("token acquisition failed", e.Detail);
    }

    [Fact]
    public void AppFailed_CapturesPackageName()
    {
        var e = One("Failed to process app 'Contoso Reader'");
        Assert.Equal(PhaseId.AppFailed, e.Phase);
        Assert.Equal("Contoso Reader", e.PackageName);
    }

    [Fact]
    public void CollisionDetected_CapturesAppNameWithSpaces()
    {
        var e = One("Collision: 'Acme Design Suite' already exists in Intune");
        Assert.Equal(PhaseId.CollisionDetected, e.Phase);
        Assert.Equal("Acme Design Suite", e.PackageName);
    }

    [Fact]
    public void AssignmentResult_CapturesNameAndCounts()
    {
        var e = One("Assignment result for 'Calc': 3 applied, 1 failed");
        Assert.Equal(PhaseId.AssignmentCompleted, e.Phase);
        Assert.Equal("Calc", e.PackageName);
        Assert.Equal("3 applied, 1 failed", e.Detail);
    }

    [Fact]
    public void CommitPolling_CapturesAttemptNumber()
    {
        var e = One("operation 'CommitFile' is in pending state (attempt 7)");
        Assert.Equal(PhaseId.UploadCommitPolling, e.Phase);
        Assert.Equal("7", e.Detail);
    }

    // ── Prefix / timestamp stripping ───────────────────────────────

    [Fact]
    public void UploadProgress_IsParsedBeforePrefixStripping()
    {
        // [PROG:nn] is checked against the RAW line, before StripPrefixes.
        var e = One("[PROG:42] Uploading content");
        Assert.Equal(PhaseId.UploadProgress, e.Phase);
        Assert.Equal("42", e.Detail);
    }

    [Fact]
    public void InfoPrefix_IsStrippedBeforeMatch()
    {
        var e = One("[INFO] Successfully authenticated to MS Graph");
        Assert.Equal(PhaseId.Authenticated, e.Phase);
    }

    [Fact]
    public void Timestamp_IsStrippedBeforeMatch()
    {
        var e = One("[2026-06-18 10:00:00] Successfully authenticated to MS Graph");
        Assert.Equal(PhaseId.Authenticated, e.Phase);
    }

    [Fact]
    public void OuterInfo_Timestamp_AndInnerInfo_AreAllStripped()
    {
        // The real worst case from Write-Log wrapped by PowerShellService:
        // outer level prefix, then a Write-Log timestamp, then an inner level.
        var e = One("[INFO] [2026-06-18 10:00:00] [INFO] Running post-auth preflight");
        Assert.Equal(PhaseId.PreflightStarted, e.Phase);
    }

    [Fact]
    public void BracketedNonTimestampLine_IsNotMangled()
    {
        // A line starting with '[' that is NOT a timestamp must survive the
        // strip and still match. (The DateTime.TryParse guard protects this.)
        var e = One("[INFO] Processing package: MyApp v2");
        Assert.Equal(PhaseId.AppProcessingStarted, e.Phase);
        Assert.Equal("MyApp v2", e.PackageName);
    }

    // ── Negative space: no false positives ─────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Some unrelated diagnostic output")]
    [InlineData("[INFO] writing temp file to disk")]
    public void NoiseLines_EmitNothing(string line)
    {
        Assert.Empty(Capture(line));
    }
}
