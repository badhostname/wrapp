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
    /// Checks SCCM connectivity by verifying ConfigurationManager module availability.
    /// </summary>
    public async Task CheckSccmAsync(ConnectionStatus status)
    {
        status.State = ConnectionState.Checking;
        status.StatusText = "Checking...";
        status.ErrorMessage = string.Empty;

        try
        {
            var result = await _ps.TestSccmConnectivityAsync();

            if (result.Available)
            {
                status.State = ConnectionState.Connected;
                status.StatusText = "Connected";
                status.TargetName = result.SiteCode ?? "SCCM";
                status.DetailLine1 = !string.IsNullOrEmpty(result.SiteCode)
                    ? $"Site: {result.SiteCode}" : string.Empty;
                status.DetailLine2 = result.ServerName ?? string.Empty;
            }
            else
            {
                status.State = ConnectionState.Disconnected;
                status.StatusText = "Not available";
                status.ErrorMessage = result.Reason ?? "ConfigurationManager module not found";
            }
        }
        catch (Exception ex)
        {
            status.State = ConnectionState.Error;
            status.StatusText = "Error";
            status.ErrorMessage = ex.Message;
        }
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
