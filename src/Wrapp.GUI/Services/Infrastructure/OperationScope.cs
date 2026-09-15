namespace Wrapp.Services;

/// <summary>
/// <para>
/// Reusable shell for "operation start / operation end / operation failed"
/// logging. Wraps the recurring boilerplate every long-running call site
/// re-implements: log a start line, capture elapsed time, log a single
/// completion or failure line at the end, and tag every log line in between
/// with a shared correlation id.
/// </para>
/// <para>
/// Typical adoption pattern:
/// <code>
/// using var op = OperationScope.Begin("MsalAuth.AcquireTokenSilent");
/// try
/// {
///     var result = await DoTheWork();
///     op.Complete($"tenant={tenantId}");
///     return result;
/// }
/// catch (Exception ex)
/// {
///     op.Fail(ex);
///     throw;
/// }
/// </code>
/// On disposal the scope auto-completes with no note if neither
/// <see cref="Complete"/> nor <see cref="Fail"/> was called - that&#x2019;s
/// the safety net for early returns. Disposal also pops the correlation id
/// so log lines outside the scope go back to whatever correlation was
/// active before <see cref="Begin"/>.
/// </para>
/// <para>
/// Backed by <see cref="SystemClock.UtcNow"/> for elapsed timing so tests
/// can install a <c>FakeTimeProvider</c> and make assertions about
/// "completed in 1.2s" lines without <c>Thread.Sleep</c>. Backed by
/// <see cref="AppLogger.BeginCorrelation"/> so every log call inside the
/// scope - not just <see cref="Complete"/> / <see cref="Fail"/> - carries
/// the same correlation prefix.
/// </para>
/// </summary>
public sealed class OperationScope : IDisposable
{
    private readonly IAppLogger _log;
    private readonly string _operation;
    private readonly DateTime _startedUtc;
    private readonly AppLogger.CorrelationScope _correlation;
    private bool _completed;
    private bool _disposed;

    private OperationScope(string operation, IAppLogger log)
    {
        _operation = operation;
        _log = log;
        _startedUtc = SystemClock.UtcNow;
        _correlation = AppLogger.BeginCorrelation();
        _log.Info($"{_operation}: started");
    }

    /// <summary>
    /// Begins a new operation scope. Logs <c>"{operation}: started"</c> at
    /// Info and starts a correlation scope so every log line until
    /// <see cref="Dispose"/> shares the same correlation id.
    /// </summary>
    /// <param name="operation">
    /// Short stable identifier for the operation (e.g.
    /// <c>"MsalAuth.AcquireTokenSilent"</c>). Used as the prefix on the
    /// start / complete / fail log lines.
    /// </param>
    /// <param name="log">
    /// Optional logger override. Defaults to <see cref="AppLogger.Instance"/>.
    /// Tests inject a capturing fake here.
    /// </param>
    public static OperationScope Begin(string operation, IAppLogger? log = null)
        => new OperationScope(operation, log ?? AppLogger.Instance);

    /// <summary>
    /// Marks the scope as successfully completed. Logs
    /// <c>"{operation}: completed in Xs[ - {note}]"</c> at Info. Idempotent
    /// - subsequent calls (or <see cref="Fail"/>) are ignored.
    /// </summary>
    public void Complete(string? note = null)
    {
        if (_completed) return;
        _completed = true;
        var elapsed = FormatElapsed(SystemClock.UtcNow - _startedUtc);
        _log.Info(string.IsNullOrEmpty(note)
            ? $"{_operation}: completed in {elapsed}"
            : $"{_operation}: completed in {elapsed} -- {note}");
    }

    /// <summary>
    /// Marks the scope as failed. Logs
    /// <c>"{operation}: failed after Xs[ in {extraContext}]"</c> via
    /// <see cref="IAppLogger.Exception"/> so the full stack trace lands in
    /// the file. Idempotent.
    /// </summary>
    /// <param name="ex">The exception that ended the operation.</param>
    /// <param name="extraContext">
    /// Optional extra detail (e.g. tenant id, app id) appended to the log
    /// context. Must be redaction-safe - do NOT include unredacted
    /// tokens or secrets.
    /// </param>
    public void Fail(Exception ex, string? extraContext = null)
    {
        if (_completed) return;
        _completed = true;
        var elapsed = FormatElapsed(SystemClock.UtcNow - _startedUtc);
        var ctx = string.IsNullOrEmpty(extraContext)
            ? $"{_operation}: failed after {elapsed}"
            : $"{_operation}: failed after {elapsed} in {extraContext}";
        _log.Exception(ctx, ex);
    }

    /// <summary>
    /// Pops the correlation scope; if neither <see cref="Complete"/> nor
    /// <see cref="Fail"/> was called, logs an auto-completion line so the
    /// scope&#x2019;s end is always visible in the log even on early returns.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_completed)
        {
            var elapsed = FormatElapsed(SystemClock.UtcNow - _startedUtc);
            _log.Info($"{_operation}: completed in {elapsed} (auto)");
        }
        _correlation.Dispose();
    }

    private static string FormatElapsed(TimeSpan ts)
    {
        if (ts.TotalSeconds < 1) return $"{ts.TotalMilliseconds:F0}ms";
        if (ts.TotalSeconds < 60) return $"{ts.TotalSeconds:F1}s";
        return $"{(int)ts.TotalMinutes}m{ts.Seconds:D2}s";
    }
}
