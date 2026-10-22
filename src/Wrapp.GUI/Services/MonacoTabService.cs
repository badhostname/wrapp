using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;

namespace Wrapp.Services;

/// <summary>
/// Manages a single WebView2 control with multiple Monaco text models (tabs).
/// Reduces WebView2 instances from 3 to 1 for the script editors.
/// </summary>
public sealed class MonacoTabService : IDisposable
{
    private readonly WebView2   _webView;
    private readonly Dispatcher _dispatcher;
    private bool   _initialized;
    private bool   _disposed;
    private string _activeModelId = string.Empty;

    // Pending model content queued before Monaco is ready
    private readonly Dictionary<string, (string Content, string Language)> _pendingModels = new();
    private string? _pendingSwitchId;

    // Last known content per model, updated on every change message
    private readonly Dictionary<string, string> _latestContent = new();

    // Tracks the language each model was created with so RefreshAsync can
    // recreate them with the same syntax highlighting after re-navigation.
    private readonly Dictionary<string, string> _modelLanguages = new();

    private readonly DispatcherTimer _debounce;
    private string _lastChangedModelId = string.Empty;

    /// <summary>Raised (debounced 500ms) when the active model's content changes.
    /// Args: (ModelId, Content).</summary>
    public event EventHandler<(string ModelId, string Content)>? ContentChanged;

    /// <summary>Returns the last known content for the given model.</summary>
    public string GetLatestContent(string modelId)
        => _latestContent.TryGetValue(modelId, out var c) ? c : string.Empty;

    /// <summary>ID of the currently displayed model.</summary>
    public string ActiveModelId => _activeModelId;

    // The host page is served from the monaco.local virtual host
    // (Assets\monaco\tabbed.html + tabbed.js) with a strict CSP. The theme
    // travels via the query string; MonacoHost.NavigateToPageAsync builds it.
    private const string PageName = "tabbed.html";

    /// <summary>Tag used to identify this service in diagnostic log lines.</summary>
    private const string LogTag = "Monaco.Tab";

    public MonacoTabService(WebView2 webView)
    {
        _webView    = webView;
        _dispatcher = webView.Dispatcher;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            ContentChanged?.Invoke(this, (_lastChangedModelId, GetLatestContent(_lastChangedModelId)));
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

        await MonacoHost.NavigateToPageAsync(_webView, PageName);

        _initialized = true;

        // Flush any models queued before initialization
        foreach (var (id, (content, language)) in _pendingModels)
            await CreateModelAsync(id, content, language);
        _pendingModels.Clear();

        // Apply pending tab switch
        if (_pendingSwitchId is not null)
        {
            await SwitchModelAsync(_pendingSwitchId);
            _pendingSwitchId = null;
        }
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
            var raw = e.TryGetWebMessageAsString();
            using var doc = JsonDocument.Parse(raw);
            var id = doc.RootElement.GetProperty("id").GetString() ?? string.Empty;
            var content = doc.RootElement.GetProperty("content").GetString() ?? string.Empty;
            _latestContent[id] = content;
            _lastChangedModelId = id;
        }
        catch
        {
            // Ignore malformed messages
        }
        _dispatcher.Invoke(() =>
        {
            _debounce.Stop();
            _debounce.Start();
        });
    }

    public async Task CreateModelAsync(string modelId, string content, string language = "powershell")
    {
        _latestContent[modelId] = content;
        _modelLanguages[modelId] = language;
        if (!_initialized)
        {
            _pendingModels[modelId] = (content, language);
            return;
        }
        var escaped     = JsonSerializer.Serialize(content);
        var langEscaped = JsonSerializer.Serialize(language);
        var idEscaped   = JsonSerializer.Serialize(modelId);
        await _webView.ExecuteScriptAsync($"window.createModel({idEscaped}, {escaped}, {langEscaped})");
    }

    public async Task SwitchModelAsync(string modelId)
    {
        _activeModelId = modelId;
        if (!_initialized)
        {
            _pendingSwitchId = modelId;
            return;
        }
        var idEscaped = JsonSerializer.Serialize(modelId);
        await _webView.ExecuteScriptAsync($"window.switchModel({idEscaped})");
    }

    public async Task SetModelContentAsync(string modelId, string content, string language = "powershell")
    {
        _latestContent[modelId] = content;
        _modelLanguages[modelId] = language;
        if (!_initialized)
        {
            _pendingModels[modelId] = (content, language);
            return;
        }
        var escaped     = JsonSerializer.Serialize(content);
        var langEscaped = JsonSerializer.Serialize(language);
        var idEscaped   = JsonSerializer.Serialize(modelId);
        await _webView.ExecuteScriptAsync($"window.setModelContent({idEscaped}, {escaped}, {langEscaped})");
    }

    public async Task<string> GetModelContentAsync(string modelId)
    {
        if (!_initialized)
            return _pendingModels.TryGetValue(modelId, out var p) ? p.Content : string.Empty;
        var idEscaped = JsonSerializer.Serialize(modelId);
        var result = await _webView.ExecuteScriptAsync($"window.getModelContent({idEscaped})");
        return JsonSerializer.Deserialize<string>(result) ?? string.Empty;
    }

    /// <summary>
    /// Tears down the JS-side Monaco editor and re-navigates the WebView2
    /// to a fresh editor page, restoring all models and the active tab.
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

        AppLogger.Info($"{LogTag}: RefreshAsync start; ActualSize={_webView.ActualWidth:F0}x{_webView.ActualHeight:F0}, models={_latestContent.Count}, active={_activeModelId}");

        // Snapshot every known model's current content. Prefer the live
        // editor value over the cached copy because the user may have typed
        // since the last debounced change message.
        var contents = new Dictionary<string, string>();
        foreach (var modelId in _latestContent.Keys.ToList())
        {
            try   { contents[modelId] = await GetModelContentAsync(modelId); }
            catch { contents[modelId] = _latestContent[modelId]; }
        }
        var savedActiveId = _activeModelId;

        // Detach handlers and flip _initialized so callers that race with
        // the navigation don't try to drive the in-flight editor.
        _initialized = false;
        try { _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived; } catch { }

        await MonacoHost.NavigateToPageAsync(_webView, PageName);

        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        _initialized = true;

        // Recreate every model with its previous language + content, then
        // restore the active tab.
        foreach (var (id, content) in contents)
        {
            var lang = _modelLanguages.TryGetValue(id, out var l) ? l : "powershell";
            await CreateModelAsync(id, content, lang);
        }
        if (!string.IsNullOrEmpty(savedActiveId))
            await SwitchModelAsync(savedActiveId);

        AppLogger.Info($"{LogTag}: RefreshAsync done; restored {contents.Count} models, active={savedActiveId}");
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
