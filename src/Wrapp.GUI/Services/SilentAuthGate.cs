namespace Wrapp.Services;

/// <summary>How a silent token attempt for a tenant ended.</summary>
public enum SilentAttemptOutcome
{
    /// <summary>A token was acquired.</summary>
    Success,
    /// <summary>MSAL needs interactive sign-in (or has no cached account) —
    /// retrying silently is pointless until the user signs in.</summary>
    UiRequired,
    /// <summary>Network / timeout / unexpected failure — retrying later may
    /// succeed on its own.</summary>
    Transient,
}

/// <summary>
/// Decides when a <em>silent</em> MSAL attempt for a tenant is worth making.
///
/// <para>Diagnosis behind this type (0.6.322 field logs): a 15-second timer
/// retried silent acquisition for a tenant stuck in <c>ui-required</c>
/// ~5,760×/day on the dispatcher — every "Not Responding" stall in three days
/// of logs traced to those calls. Silent auth for a ui-required tenant cannot
/// succeed until the user signs in interactively, so the only sane retry
/// policy is: don't — until an event says the world changed.</para>
///
/// <para><see cref="SilentAttemptOutcome.UiRequired"/> blocks further attempts
/// until <see cref="Unlock"/>/<see cref="UnlockAll"/> (callers fire these on
/// interactive sign-in, network-restored, resume-from-sleep, tenant-list
/// changes, and token expiry). <see cref="SilentAttemptOutcome.Transient"/>
/// backs off exponentially (30s → 5m cap) instead of hammering a dead
/// network. Success clears all state for the tenant.</para>
///
/// <para>Pure decision logic — callers pass the clock — so the policy is unit
/// testable. Thread-safe: recorded from background acquisition contexts,
/// queried from the dispatcher.</para>
/// </summary>
public sealed class SilentAuthGate
{
    private static readonly TimeSpan[] TransientBackoff =
    {
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(4),
    };
    private static readonly TimeSpan TransientBackoffCap = TimeSpan.FromMinutes(5);

    private sealed class Entry
    {
        public SilentAttemptOutcome LastOutcome;
        public DateTime NextAllowedUtc;
        public int TransientFailures;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _tenants = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when a silent attempt for the tenant is currently worth making.</summary>
    public bool ShouldAttempt(string tenantId, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return false;
        lock (_lock)
        {
            if (!_tenants.TryGetValue(tenantId, out var entry)) return true; // untried
            return entry.LastOutcome switch
            {
                SilentAttemptOutcome.UiRequired => false,           // wait for an unlock event
                SilentAttemptOutcome.Transient  => utcNow >= entry.NextAllowedUtc,
                _                               => true,
            };
        }
    }

    /// <summary>Records how an attempt ended; drives the next <see cref="ShouldAttempt"/>.</summary>
    public void Record(string tenantId, SilentAttemptOutcome outcome, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return;
        lock (_lock)
        {
            if (outcome == SilentAttemptOutcome.Success)
            {
                _tenants.Remove(tenantId);
                return;
            }

            if (!_tenants.TryGetValue(tenantId, out var entry))
                _tenants[tenantId] = entry = new Entry();

            entry.LastOutcome = outcome;
            if (outcome == SilentAttemptOutcome.Transient)
            {
                var step = entry.TransientFailures < TransientBackoff.Length
                    ? TransientBackoff[entry.TransientFailures]
                    : TransientBackoffCap;
                entry.TransientFailures++;
                entry.NextAllowedUtc = utcNow + step;
            }
        }
    }

    /// <summary>Makes the tenant attemptable again (e.g. its token just expired).</summary>
    public void Unlock(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return;
        lock (_lock) _tenants.Remove(tenantId);
    }

    /// <summary>Makes every tenant attemptable again (network restored, resume, sign-in).</summary>
    public void UnlockAll()
    {
        lock (_lock) _tenants.Clear();
    }
}

/// <summary>Result of a detailed silent acquisition: the token (when
/// <see cref="SilentAttemptOutcome.Success"/>) plus the outcome class the
/// <see cref="SilentAuthGate"/> needs to schedule retries.</summary>
public readonly record struct SilentTokenOutcome(
    Wrapp.Models.MsalTokenResult? Token,
    SilentAttemptOutcome Outcome);
