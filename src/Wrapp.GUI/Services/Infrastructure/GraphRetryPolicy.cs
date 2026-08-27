using System.Net;
using System.Net.Http;

namespace Wrapp.Services;

/// <summary>
/// Retry policy for wrapp's own .NET <see cref="HttpClient"/> calls to
/// Microsoft Graph / Azure. Honors the <c>Retry-After</c> header on
/// throttling responses (429/503) - delta-seconds <em>or</em> HTTP-date -
/// falling back to exponential backoff with full jitter, and captures the
/// Graph error body (<c>error.code</c> / <c>error.message</c>) for logging.
///
/// <para>This improves on the vendored IntuneWin32App module's blind
/// 7–30 s retry (which ignores <c>Retry-After</c> and swallows the error
/// body). The bulk of wrapp's Graph traffic goes through PowerShell
/// <c>Invoke-RestMethod</c> (which honors Retry-After via
/// <c>-MaximumRetryCount</c>); this helper covers the .NET
/// <see cref="HttpClient"/> paths and is the seam to reuse if the upload
/// path ever moves into .NET.</para>
///
/// <para>Parsing and backoff are pure static methods so they can be unit
/// tested without I/O; <see cref="SendAsync"/> takes an injectable delay so
/// tests never actually sleep.</para>
/// </summary>
public static class GraphRetryPolicy
{
    public const int DefaultMaxAttempts = 5;

    private static readonly HashSet<HttpStatusCode> RetryableStatuses = new()
    {
        HttpStatusCode.TooManyRequests,    // 429
        HttpStatusCode.ServiceUnavailable, // 503
        HttpStatusCode.BadGateway,         // 502
        HttpStatusCode.GatewayTimeout,     // 504
    };

    /// <summary>True if <paramref name="code"/> is a transient status worth retrying.</summary>
    public static bool IsRetryableStatus(HttpStatusCode code) => RetryableStatuses.Contains(code);

    /// <summary>
    /// Extracts the server's requested wait from a <c>Retry-After</c> header,
    /// supporting both the delta-seconds form and the HTTP-date form. Returns
    /// false when the header is absent, malformed, or already in the past.
    /// The HTTP-date form is measured against <see cref="SystemClock"/> so
    /// tests can pin the clock.
    /// </summary>
    public static bool TryGetRetryAfter(HttpResponseMessage response, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        var ra = response.Headers.RetryAfter;
        if (ra is null) return false;

        if (ra.Delta is TimeSpan d && d > TimeSpan.Zero)
        {
            delay = d;
            return true;
        }
        if (ra.Date is DateTimeOffset when)
        {
            var diff = when - SystemClock.UtcOffsetNow;
            if (diff > TimeSpan.Zero)
            {
                delay = diff;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Exponential backoff with full jitter: a random value in
    /// <c>[0, min(cap, base·2^(attempt-1)))</c>. <paramref name="attempt"/> is
    /// 1-based. Full jitter (AWS "exponential backoff and jitter") spreads
    /// concurrent retries and avoids thundering-herd re-throttling.
    /// </summary>
    public static TimeSpan Backoff(int attempt, TimeSpan? baseDelay = null, TimeSpan? cap = null, Random? rng = null)
    {
        var b = (baseDelay ?? TimeSpan.FromSeconds(1)).TotalMilliseconds;
        var c = (cap ?? TimeSpan.FromSeconds(30)).TotalMilliseconds;
        var ceiling = Math.Min(c, b * Math.Pow(2, Math.Max(0, attempt - 1)));
        var jittered = (rng ?? Random.Shared).NextDouble() * ceiling;
        return TimeSpan.FromMilliseconds(jittered);
    }

    /// <summary>
    /// Sends via <paramref name="send"/>, retrying transient failures (429/503/
    /// 502/504 and network <see cref="HttpRequestException"/>) up to
    /// <paramref name="maxAttempts"/> times, waiting the greater of the
    /// server's <c>Retry-After</c> and the jittered backoff between attempts.
    /// Returns the final <see cref="HttpResponseMessage"/> - success or not -
    /// so callers keep their own status handling. Non-retryable responses are
    /// returned immediately.
    /// </summary>
    /// <param name="send">Issues one attempt. Receives the cancellation token.</param>
    /// <param name="log">Optional sink for retry/error diagnostics.</param>
    /// <param name="delayAsync">Injectable delay (tests pass a no-op).</param>
    public static async Task<HttpResponseMessage> SendAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken ct = default,
        int maxAttempts = DefaultMaxAttempts,
        TimeSpan? backoffCap = null,
        Action<string>? log = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        delayAsync ??= static (ts, c) => Task.Delay(ts, c);
        HttpResponseMessage? response = null;

        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                response?.Dispose();
                response = await send(ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts)
            {
                var wait = Backoff(attempt, cap: backoffCap);
                log?.Invoke($"GraphRetry: network error (attempt {attempt}/{maxAttempts}): {ex.Message}; retrying in {wait.TotalSeconds:F1}s");
                await delayAsync(wait, ct).ConfigureAwait(false);
                continue;
            }

            // Terminal: success, non-retryable, or out of attempts.
            if (response.IsSuccessStatusCode || !IsRetryableStatus(response.StatusCode) || attempt >= maxAttempts)
            {
                if (!response.IsSuccessStatusCode && log is not null)
                {
                    var body = await SafeReadErrorAsync(response).ConfigureAwait(false);
                    log($"GraphRetry: {(int)response.StatusCode} {response.StatusCode} after {attempt} attempt(s)"
                        + (body is null ? "" : $" -- {body}"));
                }
                return response;
            }

            var backoff = Backoff(attempt, cap: backoffCap);
            var delay = TryGetRetryAfter(response, out var ra) && ra > backoff ? ra : backoff;
            log?.Invoke($"GraphRetry: {(int)response.StatusCode} {response.StatusCode} (attempt {attempt}/{maxAttempts}); retrying in {delay.TotalSeconds:F1}s");
            await delayAsync(delay, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Best-effort read of a Graph error body for logging. Extracts
    /// <c>error.code</c>/<c>error.message</c> when the payload is the standard
    /// Graph error envelope; otherwise returns the raw text. Never throws, and
    /// truncates to keep logs readable. Only called on buffered (non-streamed)
    /// responses.
    /// </summary>
    private static async Task<string?> SafeReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) return null;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("error", out var err))
                {
                    var code = err.TryGetProperty("code", out var c) ? c.GetString() : null;
                    var msg = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                    var combined = string.Join(": ", new[] { code, msg }.Where(s => !string.IsNullOrEmpty(s)));
                    if (!string.IsNullOrEmpty(combined)) return Truncate(combined, 500);
                }
            }
            catch { /* body is not JSON - fall through to raw */ }

            return Truncate(raw, 500);
        }
        catch { return null; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");
}
