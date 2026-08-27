using System;
using System.IO;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Workstream D: continuous updates for installed (Velopack-packaged) Wrapp.
/// Wraps <see cref="UpdateManager"/> with Wrapp's policy layer:
/// <list type="bullet">
/// <item><description>No-ops entirely for non-installed runs (dev F5, portable
///   publish folder) — only a Velopack install has an Update.exe to apply
///   packages.</description></item>
/// <item><description>The feed URL is treated as remote-code-execution
///   equivalent: HTTPS or UNC only, and it must carry a machine-bound DPAPI
///   trust token (same SEC-1 pattern as the Key Vault URL) before it is ever
///   contacted. Approval is surfaced via <see cref="Gates.UpdateFeedApprovalGate"/>
///   and the Settings Save flow.</description></item>
/// <item><description>Mode policy from <see cref="AppSettings.UpdateMode"/>:
///   Auto (download + stage on exit, offer restart), NotifyOnly, Disabled.
///   Manual checks from Settings run regardless of mode.</description></item>
/// </list>
/// Deliberately stateless between calls apart from the process-wide
/// "update already staged" latch — checks are cheap and the feed is the truth.
/// </summary>
public static class UpdateService
{
    /// <summary>Result states for a manual / startup update check.</summary>
    public enum CheckStatus
    {
        NotInstalled,
        NoFeedConfigured,
        InvalidFeedUrl,
        FeedNotApproved,
        UpToDate,
        UpdateAvailable,
        Downloaded,
        Failed,
    }

    /// <param name="SizeBytes">Full-package size from the feed manifest.</param>
    /// <param name="DeltaSizeBytes">Sum of the delta packages Velopack will
    /// actually download when a delta chain to the target exists (0 = full
    /// download). The full package is then REBUILT locally from the installed
    /// base + deltas — the "Rebuilding package" step on the update splash.</param>
    public readonly record struct CheckResult(
        CheckStatus Status, string? Version, string? NotesMarkdown, string? Error,
        long SizeBytes = 0, string? Sha = null, long DeltaSizeBytes = 0)
    {
        public static CheckResult Of(CheckStatus s) => new(s, null, null, null);
    }

    /// <summary>
    /// Version an already-scheduled apply will install on exit, or null when
    /// none is staged. App.OnExit stamps the InstanceCoordinator update
    /// marker with this so launches during the apply window wait instead of
    /// starting the old binary mid-swap (Phase B launch guard).
    /// </summary>
    public static string? ScheduledApplyVersion
        => _applyScheduled ? _downloadedInfo?.TargetFullRelease?.Version?.ToString() : null;

    /// <summary>
    /// True once WaitExitThenApplyUpdates has been called this session.
    /// CRITICAL single-call guard: each call spawns an Update.exe watcher on
    /// our PID, and a second watcher applies the update a SECOND time after
    /// the first one relaunches us (the double install-screen/splash cycle
    /// observed in the 0.6.299→301 field test — ApplyUpdatesAndRestart itself
    /// calls WaitExitThenApplyUpdates internally, so the old code registered
    /// two watchers).
    /// </summary>
    private static volatile bool _applyScheduled;

    // The manager + update from the completed download, kept so the install
    // step applies exactly what was verified without re-contacting the feed.
    private static UpdateManager? _downloadedMgr;
    private static UpdateInfo? _downloadedInfo;

    /// <summary>
    /// The update the startup (or manual) check found, surfaced through the
    /// advisory <see cref="Gates.UpdatePendingGate"/> as the action-needed
    /// indicator — never as a dialog over live work. Cleared once an apply
    /// is scheduled.
    /// </summary>
    public static string? PendingUpdateVersion { get; private set; }

    /// <summary>True while an available update awaits the user's opt-in.</summary>
    public static bool HasPendingUpdate => PendingUpdateVersion is not null && !_applyScheduled;

    // -----------------------------------------------------------------------
    // Feed URL validation + SEC-1 style trust tokens
    // -----------------------------------------------------------------------

    internal static string NormalizeFeedUrl(string? url)
        => (url ?? string.Empty).Trim().TrimEnd('/', '\\').ToLowerInvariant();

    /// <summary>
    /// HTTPS, UNC, or a rooted local folder. Plain HTTP is rejected outright —
    /// the feed delivers executable code and must not be fetchable over a
    /// spoofable transport. Local folders (e.g. <c>C:\wrapp\releases</c>) are
    /// same-machine trust — legitimate for update testing and offline feeds,
    /// and still gated by the per-user TOFU approval like every other feed.
    /// </summary>
    internal static bool IsValidFeedUrl(string? url)
    {
        var norm = (url ?? string.Empty).Trim();
        if (norm.Length == 0) return false;
        if (norm.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        if (IsLocalRootedPath(norm)) return true;
        return Uri.TryCreate(norm, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps;
    }

    /// <summary>Drive-letter-rooted path, e.g. "C:\feed" (not relative, not UNC).</summary>
    private static bool IsLocalRootedPath(string s)
        => s.Length >= 3
           && char.IsAsciiLetter(s[0])
           && s[1] == ':'
           && (s[2] == '\\' || s[2] == '/');

    /// <summary>DPAPI trust token for an approved feed URL (SEC-1 pattern).</summary>
    public static string IssueFeedTrustToken(string? url)
    {
        var norm = NormalizeFeedUrl(url);
        return norm.Length == 0 ? string.Empty : SecretProtection.Encrypt(norm);
    }

    /// <summary>
    /// True only when <paramref name="storedToken"/> is an authentic DPAPI
    /// envelope of the normalised <paramref name="url"/>. A plaintext or forged
    /// value, or a token minted by another user, fails — forcing a one-time
    /// approval before the feed is contacted.
    /// </summary>
    public static bool IsFeedTrusted(string? url, string? storedToken)
    {
        var norm = NormalizeFeedUrl(url);
        if (norm.Length == 0) return false;

        // D3: a feed mandated from MACHINE policy (HKLM — requires local
        // admin to write, a stronger authority than the per-user DPAPI
        // token) is trusted without the per-user TOFU approval. HKCU-
        // mandated feeds deliberately do NOT bypass: the user's own
        // processes can write HKCU, so bypassing there would let malware
        // self-approve a feed.
        if (Policy.PolicyService.Current.MachineMandatedEquals("UpdateFeedUrl", url))
            return true;

        if (string.IsNullOrEmpty(storedToken)) return false;
        var approved = SecretProtection.DecryptAuthentic(storedToken);
        return !string.IsNullOrEmpty(approved)
            && string.Equals(approved, norm, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Velopack plumbing
    // -----------------------------------------------------------------------

    /// <summary>
    /// True when this process runs from a Velopack install. Prefers the
    /// authoritative <see cref="UpdateManager.IsInstalled"/>; if the
    /// source-less construction is rejected, falls back to the documented
    /// install layout (Update.exe one level above the app dir:
    /// %LocalAppData%\{packId}\Update.exe beside current\). Exception-free —
    /// safe to call from bindings.
    /// </summary>
    public static bool IsInstalled
    {
        get
        {
            try { return new UpdateManager((IUpdateSource?)null!).IsInstalled; }
            catch
            {
                try
                {
                    var appDir = AppDomain.CurrentDomain.BaseDirectory;
                    var updateExe = Path.Combine(appDir, "..", "Update.exe");
                    return File.Exists(Path.GetFullPath(updateExe));
                }
                catch { return false; }
            }
        }
    }

    private static UpdateManager CreateManager(string feedUrl)
    {
        var trimmed = feedUrl.Trim();
        // UNC / local path feeds use the file source explicitly; string ctor
        // is reserved for HTTP(S).
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) || IsLocalRootedPath(trimmed))
            return new UpdateManager(new SimpleFileSource(new DirectoryInfo(trimmed)));
        return new UpdateManager(trimmed);
    }

    /// <summary>
    /// One update check. Pure policy evaluation + Velopack calls; never shows
    /// UI. <paramref name="download"/> additionally downloads and verifies the
    /// update packages (delta reconstruction happens here too, so this is the
    /// slow part — wire <paramref name="progress"/> to a background job).
    /// Downloading does NOT schedule the install; see
    /// <see cref="ScheduleApplyOnExit"/> (driven by UpdateFlowController).
    /// </summary>
    public static async Task<CheckResult> CheckAsync(AppSettings settings, bool download, Action<int>? progress = null)
    {
        if (!IsInstalled) return CheckResult.Of(CheckStatus.NotInstalled);
        if (string.IsNullOrWhiteSpace(settings.UpdateFeedUrl)) return CheckResult.Of(CheckStatus.NoFeedConfigured);
        if (!IsValidFeedUrl(settings.UpdateFeedUrl)) return CheckResult.Of(CheckStatus.InvalidFeedUrl);
        if (!IsFeedTrusted(settings.UpdateFeedUrl, settings.UpdateFeedTrustToken))
            return CheckResult.Of(CheckStatus.FeedNotApproved);

        try
        {
            var mgr = CreateManager(settings.UpdateFeedUrl);
            var info = await mgr.CheckForUpdatesAsync();
            if (info is null) return CheckResult.Of(CheckStatus.UpToDate);

            var target = info.TargetFullRelease;
            var version = target?.Version?.ToString() ?? "(unknown)";
            var notes = target?.NotesMarkdown;
            var size  = target?.Size ?? 0;
            // Feed-manifest hash, shown on the update splash for operator
            // verification (SHA256 when the feed carries it, else SHA1).
            var sha = string.IsNullOrEmpty(target?.SHA256) ? target?.SHA1 : target?.SHA256;
            // What actually crosses the wire when a delta chain exists.
            var deltaBytes = info.DeltasToTarget?.Sum(a => a.Size) ?? 0;
            if (deltaBytes > 0)
                AppLogger.Info($"Update: v{version} reachable via {info.DeltasToTarget!.Count()} delta(s), "
                    + $"{deltaBytes / 1024.0 / 1024.0:0.0} MB download (full package {size / 1024.0 / 1024.0:0.0} MB)");

            if (!download)
                return new CheckResult(CheckStatus.UpdateAvailable, version, notes, null, size, sha, deltaBytes);

            await mgr.DownloadUpdatesAsync(info, progress);
            _downloadedMgr = mgr;
            _downloadedInfo = info;
            AppLogger.Info($"Update: v{version} downloaded and verified; ready to install");
            return new CheckResult(CheckStatus.Downloaded, version, notes, null, size, sha, deltaBytes);
        }
        catch (Exception ex)
        {
            AppLogger.Exception("Update: check failed", ex);
            return new CheckResult(CheckStatus.Failed, null, null, ex.Message);
        }
    }

    /// <summary>
    /// Registers the (single) Update.exe apply-on-exit watcher for the
    /// downloaded update. Silent, so the user never sees the generic Velopack
    /// progress window — the app closes, the update applies, and (when
    /// <paramref name="restart"/>) Wrapp relaunches. Idempotent.
    /// </summary>
    public static bool ScheduleApplyOnExit()
    {
        var mgr = _downloadedMgr;
        var info = _downloadedInfo;
        if (mgr is null || info is null) return false;
        if (_applyScheduled) return true;
        try
        {
            mgr.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: true, restart: true);
            _applyScheduled = true;
            PendingUpdateVersion = null;   // no longer "pending" — it's staged
            AppLogger.Info($"Update: v{info.TargetFullRelease?.Version} scheduled to apply on exit (silent, relaunch)");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Exception("Update: scheduling apply-on-exit failed", ex);
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // Startup flow (fire-and-forget from App.StartupCoreAsync)
    // -----------------------------------------------------------------------

    /// <summary>
    /// The launch-time check. Phase C policy: an available update NEVER
    /// interrupts the session — it records itself as pending, which lights
    /// the action-needed indicator (advisory <see cref="Gates.UpdatePendingGate"/>);
    /// the user opts in from there or from Settings. Hard enforcement moves
    /// to the splash on a sibling-free launch (Phase D). Disabled mode / not
    /// installed / unapproved feed: log-only.
    /// </summary>
    public static async Task StartupCheckAsync(AppSettings settings)
    {
        var mode = settings.UpdateMode;
        if (string.Equals(mode, AppUpdateModes.Disabled, StringComparison.OrdinalIgnoreCase)) return;
        if (_applyScheduled) return;

        var result = await CheckAsync(settings, download: false);
        switch (result.Status)
        {
            case CheckStatus.UpdateAvailable:
                PendingUpdateVersion = result.Version;
                AppLogger.Info($"Update: v{result.Version} available -- surfaced via the action-needed indicator");
                break;

            case CheckStatus.FeedNotApproved:
                AppLogger.Info("Update: feed URL configured but not approved on this machine -- see the action-needed indicator");
                break;

            case CheckStatus.Failed:
            case CheckStatus.InvalidFeedUrl:
                AppLogger.Warn($"Update: startup check ended with {result.Status}{(result.Error is null ? "" : $" ({result.Error})")}");
                break;

            // NotInstalled / NoFeedConfigured / UpToDate: nothing to say at startup.
        }
    }

    /// <summary>Human-readable status line for the Settings page manual check.</summary>
    public static string Describe(CheckResult r) => r.Status switch
    {
        CheckStatus.NotInstalled => "Not an installed copy of Wrapp — updates apply only to installs from Setup.exe or the MSI.",
        CheckStatus.NoFeedConfigured => "No update feed configured.",
        CheckStatus.InvalidFeedUrl => "Update feed must be an https:// URL, a \\\\server\\share UNC path, or a local folder.",
        CheckStatus.FeedNotApproved => "Feed URL not yet approved on this machine — save settings to approve it.",
        CheckStatus.UpToDate => $"Up to date ({AppInfo.VersionDisplay}).",
        CheckStatus.UpdateAvailable => $"Update available: {r.Version}",
        CheckStatus.Downloaded => $"Update {r.Version} downloaded — ready to install.",
        CheckStatus.Failed => $"Check failed: {r.Error}",
        _ => string.Empty,
    };
}
