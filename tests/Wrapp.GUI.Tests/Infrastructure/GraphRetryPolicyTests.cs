using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Unit tests for <see cref="GraphRetryPolicy"/>. All timing is injected
/// (a no-op delay that records requested waits) so the suite never sleeps,
/// and the HTTP-date branch pins <see cref="SystemClock"/> via
/// <see cref="FakeTimeProvider"/>.
/// </summary>
public class GraphRetryPolicyTests
{
    private static HttpResponseMessage Resp(HttpStatusCode code, string? body = null)
    {
        var r = new HttpResponseMessage(code);
        if (body is not null) r.Content = new StringContent(body);
        return r;
    }

    // ── IsRetryableStatus ───────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    public void IsRetryableStatus_ClassifiesTransientVsPermanent(HttpStatusCode code, bool expected)
        => Assert.Equal(expected, GraphRetryPolicy.IsRetryableStatus(code));

    // ── TryGetRetryAfter ────────────────────────────────────────────

    [Fact]
    public void TryGetRetryAfter_DeltaSeconds_Parses()
    {
        using var resp = Resp(HttpStatusCode.TooManyRequests);
        resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(12));

        Assert.True(GraphRetryPolicy.TryGetRetryAfter(resp, out var delay));
        Assert.Equal(TimeSpan.FromSeconds(12), delay);
    }

    [Fact]
    public void TryGetRetryAfter_HttpDateInFuture_Parses()
    {
        var start = DateTimeOffset.Parse("2026-04-22T10:00:00Z");
        using (SystemClock.Override(new FakeTimeProvider(start)))
        {
            using var resp = Resp(HttpStatusCode.ServiceUnavailable);
            resp.Headers.RetryAfter = new RetryConditionHeaderValue(start.AddSeconds(30));

            Assert.True(GraphRetryPolicy.TryGetRetryAfter(resp, out var delay));
            // Allow a tolerance for sub-second rounding in header formatting.
            Assert.InRange(delay.TotalSeconds, 29, 31);
        }
    }

    [Fact]
    public void TryGetRetryAfter_HttpDateInPast_ReturnsFalse()
    {
        var start = DateTimeOffset.Parse("2026-04-22T10:00:00Z");
        using (SystemClock.Override(new FakeTimeProvider(start)))
        {
            using var resp = Resp(HttpStatusCode.ServiceUnavailable);
            resp.Headers.RetryAfter = new RetryConditionHeaderValue(start.AddSeconds(-30));

            Assert.False(GraphRetryPolicy.TryGetRetryAfter(resp, out var delay));
            Assert.Equal(TimeSpan.Zero, delay);
        }
    }

    [Fact]
    public void TryGetRetryAfter_Absent_ReturnsFalse()
    {
        using var resp = Resp(HttpStatusCode.TooManyRequests);
        Assert.False(GraphRetryPolicy.TryGetRetryAfter(resp, out var delay));
        Assert.Equal(TimeSpan.Zero, delay);
    }

    // ── Backoff ─────────────────────────────────────────────────────

    [Fact]
    public void Backoff_StaysWithinExponentialCeiling_AndGrows()
    {
        // Zero-jitter RNG makes the ceiling deterministic (NextDouble → 0 gives 0;
        // we assert the ceiling instead by using a max-jitter stub).
        var maxRng = new StubRandom(0.999999);
        var baseDelay = TimeSpan.FromSeconds(1);
        var cap = TimeSpan.FromSeconds(30);

        var a1 = GraphRetryPolicy.Backoff(1, baseDelay, cap, maxRng); // ceiling ~1s
        var a2 = GraphRetryPolicy.Backoff(2, baseDelay, cap, maxRng); // ceiling ~2s
        var a3 = GraphRetryPolicy.Backoff(3, baseDelay, cap, maxRng); // ceiling ~4s

        Assert.True(a1 <= TimeSpan.FromSeconds(1));
        Assert.True(a2 <= TimeSpan.FromSeconds(2) && a2 > a1);
        Assert.True(a3 <= TimeSpan.FromSeconds(4) && a3 > a2);
    }

    [Fact]
    public void Backoff_NeverExceedsCap()
    {
        var maxRng = new StubRandom(0.999999);
        var delay = GraphRetryPolicy.Backoff(20, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), maxRng);
        Assert.True(delay <= TimeSpan.FromSeconds(30));
    }

    // ── SendAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_RetriesTransientThenSucceeds()
    {
        int calls = 0;
        var waits = new List<TimeSpan>();

        var result = await GraphRetryPolicy.SendAsync(
            send: _ =>
            {
                calls++;
                return Task.FromResult(Resp(calls < 3 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK));
            },
            delayAsync: (ts, _) => { waits.Add(ts); return Task.CompletedTask; });

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(3, calls);      // 429, 429, 200
        Assert.Equal(2, waits.Count); // waited before attempts 2 and 3
    }

    [Fact]
    public async Task SendAsync_NonRetryable_ReturnsImmediately()
    {
        int calls = 0;
        var result = await GraphRetryPolicy.SendAsync(
            send: _ => { calls++; return Task.FromResult(Resp(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"BadRequest\",\"message\":\"nope\"}}")); },
            delayAsync: (_, _) => Task.CompletedTask);

        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal(1, calls); // no retry on 400
    }

    [Fact]
    public async Task SendAsync_ExhaustsAttempts_ReturnsLastResponse()
    {
        int calls = 0;
        var result = await GraphRetryPolicy.SendAsync(
            send: _ => { calls++; return Task.FromResult(Resp(HttpStatusCode.ServiceUnavailable)); },
            maxAttempts: 4,
            delayAsync: (_, _) => Task.CompletedTask);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task SendAsync_HonorsRetryAfterOverBackoff()
    {
        var waits = new List<TimeSpan>();
        int calls = 0;

        await GraphRetryPolicy.SendAsync(
            send: _ =>
            {
                calls++;
                if (calls == 1)
                {
                    var r = Resp(HttpStatusCode.TooManyRequests);
                    r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
                    return Task.FromResult(r);
                }
                return Task.FromResult(Resp(HttpStatusCode.OK));
            },
            // Small backoff cap so Retry-After (20s) must win.
            backoffCap: TimeSpan.FromSeconds(2),
            delayAsync: (ts, _) => { waits.Add(ts); return Task.CompletedTask; });

        Assert.Single(waits);
        Assert.Equal(TimeSpan.FromSeconds(20), waits[0]);
    }

    [Fact]
    public async Task SendAsync_RetriesNetworkExceptionThenSucceeds()
    {
        int calls = 0;
        var result = await GraphRetryPolicy.SendAsync(
            send: _ =>
            {
                calls++;
                if (calls == 1) throw new HttpRequestException("connection reset");
                return Task.FromResult(Resp(HttpStatusCode.OK));
            },
            delayAsync: (_, _) => Task.CompletedTask);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SendAsync_NetworkExceptionOnLastAttempt_Propagates()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => GraphRetryPolicy.SendAsync(
            send: _ => throw new HttpRequestException("down"),
            maxAttempts: 2,
            delayAsync: (_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task SendAsync_CancellationIsObserved()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => GraphRetryPolicy.SendAsync(
            send: _ => Task.FromResult(Resp(HttpStatusCode.OK)),
            ct: cts.Token,
            delayAsync: (_, _) => Task.CompletedTask));
    }

    /// <summary>Deterministic <see cref="Random"/> returning a fixed NextDouble.</summary>
    private sealed class StubRandom : Random
    {
        private readonly double _value;
        public StubRandom(double value) { _value = value; }
        public override double NextDouble() => _value;
    }
}
