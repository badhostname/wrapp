using System.IO;
using Wrapp.Services;

namespace Wrapp.Helpers;

/// <summary>
/// Retries a file operation that failed because something else still holds
/// the file: on-access scanners and SMB oplocks keep a handle on a file that
/// was just written for a moment, and a second write in that window fails
/// with ERROR_SHARING_VIOLATION (0x20) or ERROR_LOCK_VIOLATION (0x21). Five
/// attempts over about a second and a half outlast the scanners we have seen;
/// anything else, and anything that still fails after the last attempt, is
/// the caller's to report.
/// </summary>
public static class FileRetry
{
    /// <summary>Attempts in all, the first one included.</summary>
    public const int Attempts = 5;

    /// <summary>Waits between attempts, in milliseconds: about 1.5 s in all.</summary>
    private static readonly int[] Waits = { 200, 300, 400, 600 };

    /// <summary>How a wait is performed; tests shorten it.</summary>
    internal static Func<int, Task> WaitAsync { get; set; } = ms => Task.Delay(ms);

    /// <summary>True for the two Win32 errors that mean "someone else has the file open right now".</summary>
    public static bool IsSharingViolation(Exception ex)
        => ex is IOException io && (io.HResult & 0xFFFF) is 0x20 or 0x21;

    /// <summary>Runs <paramref name="operation"/>, retrying on a sharing violation.</summary>
    /// <param name="what">The file or step, for the log line each retry writes.</param>
    public static async Task RunAsync(Func<Task> operation, string what)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await operation().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsSharingViolation(ex) && attempt < Attempts)
            {
                var wait = Waits[attempt - 1];
                AppLogger.Warn($"FileRetry: {what} is in use (attempt {attempt} of {Attempts}); retrying in {wait} ms");
                await WaitAsync(wait).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Synchronous twin of <see cref="RunAsync(Func{Task}, string)"/> for callers already off the UI thread.</summary>
    public static void Run(Action operation, string what)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception ex) when (IsSharingViolation(ex) && attempt < Attempts)
            {
                var wait = Waits[attempt - 1];
                AppLogger.Warn($"FileRetry: {what} is in use (attempt {attempt} of {Attempts}); retrying in {wait} ms");
                WaitAsync(wait).GetAwaiter().GetResult();
            }
        }
    }
}
