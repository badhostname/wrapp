namespace Wrapp.Services;

/// <summary>
/// Opt-in verbose UI interaction trace (Settings → toggle, applied live).
/// When enabled, navigation, clicks (with their containing view), dialog
/// opens, view construction, and slow resource loads write <c>[TRACE]</c>
/// lines to app.log — so a UI freeze report carries the complete trail of
/// what the operator did leading up to it. Added after the 0.6.326 field
/// stall (14s, zero log context beyond "was near the icon flow, had
/// navigated to Settings"). Off by default: at ordinary log volume the
/// trace would drown the rotation window.
/// </summary>
public static class UiTrace
{
    /// <summary>Live toggle; wired from AppSettings at startup and on save.</summary>
    public static volatile bool Enabled;

    public static void Log(string message)
    {
        if (Enabled) AppLogger.Info($"[TRACE] {message}");
    }
}
