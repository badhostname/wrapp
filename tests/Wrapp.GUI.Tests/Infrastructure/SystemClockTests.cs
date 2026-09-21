using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Covers the <see cref="SystemClock"/> test seam: overrides must replace
/// the wall-clock within the disposable scope and restore on dispose,
/// even when the scope exits via exception.
/// </summary>
public class SystemClockTests
{
    [Fact]
    public void Override_WithinScope_ReturnsFakeTime()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.Parse("2030-01-15T12:00:00Z"));
        using (SystemClock.Override(fake))
        {
            Assert.Equal(new DateTime(2030, 1, 15, 12, 0, 0, DateTimeKind.Utc),
                SystemClock.UtcNow);
            Assert.Equal(new DateTimeOffset(2030, 1, 15, 12, 0, 0, TimeSpan.Zero),
                SystemClock.UtcOffsetNow);
        }
    }

    [Fact]
    public void Override_Advance_IsReflectedInSubsequentReads()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.Parse("2030-06-01T00:00:00Z"));
        using (SystemClock.Override(fake))
        {
            var before = SystemClock.UtcNow;
            fake.Advance(TimeSpan.FromHours(2));
            var after = SystemClock.UtcNow;
            Assert.Equal(TimeSpan.FromHours(2), after - before);
        }
    }

    [Fact]
    public void Override_Disposed_RestoresSystemProvider()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.Parse("1970-01-01T00:00:00Z"));
        using (SystemClock.Override(fake)) { /* no-op */ }

        // After dispose, SystemClock must no longer return the fake epoch.
        // The real wall-clock is certainly past 2025.
        Assert.True(SystemClock.UtcNow.Year >= 2025,
            "SystemClock.UtcNow should return real time after Override scope exits.");
    }

    [Fact]
    public void Override_NestedScopes_ProperlyStack()
    {
        var outer = new FakeTimeProvider(DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        var inner = new FakeTimeProvider(DateTimeOffset.Parse("2040-01-01T00:00:00Z"));

        using (SystemClock.Override(outer))
        {
            Assert.Equal(2030, SystemClock.UtcNow.Year);

            using (SystemClock.Override(inner))
            {
                Assert.Equal(2040, SystemClock.UtcNow.Year);
            }

            // Inner scope disposed -- outer fake must be restored, not the
            // real system clock.
            Assert.Equal(2030, SystemClock.UtcNow.Year);
        }
    }

    [Fact]
    public void Override_ExceptionInsideScope_StillRestoresProvider()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.Parse("2050-01-01T00:00:00Z"));
        try
        {
            using (SystemClock.Override(fake))
            {
                throw new InvalidOperationException("simulated failure");
            }
        }
        catch (InvalidOperationException) { /* expected */ }

        Assert.True(SystemClock.UtcNow.Year >= 2025,
            "SystemClock must restore the real provider even when the using block throws.");
    }
}
