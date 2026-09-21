using System.IO;
using System.Threading;

namespace Wrapp.Services;

/// <summary>
/// Cross-instance coordination.
/// Three concerns, one kernel-object namespace:
///
/// <para><b>Instance registry</b> - each process holds an exclusive
/// <see cref="FileStream"/> on <c>Locks\instances\&lt;pid&gt;.lock</c> (the
/// <see cref="BundleLockService"/> pattern: zero-content file, a crashed
/// process's leftover is reclaimable because the OS released its handle).
/// Enumerating the directory yields a truthful list of live Wrapp PIDs -
/// the update flow refuses to apply while siblings are alive.</para>
///
/// <para><b>Close-request channel</b> - each instance hosts an
/// <see cref="EventWaitHandle"/> named <c>Local\Wrapp.CloseRequest.&lt;pid&gt;</c>.
/// Signaling it asks that instance to run its NORMAL close pipeline (save
/// prompts included, Cancel intact). Requesting is all this API can do -
/// there is deliberately no force path.</para>
///
/// <para><b>Update-apply guard</b> - <c>Local\Wrapp.UpdateInProgress</c>
/// is held from "Applying" until Velopack relaunches the app. Launching
/// processes probe it and wait instead of starting an old binary mid-swap.</para>
/// </summary>
public static class InstanceCoordinator
{
    private const string CloseRequestPrefix = @"Local\Wrapp.CloseRequest.";

    /// <summary>Test seam: redirects the pid-lock directory.</summary>
    internal static string? InstanceDirOverride;

    private static string InstanceDir => InstanceDirOverride ?? Path.Combine(PlatformConfig.LockDir, "instances");

    private static readonly object _sync = new();
    private static FileStream?           _instanceLock;
    private static EventWaitHandle?      _closeRequestEvent;
    private static RegisteredWaitHandle? _closeRequestWait;

    /// <summary>
    /// Raised (on a threadpool thread) when another process asks this one to
    /// close. Subscribers marshal to the dispatcher and drive the normal
    /// close pipeline - never a hard exit.
    /// </summary>
    public static event Action? CloseRequested;

    // -------------------------------------------------------------------
    // Instance registry
    // -------------------------------------------------------------------

    /// <summary>
    /// Registers this process: pid lock taken, close-request event hosted.
    /// Idempotent; failures are logged and non-fatal (coordination is a
    /// safety net, not a launch prerequisite).
    /// </summary>
    public static void RegisterInstance()
    {
        lock (_sync)
        {
            if (_instanceLock is not null) return;
            try
            {
                Directory.CreateDirectory(InstanceDir);
                var path = Path.Combine(InstanceDir, $"{Environment.ProcessId}.lock");
                _instanceLock = new FileStream(
                    path, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.DeleteOnClose);

                _closeRequestEvent = new EventWaitHandle(
                    initialState: false, EventResetMode.AutoReset,
                    CloseRequestPrefix + Environment.ProcessId);
                _closeRequestWait = ThreadPool.RegisterWaitForSingleObject(
                    _closeRequestEvent,
                    callBack: (_, _) => CloseRequested?.Invoke(),
                    state: null, millisecondsTimeOutInterval: -1, executeOnlyOnce: false);

                AppLogger.Info($"InstanceCoordinator: registered pid {Environment.ProcessId}");
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"InstanceCoordinator: registration failed -- {ex.Message}");
            }
        }
    }

    /// <summary>Drops the registration (process exit). Safe to call repeatedly.</summary>
    public static void ReleaseInstance()
    {
        lock (_sync)
        {
            try { _closeRequestWait?.Unregister(null); } catch { }
            _closeRequestWait = null;
            _closeRequestEvent?.Dispose();
            _closeRequestEvent = null;
            _instanceLock?.Dispose();   // DeleteOnClose removes the pid file
            _instanceLock = null;
        }
    }

    /// <summary>
    /// Live Wrapp PIDs other than this process. A pid file that can be
    /// opened exclusively is stale (its owner crashed) and is cleaned up on
    /// the way through.
    /// </summary>
    public static IReadOnlyList<int> GetOtherLiveInstanceIds()
    {
        var live = new List<int>();
        try
        {
            if (!Directory.Exists(InstanceDir)) return live;
            foreach (var file in Directory.EnumerateFiles(InstanceDir, "*.lock"))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var pid)) continue;
                if (pid == Environment.ProcessId) continue;
                try
                {
                    // Openable ⇒ nobody holds it ⇒ stale leftover.
                    using var probe = new FileStream(
                        file, FileMode.Open, FileAccess.ReadWrite, FileShare.None,
                        bufferSize: 1, FileOptions.DeleteOnClose);
                }
                catch (IOException)
                {
                    live.Add(pid);      // held ⇒ that instance is alive
                }
                catch (UnauthorizedAccessException)
                {
                    live.Add(pid);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"InstanceCoordinator: enumeration failed -- {ex.Message}");
        }
        return live;
    }

    // -------------------------------------------------------------------
    // Close requests
    // -------------------------------------------------------------------

    /// <summary>
    /// Asks the instance with <paramref name="pid"/> to close (its normal
    /// save-prompt pipeline decides the outcome). False when that instance
    /// hosts no channel (old build, or already gone).
    /// </summary>
    public static bool RequestClose(int pid)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(CloseRequestPrefix + pid, out var handle))
                return false;
            using (handle) handle.Set();
            AppLogger.Info($"InstanceCoordinator: close requested for pid {pid}");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"InstanceCoordinator: close request for pid {pid} failed -- {ex.Message}");
            return false;
        }
    }

    /// <summary>Fires a close request at every other live instance; returns how many were reachable.</summary>
    public static int RequestCloseAll()
        => GetOtherLiveInstanceIds().Count(RequestClose);

    // -------------------------------------------------------------------
    // Update-apply guard
    // -------------------------------------------------------------------
    // A marker FILE, not a kernel mutex, and that distinction is the whole
    // design: Velopack's Update.exe swaps current\ AFTER the updating
    // process exits, so any guard owned by that process (mutex, held file
    // handle) evaporates at the exact moment the dangerous window opens.
    // The marker persists across the exit. It resolves three ways:
    //   - the RELAUNCHED (new-version) app sees its own version in the
    //     marker at startup and deletes it - the normal happy path;
    //   - an aborted update deletes it via EndUpdateApply();
    //   - a failed apply (no relaunch) leaves it behind, so staleness
    //     (2 min) expires it - launches FAIL OPEN rather than locking the
    //     user out behind a dead marker.

    private static readonly TimeSpan UpdateMarkerStaleAfter = TimeSpan.FromMinutes(2);

    private static string UpdateMarkerPath => Path.Combine(
        InstanceDirOverride ?? PlatformConfig.LockDir,
        "update-in-progress.marker");

    /// <summary>
    /// Writes the guard marker just before the updating process exits into
    /// the apply. <paramref name="targetVersion"/> lets the relaunched build
    /// recognize the marker as its own completed apply.
    /// </summary>
    public static void BeginUpdateApply(string targetVersion)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UpdateMarkerPath)!);
            File.WriteAllText(UpdateMarkerPath, targetVersion.Trim());
            AppLogger.Info($"InstanceCoordinator: update-apply marker written (target {targetVersion})");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"InstanceCoordinator: writing update marker failed -- {ex.Message}");
        }
    }

    /// <summary>Removes the marker (update aborted, or completed apply detected).</summary>
    public static void EndUpdateApply()
    {
        try { File.Delete(UpdateMarkerPath); } catch { }
    }

    /// <summary>
    /// True while an apply targeting a DIFFERENT version than this binary is
    /// plausibly in flight. Sees three exits: our own version in the marker
    /// (we are the relaunched result - clear it, done), staleness (failed
    /// apply - clear it, fail open), or a fresh foreign-version marker (wait).
    /// </summary>
    public static bool IsUpdateInProgress()
    {
        try
        {
            var path = UpdateMarkerPath;
            if (!File.Exists(path)) return false;

            var target = File.ReadAllText(path).Trim();
            if (string.Equals(target, AppInfo.Version.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                // We ARE the applied version: the update completed. Clean up.
                EndUpdateApply();
                return false;
            }

            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age > UpdateMarkerStaleAfter)
            {
                AppLogger.Warn($"InstanceCoordinator: stale update marker (target {target}, {age.TotalMinutes:0.0}m old) -- clearing, launching anyway");
                EndUpdateApply();
                return false;
            }
            return true;
        }
        catch
        {
            return false;   // unreadable marker must not block launches
        }
    }

    /// <summary>
    /// Launch-guard wait: polls until the apply finishes (marker cleared or
    /// claimed by our version) or <paramref name="timeout"/> elapses. Runs
    /// once, pre-UI, in a launching process - polling is fine.
    /// </summary>
    public static async Task<bool> WaitForUpdateToFinishAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsUpdateInProgress()) return true;
            await Task.Delay(500);
        }
        return !IsUpdateInProgress();
    }
}
