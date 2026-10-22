using System.Net.Http;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Probes Intune and SCCM connectivity. Used by RunViewModel to populate
/// the connection status panel. All methods are async and non-blocking.
/// </summary>
public sealed class ConnectionChecker
{
    private readonly PowerShellService _ps;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public ConnectionChecker(PowerShellService ps)
    {
        _ps = ps;
    }

    /// <summary>
    /// Checks whether the Graph API endpoint is reachable (unauthenticated).
    /// Call once and share the result across multiple tenant checks.
    /// </summary>
    public async Task<bool> CheckGraphReachableAsync()
    {
        try
        {
            var response = await _http.GetAsync("https://graph.microsoft.com/v1.0/$metadata");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Applies token validity status to a ConnectionStatus, assuming Graph
    /// reachability has already been checked. Use after CheckGraphReachableAsync.
    /// </summary>
    public static void ApplyTokenStatus(ConnectionStatus status, MsalTokenResult? token, bool graphReachable)
    {
        status.ErrorMessage = string.Empty;

        if (!graphReachable)
        {
            status.State = ConnectionState.Disconnected;
            status.StatusText = "Graph API unreachable";
            status.DetailLine1 = string.Empty;
            return;
        }

        if (token is null || string.IsNullOrEmpty(token.AccessToken))
        {
            status.State = ConnectionState.Disconnected;
            status.StatusText = "Not authenticated";
            status.DetailLine1 = "Sign in to connect";
            status.DetailLine2 = string.Empty;
            return;
        }

        if (token.ExpiresOnUtc < SystemClock.UtcNow)
        {
            status.State = ConnectionState.Disconnected;
            status.StatusText = "Token expired";
            status.DetailLine2 = $"Expired: {token.ExpiresOnUtc:HH:mm:ss} UTC";
            return;
        }

        // A token proves a connection only to the tenant it was issued for.
        // The card is per tenant; a token for another tenant (the account's
        // home tenant answering an unscoped request) is not this tenant's.
        if (MsalAuthService.IsTenantMismatch(status.TenantId, token.TenantId))
        {
            status.State = ConnectionState.Disconnected;
            status.StatusText = "Not authenticated";
            status.DetailLine1 = "Signed in to another tenant";
            status.DetailLine2 = token.UserPrincipalName ?? string.Empty;
            return;
        }

        status.State = ConnectionState.Connected;
        status.StatusText = "Connected";
        status.DetailLine1 = token.UserPrincipalName ?? "App credentials";
        status.TokenExpiresUtc = token.ExpiresOnUtc;
        UpdateTokenCountdown(status);
    }

    /// <summary>
    /// Checks Intune connectivity: (1) Graph API reachable, (2) token valid.
    /// Convenience method that combines CheckGraphReachableAsync + ApplyTokenStatus.
    /// </summary>
    public async Task CheckIntuneAsync(ConnectionStatus status, MsalTokenResult? token)
    {
        status.State = ConnectionState.Checking;
        status.StatusText = "Checking...";

        bool graphReachable = await CheckGraphReachableAsync();
        ApplyTokenStatus(status, token, graphReachable);
    }

    /// <summary>
    /// Applies one site's probe to its card, assuming the console check has
    /// already run. The SCCM twin of <see cref="ApplyTokenStatus"/>: the
    /// console being installed is the shared precondition (Graph reachable),
    /// the site probe is the per-card evidence (the token), and a card says
    /// "Connected" only when its own site answered.
    /// </summary>
    public static void ApplySccmSiteStatus(ConnectionStatus status, SccmSiteResult? probe, SccmConnectivityResult console)
    {
        status.ErrorMessage = string.Empty;
        status.TokenExpiresUtc = null;
        status.TokenCountdown = string.Empty;

        if (!console.Available)
        {
            status.State = ConnectionState.Disconnected;
            status.StatusText = "Console not found";
            status.DetailLine1 = string.Empty;
            status.DetailLine2 = string.Empty;
            status.ErrorMessage = console.Reason ?? "ConfigurationManager module not found";
            return;
        }

        if (probe is null || !probe.Connected)
        {
            status.State = ConnectionState.Disconnected;
            status.StatusText = "Not reachable";
            status.DetailLine1 = string.Empty;
            status.DetailLine2 = string.Empty;
            status.ErrorMessage = probe?.Reason ?? "Not checked";
            return;
        }

        status.State = ConnectionState.Connected;
        status.StatusText = "Connected";
        status.DetailLine1 = probe.ServerName ?? string.Empty;
        status.DetailLine2 = probe.SiteName ?? string.Empty;
    }

    /// <summary>
    /// Checks SCCM connectivity for the site on the card: (1) console
    /// installed, (2) that site answers through its drive. Convenience
    /// method that combines the two probes with <see cref="ApplySccmSiteStatus"/>,
    /// the way <see cref="CheckIntuneAsync"/> does for a tenant.
    /// </summary>
    public async Task CheckSccmAsync(ConnectionStatus status)
    {
        status.State = ConnectionState.Checking;
        status.StatusText = "Checking...";
        status.ErrorMessage = string.Empty;

        var console = await _ps.TestSccmConnectivityAsync();
        var probe = console.Available && !string.IsNullOrWhiteSpace(status.TenantId)
            ? await _ps.TestSccmSiteAsync(status.TenantId)
            : null;
        ApplySccmSiteStatus(status, probe, console);
    }

    /// <summary>
    /// Updates the token countdown string on a ConnectionStatus object.
    /// </summary>
    public static void UpdateTokenCountdown(ConnectionStatus status)
    {
        if (status.TokenExpiresUtc is null) return;

        var remaining = status.TokenExpiresUtc.Value - SystemClock.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            status.TokenCountdown = "Expired";
            status.State = ConnectionState.Disconnected;
            status.StatusText = "Token expired";
        }
        else if (remaining.TotalHours >= 1)
        {
            status.TokenCountdown = $"{(int)remaining.TotalHours}h {remaining.Minutes}m remaining";
        }
        else
        {
            status.TokenCountdown = $"{remaining.Minutes}m {remaining.Seconds}s remaining";
        }
    }
}
