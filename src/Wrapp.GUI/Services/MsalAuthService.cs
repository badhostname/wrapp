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

/// <summary>
/// Singleton MSAL.NET authentication service supporting all 4 auth flows:
/// Interactive (WAM broker with system browser fallback), DeviceCode,
/// ClientSecret, and ClientCert.
/// Manages token caching (DPAPI-encrypted) and silent refresh.
/// </summary>
public sealed partial class MsalAuthService : IDisposable
{
    // Microsoft Graph PowerShell well-known client ID (works without app registration)
    private const string WellKnownClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";

    // MSAL cache file path resolved via PlatformConfig (env var > appsettings.json > default).
    // CacheDir is the parent directory (created on first write).

    /// <summary>Delegated permission scopes requested for Graph API access.</summary>
    public static readonly string[] DelegatedScopes =
    {
        "DeviceManagementApps.ReadWrite.All",
        "DeviceManagementConfiguration.ReadWrite.All",
        "DeviceManagementRBAC.ReadWrite.All",
        // Group.READ.All, not ReadWrite. Wrapp only reads
        // groups (resolve GroupID by display name, member counts, nested
        // membership); app-to-group assignment goes through the Intune
        // DeviceManagementApps scope, not a directory group write. This matches
        // the vendored IntuneWin32App module's own default scope set and drops
        // full directory group read/write from the consent.
        "Group.Read.All"
        // Note: offline_access is NOT listed here -- MSAL adds it automatically.
        // Including it causes a "Disallowed scope detected" warning.
    };

    private static readonly string[] ApplicationScopes =
    {
        "https://graph.microsoft.com/.default"
    };

    // Prevents overlapping interactive WAM requests (WAM crashes if two run concurrently)
    private readonly SemaphoreSlim _interactiveLock = new(1, 1);

    // Serializes MSAL client initialization to prevent race conditions
    private readonly SemaphoreSlim _initLock = new(1, 1);

    // Current configuration
    private string _tenantId = string.Empty;
    private string _clientId = string.Empty;
    private AuthFlow _authFlow = AuthFlow.Interactive;

    /// <summary>
    /// ClientSecret stored as <see cref="SecureString"/> so the plaintext only
    /// exists in CLR-managed memory for the duration of the narrow
    /// <see cref="SecretProtection.WithPlaintext{T}"/> unwrap at the
    /// <see cref="ConfidentialClientApplicationBuilder.WithClientSecret(string)"/>
    /// call. MSAL takes its own internal copy that we can't zero, but the
    /// app's own storage never holds a long-lived plaintext string.
    /// </summary>
    private SecureString? _clientSecret;
    private string? _certThumbprint;
    private IntPtr _parentWindow;
    private Func<IntPtr>? _parentWindowFunc;
    private bool _initialized;

    // MSAL client instances (recreated when tenant/flow changes)
    private IPublicClientApplication? _publicApp;
    private IPublicClientApplication? _browserFallbackApp;
    private IConfidentialClientApplication? _confidentialApp;

    // DPAPI-encrypted file cache (same pattern as IntuneManagement TokenCacheHelperEx).
    // Raw BeforeAccess/AfterAccess hooks ensure the full token cache -- including
    // refresh tokens from WAM broker -- is persisted to disk and recovered on restart.
    private static readonly string CacheFilePath = PlatformConfig.MsalCachePath;
    private static readonly object CacheLock = new();

    /// <summary>
    /// Fired when the auth status changes (e.g. "Authenticating...", "Token acquired").
    /// </summary>
    public event Action<string>? StatusChanged;

    /// <summary>
    /// Fired after every successful token acquisition (interactive, silent, refresh).
    /// Subscribers receive the fresh token result and can update their state.
    /// </summary>
    public event Action<MsalTokenResult>? TokenAcquired;

    /// <summary>
    /// Fired when device code flow needs the user to visit a URL and enter a code.
    /// The callback receives the DeviceCodeResult with UserCode and VerificationUrl.
    /// </summary>
    public event Func<DeviceCodeResult, Task>? DeviceCodeReceived;

    /// <summary>
    /// Sets a callback that resolves the parent window handle on demand.
    /// WAM uses this to parent its auth dialog. Call once at startup before
    /// any token acquisition. The callback is invoked each time WAM needs
    /// the handle, so it always gets the current foreground window.
    /// </summary>
    public void SetParentWindowFunc(Func<IntPtr> getParentWindow)
    {
        _parentWindowFunc = getParentWindow;
        // Force the public client to be rebuilt on next use so the new
        // window handle resolver is baked into the PCA builder.
        _publicApp = null;
    }

    /// <summary>
    /// Configures the service for a specific tenant and auth flow.
    /// Call this before AcquireTokenAsync when the tenant selection changes.
    /// When tenantId is empty, uses the /organizations authority (multi-tenant)
    /// and discovers the actual tenant ID from the auth result.
    /// </summary>
    public async Task InitializeAsync(
        string tenantId,
        string clientId,
        AuthFlow authFlow,
        IntPtr parentWindow,
        SecureString? clientSecret = null,
        string? certThumbprint = null)
    {
        // Only rebuild if parameters that affect PCA construction changed.
        // tenantId is NOT included because the PCA uses AzureAdMultipleOrgs
        // (organizations authority) -- tenant switching just changes which
        // cached account is matched, not the PCA itself. Rebuilding the PCA
        // on tenant change destroys the WAM broker association and loses
        // cached tokens.
        var effectiveClientId = string.IsNullOrWhiteSpace(clientId) ? WellKnownClientId : clientId;
        bool secretChanged = !SecureStringsEqual(clientSecret, _clientSecret);
        bool changed = effectiveClientId != _clientId
            || authFlow != _authFlow
            || secretChanged
            || certThumbprint != _certThumbprint;

        _tenantId = tenantId;
        _clientId = effectiveClientId;
        _authFlow = authFlow;
        // Replace + dispose pattern: dispose the old SecureString so its
        // encrypted buffer is released immediately, then take ownership of the
        // caller's SecureString. Callers must not dispose the SecureString
        // they pass in after this point.
        if (secretChanged)
        {
            _clientSecret?.Dispose();
            _clientSecret = clientSecret;
        }
        _certThumbprint = certThumbprint;
        _parentWindow = parentWindow;
        _initialized = true;

        if (changed)
        {
            _publicApp = null;
            _browserFallbackApp = null;
            _confidentialApp = null;
        }

        // Pre-build the appropriate client
        if (_authFlow is AuthFlow.Interactive or AuthFlow.DeviceCode)
            await EnsurePublicClientAsync();
        else
            EnsureConfidentialClient();
    }

    /// <summary>
    /// Convenience overload of <see cref="InitializeAsync"/> that pulls every
    /// parameter from an <see cref="IntuneTenantEntry"/>. Replaces the 7-line
    /// boilerplate that recurred across <c>AccountViewModel</c>,
    /// <c>RunViewModel</c>, <c>TenantsViewModel</c>, and friends:
    /// <code>
    /// await _authService.InitializeAsync(
    ///     tenantId: tenant?.Key ?? string.Empty,
    ///     clientId: tenant?.ClientID ?? string.Empty,
    ///     authFlow: tenant?.AuthFlow ?? AuthFlow.Interactive,
    ///     parentWindow: hwnd,
    ///     clientSecret: SecretProtection.ResolveTenantSecret(
    ///         tenant?.ClientSecretCipher, tenant?.ClientSecret), // SecureString
    ///     certThumbprint: tenant?.CertThumbprint);
    /// </code>
    /// becomes a single <c>await _authService.InitializeForTenantAsync(tenant, hwnd)</c>.
    /// </summary>
    /// <param name="tenant">
    /// Tenant configuration. <c>null</c> falls back to the
    /// <see cref="AuthFlow.Interactive"/> flow with empty tenant/client IDs
    /// (the /organizations multi-tenant path).
    /// </param>
    /// <param name="parentWindow">
    /// Win32 window handle for WAM dialog parenting. Pass
    /// <see cref="IntPtr.Zero"/> for headless / background contexts where no
    /// interactive prompt is expected.
    /// </param>
    /// <param name="authFlowOverride">
    /// Optional override for the auth flow. Defaults to the tenant&#x2019;s
    /// configured <see cref="IntuneTenantEntry.AuthFlow"/> (or
    /// <see cref="AuthFlow.Interactive"/> when no tenant is supplied). Use
    /// when the caller specifically wants <see cref="AuthFlow.Interactive"/>
    /// regardless of the tenant&#x2019;s saved preference (e.g. the &#x201C;sign in
    /// with a different account&#x201D; flow that always shows the WAM picker).
    /// </param>
    public Task InitializeForTenantAsync(
        IntuneTenantEntry? tenant,
        IntPtr parentWindow,
        AuthFlow? authFlowOverride = null)
        => InitializeAsync(
            tenantId:       tenant?.Key ?? string.Empty,
            clientId:       tenant?.ClientID ?? string.Empty,
            authFlow:       authFlowOverride ?? tenant?.AuthFlow ?? AuthFlow.Interactive,
            parentWindow:   parentWindow,
            clientSecret:   SecretProtection.ResolveTenantSecret(
                                tenant?.ClientSecretCipher, tenant?.ClientSecret),
            certThumbprint: tenant?.CertThumbprint);

    /// <summary>
    /// Acquires a token. Attempts silent (cached) first, then falls back
    /// to the active auth flow. For client credentials, MSAL manages its own
    /// application token cache internally.
    /// </summary>
    /// <param name="forceRefresh">
    /// When true, bypasses the in-memory token cache and forces MSAL to use
    /// the refresh token to get a new access token from AAD. Use this for the
    /// explicit "Refresh" button so users get updated claims/roles.
    /// </param>
    public async Task<MsalTokenResult> AcquireTokenAsync(bool forceRefresh = false)
    {
        return _authFlow switch
        {
            AuthFlow.Interactive  => await AcquireInteractiveAsync(forceRefresh),
            AuthFlow.DeviceCode   => await AcquireDeviceCodeAsync(forceRefresh),
            AuthFlow.ClientSecret => await AcquireClientSecretAsync(),
            AuthFlow.ClientCert   => await AcquireClientCertAsync(),
            _ => throw new InvalidOperationException($"Unknown auth flow: {_authFlow}")
        };
    }

    /// <summary>
    /// Signs out by clearing all cached accounts for the current public client.
    /// </summary>
    public async Task SignOutAsync()
    {
        if (_publicApp is null) return;
        var accounts = (await _publicApp.GetAccountsAsync()).ToList();
        foreach (var account in accounts)
            await _publicApp.RemoveAsync(account);

        // Also clear fallback app if it was used
        if (_browserFallbackApp is not null)
        {
            var fallbackAccounts = (await _browserFallbackApp.GetAccountsAsync()).ToList();
            foreach (var account in fallbackAccounts)
                await _browserFallbackApp.RemoveAsync(account);
        }

        StatusChanged?.Invoke("Signed out");
    }

    /// <summary>
    /// Removes a single account from the MSAL token cache without affecting
    /// other cached accounts. Used by the "Forget" button in the account flyout.
    /// </summary>
    public async Task ForgetAccountAsync(IAccount account)
    {
        if (_publicApp is not null)
            await _publicApp.RemoveAsync(account);

        if (_browserFallbackApp is not null)
        {
            var fallback = (await _browserFallbackApp.GetAccountsAsync())
                .FirstOrDefault(a => a.HomeAccountId?.Identifier == account.HomeAccountId?.Identifier);
            if (fallback is not null)
                await _browserFallbackApp.RemoveAsync(fallback);
        }
    }

    /// <summary>
    /// Attempts to acquire a token silently from the MSAL cache without any
    /// user interaction. Returns null if no cached account exists or the silent
    /// call fails (e.g. refresh token expired). Never shows a login prompt.
    /// </summary>
    public async Task<MsalTokenResult?> TryAcquireTokenSilentAsync()
    {
        using var op = OperationScope.Begin("MsalAuth.TryAcquireTokenSilent");
        // Same worker-thread + timeout treatment as the per-tenant variant:
        // the cache callbacks' synchronous stretches must never run on the
        // dispatcher, and no silent call may park indefinitely.
        using var timeout = new CancellationTokenSource(SilentCallTimeout);
        try
        {
            var result = await Task.Run(async () =>
            {
                var app = await EnsurePublicClientAsync();
                var accounts = (await app.GetAccountsAsync()).ToList();
                AppLogger.Info($"[MSAL] Silent: {accounts.Count} account(s) in cache, looking for tenant={_tenantId}");

                // Prefer the account matching the currently configured tenant
                var existing = accounts.FirstOrDefault(a =>
                    !string.IsNullOrEmpty(_tenantId) &&
                    string.Equals(a.HomeAccountId?.TenantId, _tenantId,
                        StringComparison.OrdinalIgnoreCase))
                    ?? accounts.FirstOrDefault();
                if (existing is null) return (AuthenticationResult?)null;
                AppLogger.Info($"[MSAL] Silent: using account {existing.Username} (tenant={existing.HomeAccountId?.TenantId})");

                return await app.AcquireTokenSilent(DelegatedScopes, existing)
                    .ExecuteAsync(timeout.Token);
            }, timeout.Token);

            if (result is null)
            {
                op.Complete("no cached account");
                return null;
            }
            op.Complete($"tenant={result.TenantId}");
            return ToResult(result);
        }
        catch (OperationCanceledException)
        {
            op.Complete($"timed out after {SilentCallTimeout.TotalSeconds:0}s");
            return null;
        }
        catch (MsalUiRequiredException ex)
        {
            // Expected when refresh token expired; not an error -- caller falls
            // back to interactive sign-in. Complete the scope (don't Fail it)
            // so the log line is INFO not ERROR.
            op.Complete($"ui-required: classification={ex.Classification}, errorCode={ex.ErrorCode}");
            return null;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            return null;
        }
    }

    /// <summary>
    /// Hard ceiling on any silent acquisition. Field logs (0.6.322) caught a
    /// silent call parked for ~20 hours across a sleep window - MSAL's HTTP
    /// layer has no default timeout, so we impose one. Silent means
    /// non-interactive: nothing legitimate takes longer than this.
    /// </summary>
    private static readonly TimeSpan SilentCallTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Attempts to acquire a token silently for a specific tenant ID by
    /// finding a matching cached account. Never shows UI.
    /// <para>Returns the token plus the <see cref="SilentAttemptOutcome"/>
    /// classification the <see cref="SilentAuthGate"/> uses to schedule (or
    /// permanently skip) retries. "No cached account" classifies as
    /// <see cref="SilentAttemptOutcome.UiRequired"/> - silent cannot succeed
    /// until someone signs in.</para>
    /// <para>The acquisition body runs on a worker thread: its synchronous
    /// stretches (DPAPI cache decrypt + cross-process mutex in the cache
    /// callbacks, broker interop) blocked the dispatcher for up to 30s in the
    /// 0.6.322 field logs. Callers get their continuations back on their own
    /// context as usual.</para>
    /// </summary>
    public async Task<SilentTokenOutcome> TryAcquireTokenSilentForTenantDetailedAsync(
        string tenantId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return new SilentTokenOutcome(null, SilentAttemptOutcome.UiRequired);

        using var op = OperationScope.Begin($"MsalAuth.TryAcquireTokenSilentForTenant");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SilentCallTimeout);
        try
        {
            var result = await Task.Run(async () =>
            {
                var app = await EnsurePublicClientAsync();
                var accounts = (await app.GetAccountsAsync()).ToList();

                var account = accounts.FirstOrDefault(a =>
                    string.Equals(a.HomeAccountId?.TenantId, tenantId,
                        StringComparison.OrdinalIgnoreCase));
                if (account is null) return (AuthenticationResult?)null;

                return await app.AcquireTokenSilent(DelegatedScopes, account)
                    .ExecuteAsync(timeout.Token);
            }, timeout.Token);

            if (result is null)
            {
                op.Complete($"no cached account for tenant={tenantId}");
                return new SilentTokenOutcome(null, SilentAttemptOutcome.UiRequired);
            }

            op.Complete($"tenant={tenantId}");
            return new SilentTokenOutcome(ToResult(result), SilentAttemptOutcome.Success);
        }
        catch (MsalUiRequiredException)
        {
            // Expected; caller falls back to interactive. Not an error.
            op.Complete($"ui-required for tenant={tenantId}");
            return new SilentTokenOutcome(null, SilentAttemptOutcome.UiRequired);
        }
        catch (OperationCanceledException)
        {
            op.Complete($"timed out after {SilentCallTimeout.TotalSeconds:0}s for tenant={tenantId}");
            return new SilentTokenOutcome(null, SilentAttemptOutcome.Transient);
        }
        catch (Exception ex)
        {
            op.Fail(ex, $"tenant={tenantId}");
            return new SilentTokenOutcome(null, SilentAttemptOutcome.Transient);
        }
    }

    /// <summary>Token-only convenience over the detailed variant (existing callers).</summary>
    public async Task<MsalTokenResult?> TryAcquireTokenSilentForTenantAsync(
        string tenantId, CancellationToken cancellationToken = default)
        => (await TryAcquireTokenSilentForTenantDetailedAsync(tenantId, cancellationToken)).Token;

    /// <summary>
    /// Returns all cached MSAL accounts. Used by the account flyout to list
    /// previously signed-in users for account switching.
    /// </summary>
    public async Task<IReadOnlyList<IAccount>> GetCachedAccountsAsync()
    {
        // Build the public client (with token cache) if needed so we can
        // enumerate cached accounts even before full initialization.
        var app = await EnsurePublicClientAsync();
        return (await app.GetAccountsAsync()).ToList();
    }

    /// <summary>
    /// Returns the IPublicClientApplication for injection into PowerShell.
    /// The app object holds the MSAL token cache and can perform AcquireTokenSilent.
    /// </summary>
    public async Task<IPublicClientApplication> GetPublicClientAppAsync()
        => await EnsurePublicClientAsync();

    /// <summary>
    /// Returns the IAccount for the most recently authenticated user in the given tenant.
    /// Used alongside GetPublicClientAppAsync for mid-run token refresh in PowerShell.
    /// </summary>
    public async Task<IAccount?> GetAccountForTenantAsync(string tenantId)
    {
        var app = await EnsurePublicClientAsync();
        var accounts = await app.GetAccountsAsync();
        return accounts.FirstOrDefault(a =>
            string.Equals(a.HomeAccountId?.TenantId, tenantId,
                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Acquires a token silently for a specific cached account (account switching).
    /// Falls back to interactive if the silent call fails.
    /// </summary>
    public async Task<MsalTokenResult> AcquireTokenForAccountAsync(IAccount account)
    {
        var app = await EnsurePublicClientAsync();
        AuthenticationResult result;
        try
        {
            StatusChanged?.Invoke("Switching account...");
            result = await app.AcquireTokenSilent(DelegatedScopes, account)
                .ExecuteAsync();
        }
        catch (MsalUiRequiredException)
        {
            StatusChanged?.Invoke("Re-authentication required...");
            result = await ExecuteInteractiveAsync(account, Prompt.NoPrompt);
        }

        StatusChanged?.Invoke($"Authenticated as {result.Account?.Username ?? "unknown"}");
        return ToResult(result);
    }

    /// <summary>
    /// Resolves the tenant organization display name by calling Graph API.
    /// Returns null if the call fails (non-critical).
    /// </summary>
    private static readonly HttpClient _graphClient = new();

    public static async Task<string?> ResolveOrganizationNameAsync(string accessToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://graph.microsoft.com/v1.0/organization?$select=displayName");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _graphClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var values = doc.RootElement.GetProperty("value");
            if (values.GetArrayLength() > 0)
                return values[0].GetProperty("displayName").GetString();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to resolve organization name: {ex.Message}");
        }
        return null;
    }

    /// <summary>Current tenant ID. May be updated after auth if /organizations was used.</summary>
    public string CurrentTenantId => _tenantId;

    /// <summary>Current auth flow. Exposed for UI display.</summary>
    public AuthFlow CurrentAuthFlow => _authFlow;

    /// <summary>True when the service has been initialized (even without a specific tenant).</summary>
    public bool IsInitialized => _initialized;

    /// <summary>
    /// Validates that a tenant ID is configured (required for confidential client flows).
    /// </summary>
    private string ConfidentialTenantId => string.IsNullOrWhiteSpace(_tenantId)
        ? throw new InvalidOperationException(
            "Tenant ID (Domain) is required for confidential client flows (ClientSecret/ClientCert).")
        : _tenantId;

    // -------------------------------------------------------------------
    // Interactive flow (WAM broker with system browser fallback)
    // -------------------------------------------------------------------

    public void Dispose()
    {
        // MSAL client instances don't implement IDisposable, but we
        // null them out to release references to the token cache.
        _publicApp = null;
        _browserFallbackApp = null;
        _confidentialApp = null;
        _clientSecret?.Dispose();
        _clientSecret = null;
        _initLock.Dispose();
    }
}