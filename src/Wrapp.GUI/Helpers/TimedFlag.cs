using System.Windows.Threading;

namespace Wrapp.Helpers;

/// <summary>
/// A re-triggerable timed flag helper for UI flash/confirmation feedback
/// (e.g. "Copied!", "Saved ✓"). Set your observable bool to <c>true</c>,
/// call <see cref="Trigger"/>, and a <see cref="DispatcherTimer"/> will
/// invoke the provided reset action on the UI thread after the duration.
///
/// <para>Repeated <see cref="Trigger"/> calls restart the timer so rapid
/// activity extends the flash rather than letting it expire mid-action.</para>
/// </summary>
public sealed class TimedFlag
{
    private readonly TimeSpan _duration;
    private readonly Action _onExpire;
    private DispatcherTimer? _timer;

    public TimedFlag(TimeSpan duration, Action onExpire)
    {
        _duration = duration;
        _onExpire = onExpire;
    }

    /// <summary>(Re)start the timer. On expiry, <c>onExpire</c> fires on the UI thread.</summary>
    public void Trigger()
    {
        // Reuse a single DispatcherTimer and reset its countdown via
        // Stop+Start -- allocating a new timer every Trigger wastes
        // objects and, worse, the Tick handler on the old timer would
        // close over the `_timer` field which already points at the
        // *new* timer by the time it fires. That would Stop the wrong
        // instance. Reusing one timer sidesteps the race entirely.
        if (_timer is null)
        {
            _timer = new DispatcherTimer { Interval = _duration };
            _timer.Tick += (_, _) =>
            {
                _timer!.Stop();
                _onExpire();
            };
        }
        else
        {
            _timer.Stop();
        }
        _timer.Start();
    }
}
