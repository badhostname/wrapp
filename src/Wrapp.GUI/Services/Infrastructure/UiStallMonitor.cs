using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;

namespace Wrapp.Services;

/// <summary>
/// Perf-plan P2.1: makes UI freezes self-documenting. A background thread
/// posts a heartbeat to the dispatcher and measures how long it takes to be
/// serviced; when the dispatcher goes unresponsive past the threshold, the
/// stall's total duration is logged on recovery:
/// <c>[STALL] UI thread blocked for N.Ns</c> (WARN ≥1s, ERROR ≥5s).
///
/// <para>Until now every freeze investigation needed external tooling
/// (dotnet-stack) and a live repro; with this, app.log carries the evidence.
/// The heartbeat is posted at Input priority so ordinary rendering doesn't
/// count as a stall, only genuine dispatcher blockage. Cost: one no-op
/// dispatcher item every 2 seconds.</para>
/// </summary>
public static class UiStallMonitor
{
    private const int HeartbeatIntervalMs = 2000;
    private const int StallThresholdMs = 1000;

    private static Thread? _thread;

    // Last user input, recorded by a global class handler in App (element
    // type + name + timestamp). A stall almost always starts from something
    // the user just did; without this, a [STALL] line proves a freeze
    // happened but cannot say what triggered it (the 0.6.326 field case:
    // 14s stall, zero log context).
    private static volatile string _lastInput = "(none)";
    private static long _lastInputMs;

    /// <summary>Records the most recent user input for stall attribution.</summary>
    public static void RecordInput(string description)
    {
        _lastInput = description;
        _lastInputMs = Environment.TickCount64;
    }

    /// <summary>Starts the monitor (idempotent). Thread dies with the process.</summary>
    public static void Start(Dispatcher dispatcher)
    {
        if (_thread is not null) return;
        _thread = new Thread(() => Loop(dispatcher))
        {
            IsBackground = true,
            Name = "UiStallMonitor",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
        AppLogger.Info("UiStallMonitor: active (threshold 1.0s)");
    }

    private static void Loop(Dispatcher dispatcher)
    {
        var sw = new Stopwatch();
        while (!dispatcher.HasShutdownStarted)
        {
            // Not disposed deliberately: the dispatcher callback may fire after
            // the 10-min cap path moves on, and Set() on a disposed MRES throws
            // on the UI thread. GC handles it; no kernel handle is allocated.
            var echoed = new ManualResetEventSlim(false);
            sw.Restart();
            try
            {
                dispatcher.BeginInvoke(() => echoed.Set(), DispatcherPriority.Input);
            }
            catch
            {
                return;   // dispatcher shutting down
            }

            if (!echoed.Wait(StallThresholdMs))
            {
                // Stall in progress — wait it out (cap 10 min so a hung-forever
                // dispatcher still produces a log line eventually).
                echoed.Wait(TimeSpan.FromMinutes(10));
                sw.Stop();
                var seconds = sw.Elapsed.TotalSeconds;
                var inputAge = (Environment.TickCount64 - _lastInputMs - (long)sw.Elapsed.TotalMilliseconds) / 1000.0;
                var line = $"[STALL] UI thread blocked for {seconds:0.0}s " +
                           $"(last input {(inputAge < 0 ? 0 : inputAge):0.0}s before stall: {_lastInput})";
                if (seconds >= 5) AppLogger.Error(line);
                else AppLogger.Warn(line);
            }

            Thread.Sleep(HeartbeatIntervalMs);
        }
    }
}
