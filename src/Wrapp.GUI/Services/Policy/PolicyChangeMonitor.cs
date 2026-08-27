using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Wrapp.Services.Policy;

/// <summary>
/// Event-driven watcher for external policy changes (gpupdate, Intune sync,
/// Apply-WrappPolicy.ps1). Uses RegNotifyChangeKeyValue on the
/// <c>SOFTWARE\Policies</c> subtrees of both hives — NO polling (this
/// codebase deliberately killed its pollers). On a notification it re-reads
/// the policy fresh, compares fingerprints against the launch snapshot, and
/// when they differ sets <see cref="PolicyService.ChangedSinceLaunch"/> and
/// invokes the callback (which re-evaluates the action-required indicator so
/// the existing gate flow surfaces "restart to apply"). The running session
/// keeps the LAUNCH policy — restart-to-apply stays the contract; this only
/// makes the operator aware, through the same flow every other pending
/// action uses.
/// </summary>
public static class PolicyChangeMonitor
{
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegNotifyChangeKeyValue(
        SafeRegistryHandle hKey, bool bWatchSubtree, int dwNotifyFilter,
        SafeWaitHandle hEvent, bool fAsynchronous);

    private const int REG_NOTIFY_CHANGE_NAME = 0x1;
    private const int REG_NOTIFY_CHANGE_LAST_SET = 0x4;

    private static Thread? _thread;
    private static ManualResetEvent? _stop;

    /// <summary>Starts the watcher (idempotent). <paramref name="onChanged"/>
    /// fires at most once per drift episode, on a background thread.</summary>
    public static void Start(Action onChanged)
    {
        if (_thread is not null) return;
        var launchFingerprint = PolicyService.Current.Fingerprint();
        _stop = new ManualResetEvent(false);

        _thread = new Thread(() => WatchLoop(launchFingerprint, onChanged))
        {
            IsBackground = true,
            Name = "PolicyChangeMonitor",
        };
        _thread.Start();
    }

    public static void Stop() => _stop?.Set();

    private static void WatchLoop(string launchFingerprint, Action onChanged)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Policies", writable: false);
            // HKCU\SOFTWARE\Policies may not exist on an unmanaged profile —
            // creating it is a benign, user-writable no-op that lets us watch.
            using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64)
                .CreateSubKey(@"SOFTWARE\Policies");

            var keys = new[] { hklm, hkcu }.Where(k => k is not null).Cast<RegistryKey>().ToArray();
            var events = keys.Select(_ => new AutoResetEvent(false)).ToArray();

            void Arm()
            {
                for (var i = 0; i < keys.Length; i++)
                    RegNotifyChangeKeyValue(keys[i].Handle, bWatchSubtree: true,
                        REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET,
                        events[i].SafeWaitHandle, fAsynchronous: true);
            }

            Arm();
            var handles = events.Cast<WaitHandle>().Append(_stop!).ToArray();
            var notified = false;

            while (true)
            {
                var signaled = WaitHandle.WaitAny(handles);
                if (handles[signaled] == _stop) return;

                // Debounce: policy writes come in bursts (script sets dozens
                // of values); settle before reading.
                if (_stop!.WaitOne(1500)) return;
                Arm(); // notifications are one-shot per registration

                var fresh = PolicyService.BuildFresh().Fingerprint();
                var drifted = !string.Equals(fresh, launchFingerprint, StringComparison.Ordinal);
                PolicyService.ChangedSinceLaunch = drifted;

                if (drifted && !notified)
                {
                    notified = true;
                    AppLogger.Info("Policy: registry policy changed since launch — restart required to apply");
                    onChanged();
                }
                else if (!drifted && notified)
                {
                    // Reverted to the launch state (e.g. the change was rolled
                    // back) — clear the pending action.
                    notified = false;
                    AppLogger.Info("Policy: registry policy reverted to the launch state");
                    onChanged();
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"PolicyChangeMonitor: watcher stopped — {ex.Message}");
        }
    }
}
