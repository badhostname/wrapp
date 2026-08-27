using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Wrapp.Services;

/// <summary>
/// Shared WebView2 bootstrap for the Monaco-backed editors (single, tabbed,
/// diff). Centralizes the environment creation, GPU-fallback flags, offline
/// <c>monaco.local</c> virtual-host mapping, and the navigate-and-wait
/// handshake so the three editor services can't drift in how they host
/// WebView2. The per-editor HTML stays in each service (it genuinely differs).
/// </summary>
internal static class MonacoHost
{
    /// <summary>
    /// Creates the WebView2 environment with Wrapp's per-user data folder and
    /// GPU-fallback flags (for AVD multi-session / software rendering), attaches
    /// it to <paramref name="webView"/>, and maps the bundled Monaco assets to
    /// the offline <c>monaco.local</c> virtual host (falling back to CDN if the
    /// bundled files are absent).
    /// </summary>
    public static async Task InitAsync(WebView2 webView)
    {
        // M4 (multi-instance): each process gets its own user-data folder.
        // Two processes CAN share a UDF only while their env options and
        // runtime version match exactly -- an app update while an older
        // instance runs would leave the new instance unable to start its
        // browser process. Per-pid folders sidestep that coupling entirely.
        var userDataRoot = Path.Combine(PlatformConfig.WrappRoot, "WebView2");
        var userDataFolder = Path.Combine(userDataRoot, $"pid{Environment.ProcessId}");
        CleanupOrphanedUserDataFolders(userDataRoot, userDataFolder);

        var options = new CoreWebView2EnvironmentOptions("--disable-gpu --disable-gpu-compositing");
        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
        await webView.EnsureCoreWebView2Async(env);

        // Workstream G (WebView2 hardening). Applied centrally so all four
        // editor instances (single / tabbed / diff / history) are hardened
        // identically. These touch ONLY the browser settings + navigation
        // policy -- NOT the HTML, the NavigateToString handshake,
        // automaticLayout, or the RefreshAsync DPI escape-hatch -- so the
        // PerMonitorV2 rendering behaviour is unaffected.
        HardenSettings(webView.CoreWebView2);
        AddNavigationAllowList(webView.CoreWebView2);

        var monacoDir = Path.Combine(AppContext.BaseDirectory, "monaco");
        if (Directory.Exists(monacoDir))
        {
            // G1: DenyCors (was Allow) -- the host pages now live ON
            // monaco.local, so every asset request is same-origin and CORS
            // grants are unnecessary surface.
            webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "monaco.local", monacoDir,
                CoreWebView2HostResourceAccessKind.DenyCors);
            AppLogger.Info($"Monaco: mapped virtual host to {monacoDir}");
        }
        else
        {
            // No fallback exists: the host page itself is served from the
            // virtual host, and the G2 allow-list blocks external origins.
            AppLogger.Warn($"Monaco: bundled files not found at {monacoDir} -- editors cannot load");
        }
    }

    private static int _udfCleanupStarted;

    /// <summary>
    /// M4: best-effort background sweep of the WebView2 root -- removes other
    /// pids' orphaned user-data folders and the legacy shared-layout content.
    /// A LIVE instance's folder has files held open by its browser processes,
    /// so its delete fails and is skipped; only leftovers actually go. Runs
    /// once per process, off the editor-init path.
    /// </summary>
    private static void CleanupOrphanedUserDataFolders(string root, string ownFolder)
    {
        if (System.Threading.Interlocked.Exchange(ref _udfCleanupStarted, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(root)) return;
                foreach (var entry in Directory.EnumerateFileSystemEntries(root))
                {
                    if (string.Equals(entry, ownFolder, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                        else File.Delete(entry);
                    }
                    catch { /* held by a live instance -- skip */ }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Monaco: WebView2 UDF cleanup failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// The only origin editor pages may have. Also the prefix
    /// <see cref="IsTrustedSource"/> validates web messages against (G4).
    /// </summary>
    internal const string Origin = "https://monaco.local/";

    /// <summary>
    /// G4. True when a WebMessage's <c>e.Source</c> belongs to the
    /// monaco.local host page. Handlers fail closed on anything else.
    /// </summary>
    internal static bool IsTrustedSource(string? source)
        => source is not null && source.StartsWith(Origin, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// G1. Navigates to a host page served from the <c>monaco.local</c>
    /// virtual host (real origin -- enables the CSP on the page and the G4
    /// source checks) and awaits <c>NavigationCompleted</c>. The current
    /// Monaco theme travels via the query string, replacing the C# string
    /// interpolation the old inline HTML used. This is still a plain
    /// navigation, so the RefreshAsync DPI escape-hatch semantics
    /// (fresh startup path on re-navigate) are unchanged.
    /// </summary>
    public static async Task<bool> NavigateToPageAsync(WebView2 webView, string pageName)
    {
        var url = $"{Origin}{pageName}?theme={Uri.EscapeDataString(App.CurrentMonacoTheme)}";
        var navCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e)
            => navCompleted.TrySetResult(e.IsSuccess);
        webView.CoreWebView2.NavigationCompleted += OnNav;
        try
        {
            webView.CoreWebView2.Navigate(url);
            var ok = await navCompleted.Task;
            if (!ok) AppLogger.Warn($"Monaco: navigation to {pageName} failed");
            return ok;
        }
        finally
        {
            webView.CoreWebView2.NavigationCompleted -= OnNav;
        }
    }

    /// <summary>
    /// G3. Disables WebView2 surface area Wrapp's editors never use (dev tools,
    /// the browser's default context menu + accelerator keys, status bar, host
    /// objects, password/form autofill). Leaves <c>IsScriptEnabled</c> and
    /// <c>IsWebMessageEnabled</c> at their defaults (true) -- Monaco needs both.
    /// Intentionally does NOT touch zoom / DPI-adjacent settings.
    /// </summary>
    private static void HardenSettings(CoreWebView2 core)
    {
        var s = core.Settings;
        s.AreDevToolsEnabled            = false;
        s.AreDefaultContextMenusEnabled = false; // Monaco renders its own (JS) context menu
        s.AreBrowserAcceleratorKeysEnabled = false; // Monaco's editor keybindings still fire
        s.IsStatusBarEnabled            = false;
        s.AreHostObjectsAllowed         = false; // Wrapp uses postMessage, never AddHostObjectToScript
        s.IsPasswordAutosaveEnabled     = false;
        s.IsGeneralAutofillEnabled      = false;
    }

    /// <summary>
    /// G2. Central navigation allow-list for every editor instance. Cancels any
    /// top-level or frame navigation to an external web origin (only
    /// <c>https://monaco.local</c> is permitted) and suppresses new-window
    /// requests. Non-web schemes (<c>about:</c>, <c>data:</c>, <c>blob:</c>)
    /// are allowed so the <c>NavigateToString</c> host document and Monaco's
    /// own worker/resource loading keep working.
    /// </summary>
    private static void AddNavigationAllowList(CoreWebView2 core)
    {
        core.NavigationStarting      += (_, e) => CancelIfBlocked(e.Uri, () => e.Cancel = true);
        core.FrameNavigationStarting += (_, e) => CancelIfBlocked(e.Uri, () => e.Cancel = true);
        // Editors never open popups; suppress window.open / target=_blank.
        core.NewWindowRequested      += (_, e) => e.Handled = true;
    }

    private static void CancelIfBlocked(string uri, Action cancel)
    {
        if (!IsNavigationBlocked(uri)) return;
        AppLogger.Warn($"Monaco: blocked navigation to '{uri}' (not monaco.local)");
        cancel();
    }

    /// <summary>
    /// True only for external web navigations. Local schemes used by Monaco's
    /// internals (about:/data:/blob:) are always allowed; among web origins,
    /// only <c>https://monaco.local/</c> passes. The prefix is slash-terminated
    /// so lookalike hosts (<c>monaco.localevil.example</c>) cannot match.
    /// </summary>
    private static bool IsNavigationBlocked(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return false;
        bool isWeb = uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                  || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!isWeb) return false;
        return !uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase);
    }

}
