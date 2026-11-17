using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Smoke tests for the <see cref="IAppLogger"/> surface introduced in
/// Phase 1. The default instance delegates to the static logger; a fake
/// can capture entries for assertion. Actual capture-style tests live
/// beside the service ctor that takes the IAppLogger -- here we just
/// prove the plumbing is wired.
/// </summary>
public class AppLoggerInstanceTests
{
    [Fact]
    public void AppLogger_Instance_IsNotNull()
    {
        Assert.NotNull(AppLogger.Instance);
    }

    [Fact]
    public void AppLogger_Instance_DelegatesToStatic()
    {
        // Subscribing to the static EntryLogged event proves that calling
        // IAppLogger.Info reaches the same redaction + channel pipeline as
        // the static AppLogger.Info call. If a future refactor splits these,
        // this test catches it.
        var captured = new List<string>();
        EventHandler<LogEntry> handler = (_, e) => captured.Add(e.Message);
        AppLogger.EntryLogged += handler;
        try
        {
            AppLogger.Instance.Info("via-instance-message");
        }
        finally
        {
            AppLogger.EntryLogged -= handler;
        }

        Assert.Contains("via-instance-message", captured);
    }

    [Fact]
    public void CapturingAppLogger_Example_CompilesAndRuns()
    {
        // Demonstrates the pattern a future service test will use: inject
        // a CapturingAppLogger to assert on log output without touching
        // disk or the async writer.
        var capturing = new CapturingAppLogger();
        capturing.Info("hello");
        capturing.Warn("careful");
        capturing.Error("oh no");

        Assert.Equal(3, capturing.Entries.Count);
        Assert.Equal(("INFO",  "hello"),   capturing.Entries[0]);
        Assert.Equal(("WARN",  "careful"), capturing.Entries[1]);
        Assert.Equal(("ERROR", "oh no"),   capturing.Entries[2]);
    }

    /// <summary>
    /// Example test-double: collects log calls into an in-memory list so
    /// tests can assert on them. Lives in the test assembly so it can be
    /// shared across every future service test that takes IAppLogger.
    /// </summary>
    public sealed class CapturingAppLogger : IAppLogger
    {
        public List<(string Level, string Message)> Entries { get; } = new();
        public void Info(string message)  => Entries.Add(("INFO",  message));
        public void Warn(string message)  => Entries.Add(("WARN",  message));
        public void Error(string message) => Entries.Add(("ERROR", message));
        public void Exception(string context, Exception ex)
            => Entries.Add(("ERROR", $"{context}: {ex.Message}"));
    }
}
