using System.Text.Json;
using Microsoft.Web.WebView2.Wpf;

namespace Wrapp.Services;

/// <summary>
/// Bridges a WebView2 control to a Monaco DiffEditor instance.
/// Read-only side-by-side diff view for comparing two versions of a file.
/// </summary>
public sealed class MonacoDiffService : IDisposable
{
    private readonly WebView2 _webView;
    private readonly Task _initTask;
    private bool _initialized;
    private bool _disposed;

    // The host page is served from the monaco.local virtual host
    // (Assets\monaco\diff.html + diff.js) with a strict CSP -- no CDN
    // dependency, works offline. The theme travels via the query string;
    // MonacoHost.NavigateToPageAsync builds it.
    private const string PageName = "diff.html";

    public MonacoDiffService(WebView2 webView)
    {
        _webView = webView;
        App.ThemeChanged += OnAppThemeChanged;
        _initTask = InitializeAsync();
    }

    private void OnAppThemeChanged(string monacoTheme)
        => _ = SetThemeAsync(monacoTheme);

    private async Task InitializeAsync()
    {
        // Shares MonacoService's environment setup (same per-user data folder
        // and `--disable-gpu` fallback flags) via MonacoHost.
        await MonacoHost.InitAsync(_webView);

        await MonacoHost.NavigateToPageAsync(_webView, PageName);

        _initialized = true;
    }

    public async Task SetDiffAsync(string original, string modified, string language = "plaintext")
    {
        await _initTask;
        if (!_initialized) return;
        var origEscaped = JsonSerializer.Serialize(original);
        var modEscaped  = JsonSerializer.Serialize(modified);
        var langEscaped = JsonSerializer.Serialize(language);
        await _webView.ExecuteScriptAsync($"window.setDiff({origEscaped}, {modEscaped}, {langEscaped})");
    }

    /// <summary>Forces Monaco to recalculate its layout dimensions.</summary>
    public Task LayoutAsync() => LayoutAsync(false);

    /// <summary>
    /// Forces Monaco to recalculate its layout. When <paramref name="force"/> is true,
    /// issues a second layout after a short delay so WebView2 has time to finish
    /// applying a DPI change before Monaco re-measures.
    /// </summary>
    public async Task LayoutAsync(bool force)
    {
        await _initTask;
        if (!_initialized) return;
        await _webView.ExecuteScriptAsync("if(window.diffEditor) window.diffEditor.layout()");
        if (force)
        {
            await Task.Delay(50);
            await _webView.ExecuteScriptAsync("if(window.diffEditor) window.diffEditor.layout()");
        }
    }

    public async Task SetThemeAsync(string monacoTheme)
    {
        await _initTask;
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
        App.ThemeChanged -= OnAppThemeChanged;
    }
}
