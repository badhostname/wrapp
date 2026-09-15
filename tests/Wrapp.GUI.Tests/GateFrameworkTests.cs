using System;
using System.Threading.Tasks;
using Wrapp.Models;
using Wrapp.Services;
using Wrapp.Services.Gates;

namespace Wrapp.Tests;

/// <summary>
/// Covers the gate framework (2026-07): the pending-required-action mechanism
/// that surfaces re-approval / consent / migration prompts after a code, config
/// or version change - deliberately separate from the "dirty" (unsaved-edits)
/// flag. Exercises <see cref="GateState"/> persistence, <see cref="GateService"/>
/// orchestration (advisory enumeration, blocking resolution, run recording via
/// an injected save callback so no disk I/O happens), the
/// <see cref="VaultUrlApprovalGate"/> pending matrix, and that the
/// <see cref="LiabilityWaiverGate"/> scaffold is inert until enabled.
/// </summary>
public class GateFrameworkTests
{
    private sealed class FakeGate : IAppGate
    {
        public string Id { get; init; } = "fake";
        public GateKind Kind { get; init; } = GateKind.Advisory;
        public string Title { get; init; } = "Fake";
        public Func<AppSettings, bool> PendingFunc { get; init; } = _ => false;
        public Func<AppSettings, Task<bool>> ResolveFunc { get; init; } = _ => Task.FromResult(true);
        public int ResolveCalls { get; private set; }

        public bool IsPending(AppSettings s) => PendingFunc(s);
        public Task<bool> ResolveAsync(AppSettings s) { ResolveCalls++; return ResolveFunc(s); }
    }

    // ── GateState persistence ──────────────────────────────────────────

    [Fact]
    public void GateState_Int_RoundTrips()
    {
        var s = new AppSettings();
        Assert.Equal(0, GateState.GetInt(s, "k"));
        GateState.SetInt(s, "k", 3);
        Assert.Equal(3, GateState.GetInt(s, "k"));
        Assert.Equal("3", s.GateState["k"]);
    }

    [Fact]
    public void GateState_Bool_RoundTrips()
    {
        var s = new AppSettings();
        Assert.False(GateState.GetBool(s, "k"));
        GateState.SetBool(s, "k", true);
        Assert.True(GateState.GetBool(s, "k"));
    }

    // ── GateService orchestration ──────────────────────────────────────

    [Fact]
    public void PendingAdvisory_ReturnsOnlyPendingAdvisoryGates()
    {
        var s = new AppSettings();
        var advisoryPending  = new FakeGate { Id = "a", Kind = GateKind.Advisory, PendingFunc = _ => true };
        var advisoryClear    = new FakeGate { Id = "b", Kind = GateKind.Advisory, PendingFunc = _ => false };
        var blockingPending  = new FakeGate { Id = "c", Kind = GateKind.Blocking, PendingFunc = _ => true };
        var svc = new GateService(s, new IAppGate[] { advisoryPending, advisoryClear, blockingPending }, _ => { });

        var pending = svc.PendingAdvisory();

        Assert.Single(pending);
        Assert.Equal("a", pending[0].Id);
    }

    [Fact]
    public async Task ResolveBlocking_AllAccepted_ReturnsTrue_AndSavesEach()
    {
        var s = new AppSettings();
        var saves = 0;
        var g1 = new FakeGate { Id = "g1", Kind = GateKind.Blocking, PendingFunc = _ => true, ResolveFunc = _ => Task.FromResult(true) };
        var g2 = new FakeGate { Id = "g2", Kind = GateKind.Blocking, PendingFunc = _ => true, ResolveFunc = _ => Task.FromResult(true) };
        var svc = new GateService(s, new IAppGate[] { g1, g2 }, _ => saves++);

        var ok = await svc.ResolveBlockingAsync();

        Assert.True(ok);
        Assert.Equal(2, saves);
    }

    [Fact]
    public async Task ResolveBlocking_Declined_ReturnsFalse_AndDoesNotSaveDeclined()
    {
        var s = new AppSettings();
        var saves = 0;
        var declined = new FakeGate { Id = "g1", Kind = GateKind.Blocking, PendingFunc = _ => true, ResolveFunc = _ => Task.FromResult(false) };
        var never    = new FakeGate { Id = "g2", Kind = GateKind.Blocking, PendingFunc = _ => true, ResolveFunc = _ => Task.FromResult(true) };
        var svc = new GateService(s, new IAppGate[] { declined, never }, _ => saves++);

        var ok = await svc.ResolveBlockingAsync();

        Assert.False(ok);
        Assert.Equal(0, saves);            // declined gate not saved; second gate never reached
        Assert.Equal(0, never.ResolveCalls);
    }

    [Fact]
    public async Task ResolveBlocking_NotPending_IsSkipped()
    {
        var s = new AppSettings();
        var g = new FakeGate { Id = "g", Kind = GateKind.Blocking, PendingFunc = _ => false };
        var svc = new GateService(s, new IAppGate[] { g }, _ => { });

        Assert.True(await svc.ResolveBlockingAsync());
        Assert.Equal(0, g.ResolveCalls);
    }

    [Fact]
    public async Task ResolveAsync_Advisory_PersistsOnSuccess_NotOnNoop()
    {
        var s = new AppSettings();
        var saves = 0;
        var pending = new FakeGate { Id = "a", PendingFunc = _ => true, ResolveFunc = _ => Task.FromResult(true) };
        var svc = new GateService(s, new IAppGate[] { pending }, _ => saves++);

        Assert.True(await svc.ResolveAsync(pending));
        Assert.Equal(1, saves);

        // A gate that is no longer pending is a no-op that returns true without saving.
        var clear = new FakeGate { Id = "b", PendingFunc = _ => false };
        Assert.True(await svc.ResolveAsync(clear));
        Assert.Equal(1, saves);
    }

    [Fact]
    public void RecordRun_SetsVersion_AndSavesOnlyOnChange()
    {
        var s = new AppSettings();
        var saves = 0;
        var svc = new GateService(s, Array.Empty<IAppGate>(), _ => saves++);

        svc.RecordRun("0.6.0.246");
        Assert.Equal("0.6.0.246", s.LastRunVersion);
        Assert.Equal(1, saves);

        svc.RecordRun("0.6.0.246");        // unchanged -> no save
        Assert.Equal(1, saves);

        svc.RecordRun("0.6.0.247");        // changed -> save
        Assert.Equal(2, saves);
    }

    // ── VaultUrlApprovalGate ───────────────────────────────────────────

    [Fact]
    public void VaultGate_NotPending_WhenUrlEmpty()
    {
        var gate = new VaultUrlApprovalGate();
        Assert.False(gate.IsPending(new AppSettings { KeyVaultRepoUrl = "" }));
    }

    [Fact]
    public void VaultGate_Pending_WhenUrlSet_ButUntrusted()
    {
        var gate = new VaultUrlApprovalGate();
        var s = new AppSettings
        {
            KeyVaultRepoUrl = "https://dev.azure.com/org/proj/_git/keys",
            KeyVaultRepoUrlHash = "",            // never approved
        };
        Assert.True(gate.IsPending(s));

        s.KeyVaultRepoUrlHash = "LEGACYHEXHASH"; // pre-SEC-1 value, no longer valid
        Assert.True(gate.IsPending(s));
    }

    [Fact]
    public void VaultGate_NotPending_WhenTrustedTokenPresent()
    {
        var gate = new VaultUrlApprovalGate();
        var url = "https://dev.azure.com/org/proj/_git/keys";
        var s = new AppSettings
        {
            KeyVaultRepoUrl = url,
            KeyVaultRepoUrlHash = EncryptionKeyStoreService.IssueKeyVaultTrustToken(url),
        };
        Assert.False(gate.IsPending(s));
    }

    // ── LiabilityWaiverGate scaffold ───────────────────────────────────

    [Fact]
    public void WaiverGate_Pending_UntilAcceptedVersionRecorded()
    {
        // The waiver is active (RequiredVersion >= 1): it blocks a fresh install
        // and clears once the accepted version is recorded. A later bump
        // re-blocks until re-accepted (versioned-consent contract).
        Assert.True(LiabilityWaiverGate.RequiredVersion >= 1);
        var gate = new LiabilityWaiverGate();
        var s = new AppSettings();

        Assert.True(gate.IsPending(s));                                   // not yet accepted
        GateState.SetInt(s, gate.Id, LiabilityWaiverGate.RequiredVersion);
        Assert.False(gate.IsPending(s));                                  // accepted current version

        GateState.SetInt(s, gate.Id, LiabilityWaiverGate.RequiredVersion - 1);
        Assert.True(gate.IsPending(s));                                   // stale acceptance re-blocks
    }

    [Fact]
    public void WaiverGate_IsBlocking()
    {
        Assert.Equal(GateKind.Blocking, new LiabilityWaiverGate().Kind);
    }
}
