using System.IO;
using System.Text.Json;

namespace Wrapp.Services;

/// <summary>
/// Platform/environment-driven paths and log level.
///
/// <para>Resolution order for every setting:</para>
/// <list type="number">
///   <item>Environment variable (e.g. <c>WRAPP_LOG_PATH</c>) — highest priority;
///   enterprise deployments use GPO / user-scope env vars to roam paths.</item>
///   <item>Matching key in <c>appsettings.json</c> (next to the exe).</item>
///   <item>Hardcoded default (under <c>%LOCALAPPDATA%\Wrapp\</c>).</item>
/// </list>
///
/// <para>All paths are resolved once at startup (lazy) to avoid repeated file
/// I/O. Callers should hold the resolved path for the lifetime of the process.</para>
/// </summary>
public static class PlatformConfig
{
    // -----------------------------------------------------------------------
    // Backing store
    // -----------------------------------------------------------------------

    private static readonly Lazy<JsonElement?> _appSettings = new(LoadAppSettings);

    private static readonly string _localAppData = Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData);

    private static readonly string _wrappRoot = Path.Combine(_localAppData, "Wrapp");

    // -----------------------------------------------------------------------
    // Resolved values (cached on first access)
    // -----------------------------------------------------------------------

    private static readonly Lazy<string> _logPath = new(() =>
        Resolve("WRAPP_LOG_PATH", "LogPath", Path.Combine(_wrappRoot, "app.log")));

    private static readonly Lazy<LogLevel> _logLevel = new(() =>
        ParseLogLevel(Resolve("WRAPP_LOG_LEVEL", "LogLevel", "Info")));

    private static readonly Lazy<string> _settingsPath = new(() =>
        Resolve("WRAPP_SETTINGS_PATH", "SettingsPath", Path.Combine(_wrappRoot, "settings.json")));

    private static readonly Lazy<string> _msalCachePath = new(() =>
        Resolve("WRAPP_MSAL_CACHE_PATH", "MsalCachePath", Path.Combine(_wrappRoot, "msal_cache.bin")));

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    public static string LogPath       => _logPath.Value;
    public static LogLevel LogLevel    => _logLevel.Value;
    public static string SettingsPath  => _settingsPath.Value;

    /// <summary>
    /// THE per-user app data root (<c>%LOCALAPPDATA%\Wrapp</c>). Every service
    /// must build its paths from here (or the named subpaths below) — the
    /// audit found 11 files re-deriving it by hand, two of which silently
    /// bypassed the env-var overrides this class exists to honor. A lint rule
    /// (LocalApplicationDataOnlyInPlatformConfig) enforces this now.
    /// </summary>
    public static string WrappRoot => _wrappRoot;

    /// <summary>Cross-process lock files (bundle locks, instance registry).</summary>
    public static string LockDir => Path.Combine(_wrappRoot, "Locks");
    public static string MsalCachePath => _msalCachePath.Value;

    /// <summary>Convenience: directory portion of <see cref="LogPath"/>.</summary>
    public static string LogDirectory => Path.GetDirectoryName(LogPath) ?? _wrappRoot;

    /// <summary>
    /// Shared <see cref="IEnvironmentConfig"/> instance delegating to these
    /// statics. Use from constructor parameters with default-argument fallback
    /// so services become mock-friendly:
    /// <code>public MyService(IEnvironmentConfig? env = null) { _env = env ?? PlatformConfig.Instance; }</code>
    /// </summary>
    public static IEnvironmentConfig Instance { get; } = new DefaultEnvironmentConfig();

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string Resolve(string envVar, string settingsKey, string defaultValue)
    {
        // 1. Env var
        var env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(env)) return Expand(env);

        // 2. appsettings.json
        if (_appSettings.Value is JsonElement settings
            && settings.TryGetProperty(settingsKey, out var prop)
            && prop.ValueKind == JsonValueKind.String)
        {
            var value = prop.GetString();
            if (!string.IsNullOrWhiteSpace(value)) return Expand(value);
        }

        // 3. Hardcoded default
        return defaultValue;
    }

    private static string Expand(string path)
        => Environment.ExpandEnvironmentVariables(path);

    private static JsonElement? LoadAppSettings()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return JsonDocument.Parse(stream).RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static LogLevel ParseLogLevel(string value) => value.Trim().ToLowerInvariant() switch
    {
        "error" => Services.LogLevel.Error,
        "warn" or "warning" => Services.LogLevel.Warn,
        "info" => Services.LogLevel.Info,
        "debug" or "verbose" => Services.LogLevel.Debug,
        _ => Services.LogLevel.Info,
    };
}

/// <summary>
/// Minimum log level filter. Entries with a numerically-lower value than
/// the configured threshold are dropped before the queue write.
/// </summary>
public enum LogLevel
{
    Debug = 0,
    Info  = 1,
    Warn  = 2,
    Error = 3,
}
