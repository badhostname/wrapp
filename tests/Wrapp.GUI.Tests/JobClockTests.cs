// Back-ported from AlwaysBlue via Unwrapped (tests/Unwrapped.GUI.Tests/JobClockTests.cs) - keep near-verbatim.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Wrapp.Helpers;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// The job cards' clocks: one format for cards and steps, running steps tick, and the tracker's timer runs only
/// while cards are on screen. A hand-moved clock stands in for time, so nothing here waits on the real one.
/// </summary>
[Collection("SystemClock")] // overrides the static clock
public class JobClockTests
{
    /// <summary>The tracker's clock is a DispatcherTimer: it wants an STA thread with a dispatcher of its own.</summary>
    private static void Sta(Action body)
    {
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static string Tenths(double seconds) => seconds.ToString("0.0") + "s";

    [Fact]
    public void Durations_read_in_whole_seconds_while_running_and_in_tenths_once_finished()
    {
        Assert.Equal("0s", DurationText.Running(TimeSpan.FromSeconds(0.9)));
        Assert.Equal("4s", DurationText.Running(TimeSpan.FromSeconds(4.99)));
        Assert.Equal("1m 05s", DurationText.Running(TimeSpan.FromSeconds(65.4)));
        Assert.Equal("1h 02m", DurationText.Running(TimeSpan.FromSeconds(3725)));
        Assert.Equal(Tenths(4.3), DurationText.Finished(TimeSpan.FromSeconds(4.37)));
        Assert.Equal(Tenths(59.9), DurationText.Finished(TimeSpan.FromSeconds(59.97)));
        Assert.Equal("<0.1s", DurationText.Finished(TimeSpan.FromMilliseconds(40)));
        Assert.Equal("2m 10s", DurationText.Finished(TimeSpan.FromSeconds(130.6)));

        // The next change: the next whole second, or the next minute past an hour.
        Assert.Equal(TimeSpan.FromMilliseconds(700), DurationText.UntilChange(TimeSpan.FromSeconds(4.3)));
        Assert.Equal(TimeSpan.FromSeconds(1), DurationText.UntilChange(TimeSpan.FromSeconds(4)));
        Assert.Equal(TimeSpan.FromSeconds(50), DurationText.UntilChange(TimeSpan.FromSeconds(3610)));

        var now = new DateTimeOffset(2026, 11, 2, 14, 0, 0, TimeSpan.Zero);
        Assert.Equal("just now", DurationText.Ago(now.AddSeconds(-40), now));
        Assert.Equal("5 min ago", DurationText.Ago(now.AddMinutes(-5.5), now));
        Assert.Equal("3 h ago", DurationText.Ago(now.AddHours(-3.2), now));
        Assert.Equal("2 d ago", DurationText.Ago(now.AddDays(-2.5), now));
        Assert.Equal(now.AddDays(-9).ToLocalTime().ToString("yyyy-MM-dd"), DurationText.Ago(now.AddDays(-9), now));
    }

    [Fact]
    public void A_running_step_ticks_with_its_job_and_is_announced_only_when_its_text_changes()
    {
        Sta(() =>
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 14, 0, 0, TimeSpan.Zero));
            using var _ = SystemClock.Override(clock);
            var tree = new JobStepTree();
            var download = tree.Add("Download");
            var verify = download.AddSubStep("Verify");
            var tracker = new BackgroundJobTracker();
            var job = tracker.BeginJob("Package HP Driver", context: tree).Job!;

            clock.Advance(TimeSpan.FromSeconds(0.4));
            download.Start();
            var announced = 0;
            download.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(JobStep.DurationDisplay)) announced++; };

            clock.Advance(TimeSpan.FromSeconds(1.2));
            verify.Start();
            // The job is 1.6 s in (next change in 0.4 s), Download 1.2 s (0.8 s), Verify 0 s (1 s): the soonest wins.
            Assert.Equal(TimeSpan.FromMilliseconds(400), job.RefreshClocks());
            Assert.Equal("1s", job.ElapsedDisplay);
            Assert.Equal("1s", download.DurationDisplay);
            Assert.Equal("0s", verify.DurationDisplay);
            Assert.Equal(1, announced);

            // Nothing changed: nothing announced.
            job.RefreshClocks();
            Assert.Equal(1, announced);

            clock.Advance(TimeSpan.FromSeconds(64));
            job.RefreshClocks();
            Assert.Equal("1m 05s", download.DurationDisplay);
            Assert.Equal("1m 04s", verify.DurationDisplay);
            Assert.Equal(2, announced);

            clock.Advance(TimeSpan.FromSeconds(0.25));
            verify.Finish(StepState.Succeeded);
            download.Finish(StepState.Succeeded);
            Assert.Equal("1m 05s", download.DurationDisplay);
            Assert.Equal("1m 04s", verify.DurationDisplay);
            Assert.Null(tree.RefreshRunning());

            // A short finished step shows tenths; the finished job freezes.
            var quick = tree.Add("Write");
            quick.Start();
            clock.Advance(TimeSpan.FromSeconds(0.43));
            quick.Finish(StepState.Succeeded);
            Assert.Equal(Tenths(0.4), quick.DurationDisplay);
            tracker.Complete(job);
            var final = job.ElapsedDisplay;
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(final, job.ElapsedDisplay);
            Assert.Null(job.RefreshClocks());
        });
    }

    [Fact]
    public void The_clock_runs_only_while_cards_are_watched_and_something_on_them_can_change()
    {
        Sta(() =>
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 14, 0, 0, TimeSpan.Zero));
            using var _ = SystemClock.Override(clock);
            var tracker = new BackgroundJobTracker();
            var handle = tracker.BeginJob("Package HP Driver");
            Assert.False(tracker.ClockRunning); // a running job nobody is looking at costs nothing

            var watch = tracker.Watch();
            Assert.True(tracker.ClockRunning);
            watch.Dispose();
            watch.Dispose(); // twice is harmless
            Assert.False(tracker.ClockRunning);

            using (tracker.Watch())
            {
                handle.Complete("done");
                var job = tracker.Jobs[0];
                Assert.Equal("just now", job.FinishedAgo);
                Assert.Equal("Finished " + job.CompletedAt!.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), job.FinishedAtText);
                // Only "finished ... ago" can change now: the clock slows to every 30 seconds.
                Assert.True(tracker.ClockRunning);
                clock.Advance(TimeSpan.FromMinutes(2));
                Assert.Equal(BackgroundJobTracker.AgoEvery, tracker.RefreshClocks());
                Assert.Equal("2 min ago", job.FinishedAgo);

                tracker.ClearCompleted();
                tracker.BeginJob("Cache maintenance").Complete();
                tracker.ClearCompleted();
            }
            Assert.False(tracker.ClockRunning);
        });
    }

    [Fact]
    public void The_jobs_list_holds_the_clock_while_it_is_on_screen_and_lets_go_when_it_leaves()
    {
        Sta(() =>
        {
            var tracker = new BackgroundJobTracker();
            var running = tracker.BeginJob("Package HP Driver");
            try
            {
                var list = new ItemsControl { ItemsSource = tracker.Jobs };
                JobClock.SetTracker(list, tracker);
                Assert.False(tracker.ClockRunning); // not on screen yet

                var window = new Window
                {
                    Content = list, Width = 300, Height = 200, WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false, ShowActivated = false, Left = -32000, Top = -32000,
                };
                window.Show();
                Assert.True(list.IsVisible);
                Assert.True(tracker.ClockRunning);

                list.Visibility = Visibility.Collapsed;
                Assert.False(tracker.ClockRunning);
                list.Visibility = Visibility.Visible;
                Assert.True(tracker.ClockRunning);

                window.Close();
                Assert.False(tracker.ClockRunning); // the window closed: no cards on screen

                // Clearing the property releases the watch without a visibility change.
                window = new Window
                {
                    Content = list, Width = 300, Height = 200, WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false, ShowActivated = false, Left = -32000, Top = -32000,
                };
                window.Show();
                Assert.True(tracker.ClockRunning);
                JobClock.SetTracker(list, null);
                Assert.False(tracker.ClockRunning);
                window.Close();
            }
            finally { running.Complete(); }
        });
    }
}
