using System.IO;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Wrapp.Services;

/// <summary>
/// Dedicated MSAL authentication service for Azure DevOps.
/// Completely isolated from the Intune/Graph auth -- separate PCA, separate token cache.
/// Uses system browser (not WAM broker) to avoid first-party app provisioning issues.
/// </summary>
public class DevOpsAuthService
{
    // Azure DevOps first-party app ID that supports public client flows
    // Using the Visual Studio client ID which is pre-authorized for DevOps
    private const string ClientId = "872cd9fa-d31f-45e0-9eab-6e460a02d1f1"; // Visual Studio

    // Azure DevOps delegated scope
    private static readonly string[] Scopes = { "499b84ac-1321-427f-aa17-267ca6975798/.default" };

    private static readonly string CacheDir = PlatformConfig.WrappRoot;

    private IPublicClientApplication? _app;
    private MsalCacheHelper? _cacheHelper;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>Last acquired token result (in-memory).</summary>
    public Models.MsalTokenResult? CachedToken { get; private set; }

    // No-op for the browser-based DevOps flow; kept for API compat (called from App.xaml.cs).
    public void SetParentWindowFunc(Func<IntPtr> func) { }

    /// <summary>Acquires a DevOps token. Tries silent first, falls back to system browser.</summary>
    public async Task<Models.MsalTokenResult?> AcquireTokenAsync()
    {
        var app = await EnsureAppAsync();
        var accounts = (await app.GetAccountsAsync()).ToList();
        var account = accounts.FirstOrDefault();

        AuthenticationResult result;
        try
        {
            if (account is not null)
            {
                result = await app.AcquireTokenSilent(Scopes, account).ExecuteAsync();
            }
            else
            {
                throw new MsalUiRequiredException("no_account", "No cached DevOps account.");
            }
        }
        catch (MsalUiRequiredException)
        {
            // Use system browser -- avoids WAM/broker app provisioning issues
            result = await app.AcquireTokenInteractive(Scopes)
                .WithPrompt(Prompt.SelectAccount)
                .WithUseEmbeddedWebView(false)
                .ExecuteAsync();
        }

        CachedToken = ToResult(result);
        AppLogger.Info($"DevOpsAuth: authenticated as {result.Account?.Username ?? "unknown"}");
        return CachedToken;
    }

    /// <summary>Tries to acquire silently from cache. Returns null if interaction needed.</summary>
    public async Task<Models.MsalTokenResult?> TryAcquireTokenSilentAsync()
    {
        try
        {
            var app = await EnsureAppAsync();
            var accounts = (await app.GetAccountsAsync()).ToList();
            var account = accounts.FirstOrDefault();
            if (account is null) return null;

            var result = await app.AcquireTokenSilent(Scopes, account).ExecuteAsync();
            CachedToken = ToResult(result);
            return CachedToken;
        }
        catch
        {
            return null;
        }
    }

    public async Task SignOutAsync()
    {
        if (_app is null) return;
        foreach (var account in await _app.GetAccountsAsync())
            await _app.RemoveAsync(account);
        CachedToken = null;
    }

    private async Task<IPublicClientApplication> EnsureAppAsync()
    {
        if (_app is not null) return _app;

        await _initLock.WaitAsync();
        try
        {
            if (_app is not null) return _app;

            // No WAM broker -- use system browser for interactive auth
            // This avoids the "app not found in directory" errors that occur
            // when the client ID isn't provisioned in the user's tenant via WAM
            _app = PublicClientApplicationBuilder.Create(ClientId)
                .WithAuthority("https://login.microsoftonline.com/common")
                .WithDefaultRedirectUri()
                .Build();

            // Separate token cache file from the Intune auth cache.
            //
            // Encryption-at-rest divergence (intentional, audited 2026-05):
            // The Intune token cache (`MsalAuthService.Cache.cs`'s
            // BeforeAccess/AfterAccess callbacks) uses Wrapp's hand-rolled
            // DPAPI v2 envelope -- 16-byte per-app entropy + version byte for
            // backward compat. The DevOps cache here uses the Microsoft-
            // recommended `MsalCacheHelper`, which on Windows wraps the cache
            // with platform DPAPI (CurrentUser scope, no entropy).
            //
            // The asymmetry is acceptable because:
            //   1. DevOps tokens have narrower scope (just the configured
            //      DevOps repo) than Intune Graph tokens (tenant-wide).
            //   2. The v2 entropy constant is recoverable from the Wrapp
            //      binary, so it only defends against opportunistic same-
            //      user DPAPI-blob harvesting -- not a targeted attacker.
            //      MsalCacheHelper's plain DPAPI has the same threat-model
            //      gap by design.
            //   3. MsalCacheHelper is the Microsoft-supported path; using it
            //      keeps us in the upgrade lane for future MSAL improvements.
            if (_cacheHelper is null)
            {
                Directory.CreateDirectory(CacheDir);
                var storageProps = new StorageCreationPropertiesBuilder(
                        "msal_devops_cache.bin", CacheDir)
                    .Build();
                _cacheHelper = await MsalCacheHelper.CreateAsync(storageProps);
            }
            _cacheHelper.RegisterCache(_app.UserTokenCache);

            return _app;
        }
        finally { _initLock.Release(); }
    }

    private static Models.MsalTokenResult ToResult(AuthenticationResult r) => new(
        AccessToken: r.AccessToken,
        ExpiresOnUtc: r.ExpiresOn.UtcDateTime,
        Scopes: r.Scopes.ToArray(),
        TenantId: r.TenantId ?? "",
        ClientId: ClientId,
        UserPrincipalName: r.Account?.Username
    );
}
