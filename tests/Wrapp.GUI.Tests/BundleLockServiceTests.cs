using System.IO;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Workstream M1: cross-instance bundle lock semantics. "Another process" is
/// simulated by holding the lock file with a raw exclusive FileStream, which
/// is byte-for-byte what a second Wrapp instance does.
/// </summary>
public sealed class BundleLockServiceTests : IDisposable
{
    private readonly string _rootA;
    private readonly string _rootB;

    public BundleLockServiceTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "WrappTests", Guid.NewGuid().ToString("N"));
        _rootA = Path.Combine(baseDir, "BundleA");
        _rootB = Path.Combine(baseDir, "BundleB");
        Directory.CreateDirectory(_rootA);
        Directory.CreateDirectory(_rootB);
    }

    public void Dispose()
    {
        BundleLockService.Release();
        // Remove any lock files the "foreign process" streams created so the
        // real %LOCALAPPDATA%\Wrapp\Locks dir doesn't accumulate test litter.
        foreach (var root in new[] { _rootA, _rootB })
        {
            try { File.Delete(BundleLockService.LockPathFor(BundleLockService.Normalize(root))); } catch { }
        }
        try { Directory.Delete(Path.GetDirectoryName(_rootA)!, recursive: true); } catch { }
    }

    private static FileStream HoldAsForeignProcess(string bundleRoot)
    {
        Directory.CreateDirectory(BundleLockService.LockDir);
        var path = BundleLockService.LockPathFor(BundleLockService.Normalize(bundleRoot));
        return new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void TryAcquire_Succeeds_AndIsIdempotentForSameRoot()
    {
        Assert.True(BundleLockService.TryAcquire(_rootA));
        Assert.Equal(BundleLockService.Normalize(_rootA), BundleLockService.CurrentRoot);
        // Second acquire of the same bundle is a no-op success
        Assert.True(BundleLockService.TryAcquire(_rootA));
    }

    [Fact]
    public void TryAcquire_Fails_WhenAnotherProcessHoldsTheBundle()
    {
        using var foreign = HoldAsForeignProcess(_rootA);
        Assert.False(BundleLockService.TryAcquire(_rootA));
        Assert.Null(BundleLockService.CurrentRoot);
        Assert.True(BundleLockService.IsHeldByAnotherProcess(_rootA));
    }

    [Fact]
    public void FailedSwitch_KeepsTheCurrentLock()
    {
        Assert.True(BundleLockService.TryAcquire(_rootA));
        using (var foreign = HoldAsForeignProcess(_rootB))
        {
            // Acquire-then-release ordering: B is refused, A must survive
            Assert.False(BundleLockService.TryAcquire(_rootB));
            Assert.Equal(BundleLockService.Normalize(_rootA), BundleLockService.CurrentRoot);
        }
    }

    [Fact]
    public void SuccessfulSwitch_ReleasesThePreviousBundle()
    {
        Assert.True(BundleLockService.TryAcquire(_rootA));
        Assert.True(BundleLockService.TryAcquire(_rootB));
        Assert.Equal(BundleLockService.Normalize(_rootB), BundleLockService.CurrentRoot);

        // A's lock must now be takeable by a "foreign process"
        using var foreign = HoldAsForeignProcess(_rootA);
        Assert.True(BundleLockService.IsHeldByAnotherProcess(_rootA));
    }

    [Fact]
    public void Release_DeletesLockFile_AndAllowsReacquire()
    {
        Assert.True(BundleLockService.TryAcquire(_rootA));
        var lockPath = BundleLockService.LockPathFor(BundleLockService.Normalize(_rootA));
        Assert.True(File.Exists(lockPath));

        BundleLockService.Release();
        Assert.Null(BundleLockService.CurrentRoot);
        Assert.False(File.Exists(lockPath));
        Assert.False(BundleLockService.IsHeldByAnotherProcess(_rootA));
        Assert.True(BundleLockService.TryAcquire(_rootA));
    }

    [Fact]
    public void StaleLockFile_IsReclaimed()
    {
        // Crash simulation: lock file exists but no live handle holds it
        Directory.CreateDirectory(BundleLockService.LockDir);
        var lockPath = BundleLockService.LockPathFor(BundleLockService.Normalize(_rootA));
        File.WriteAllText(lockPath, string.Empty);

        Assert.False(BundleLockService.IsHeldByAnotherProcess(_rootA));
        Assert.True(BundleLockService.TryAcquire(_rootA));
    }

    [Fact]
    public void PathSpellingVariants_MapToTheSameLock()
    {
        var withSlash = _rootA + Path.DirectorySeparatorChar;
        var upper = _rootA.ToUpperInvariant();

        Assert.Equal(
            BundleLockService.LockPathFor(BundleLockService.Normalize(_rootA)),
            BundleLockService.LockPathFor(BundleLockService.Normalize(withSlash)));
        Assert.Equal(
            BundleLockService.LockPathFor(BundleLockService.Normalize(_rootA)),
            BundleLockService.LockPathFor(BundleLockService.Normalize(upper)));

        Assert.True(BundleLockService.TryAcquire(_rootA));
        using var foreign = HoldAsForeignProcessProbe(withSlash, out var blocked);
        Assert.True(blocked); // same underlying lock file -> foreign open fails
    }

    /// <summary>Foreign-open attempt that reports whether it was blocked.</summary>
    private static IDisposable HoldAsForeignProcessProbe(string bundleRoot, out bool blocked)
    {
        try
        {
            var fs = HoldAsForeignProcess(bundleRoot);
            blocked = false;
            return fs;
        }
        catch (IOException)
        {
            blocked = true;
            return new MemoryStream(); // dummy disposable
        }
    }
}
