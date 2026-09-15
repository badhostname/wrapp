using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;

namespace Wrapp.Services;

/// <summary>
/// Bridges a WebView2 control to a Monaco Editor instance.
/// Handles async initialization, content set/get, and change events.
/// </summary>
public sealed class MonacoService : IDisposable
{
    private readonly WebView2   _webView;
    private readonly Dispatcher _dispatcher;
    private bool   _initialized;
    private bool   _disposed;
    private string _pendingContent  = string.Empty;
    private string _pendingLanguage = "powershell";

    // Current editor language, updated on every SetContentAsync regardless
    // of init state so RefreshAsync can recreate the editor with the same
    // syntax highlighting.
    private string _currentLanguage = "powershell";

    private readonly DispatcherTimer _debounce;
    private string   _latestContent = string.Empty;

    /// <summary>Last known editor content, updated on every change message.</summary>
    public string LatestContent => _latestContent;

    /// <summary>Raised (debounced 500ms) when Monaco content changes.</summary>
    public event EventHandler<string>? ContentChanged;

    // The host page is served from the monaco.local virtual host
    // (Assets\monaco\editor.html + editor.js) with a strict CSP. The theme
    // travels via the query string; MonacoHost.NavigateToPageAsync builds it.
    private const string PageName = "editor.html";

    /// <summary>Tag used to identify this service in diagnostic log lines.</summary>
    private const string LogTag = "Monaco.Json";

    public MonacoService(WebView2 webView)
    {
        _webView    = webView;
        _dispatcher = webView.Dispatcher;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            ContentChanged?.Invoke(this, _latestContent);
        };

        App.ThemeChanged += OnAppThemeChanged;
        AppLogger.Info($"{LogTag}: ctor; webView.IsVisible={_webView.IsVisible}, ActualSize={_webView.ActualWidth:F0}x{_webView.ActualHeight:F0}");
        _ = InitializeAsync();
    }

    private void OnAppThemeChanged(string monacoTheme)
        => _ = SetThemeAsync(monacoTheme);

    private async Task InitializeAsync()
    {
        await MonacoHost.InitAsync(_webView);

        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

        // Wait for the page to fully load before marking initialized and executing scripts.
        await MonacoHost.NavigateToPageAsync(_webView, PageName);

        _initialized = true;

        if (!string.IsNullOrEmpty(_pendingContent))
            await SetContentAsync(_pendingContent, _pendingLanguage);
    }

    private void OnWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Fail closed -- only the monaco.local host page may feed content.
        if (!MonacoHost.IsTrustedSource(e.Source))
        {
            AppLogger.Warn($"{LogTag}: dropped web message from untrusted source '{e.Source}'");
            return;
        }
        try
        {
            _latestContent = e.TryGetWebMessageAsString();
        }
        catch
        {
            _latestContent = string.Empty;
        }
        _dispatcher.Invoke(() =>
        {
            _debounce.Stop();
            _debounce.Start();
        });
    }

    public async Task SetContentAsync(string content, string language = "powershell")
    {
        _latestContent = content;
        _currentLanguage = language;
        if (!_initialized)
        {
            _pendingContent  = content;
            _pendingLanguage = language;
            return;
        }
        var escaped     = JsonSerializer.Serialize(content);
        var langEscaped = JsonSerializer.Serialize(language);
        await _webView.ExecuteScriptAsync($"window.setValue({escaped}, {langEscaped})");
    }

    public async Task<string> GetContentAsync()
    {
        if (!_initialized) return _pendingContent;
        var result = await _webView.ExecuteScriptAsync("window.getValue()");
        return JsonSerializer.Deserialize<string>(result) ?? string.Empty;
    }

    /// <summary>
    /// Tears down the JS-side Monaco editor and re-navigates the WebView2
    /// to a fresh editor page, restoring the previous content and language.
    /// Use this when display-topology changes have left the editor in a
    /// wrong-sized / clipped state -- the fresh navigation triggers
    /// WebView2's natural startup path, which detects the current monitor
    /// DPI cleanly.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (!_initialized)
        {
            AppLogger.Info($"{LogTag}: RefreshAsync skipped -- not initialized");
            return;
        }

        AppLogger.Info($"{LogTag}: RefreshAsync start; ActualSize={_webView.ActualWidth:F0}x{_webView.ActualHeight:F0}");

        // Snapshot the live editor value (more current than _latestContent
        // since the user may have typed since the last debounced change msg).
        string content;
        try   { content = await GetContentAsync(); }
        catch { content = _latestContent; }
        var language = _currentLanguage;

        _initialized = false;
        try { _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived; } catch { }

        await MonacoHost.NavigateToPageAsync(_webView, PageName);

        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        _initialized = true;

        await SetContentAsync(content, language);
        AppLogger.Info($"{LogTag}: RefreshAsync done; lang={language}, len={content.Length}");
    }

    /// <summary>Forces Monaco to recalculate its layout dimensions.</summary>
    public Task LayoutAsync() => LayoutAsync(false);

    /// <summary>
    /// Asks Monaco to recompute its layout from the current DOM container
    /// size. Monaco is configured with <c>automaticLayout: true</c>, so this
    /// is mostly a safety net for size-changed / visibility-changed events
    /// where the DOM <c>ResizeObserver</c> might lag a frame. The
    /// <paramref name="force"/> flag is kept for call-site compatibility but
    /// no longer drives a second pass -- one layout call is enough.
    /// </summary>
    public async Task LayoutAsync(bool force)
    {
        if (!_initialized) return;
        await _webView.ExecuteScriptAsync("if(window.editor){window.editor.layout();}");
    }

    public async Task SetThemeAsync(string monacoTheme)
    {
        if (!_initialized) return;
        var escaped = JsonSerializer.Serialize(monacoTheme);
        var bg = monacoTheme == "vs" ? "#f3f3f3" : "#1e1e1e";
        await _webView.ExecuteScriptAsync(
            $"monaco.editor.setTheme({escaped});" +
            $"document.getElementById('container').style.background='{bg}';");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce.Stop();
        App.ThemeChanged -= OnAppThemeChanged;
        if (_initialized)
            _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
    }
}
