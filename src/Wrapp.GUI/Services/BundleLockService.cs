using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Wrapp.Services;

/// <summary>
/// Workstream M1: cross-instance exclusive lock on an open bundle.
/// <para>
/// Each Wrapp process holds at most one bundle lock -- an exclusive
/// <see cref="FileStream"/> (FileShare.None) on an empty file under
/// <c>%LOCALAPPDATA%\Wrapp\Locks\</c>, named by a hash of the normalized
/// bundle root path. A second instance opening (or saving into) the same
/// bundle fails to acquire and must refuse the operation -- this is the
/// data-integrity core that makes multiple instances safe (Config.json
/// clobber, dueling git auto-commits).
/// </para>
/// <para>
/// The lock lives in a central per-user registry, NOT inside the bundle:
/// an in-bundle lock file would be swept into bundle copies, the Script/
/// git repo, and potentially the packaged .intunewin payload -- and its
/// held handle would break the upgrade-copy enumeration. Lock files carry
/// zero content; a crashed process's leftover file is reclaimable because
/// the OS released its handle (FileMode.Create simply wins), mirroring the
/// stale-lock semantics of <see cref="TempWorkspaceService"/>. Temp
/// workspaces are NOT locked here -- they already carry their own
/// per-directory lock.
/// </para>
/// </summary>
public static class BundleLockService
{
    public static readonly string LockDir = PlatformConfig.LockDir;

    private static readonly object _sync = new();
    private static FileStream? _current;
    private static string? _currentRoot;
    private static string? _currentLockPath;

    /// <summary>Normalized root of the bundle this process currently holds, or null.</summary>
    public static string? CurrentRoot { get { lock (_sync) return _currentRoot; } }

    /// <summary>
    /// Attempts to take (or keep) the exclusive lock for <paramref name="bundleRoot"/>.
    /// Acquire-then-release ordering: the new lock is taken BEFORE the previous
    /// one is dropped, so a failed switch leaves the current bundle protected.
    /// Returns false when another process holds the bundle.
    /// </summary>
    public static bool TryAcquire(string bundleRoot)
    {
        var root = Normalize(bundleRoot);
        if (string.IsNullOrEmpty(root)) return true;

        lock (_sync)
        {
            if (string.Equals(_currentRoot, root, StringComparison.OrdinalIgnoreCase))
                return true; // already ours

            var lockPath = LockPathFor(root);
            FileStream acquired;
            try
            {
                Directory.CreateDirectory(LockDir);
                // FileMode.Create over a stale (crash-orphaned) file succeeds
                // because its handle died with the process; over a LIVE lock it
                // throws IOException -- that's the "another instance" signal.
                acquired = new FileStream(lockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            ReleaseCore();
            _current = acquired;
            _currentRoot = root;
            _currentLockPath = lockPath;
            AppLogger.Info($"BundleLock: acquired {root}");
            return true;
        }
    }

    /// <summary>
    /// True when another process currently holds the lock for
    /// <paramref name="bundleRoot"/> (probe only -- does not take the lock).
    /// </summary>
    public static bool IsHeldByAnotherProcess(string bundleRoot)
    {
        var root = Normalize(bundleRoot);
        if (string.IsNullOrEmpty(root)) return false;

        lock (_sync)
        {
            if (string.Equals(_currentRoot, root, StringComparison.OrdinalIgnoreCase))
                return false; // ours, not "another process"
        }

        var lockPath = LockPathFor(root);
        if (!File.Exists(lockPath)) return false;
        try
        {
            using var fs = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false; // openable => stale leftover, not held
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return true; // unknown failure: assume held, fail safe
        }
    }

    /// <summary>Releases the held lock (if any) and deletes its file. Safe to call repeatedly.</summary>
    public static void Release()
    {
        lock (_sync)
        {
            if (_currentRoot is not null)
                AppLogger.Info($"BundleLock: released {_currentRoot}");
            ReleaseCore();
        }
    }

    private static void ReleaseCore()
    {
        try { _current?.Dispose(); } catch { /* already closed */ }
        if (_currentLockPath is not null)
        {
            try { File.Delete(_currentLockPath); } catch { /* another instance may already own it */ }
        }
        _current = null;
        _currentRoot = null;
        _currentLockPath = null;
    }

    /// <summary>Full path, no trailing separator -- so path spelling variants map to one lock.</summary>
    internal static string Normalize(string bundleRoot)
    {
        if (string.IsNullOrWhiteSpace(bundleRoot)) return string.Empty;
        try
        {
            return Path.GetFullPath(bundleRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return bundleRoot.Trim();
        }
    }

    /// <summary>Lock file for a normalized root: SHA-256 of the case-folded path.</summary>
    internal static string LockPathFor(string normalizedRoot)
    {
        var bytes = Encoding.UTF8.GetBytes(normalizedRoot.ToUpperInvariant());
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        return Path.Combine(LockDir, hash + ".lock");
    }
}
