using System.Windows.Threading;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Owns the splash-level update
/// flow - the ONLY place an update executes. Two entries:
///
/// <para><b>From a running session</b> (action-needed indicator / Settings):
/// <see cref="BeginFromSessionAsync"/> runs the normal CloseGuard (Cancel
/// aborts the update), then MainWindow's Closed handler - seeing
/// <see cref="IsHandoffActive"/> - skips its usual <c>Environment.Exit(0)</c>
/// and calls <see cref="ContinueAfterMainWindowClosed"/>: session resources
/// are released behind a fresh update-mode splash and the flow runs.</para>
///
/// <para><b>From startup</b> (Auto mode, no sibling instances, update found
/// before the user picks a bundle): the splash flips to update mode and
/// "Update now" calls <see cref="RunSplashFlowAsync"/> directly - MainWindow
/// is never built.</para>
///
/// <para>The flow: download+verify (worker thread, step-tracked progress) →
/// wait for sibling windows (close REQUESTS every 30s, never force; the
/// user can cancel out) → schedule the apply → shutdown. App.OnExit stamps
/// the InstanceCoordinator marker so launches during the apply window wait
///. Velopack applies and relaunches.</para>
/// </summary>
public static class UpdateFlowController
{
    /// <summary>
    /// True from the moment the update-handoff close is committed. MainWindow's
    /// Closed handler consults this: normal closes keep their exact historical
    /// behavior (including the deliberate Environment.Exit(0)); only the
    /// handoff path takes the survive-and-continue branch.
    /// </summary>
    public static bool IsHandoffActive { get; private set; }

    /// <summary>
    /// Registered by MainWindow (view-registers-itself pattern): runs
    /// CloseGuard with the UpdateHandoff reason and, on Proceed, marks the
    /// handoff, switches to OnExplicitShutdown, and closes the window.
    /// </summary>
    public static Func<Task<bool>>? TryCloseMainForUpdateAsync { get; set; }

    private static AppSettings? _settings;
    private static string _targetVersion = string.Empty;

    private const int SiblingRerequestMs = 30_000;

    /// <summary>
    /// Session entry: CloseGuard first (a cancelled save prompt aborts the
    /// update and returns false - the pending indicator stays lit), then the
    /// handoff continues from MainWindow.Closed.
    /// </summary>
    public static async Task<bool> BeginFromSessionAsync(AppSettings settings, string? targetVersion)
    {
        if (IsHandoffActive) return true;
        if (string.IsNullOrWhiteSpace(targetVersion)) return false;
        var closeMain = TryCloseMainForUpdateAsync;
        if (closeMain is null) return false;

        _settings = settings;
        _targetVersion = targetVersion!;
        return await closeMain();
    }

    /// <summary>Called by MainWindow at the point of no return, BEFORE Close().</summary>
    internal static void MarkHandoffActive() => IsHandoffActive = true;

    /// <summary>
    /// Called by the MainWindow.Closed handler when <see cref="IsHandoffActive"/>:
    /// show the update splash, release session resources behind it (off-thread -
    /// runspace disposal can hang, which is why the normal path uses
    /// Environment.Exit), and run the flow.
    /// </summary>
    public static void ContinueAfterMainWindowClosed(Action releaseSessionResources)
    {
        SafeFireAndForget.Run(async () =>
        {
            var splash = new Views.SplashWindow();
            splash.EnterUpdateHandoff(_targetVersion, _settings!);
            splash.Show();

            _ = Task.Run(() =>
            {
                try { releaseSessionResources(); }
                catch (Exception ex) { AppLogger.Warn($"Update handoff: session teardown failed -- {ex.Message}"); }
            });

            await RunSplashFlowAsync(splash, _settings!);
        }, "update-handoff");
    }

    /// <summary>
    /// Sibling-close gate: asks every other live instance to close (their
    /// CloseGuard save prompts decide) and waits until none remain. Skips
    /// instantly when there are none. False = the user cancelled the update
    /// (clean shutdown, nothing staged).
    /// </summary>
    private static async Task<bool> WaitForSiblingsAsync(ViewModels.SplashViewModel vm, UpdateStepTracker tracker)
    {
        long lastRequest = 0;
        var announced = false;
        while (true)
        {
            if (vm.UpdateCancelRequested)
            {
                AppLogger.Info("UpdateFlow: cancelled by the user before apply; closing without updating");
                System.Windows.Application.Current.Shutdown();
                return false;
            }
            var others = InstanceCoordinator.GetOtherLiveInstanceIds();
            if (others.Count == 0) return true;
            if (!announced)
            {
                tracker.Begin(UpdateFlowStep.WaitingForWindows);
                vm.ApplyTracker(tracker);
                announced = true;
            }
            if (Environment.TickCount64 - lastRequest > SiblingRerequestMs)
            {
                InstanceCoordinator.RequestCloseAll();
                lastRequest = Environment.TickCount64;
            }
            vm.SetWaitingCount(others.Count);
            await Task.Delay(1000);
        }
    }

    /// <summary>
    /// The update flow itself, driven on the splash. Runs on the dispatcher;
    /// every heavy call is pushed to a worker thread.
    /// </summary>
    public static async Task RunSplashFlowAsync(Views.SplashWindow splash, AppSettings settings)
    {
        var vm = splash.Vm;
        vm.EnterUpdateProgress();

        var tracker = new UpdateStepTracker();
        // 1s ticker owns the stall transition (Downloading → Rebuilding once
        // the percent freezes >5s - the honest version of "stuck at 70%").
        var stallTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        stallTicker.Tick += (_, _) =>
        {
            if (tracker.EvaluateStall(Environment.TickCount64))
                vm.ApplyTracker(tracker);
        };
        stallTicker.Start();

        try
        {
            vm.ApplyTracker(tracker);   // "Checking update feed"

            // Check-only first: fast, and it carries the target's size and
            // feed hash so the info block is on screen before the long work.
            var check = await Task.Run(() => UpdateService.CheckAsync(settings, download: false));
            if (check.Status != UpdateService.CheckStatus.UpdateAvailable)
            {
                splash.HandleUpdateFailure(UpdateService.Describe(check));
                return;
            }
            vm.SetUpdateDetail(check.SizeBytes, check.Sha, check.DeltaSizeBytes);

            // OPEN WORK FIRST: sibling windows close - each through its own
            // CloseGuard save prompts - BEFORE the download/rebuild begins,
            // so the rebuild's CPU burst can never touch a window someone is
            // working in. Requests only, re-fired every 30s, live count on
            // screen, cancel always available.
            if (!await WaitForSiblingsAsync(vm, tracker)) return;

            // The delta rebuild spins up parallel compression threads; at
            // Normal priority that storm can starve the system's input
            // pipeline (GetKeyState stalls observed three times in the
            // 0.6.324 validation runs, always during a rebuild). Every other
            // window is closed by now, but stay a good citizen system-wide.
            var thisProcess = System.Diagnostics.Process.GetCurrentProcess();
            var normalPriority = thisProcess.PriorityClass;
            try { thisProcess.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; }
            catch { /* not fatal - continue at normal priority */ }

            UpdateService.CheckResult dl;
            try
            {
                dl = await Task.Run(() => UpdateService.CheckAsync(settings, download: true, p =>
                {
                    // Callback arrives on a Velopack worker; only forward actual
                    // movement, and never block the worker on the dispatcher.
                    if (tracker.ReportPercent(p, Environment.TickCount64))
                        splash.Dispatcher.BeginInvoke(() => vm.ApplyTracker(tracker));
                }));
            }
            finally
            {
                try { thisProcess.PriorityClass = normalPriority; } catch { }
            }

            if (dl.Status != UpdateService.CheckStatus.Downloaded)
            {
                splash.HandleUpdateFailure(UpdateService.Describe(dl));
                return;
            }
            vm.SetUpdateDetail(dl.SizeBytes, dl.Sha, dl.DeltaSizeBytes);

            // A window may have been launched during the download - re-check
            // before the point of no return.
            if (!await WaitForSiblingsAsync(vm, tracker)) return;

            tracker.Begin(UpdateFlowStep.Applying);
            vm.ApplyTracker(tracker);
            if (!UpdateService.ScheduleApplyOnExit())
            {
                splash.HandleUpdateFailure("The update could not be scheduled for install.");
                return;
            }

            tracker.Begin(UpdateFlowStep.Restarting);
            vm.ApplyTracker(tracker);
            await Task.Delay(500);   // let the step register before the window vanishes

            // App.OnExit stamps the update marker (ScheduledApplyVersion is
            // set now), so launches during the apply window wait.
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            AppLogger.Exception("UpdateFlow", ex);
            splash.HandleUpdateFailure(ex.Message);
        }
        finally
        {
            stallTicker.Stop();
        }
    }
}
