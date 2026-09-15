using System.IO;
using System.Threading;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Phase B (update-flow-and-token-polling-plan): cross-instance coordination.
/// Covers the pid registry's live/stale semantics, the close-request channel,
/// and the update-apply marker's three exits (own-version claim, staleness
/// fail-open, fresh foreign marker blocks). Tests share the static
/// InstanceCoordinator via the InstanceDirOverride seam, so they live in one
/// class (xUnit runs same-class tests sequentially) and restore the override
/// in finally blocks.
/// </summary>
public class InstanceCoordinatorTests : IDisposable
{
    private readonly string _dir;

    public InstanceCoordinatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wrapp-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
        InstanceCoordinator.InstanceDirOverride = _dir;
    }

    public void Dispose()
    {
        InstanceCoordinator.ReleaseInstance();
        InstanceCoordinator.EndUpdateApply();
        InstanceCoordinator.InstanceDirOverride = null;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ------------------------------------------------------------------
    // B1: registry - live vs stale pid files
    // ------------------------------------------------------------------

    [Fact]
    public void StalePidFile_IsNotLive_AndIsCleanedUp()
    {
        // A pid file nobody holds open = leftover from a crash.
        var stale = Path.Combine(_dir, "99999.lock");
        File.WriteAllText(stale, string.Empty);

        var live = InstanceCoordinator.GetOtherLiveInstanceIds();

        Assert.DoesNotContain(99999, live);
        Assert.False(File.Exists(stale), "stale pid file should be reclaimed during enumeration");
    }

    [Fact]
    public void HeldPidFile_IsReportedLive()
    {
        var path = Path.Combine(_dir, "88888.lock");
        using var holder = new FileStream(
            path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        var live = InstanceCoordinator.GetOtherLiveInstanceIds();

        Assert.Contains(88888, live);
    }

    [Fact]
    public void OwnPid_IsNeverReported()
    {
        InstanceCoordinator.RegisterInstance();
        try
        {
            Assert.True(File.Exists(Path.Combine(_dir, $"{Environment.ProcessId}.lock")));
            Assert.DoesNotContain(Environment.ProcessId, InstanceCoordinator.GetOtherLiveInstanceIds());
        }
        finally
        {
            InstanceCoordinator.ReleaseInstance();
        }
        Assert.False(File.Exists(Path.Combine(_dir, $"{Environment.ProcessId}.lock")),
            "pid file should be deleted on release");
    }

    [Fact]
    public void NonNumericFiles_AreIgnored()
    {
        File.WriteAllText(Path.Combine(_dir, "not-a-pid.lock"), string.Empty);
        Assert.Empty(InstanceCoordinator.GetOtherLiveInstanceIds());
    }

    // ------------------------------------------------------------------
    // B3: close-request channel
    // ------------------------------------------------------------------

    [Fact]
    public void RequestClose_ToHostedChannel_RaisesCloseRequested()
    {
        InstanceCoordinator.RegisterInstance();
        using var raised = new ManualResetEventSlim(false);
        Action handler = () => raised.Set();
        InstanceCoordinator.CloseRequested += handler;
        try
        {
            Assert.True(InstanceCoordinator.RequestClose(Environment.ProcessId));
            Assert.True(raised.Wait(TimeSpan.FromSeconds(5)),
                "CloseRequested should fire when this instance's channel is signaled");
        }
        finally
        {
            InstanceCoordinator.CloseRequested -= handler;
            InstanceCoordinator.ReleaseInstance();
        }
    }

    [Fact]
    public void RequestClose_NoSuchInstance_ReturnsFalse()
    {
        Assert.False(InstanceCoordinator.RequestClose(2));   // pid 2 hosts no Wrapp channel
    }

    // ------------------------------------------------------------------
    // B2: update-apply marker
    // ------------------------------------------------------------------

    private string MarkerPath => Path.Combine(_dir, "update-in-progress.marker");

    [Fact]
    public void FreshForeignMarker_BlocksLaunch()
    {
        InstanceCoordinator.BeginUpdateApply("999.999.999");
        Assert.True(InstanceCoordinator.IsUpdateInProgress());
    }

    [Fact]
    public void MarkerForOurOwnVersion_MeansApplyCompleted_AndClears()
    {
        // The relaunched build finds its own version in the marker: the
        // apply that wrote it has finished. Launch proceeds, marker gone.
        InstanceCoordinator.BeginUpdateApply(AppInfo.Version);
        Assert.False(InstanceCoordinator.IsUpdateInProgress());
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public void StaleMarker_FailsOpen_AndClears()
    {
        // A failed apply never relaunches anything to clean the marker; the
        // guard must expire rather than lock the user out of Wrapp.
        InstanceCoordinator.BeginUpdateApply("999.999.999");
        File.SetLastWriteTimeUtc(MarkerPath, DateTime.UtcNow.AddMinutes(-10));

        Assert.False(InstanceCoordinator.IsUpdateInProgress());
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public void EndUpdateApply_RemovesMarker()
    {
        InstanceCoordinator.BeginUpdateApply("999.999.999");
        InstanceCoordinator.EndUpdateApply();
        Assert.False(InstanceCoordinator.IsUpdateInProgress());
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public async Task WaitForUpdateToFinish_ReturnsWhenMarkerCleared()
    {
        InstanceCoordinator.BeginUpdateApply("999.999.999");
        var wait = InstanceCoordinator.WaitForUpdateToFinishAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(600);
        InstanceCoordinator.EndUpdateApply();

        Assert.True(await wait);
    }
}
