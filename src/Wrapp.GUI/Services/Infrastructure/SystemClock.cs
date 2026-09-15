namespace Wrapp.Services;

/// <summary>
/// <para>
/// Replacement for <c>DateTime.Now</c> across the codebase. Routes every
/// wall-clock read through a swappable <see cref="TimeProvider"/> so tests
/// can install a <c>FakeTimeProvider</c> (Microsoft.Extensions.Time.Testing)
/// and make assertions about timestamps, elapsed durations, and expiry
/// calculations without <c>Thread.Sleep</c>.
/// </para>
/// <para>
/// Migration rule: production code calls <see cref="Now"/> / <see cref="UtcNow"/>
/// / <see cref="OffsetNow"/> instead of <c>DateTime.Now</c> / <c>DateTime.UtcNow</c>
/// / <c>DateTimeOffset.Now</c>. A callback-free test can do:
/// <code>
/// var fake = new FakeTimeProvider(startsAt: DateTimeOffset.Parse("2026-04-22T10:00:00Z"));
/// using (SystemClock.Override(fake))
/// {
///     // code under test reads SystemClock.Now and gets 2026-04-22 10:00:00
///     fake.Advance(TimeSpan.FromHours(1));
///     // next read returns 11:00:00
/// }
/// </code>
/// </para>
/// <para>
/// The <see cref="Override"/> scope uses <see cref="IDisposable"/> so tests
/// can't forget to reset the clock - the <c>using</c> restores the previous
/// provider on dispose, even on test failure.
/// </para>
/// </summary>
public static class SystemClock
{
    private static TimeProvider _provider = TimeProvider.System;

    /// <summary>
    /// Current local time. Replaces <c>DateTime.Now</c>.
    /// </summary>
    public static DateTime Now => _provider.GetLocalNow().DateTime;

    /// <summary>
    /// Current UTC time. Replaces <c>DateTime.UtcNow</c>.
    /// </summary>
    public static DateTime UtcNow => _provider.GetUtcNow().DateTime;

    /// <summary>
    /// Current local time as a <see cref="DateTimeOffset"/>. Replaces
    /// <c>DateTimeOffset.Now</c>.
    /// </summary>
    public static DateTimeOffset OffsetNow => _provider.GetLocalNow();

    /// <summary>
    /// Current UTC <see cref="DateTimeOffset"/>. Replaces <c>DateTimeOffset.UtcNow</c>.
    /// </summary>
    public static DateTimeOffset UtcOffsetNow => _provider.GetUtcNow();

    /// <summary>
    /// Installs a fake <see cref="TimeProvider"/> for the duration of the
    /// returned scope. Dispose restores the previous provider. Intended for
    /// tests only; not thread-safe across concurrent tests (xUnit runs
    /// collection members sequentially by default, which is what we want).
    /// </summary>
    public static IDisposable Override(TimeProvider provider)
    {
        var previous = _provider;
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        return new OverrideScope(previous);
    }

    private sealed class OverrideScope : IDisposable
    {
        private readonly TimeProvider _previous;
        private bool _disposed;
        internal OverrideScope(TimeProvider previous) { _previous = previous; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _provider = _previous;
        }
    }
}
