using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.ViewModels;

/// <summary>
/// Multi-tenant connection-check + sign-in path for <see cref="RunViewModel"/>.
/// Owns periodic connection refresh, per-tenant token expiry timer,
/// configured-vs-detected reconciliation, and the manual sign-in command
/// for individual connection cards. Moved into a partial file so the core
/// VM focuses on the packaging-run lifecycle.
/// </summary>
public partial class RunViewModel
{
    // -----------------------------------------------------------------------
    // Connection checking (multi-tenant Intune + single SCCM)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Retry policy for silent token attempts. Replaces the 15s always-on
    /// poll (the app's top "Not Responding" source — see SilentAuthGate docs):
    /// silent attempts now happen only when an event makes success plausible.
    /// </summary>
    private readonly SilentAuthGate _silentGate = new();

    /// <summary>
    /// Fire-and-forget wrapper that catches all exceptions (including
    /// OperationCanceledException from a dismissed MSAL prompt) so they
    /// don't crash the app when called from constructors or event handlers.
    /// </summary>
    private async Task SafeRefreshAsync()
    {
        try
        {
            await RefreshConnectionStatusAsync();
        }
        catch (OperationCanceledException)
        {
            AppLogger.Info("[Run] Connection refresh cancelled by user.");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[Run] Connection refresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Proactively checks connection status for the current target.
    /// Creates one ConnectionStatus per configured Intune tenant and checks
    /// each for a cached token. Safe to call at any time.
    /// Guarded by a SemaphoreSlim so only one refresh runs at a time --
    /// concurrent callers are silently skipped to prevent stacking
    /// multiple MSAL interactive prompts.
    /// </summary>
    public async Task RefreshConnectionStatusAsync()
    {
        // Non-blocking: if another refresh is already running, skip this one.
        if (!await _refreshLock.WaitAsync(0))
        {
            AppLogger.Info("[Run] Skipping overlapping connection refresh.");
            return;
        }

        try
        {
            if (Target is RunTarget.Intune or RunTarget.Both)
            {
                var tenants = _appInfoVm.FullConfig.IntuneTenants;
                SyncIntuneConnections(tenants);

                // Ensure MSAL client is available for silent checks
                await EnsureAuthServiceInitializedAsync(tenants);

                // Check Graph API reachability once (shared for all tenants)
                bool graphReachable = await _connectionChecker.CheckGraphReachableAsync();
                AppLogger.Info($"[Run] Graph API reachable: {graphReachable}");

                // Check each tenant's token status
                foreach (var conn in IntuneConnections)
                {
                    if (!conn.IsEnabled)
                    {
                        conn.State = ConnectionState.Skipped;
                        conn.StatusText = "Skipped";
                        continue;
                    }

                    conn.State = ConnectionState.Checking;
                    conn.StatusText = "Checking...";

                    MsalTokenResult? token = GetCachedTenantToken(conn.TenantId);

                    // Try silent acquisition if no valid cached token — but only
                    // when the gate says an attempt is worth making (a tenant in
                    // ui-required stays quiet until an unlock event; transient
                    // failures back off instead of retrying every view load).
                    if (token is null && _silentGate.ShouldAttempt(conn.TenantId, SystemClock.UtcNow))
                    {
                        var attempt = await _authService.TryAcquireTokenSilentForTenantDetailedAsync(conn.TenantId);
                        _silentGate.Record(conn.TenantId, attempt.Outcome, SystemClock.UtcNow);
                        token = attempt.Token;
                        if (token is not null)
                        {
                            _tenantTokens[conn.TenantId] = token;
                            AppLogger.Info($"[Run] Cached token found for tenant {conn.TargetName}: {token.UserPrincipalName ?? "app"}");
                        }
                    }

                    ConnectionChecker.ApplyTokenStatus(conn, token, graphReachable);
                }

                // Log per-tenant results summary
                int connected = IntuneConnections.Count(c => c.State == ConnectionState.Connected);
                int total = IntuneConnections.Count;
                AppLogger.Info($"[Run] Intune tenants: {connected}/{total} connected");
                foreach (var conn in IntuneConnections)
                {
                    var detail = conn.State == ConnectionState.Connected
                        ? conn.DetailLine1
                        : conn.StatusText;
                    AppLogger.Info($"[Run]   {conn.TargetName} ({conn.TenantId}): {conn.State} - {detail}");
                }

                // Start timer for countdown updates and periodic silent re-checks
                if (IntuneConnections.Count > 0)
                    StartTokenTimer();
            }
            else
            {
                IntuneConnections.Clear();
                _tokenTimer?.Stop();
            }

            if (Target is RunTarget.SCCM or RunTarget.Both)
            {
                // Detect the locally available SCCM site
                var sccmResult = await _ps.TestSccmConnectivityAsync();
                var detectedSite = sccmResult.Available ? sccmResult.SiteCode : null;

                if (sccmResult.Available)
                    AppLogger.Info($"[Run] SCCM detected: site={sccmResult.SiteCode}, server={sccmResult.ServerName}");
                else
                    AppLogger.Warn($"[Run] SCCM not available: {sccmResult.Reason}");

                // Sync one ConnectionStatus per configured site
                var sccmSites = _appInfoVm.FullConfig.SccmSites;
                SyncSccmConnections(sccmSites, detectedSite);

                // Set detail info on the connected site
                if (detectedSite is not null)
                {
                    var connSite = SccmConnections.FirstOrDefault(c =>
                        string.Equals(c.TenantId, detectedSite, StringComparison.OrdinalIgnoreCase));
                    if (connSite is not null)
                    {
                        connSite.DetailLine1 = sccmResult.ServerName ?? string.Empty;
                        connSite.StatusText = "Connected";
                    }
                }

                foreach (var conn in SccmConnections)
                    AppLogger.Info($"[Run]   SCCM {conn.TenantId}: {conn.State} - {conn.StatusText}");
            }
            else
            {
                SccmConnections.Clear();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[Run] Connection check failed: {ex.Message}");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Syncs IntuneConnections to match configured tenants. Adds new, removes stale.
    /// Only includes tenants with a valid GUID key (tenant IDs are always GUIDs).
    /// </summary>
    private void SyncIntuneConnections(IList<IntuneTenantEntry> tenants)
    {
        // Filter to tenants with valid GUID keys only
        var validTenants = tenants
            .Where(t => !string.IsNullOrWhiteSpace(t.Key) && Guid.TryParse(t.Key, out _))
            .ToList();

        AppLogger.Info($"[Run] IntuneTenants: {tenants.Count} total, {validTenants.Count} with valid GUID keys");
        foreach (var t in tenants)
            AppLogger.Info($"[Run]   Key=\"{t.Key}\" Name=\"{t.Name}\" GUID={Guid.TryParse(t.Key, out _)}");

        var tenantKeys = new HashSet<string>(
            validTenants.Select(t => t.Key),
            StringComparer.OrdinalIgnoreCase);

        // Remove connections whose tenant no longer exists
        for (int i = IntuneConnections.Count - 1; i >= 0; i--)
        {
            if (!tenantKeys.Contains(IntuneConnections[i].TenantId))
            {
                IntuneConnections[i].PropertyChanged -= OnConnectionStatusPropertyChanged;
                IntuneConnections.RemoveAt(i);
            }
        }

        // Add connections for new tenants
        var existingKeys = new HashSet<string>(
            IntuneConnections.Select(c => c.TenantId),
            StringComparer.OrdinalIgnoreCase);

        foreach (var tenant in validTenants)
        {
            var displayName = !string.IsNullOrWhiteSpace(tenant.Name)
                ? tenant.Name
                : "Intune";

            if (existingKeys.Contains(tenant.Key))
            {
                // Update display name on existing connections (may have changed)
                var existing = IntuneConnections.FirstOrDefault(c =>
                    string.Equals(c.TenantId, tenant.Key, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                    existing.TargetName = displayName;
                continue;
            }

            var conn = new ConnectionStatus
            {
                TargetName = displayName,
                TenantId = tenant.Key
            };
            conn.PropertyChanged += OnConnectionStatusPropertyChanged;
            IntuneConnections.Add(conn);
            // A (re-)added tenant starts with a clean retry slate — a stale
            // ui-required verdict from before its removal must not gag it.
            _silentGate.Unlock(tenant.Key);
        }
    }

    /// <summary>
    /// Syncs SccmConnections to match configured SCCM sites. Adds new, removes stale.
    /// Marks the locally detected site as Connected, others as Disconnected.
    /// </summary>
    private void SyncSccmConnections(IList<SCCMSiteEntry> sites, string? detectedSiteCode)
    {
        var siteKeys = new HashSet<string>(
            sites.Select(s => s.Key).Where(k => !string.IsNullOrWhiteSpace(k)),
            StringComparer.OrdinalIgnoreCase);

        // Remove connections whose site no longer exists in config
        for (int i = SccmConnections.Count - 1; i >= 0; i--)
        {
            if (!siteKeys.Contains(SccmConnections[i].TenantId))
            {
                SccmConnections[i].PropertyChanged -= OnConnectionStatusPropertyChanged;
                SccmConnections.RemoveAt(i);
            }
        }

        var existingKeys = new HashSet<string>(
            SccmConnections.Select(c => c.TenantId),
            StringComparer.OrdinalIgnoreCase);

        foreach (var site in sites)
        {
            if (string.IsNullOrWhiteSpace(site.Key)) continue;

            var displayName = !string.IsNullOrWhiteSpace(site.Comment)
                ? $"{site.Key} ({site.Comment})"
                : site.Key;

            bool isLocal = !string.IsNullOrEmpty(detectedSiteCode) &&
                string.Equals(site.Key, detectedSiteCode, StringComparison.OrdinalIgnoreCase);

            if (existingKeys.Contains(site.Key))
            {
                var existing = SccmConnections.FirstOrDefault(c =>
                    string.Equals(c.TenantId, site.Key, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    existing.TargetName = displayName;
                    existing.State = isLocal ? ConnectionState.Connected : ConnectionState.Disconnected;
                    existing.StatusText = isLocal ? "Connected" : "Not reachable";
                }
                continue;
            }

            var conn = new ConnectionStatus
            {
                TargetName = displayName,
                TenantId = site.Key,
                State = isLocal ? ConnectionState.Connected : ConnectionState.Disconnected,
                StatusText = isLocal ? "Connected" : "Not reachable",
                IsEnabled = isLocal // Only enable the reachable site by default
            };
            conn.PropertyChanged += OnConnectionStatusPropertyChanged;
            SccmConnections.Add(conn);
        }
    }

    /// <summary>
    /// Reacts to IsEnabled checkbox changes on individual ConnectionStatus items.
    /// Immediately flips the visual state to Skipped (or restores via cached token)
    /// without running a full RefreshConnectionStatusAsync.
    /// </summary>
    private void OnConnectionStatusPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConnectionStatus.IsEnabled)) return;
        if (sender is not ConnectionStatus conn) return;

        if (!conn.IsEnabled)
        {
            conn.State = ConnectionState.Skipped;
            conn.StatusText = "Skipped";
            conn.DetailLine1 = string.Empty;
            conn.DetailLine2 = string.Empty;
            conn.ErrorMessage = string.Empty;
            conn.TokenCountdown = string.Empty;
        }
        else
        {
            // Distinguish between Intune tenants (need tokens) and SCCM sites (need local access)
            bool isSccmSite = SccmConnections.Contains(conn);
            if (isSccmSite)
            {
                // SCCM sites are either locally reachable or not -- no token needed
                conn.State = ConnectionState.Disconnected;
                conn.StatusText = "Not reachable";
            }
            else
            {
                // Re-check this Intune tenant using cached token (no MSAL prompt)
                var token = GetCachedTenantToken(conn.TenantId);
                if (token is not null)
                {
                    ConnectionChecker.ApplyTokenStatus(conn, token, graphReachable: true);
                }
                else
                {
                    conn.State = ConnectionState.Disconnected;
                    conn.StatusText = "Not authenticated";
                }
            }
        }
    }

    /// <summary>
    /// Returns a cached token for a tenant if it's still valid, or null.
    /// </summary>
    private MsalTokenResult? GetCachedTenantToken(string tenantId)
    {
        if (_tenantTokens.TryGetValue(tenantId, out var cached) &&
            cached.ExpiresOnUtc > SystemClock.UtcNow)
            return cached;
        return null;
    }

    /// <summary>
    /// Initializes the auth service with the first configured tenant if not already done.
    /// Required before TryAcquireTokenSilentForTenantAsync can access the cache.
    /// </summary>
    private async Task EnsureAuthServiceInitializedAsync(IList<IntuneTenantEntry> tenants)
    {
        if (_authService.IsInitialized) return;
        if (tenants.Count == 0) return;

        var first = tenants[0];
        if (string.IsNullOrWhiteSpace(first.Key)) return;

        try
        {
            // Background init -- IntPtr.Zero because no interactive prompt
            // should appear; this is a status-check warm-up only.
            await _authService.InitializeForTenantAsync(first, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[Run] Auth service init failed: {ex.Message}");
        }
    }

    private void StartTokenTimer()
    {
        _tokenTimer?.Stop();
        _tokenTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _tokenTimer.Tick += (_, _) =>
        {
            // Countdown text only — pure local work, dispatcher-safe. The
            // silent MSAL re-checks that used to live in this tick were the
            // app's top "Not Responding" source (30s worst-case dispatcher
            // stalls, 76 ticks parked in-flight over three days of logs) and
            // are gone: silent attempts are event-driven now (SilentAuthGate).
            //
            // Snapshot before iterating: UpdateTokenCountdown can flip a card
            // to Disconnected, and downstream handlers may touch the list.
            foreach (var conn in IntuneConnections.ToList())
            {
                var wasConnected = conn.State == ConnectionState.Connected;
                ConnectionChecker.UpdateTokenCountdown(conn);
                if (wasConnected && conn.State != ConnectionState.Connected && conn.IsEnabled
                    && !IsRunning) // never re-auth midway through an active run
                {
                    // Access token just expired. One gated silent attempt —
                    // the refresh token usually renews it without the user
                    // ever seeing the card flicker.
                    _silentGate.Unlock(conn.TenantId);
                    _ = TrySilentPromoteAsync(conn);
                }
            }
        };
        _tokenTimer.Start();
    }

    /// <summary>
    /// One gated, bounded silent attempt for a disconnected connection.
    /// Success propagates through the service's TokenAcquired event (which
    /// promotes the card — see OnTokenAcquired); this method only records the
    /// outcome and settles the card's failure text. Exception-free: callers
    /// fire-and-forget it from timer ticks and system-event handlers.
    /// </summary>
    private async Task TrySilentPromoteAsync(ConnectionStatus conn)
    {
        try
        {
            if (!_silentGate.ShouldAttempt(conn.TenantId, SystemClock.UtcNow)) return;

            var attempt = await _authService.TryAcquireTokenSilentForTenantDetailedAsync(conn.TenantId);
            _silentGate.Record(conn.TenantId, attempt.Outcome, SystemClock.UtcNow);

            // The connection may have been discarded while we awaited (bundle
            // closed/replaced). Don't resurrect stale state.
            if (attempt.Token is null
                && IntuneConnections.Contains(conn)
                && conn.State == ConnectionState.Disconnected)
            {
                conn.StatusText  = "Not authenticated";
                conn.DetailLine1 = "Sign in to connect";
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[Run] Silent promote for {conn.TargetName} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Silent auth becomes plausible again when connectivity returns or the
    /// machine wakes: unlock every tenant and run one full refresh. Both
    /// events arrive on threadpool threads — marshal before touching UI state.
    /// </summary>
    private void OnNetworkAvailabilityChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable) return;
        _silentGate.UnlockAll();
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => _ = SafeRefreshAsync());
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        _silentGate.UnlockAll();
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => _ = SafeRefreshAsync());
    }

    /// <summary>
    /// Triggers interactive sign-in for a specific disconnected tenant.
    /// Called from the RunView when a user clicks a disconnected connection icon.
    /// </summary>
    public async Task SignInTenantAsync(string tenantId)
    {
        var tenantEntry = _appInfoVm.FullConfig.IntuneTenants
            .FirstOrDefault(t => string.Equals(t.Key, tenantId, StringComparison.OrdinalIgnoreCase));
        if (tenantEntry is null) return;

        var conn = IntuneConnections.FirstOrDefault(c =>
            string.Equals(c.TenantId, tenantId, StringComparison.OrdinalIgnoreCase));
        if (conn is null) return;

        try
        {
            conn.State = ConnectionState.Checking;
            conn.StatusText = "Signing in...";

            await _authService.InitializeForTenantAsync(tenantEntry, WindowHelper.GetMainWindowHwnd());

            var token = await _authService.AcquireTokenAsync();
            _tenantTokens[tenantEntry.Key] = token;
            ConnectionChecker.ApplyTokenStatus(conn, token, graphReachable: true);
            StartTokenTimer();

            AppLogger.Info($"[Run] Manual sign-in for {conn.TargetName}: {token.UserPrincipalName}");
        }
        catch (OperationCanceledException)
        {
            conn.State = ConnectionState.Disconnected;
            conn.StatusText = "Sign-in cancelled";
        }
        catch (Exception ex)
        {
            conn.State = ConnectionState.Error;
            conn.StatusText = $"Sign-in failed: {ex.Message}";
            AppLogger.Warn($"[Run] Manual sign-in failed for {conn.TargetName}: {ex.Message}");
        }
    }
}
