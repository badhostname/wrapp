using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Covers the <see cref="OperationScope"/> primitive: ensure the start /
/// complete / fail log shape is consistent, idempotent, deterministic on
/// elapsed time (via <see cref="SystemClock.Override"/>), and survives
/// early returns through Dispose.
/// </summary>
public class OperationScopeTests
{
    private sealed class CapturingLogger : IAppLogger
    {
        public List<(string Level, string Message)> Entries { get; } = new();
        public void Info(string message)  => Entries.Add(("INFO",  message));
        public void Warn(string message)  => Entries.Add(("WARN",  message));
        public void Error(string message) => Entries.Add(("ERROR", message));
        public void Exception(string context, Exception ex)
            => Entries.Add(("ERROR", $"{context}: {ex.Message}"));
    }

    [Fact]
    public void Begin_LogsStartedLine()
    {
        var log = new CapturingLogger();
        using (OperationScope.Begin("Test.Op", log)) { }
        Assert.Equal("INFO", log.Entries[0].Level);
        Assert.Equal("Test.Op: started", log.Entries[0].Message);
    }

    [Fact]
    public void Complete_LogsElapsedAndOptionalNote()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        using (SystemClock.Override(fake))
        {
            var log = new CapturingLogger();
            using (var op = OperationScope.Begin("Test.Op", log))
            {
                fake.Advance(TimeSpan.FromMilliseconds(250));
                op.Complete("tenant=abc");
            }
            // Entries: [0] started, [1] completed
            Assert.Equal(2, log.Entries.Count);
            Assert.Contains("Test.Op: completed in 250ms", log.Entries[1].Message);
            Assert.Contains("tenant=abc", log.Entries[1].Message);
        }
    }

    [Fact]
    public void Complete_SubSecond_FormatsMs()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        using (SystemClock.Override(fake))
        {
            var log = new CapturingLogger();
            using (var op = OperationScope.Begin("X", log))
            {
                fake.Advance(TimeSpan.FromMilliseconds(42));
                op.Complete();
            }
            Assert.Equal("X: completed in 42ms", log.Entries[1].Message);
        }
    }

    [Fact]
    public void Complete_OverOneSecond_FormatsSeconds()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        using (SystemClock.Override(fake))
        {
            var log = new CapturingLogger();
            using (var op = OperationScope.Begin("X", log))
            {
                fake.Advance(TimeSpan.FromSeconds(1.5));
                op.Complete();
            }
            Assert.Equal("X: completed in 1.5s", log.Entries[1].Message);
        }
    }

    [Fact]
    public void Complete_OverOneMinute_FormatsMinutes()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        using (SystemClock.Override(fake))
        {
            var log = new CapturingLogger();
            using (var op = OperationScope.Begin("X", log))
            {
                fake.Advance(TimeSpan.FromSeconds(75));
                op.Complete();
            }
            Assert.Equal("X: completed in 1m15s", log.Entries[1].Message);
        }
    }

    [Fact]
    public void Fail_LogsExceptionWithStackTrace()
    {
        var log = new CapturingLogger();
        using (var op = OperationScope.Begin("Test.Op", log))
        {
            var ex = new InvalidOperationException("boom");
            op.Fail(ex, "tenant=t1");
        }
        Assert.Equal("ERROR", log.Entries[1].Level);
        Assert.Contains("Test.Op: failed", log.Entries[1].Message);
        Assert.Contains("tenant=t1", log.Entries[1].Message);
        Assert.Contains("boom", log.Entries[1].Message);
    }

    [Fact]
    public void Fail_IsIdempotent()
    {
        var log = new CapturingLogger();
        var ex = new InvalidOperationException("first");
        using (var op = OperationScope.Begin("Test.Op", log))
        {
            op.Fail(ex);
            op.Fail(new InvalidOperationException("second"));
            op.Complete("ignored");
        }
        // Should be exactly: [started, failed]. Second fail and the complete
        // are dropped.
        Assert.Equal(2, log.Entries.Count);
        Assert.Equal("INFO", log.Entries[0].Level);
        Assert.Equal("ERROR", log.Entries[1].Level);
        Assert.Contains("first", log.Entries[1].Message);
    }

    [Fact]
    public void Complete_IsIdempotent()
    {
        var log = new CapturingLogger();
        using (var op = OperationScope.Begin("Test.Op", log))
        {
            op.Complete("first");
            op.Complete("second");
        }
        Assert.Equal(2, log.Entries.Count);
        Assert.Contains("first", log.Entries[1].Message);
    }

    [Fact]
    public void Dispose_WithoutCompleteOrFail_LogsAutoCompletion()
    {
        var log = new CapturingLogger();
        using (OperationScope.Begin("Test.Op", log)) { /* leak path */ }
        Assert.Equal(2, log.Entries.Count);
        Assert.Contains("(auto)", log.Entries[1].Message);
    }

    [Fact]
    public void Dispose_AfterComplete_DoesNotLogAutoCompletion()
    {
        var log = new CapturingLogger();
        using (var op = OperationScope.Begin("Test.Op", log))
        {
            op.Complete();
        }
        // Should be exactly [started, completed], no extra "(auto)" line.
        Assert.Equal(2, log.Entries.Count);
        Assert.DoesNotContain("(auto)", log.Entries[1].Message);
    }
}
