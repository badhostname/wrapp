namespace Wrapp.Tests;

/// <summary>
/// Minimal in-test <see cref="TimeProvider"/> that lets tests advance
/// wall-clock deterministically via <see cref="Advance(TimeSpan)"/>.
/// Modeled after Microsoft.Extensions.Time.Testing.FakeTimeProvider but
/// kept local so we don't pull the whole Microsoft.Extensions.Time.Testing
/// package just for the one API we need.
/// </summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset startsAt) { _now = startsAt; }

    public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();

    /// <summary>Moves the fake wall-clock forward by the given delta.</summary>
    public void Advance(TimeSpan delta) { _now = _now.Add(delta); }
}
