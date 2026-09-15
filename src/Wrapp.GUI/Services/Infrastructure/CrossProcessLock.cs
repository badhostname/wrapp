using System.Threading;

namespace Wrapp.Services;

/// <summary>
/// Serializes read-modify-write cycles on shared per-user files
/// (settings.json + user-defaults sidecar, Templates\manifest.json) across
/// Wrapp processes, using the same named-mutex pattern as the MSAL token cache.
/// <para>
/// Semantics: last-writer-wins is unchanged -- this only guarantees each
/// cycle runs whole, so one instance can never interleave inside another's
/// (e.g. read manifest → another instance saves a template → write manifest
/// = that template silently unregistered). On timeout the body runs WITHOUT
/// the lock (progress over hang, logged) -- identical to the token-cache choice.
/// </para>
/// <para>
/// Named mutexes have THREAD AFFINITY (release must happen on the acquiring
/// thread), so the body is always a synchronous delegate; <see cref="RunAsync"/>
/// moves the whole acquire-body-release onto one worker thread rather than
/// awaiting across the hold.
/// </para>
/// </summary>
internal static class CrossProcessLock
{
    private const int DefaultTimeoutMs = 5000;

    /// <summary>Runs <paramref name="body"/> under the session-scoped named mutex.</summary>
    public static void Run(string name, Action body, int timeoutMs = DefaultTimeoutMs)
    {
        // Local\ prefix: current logon session only -- same-user cross-process,
        // no admin rights, no cross-session interference.
        using var mutex = new Mutex(false, $@"Local\Wrapp_{name}");
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(timeoutMs); }
            catch (AbandonedMutexException) { acquired = true; /* prior holder crashed; state is atomic-file safe */ }

            if (!acquired)
                AppLogger.Warn($"CrossProcessLock: '{name}' timeout after {timeoutMs}ms; proceeding without lock.");

            body();
        }
        finally
        {
            if (acquired)
            {
                try { mutex.ReleaseMutex(); } catch { /* released on same thread; failure means abandoned */ }
            }
        }
    }

    /// <summary>
    /// Async-friendly variant: hops to a worker thread so acquire, body, and
    /// release all happen on that one thread. The body itself stays synchronous.
    /// </summary>
    public static Task RunAsync(string name, Action body, int timeoutMs = DefaultTimeoutMs)
        => Task.Run(() => Run(name, body, timeoutMs));
}
