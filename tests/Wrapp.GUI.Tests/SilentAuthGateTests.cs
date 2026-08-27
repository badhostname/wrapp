using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Phase A (update-flow-and-token-polling-plan): the retry policy that
/// replaced the 15-second silent-auth poll. The contract under test: a
/// ui-required tenant is never retried until an unlock event; transient
/// failures back off exponentially (30s → 5m cap); success clears all state.
/// </summary>
public class SilentAuthGateTests
{
    private static readonly DateTime T0 = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
    private const string Tenant = "d35fe7ad-abdf-4422-8ef9-8234b4c7a904";

    // ------------------------------------------------------------------
    // First contact
    // ------------------------------------------------------------------

    [Fact]
    public void UntriedTenant_ShouldAttempt()
    {
        var gate = new SilentAuthGate();
        Assert.True(gate.ShouldAttempt(Tenant, T0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankTenant_NeverAttempts(string? tenantId)
    {
        var gate = new SilentAuthGate();
        Assert.False(gate.ShouldAttempt(tenantId!, T0));
    }

    // ------------------------------------------------------------------
    // ui-required: silence until an unlock event
    // ------------------------------------------------------------------

    [Fact]
    public void UiRequired_BlocksIndefinitely()
    {
        var gate = new SilentAuthGate();
        gate.Record(Tenant, SilentAttemptOutcome.UiRequired, T0);

        Assert.False(gate.ShouldAttempt(Tenant, T0));
        Assert.False(gate.ShouldAttempt(Tenant, T0.AddHours(6)));
        Assert.False(gate.ShouldAttempt(Tenant, T0.AddDays(30)));
    }

    [Fact]
    public void UiRequired_UnlockReopensTheTenant()
    {
        var gate = new SilentAuthGate();
        gate.Record(Tenant, SilentAttemptOutcome.UiRequired, T0);

        gate.Unlock(Tenant);
        Assert.True(gate.ShouldAttempt(Tenant, T0));
    }

    [Fact]
    public void UiRequired_UnlockAllReopensEveryTenant()
    {
        var gate = new SilentAuthGate();
        gate.Record(Tenant, SilentAttemptOutcome.UiRequired, T0);
        gate.Record("second-tenant", SilentAttemptOutcome.UiRequired, T0);

        gate.UnlockAll();
        Assert.True(gate.ShouldAttempt(Tenant, T0));
        Assert.True(gate.ShouldAttempt("second-tenant", T0));
    }

    [Fact]
    public void TenantKeys_AreCaseInsensitive()
    {
        var gate = new SilentAuthGate();
        gate.Record(Tenant.ToUpperInvariant(), SilentAttemptOutcome.UiRequired, T0);
        Assert.False(gate.ShouldAttempt(Tenant, T0));
    }

    [Fact]
    public void Unlock_UnknownTenant_IsHarmless()
    {
        var gate = new SilentAuthGate();
        gate.Unlock("never-seen");
        gate.UnlockAll();
        Assert.True(gate.ShouldAttempt("never-seen", T0));
    }

    // ------------------------------------------------------------------
    // Transient: exponential backoff, 30s → 5m cap
    // ------------------------------------------------------------------

    [Fact]
    public void Transient_FirstFailure_Backs30Seconds()
    {
        var gate = new SilentAuthGate();
        gate.Record(Tenant, SilentAttemptOutcome.Transient, T0);

        Assert.False(gate.ShouldAttempt(Tenant, T0));
        Assert.False(gate.ShouldAttempt(Tenant, T0.AddSeconds(29)));
        Assert.True(gate.ShouldAttempt(Tenant, T0.AddSeconds(30)));
    }

    [Fact]
    public void Transient_BackoffDoublesThenCaps()
    {
        var gate = new SilentAuthGate();
        var now = T0;
        // Expected waits per consecutive failure: 30s, 1m, 2m, 4m, then 5m cap.
        var expected = new[] { 30, 60, 120, 240, 300, 300 };

        foreach (var waitSeconds in expected)
        {
            gate.Record(Tenant, SilentAttemptOutcome.Transient, now);
            Assert.False(gate.ShouldAttempt(Tenant, now.AddSeconds(waitSeconds - 1)));
            Assert.True(gate.ShouldAttempt(Tenant, now.AddSeconds(waitSeconds)));
            now = now.AddSeconds(waitSeconds);
        }
    }

    [Fact]
    public void Transient_ThenUiRequired_BlocksIndefinitely()
    {
        // A network blip followed by a real ui-required verdict must not keep
        // the transient backoff schedule — it must go quiet entirely.
        var gate = new SilentAuthGate();
        gate.Record(Tenant, SilentAttemptOutcome.Transient, T0);
        gate.Record(Tenant, SilentAttemptOutcome.UiRequired, T0.AddSeconds(30));

        Assert.False(gate.ShouldAttempt(Tenant, T0.AddDays(1)));
    }

    // ------------------------------------------------------------------
    // Success: clean slate
    // ------------------------------------------------------------------

    [Fact]
    public void Success_ResetsBackoffProgression()
    {
        var gate = new SilentAuthGate();
        gate.Record(Tenant, SilentAttemptOutcome.Transient, T0);
        gate.Record(Tenant, SilentAttemptOutcome.Transient, T0.AddMinutes(1));
        gate.Record(Tenant, SilentAttemptOutcome.Success, T0.AddMinutes(3));

        // Next failure starts the schedule over at 30s, not at the 2m step.
        var t = T0.AddMinutes(10);
        gate.Record(Tenant, SilentAttemptOutcome.Transient, t);
        Assert.True(gate.ShouldAttempt(Tenant, t.AddSeconds(30)));
        Assert.False(gate.ShouldAttempt(Tenant, t.AddSeconds(29)));
    }

    [Fact]
    public void Success_ClearsUiRequired()
    {
        // Interactive sign-in succeeded elsewhere (TokenAcquired event):
        // the tenant is attemptable again without an explicit Unlock.
        var gate = new SilentAuthGate();
        gate.Record(Tenant, SilentAttemptOutcome.UiRequired, T0);
        gate.Record(Tenant, SilentAttemptOutcome.Success, T0.AddMinutes(5));

        Assert.True(gate.ShouldAttempt(Tenant, T0.AddMinutes(5)));
    }

    [Fact]
    public void GateStates_AreIndependentPerTenant()
    {
        var gate = new SilentAuthGate();
        gate.Record(Tenant, SilentAttemptOutcome.UiRequired, T0);
        gate.Record("other", SilentAttemptOutcome.Transient, T0);

        Assert.False(gate.ShouldAttempt(Tenant, T0.AddHours(1)));
        Assert.True(gate.ShouldAttempt("other", T0.AddHours(1)));

        gate.Unlock("other");                       // no effect on Tenant
        Assert.False(gate.ShouldAttempt(Tenant, T0.AddHours(1)));
    }
}
