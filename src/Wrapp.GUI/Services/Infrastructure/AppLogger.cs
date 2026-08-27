using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Wrapp.Services;

/// <summary>
/// Asynchronous, redacting file logger for the app.
///
/// Producers (any thread) call <see cref="Info"/> / <see cref="Warn"/> /
/// <see cref="Error"/> / <see cref="Exception"/> and return immediately —
/// entries are enqueued on a bounded <see cref="Channel{T}"/> and written
/// sequentially by a single background task. This keeps logging off the
/// critical path of any caller (previously every log line blocked on a
/// synchronous <c>File.AppendAllText</c>).
///
/// Sensitive substrings (JWT bearer tokens, <c>Bearer &lt;…&gt;</c> headers,
/// <c>ClientSecret=…</c> assignments) are scrubbed from each message before
/// it's written to disk or forwarded to UI subscribers.
///
/// Rotation: when <c>app.log</c> exceeds <see cref="MaxLogBytes"/> it's
/// rolled to <c>app.1.log</c>; older generations shift up to
/// <see cref="RotationCount"/>; the oldest is deleted. Keeps a useful
/// history through a busy session without unbounded disk use.
/// </summary>
public static class AppLogger
{
    // M3 (multi-instance): held for process lifetime by the FIRST instance,
    // which keeps the well-known app.log name. The OS releases it on exit
    // or crash, so the next launch becomes primary again.
    private static Mutex? _primaryLogMutex;

    // Resolved once via PlatformConfig (env var > appsettings.json > default).
    private static readonly string _logDir     = PlatformConfig.LogDirectory;
    private static readonly string _logPath    = ResolveInstanceLogPath(PlatformConfig.LogPath);
    private static readonly LogLevel _minLevel = PlatformConfig.LogLevel;

    /// <summary>
    /// M3: the first instance logs to the configured app.log; concurrent
    /// instances get app.pid&lt;id&gt;.log so two processes never append to one
    /// file or race <see cref="RotateIfNeeded"/> (whose generation shifts can
    /// double-rotate/delete under a concurrent writer). The "pid" prefix keeps
    /// instance logs unambiguous next to rotation files (app.1.log..app.5.log).
    /// </summary>
    private static string ResolveInstanceLogPath(string configuredPath)
    {
        try
        {
            var mutex = new Mutex(false, @"Local\Wrapp_LogPrimary");
            bool isPrimary;
            try { isPrimary = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { isPrimary = true; }

            if (isPrimary)
            {
                _primaryLogMutex = mutex; // held until process exit
                return configuredPath;
            }

            mutex.Dispose();
            var dir  = Path.GetDirectoryName(configuredPath) ?? ".";
            var name = Path.GetFileNameWithoutExtension(configuredPath);
            return Path.Combine(dir, $"{name}.pid{Environment.ProcessId}.log");
        }
        catch
        {
            return configuredPath;
        }
    }

    /// <summary>True for app.pidNNN.log / app.pidNNN.M.log instance-log names (M3 cleanup).</summary>
    internal static bool IsInstanceLogName(string fileName)
        => Regex.IsMatch(fileName, @"^app\.pid\d+(\.\d+)?\.log$", RegexOptions.IgnoreCase);

    /// <summary>
    /// Deletes secondary-instance logs untouched for a week. A live instance
    /// keeps its file's write time fresh, so this only collects leftovers.
    /// </summary>
    private static void CleanStaleInstanceLogs()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-7);
            var ownPrefix = $"app.pid{Environment.ProcessId}.";
            foreach (var file in Directory.EnumerateFiles(_logDir, "app.pid*.log"))
            {
                var name = Path.GetFileName(file);
                if (!IsInstanceLogName(name)) continue;
                if (name.StartsWith(ownPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch { /* in use by a live instance -- skip */ }
            }
        }
        catch { /* cleanup is best-effort */ }
    }

    private const long MaxLogBytes  = 1_048_576; // 1 MB
    private const int  RotationCount = 5;        // app.log + app.1.log .. app.5.log
    private const int  QueueCapacity = 4096;     // drop on overflow rather than stall callers

    public static string LogPath => _logPath;

    /// <summary>Directory portion of <see cref="LogPath"/>. Always exists — used by views that
    /// want a jump-to-folder button independent of any specific log file.</summary>
    public static string LogDirectory => _logDir;

    /// <summary>
    /// Shared <see cref="IAppLogger"/> instance delegating to this static
    /// logger. Use from constructor parameters with default-argument fallback
    /// so services become mock-friendly without allocating per call:
    /// <code>public MyService(IAppLogger? log = null) { _log = log ?? AppLogger.Instance; }</code>
    /// </summary>
    public static IAppLogger Instance { get; } = new DefaultAppLogger();

    /// <summary>
    /// Fired on every log write so UI log views can subscribe for live updates.
    /// Subscribers must marshal to the UI thread themselves.
    /// </summary>
    public static event EventHandler<LogEntry>? EntryLogged;

    // ------------------------------------------------------------------
    // Queue + writer task
    // ------------------------------------------------------------------

    private static readonly Channel<LogEntry> _queue =
        Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private static readonly Task _writerLoop;

    // AsyncLocal so a correlation ID pushed on one thread flows across
    // awaits inside the same logical operation.
    private static readonly AsyncLocal<Guid?> _correlationId = new();

    static AppLogger()
    {
        try { Directory.CreateDirectory(_logDir); } catch { }
        CleanStaleInstanceLogs();
        _writerLoop = Task.Run(WriterLoopAsync);
    }

    // ------------------------------------------------------------------
    // Correlation ID API
    // ------------------------------------------------------------------

    /// <summary>
    /// Associates a correlation ID with the current logical operation so every
    /// log line from here forward (across awaits) carries the same id. Returns
    /// a scope that restores the previous correlation on dispose.
    /// </summary>
    public static CorrelationScope BeginCorrelation(Guid? id = null)
    {
        var previous = _correlationId.Value;
        _correlationId.Value = id ?? Guid.NewGuid();
        return new CorrelationScope(previous);
    }

    public readonly struct CorrelationScope : IDisposable
    {
        private readonly Guid? _previous;
        internal CorrelationScope(Guid? previous) { _previous = previous; }
        public void Dispose() => _correlationId.Value = _previous;
    }

    // ------------------------------------------------------------------
    // Public log API (non-blocking)
    // ------------------------------------------------------------------

    public static void Info(string message)
    {
        if (_minLevel > LogLevel.Info) return;
        Enqueue("INFO ", message);
    }
    public static void Warn(string message)
    {
        if (_minLevel > LogLevel.Warn) return;
        Enqueue("WARN ", message);
    }
    public static void Error(string message)
    {
        if (_minLevel > LogLevel.Error) return;
        Enqueue("ERROR", message);
    }

    public static void Exception(string context, Exception ex)
        => Error($"{context}: {ex}");

    /// <summary>
    /// Drains the queue synchronously and returns when all enqueued entries
    /// have been written. Call from App.OnExit so pending lines aren't lost.
    /// Bounded by a 2-second timeout to avoid blocking shutdown.
    /// </summary>
    public static void FlushAndShutdown()
    {
        try
        {
            _queue.Writer.TryComplete();
            _writerLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch { /* shutdown path — never throw */ }
    }

    // ------------------------------------------------------------------
    // Internal
    // ------------------------------------------------------------------

    private static void Enqueue(string level, string message)
    {
        // Scrub sensitive content BEFORE the entry hits the queue so neither
        // the disk nor any UI subscriber sees the raw secret.
        var scrubbed = Redact(message);
        var entry = new LogEntry(
            Timestamp:     SystemClock.Now,
            Level:         level.Trim(),
            Message:       scrubbed,
            ThreadId:      Environment.CurrentManagedThreadId,
            CorrelationId: _correlationId.Value);

        // Fire the UI event inline so LogsViewModel stays live even if the
        // writer is briefly stalled on disk I/O.
        try { EntryLogged?.Invoke(null, entry); } catch { }

        _queue.Writer.TryWrite(entry);
    }

    private static async Task WriterLoopAsync()
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var entry))
                {
                    WriteEntry(entry);
                }
            }
        }
        catch
        {
            // Logger must never crash. A writer-loop failure just stops
            // persistence — live events to UI continue to fire.
        }
    }

    private static void WriteEntry(LogEntry entry)
    {
        try
        {
            RotateIfNeeded();
            var corr = entry.CorrelationId is { } cid
                ? $" [{cid.ToString("N").Substring(0, 8)}]"
                : string.Empty;
            var line = $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} " +
                       $"[T{entry.ThreadId:D3}]{corr} [{entry.Level}] {entry.Message}" +
                       Environment.NewLine;
            File.AppendAllText(_logPath, line);
        }
        catch
        {
            // Never crash the writer loop on a single bad write.
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(_logPath)) return;
            if (new FileInfo(_logPath).Length <= MaxLogBytes) return;

            // Shift generations upward: app.(N-1).log -> app.N.log, etc.
            for (int i = RotationCount; i >= 1; i--)
            {
                var src = i == 1 ? _logPath : Path.ChangeExtension(_logPath, $".{i - 1}.log");
                var dst = Path.ChangeExtension(_logPath, $".{i}.log");
                if (File.Exists(src))
                    File.Move(src, dst, overwrite: true);
            }
        }
        catch { /* rotation is best-effort */ }
    }

    // ------------------------------------------------------------------
    // Redaction
    // ------------------------------------------------------------------

    private static readonly RegexOptions _regexOpts =
        RegexOptions.Compiled | RegexOptions.CultureInvariant;
    private static readonly RegexOptions _regexOptsIgnoreCase =
        _regexOpts | RegexOptions.IgnoreCase;

    // JWT shape: header.payload.signature, where header+payload start with eyJ.
    // Catches bare JWTs even when Bearer prefix scrub missed them (e.g. JSON
    // payload bodies that log raw tokens without an auth-header context).
    private static readonly Regex _jwtRegex = new(
        @"eyJ[A-Za-z0-9_\-]{10,}\.eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]+",
        _regexOpts);

    // Bearer <token> in any casing (RFC 7235: auth scheme is case-insensitive).
    // Character class stops at ", }, ], comma, semicolon, whitespace -- which
    // correctly terminates the capture at JSON-value / header-value boundaries.
    private static readonly Regex _bearerRegex = new(
        @"\bBearer\s+[A-Za-z0-9_\-\.=+/]+",
        _regexOptsIgnoreCase);

    // "ClientSecret": "value" / ClientSecret=value / client_secret=value.
    // Optional closing quote after the key handles JSON ("ClientSecret":),
    // optional opening quote before the value handles quoted values.
    // Case-insensitive; covers camelCase/snake_case/kebab-case variants.
    private static readonly Regex _clientSecretRegex = new(
        @"client[_\-]?secret""?\s*[=:]\s*""?(?<v>[^""\s,}\]]+)""?",
        _regexOptsIgnoreCase);

    // OAuth form-encoded tokens: access_token=..., refresh_token=..., id_token=...
    // in a URL-encoded body or query string. Captures up to next & / whitespace.
    private static readonly Regex _oauthTokenRegex = new(
        @"\b(?<name>access_token|refresh_token|id_token)\s*=\s*(?<v>[^&\s""]+)",
        _regexOptsIgnoreCase);

    // HTTP Basic auth: `Authorization: Basic <base64>` -- the credential body
    // is base64(username:password), which for Azure DevOps API calls is
    // typically `:<PAT>`. Cover all casings of the scheme word; capture the
    // base64 alphabet (A-Z a-z 0-9 + / =) plus `_` and `-` for URL-safe
    // variants. Stops at any non-base64 char including comma, semicolon,
    // whitespace -- correctly terminates inside JSON/HTTP-header contexts.
    private static readonly Regex _basicAuthRegex = new(
        @"\bBasic\s+[A-Za-z0-9+/=_\-]+",
        _regexOptsIgnoreCase);

    // Azure DevOps PAT in URL query strings: `?pat=<token>` or `&pat=<token>`.
    // Less common than Basic-auth-encoded PATs, but seen in some REST samples
    // and in URL-logging tools. Captures up to next delimiter.
    private static readonly Regex _patQueryRegex = new(
        @"\bpat\s*=\s*[^&\s""']+",
        _regexOptsIgnoreCase);

    // Workstream O: org-supplied redaction patterns from defaults.local.json
    // SensitivePatterns (regex strings -- tenant GUIDs, server names, domains).
    // Volatile snapshot-swap: the writer loop reads whatever array is current;
    // SetOrgRedactionPatterns replaces the whole array atomically.
    private static volatile Regex[] _orgPatterns = Array.Empty<Regex>();

    /// <summary>
    /// Registers additional org-specific redaction patterns (case-insensitive
    /// regex). Invalid patterns are logged and skipped; call is idempotent per
    /// pattern set (replaces the previous org set).
    /// </summary>
    /// <summary>
    /// SEC-4: org patterns come from defaults.local.json — outside this
    /// codebase's control. Redact runs synchronously on the CALLING thread
    /// (often the dispatcher), so a catastrophic-backtracking pattern without
    /// a timeout would hang the app on the next log line.
    /// </summary>
    private static readonly TimeSpan OrgPatternTimeout = TimeSpan.FromMilliseconds(100);

    public static void SetOrgRedactionPatterns(IEnumerable<string> patterns)
    {
        var compiled = new List<Regex>();
        foreach (var p in patterns ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try { compiled.Add(new Regex(p, _regexOptsIgnoreCase, OrgPatternTimeout)); }
            catch (Exception ex) { Warn($"AppLogger: invalid SensitivePatterns regex '{p}': {ex.Message}"); }
        }
        _orgPatterns = compiled.ToArray();
        if (compiled.Count > 0)
            Info($"AppLogger: {compiled.Count} org redaction pattern(s) active");
    }

    // Workstream P: literal VALUES of sensitive custom placeholders. Separate
    // array from the org patterns so neither registration clobbers the other.
    private static volatile Regex[] _sensitiveValuePatterns = Array.Empty<Regex>();

    /// <summary>
    /// Registers the literal values of sensitive placeholders so they can
    /// never appear in a log line, even after being replaced into logged
    /// content (commands, paths, field dumps). Values are escaped — they
    /// match literally, not as regex. Replaces the previous set.
    /// </summary>
    public static void SetSensitiveValuePatterns(IEnumerable<string> literalValues)
    {
        var compiled = (literalValues ?? Enumerable.Empty<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v) && v.Length >= 4) // tiny values would shred logs
            .Select(v => new Regex(Regex.Escape(v), _regexOptsIgnoreCase))
            .ToArray();
        _sensitiveValuePatterns = compiled;
        if (compiled.Length > 0)
            Info($"AppLogger: {compiled.Length} sensitive placeholder value(s) registered for redaction");
    }

    internal static string Redact(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;
        var s = _jwtRegex.Replace(message, "eyJ***REDACTED***");
        s = _bearerRegex.Replace(s, "Bearer ***REDACTED***");
        s = _basicAuthRegex.Replace(s, "Basic ***REDACTED***");
        s = _clientSecretRegex.Replace(s, "client_secret=***REDACTED***");
        s = _oauthTokenRegex.Replace(s, "${name}=***REDACTED***");
        s = _patQueryRegex.Replace(s, "pat=***REDACTED***");
        foreach (var org in _orgPatterns)
        {
            // A timed-out org pattern skips THAT pattern for THIS line rather
            // than hanging the caller; built-in + literal passes still ran.
            try { s = org.Replace(s, "***ORG-REDACTED***"); }
            catch (RegexMatchTimeoutException) { }
        }
        foreach (var val in _sensitiveValuePatterns)
            s = val.Replace(s, "***REDACTED***");
        return s;
    }

    /// <summary>
    /// Human-readable summary of active redaction for the Settings surface:
    /// the built-in scrubber categories plus the org pattern strings.
    /// </summary>
    public static (string[] BuiltInCategories, string[] OrgPatterns, int SensitiveValueCount)
        GetActiveRedactionSummary()
        => (new[]
            {
                "JWTs (eyJ…​.eyJ….sig)",
                "Bearer authorization headers",
                "Basic authorization headers",
                "client_secret values (any casing)",
                "OAuth form tokens (access_token / refresh_token / id_token)",
                "Azure DevOps pat= query values",
            },
            _orgPatterns.Select(r => r.ToString()).ToArray(),
            _sensitiveValuePatterns.Length);
}

public record LogEntry(
    DateTime Timestamp,
    string Level,
    string Message,
    int ThreadId = 0,
    Guid? CorrelationId = null);
