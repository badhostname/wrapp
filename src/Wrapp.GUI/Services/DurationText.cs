// Back-ported from AlwaysBlue via Unwrapped (src/Unwrapped.GUI/Foundation/Services/DurationText.cs) - keep near-verbatim.
using System.Globalization;

namespace Wrapp.Services;

/// <summary>
/// How a job card and its steps show time, in one place so they read the same. While something runs it shows whole
/// seconds ("4s", "1m 05s", "1h 02m"), which change exactly once a second; once it has finished the value no longer
/// moves, so under a minute it shows tenths ("4.3s"). Finished work also says how long ago it ended
/// ("just now", "5 min ago").
/// </summary>
public static class DurationText
{
    /// <summary>A running clock: whole seconds, then minutes and seconds, then hours and minutes.</summary>
    public static string Running(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds:00}s";
        return $"{(int)t.TotalSeconds}s";
    }

    /// <summary>A finished duration: tenths of a second under a minute, otherwise as <see cref="Running"/>.</summary>
    public static string Finished(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalMinutes >= 1) return Running(t);
        if (t.TotalSeconds < 0.1) return "<0.1s";
        // Truncated, not rounded, so 59.97 s never reads "60.0s".
        return (Math.Floor(t.TotalSeconds * 10) / 10).ToString("0.0", CultureInfo.CurrentCulture) + "s";
    }

    /// <summary>How long until <see cref="Running"/> shows a different text: to the next second, or the next minute past an hour.</summary>
    public static TimeSpan UntilChange(TimeSpan t)
    {
        if (t < TimeSpan.Zero) return -t;
        var unit = t.TotalHours >= 1 ? TimeSpan.TicksPerMinute : TimeSpan.TicksPerSecond;
        return TimeSpan.FromTicks(unit - t.Ticks % unit);
    }

    /// <summary>How long ago something ended: "just now", "5 min ago", "3 h ago", "2 d ago", then the date.</summary>
    public static string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        var d = now - when;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays} d ago";
        return when.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
