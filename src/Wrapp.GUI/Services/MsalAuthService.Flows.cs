using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Wrapp.Models;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace Wrapp.Services;

public sealed partial class MsalAuthService
{
    // -----------------------------------------------------------------------
    // Per-AuthFlow token acquisition. Each method dispatches to the MSAL
    // client-app variant (public / confidential / browser fallback) that
    // matches the configured AuthFlow.
    // -----------------------------------------------------------------------

    private async Task<MsalTokenResult> AcquireInteractiveAsync(bool forceRefresh = false)
    {
        var app = await EnsurePublicClientAsync();
        var accounts = (await app.GetAccountsAsync()).ToList();

        // Match cached account to the current tenant so we refresh the
        // correct identity, not just the first account in the cache.
        var existingAccount = accounts.FirstOrDefault(a =>
            !string.IsNullOrEmpty(_tenantId) &&
            string.Equals(a.HomeAccountId?.TenantId, _tenantId,
                StringComparison.OrdinalIgnoreCase))
            ?? accounts.FirstOrDefault();

        AuthenticationResult result;
        try
        {
            // Try silent first (cached token or refresh)
            if (existingAccount is not null)
            {
                StatusChanged?.Invoke(forceRefresh
                    ? "Forcing token refresh..."
                    : "Acquiring token silently...");
                result = await app.AcquireTokenSilent(DelegatedScopes, existingAccount)
                    .WithForceRefresh(forceRefresh)
                    .ExecuteAsync();
            }
            else
            {
                throw new MsalUiRequiredException("no_account", "No cached account found.");
            }
        }
        catch (MsalUiRequiredException)
        {
            // Interactive login via WAM broker (native Windows auth dialog).
            // Pass null loginHint so the account picker appears without pre-filling.
            StatusChanged?.Invoke("Waiting for sign-in...");
            result = await ExecuteInteractiveAsync(loginHintAccount: null, Prompt.SelectAccount);
        }

        StatusChanged?.Invoke($"Authenticated as {result.Account?.Username ?? "unknown"}");
        return ToResult(result);
    }

    /// <summary>
    /// Forces an interactive login with the account picker (Prompt.SelectAccount).
    /// Used by "Sign in with a different account" -- always shows the login UI,
    /// never attempts silent acquisition first.
    /// </summary>
    public async Task<MsalTokenResult> AcquireInteractiveForceAsync()
    {
        await EnsurePublicClientAsync();
        StatusChanged?.Invoke("Waiting for sign-in...");

        var result = await ExecuteInteractiveAsync(loginHintAccount: null, Prompt.SelectAccount);

        StatusChanged?.Invoke($"Authenticated as {result.Account?.Username ?? "unknown"}");
        return ToResult(result);
    }

    // -------------------------------------------------------------------
    // Device Code flow
    // -------------------------------------------------------------------

    private async Task<MsalTokenResult> AcquireDeviceCodeAsync(bool forceRefresh = false)
    {
        var app = await EnsurePublicClientAsync();
        var accounts = (await app.GetAccountsAsync()).ToList();

        AuthenticationResult result;
        try
        {
            // Match cached account to the current tenant
            var existing = accounts.FirstOrDefault(a =>
                !string.IsNullOrEmpty(_tenantId) &&
                string.Equals(a.HomeAccountId?.TenantId, _tenantId,
                    StringComparison.OrdinalIgnoreCase))
                ?? accounts.FirstOrDefault();
            if (existing is not null)
            {
                StatusChanged?.Invoke(forceRefresh
                    ? "Forcing token refresh..."
                    : "Acquiring token silently...");
                result = await app.AcquireTokenSilent(DelegatedScopes, existing)
                    .WithForceRefresh(forceRefresh)
                    .ExecuteAsync();
                StatusChanged?.Invoke($"Authenticated as {result.Account?.Username ?? "unknown"}");
                return ToResult(result);
            }
        }
        catch (MsalUiRequiredException) { /* expected, fall through */ }

        StatusChanged?.Invoke("Starting device code flow...");
        result = await app.AcquireTokenWithDeviceCode(DelegatedScopes, deviceCodeResult =>
        {
            if (DeviceCodeReceived is not null)
                return DeviceCodeReceived.Invoke(deviceCodeResult);

            // Fallback: log the message
            AppLogger.Info($"Device code: {deviceCodeResult.Message}");
            return Task.CompletedTask;
        }).ExecuteAsync();

        StatusChanged?.Invoke($"Authenticated as {result.Account?.Username ?? "unknown"}");
        return ToResult(result);
    }

    // -------------------------------------------------------------------
    // Client Secret flow (app-only / confidential)
    // -------------------------------------------------------------------

    private async Task<MsalTokenResult> AcquireClientSecretAsync()
    {
        if (_clientSecret is null || _clientSecret.Length == 0)
            throw new InvalidOperationException(
                "ClientSecret is required for the ClientSecret auth flow.");

        if (_clientId == WellKnownClientId)
            throw new InvalidOperationException(
                "ClientSecret flow requires a custom Client ID (app registration). "
                + "The well-known client ID cannot be used for app-only flows.");

        EnsureConfidentialClient();
        StatusChanged?.Invoke("Acquiring app token (client secret)...");

        // AcquireTokenForClient manages its own application token cache
        var result = await _confidentialApp!.AcquireTokenForClient(ApplicationScopes)
            .ExecuteAsync();

        StatusChanged?.Invoke("App authenticated (client credentials)");
        return ToResult(result);
    }

    // -------------------------------------------------------------------
    // Client Certificate flow (app-only / confidential)
    // -------------------------------------------------------------------

    private async Task<MsalTokenResult> AcquireClientCertAsync()
    {
        if (string.IsNullOrWhiteSpace(_certThumbprint))
            throw new InvalidOperationException(
                "CertThumbprint is required for the ClientCert auth flow.");

        if (_clientId == WellKnownClientId)
            throw new InvalidOperationException(
                "ClientCert flow requires a custom Client ID (app registration). "
                + "The well-known client ID cannot be used for app-only flows.");

        EnsureConfidentialClient();
        StatusChanged?.Invoke("Acquiring app token (certificate)...");

        var result = await _confidentialApp!.AcquireTokenForClient(ApplicationScopes)
            .ExecuteAsync();

        StatusChanged?.Invoke("App authenticated (certificate)");
        return ToResult(result);
    }
}
