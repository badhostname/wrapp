using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using Wrapp.Models;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace Wrapp.Services;

public sealed partial class MsalAuthService
{
    // -----------------------------------------------------------------------
    // Public client construction and DPAPI-protected token cache callbacks.
    // The browser-fallback client is also here -- it sits next to the
    // primary public client because both share the same cache file.
    // -----------------------------------------------------------------------

    private async Task<IPublicClientApplication> EnsurePublicClientAsync()
    {
        if (_publicApp is not null) return _publicApp;

        await _initLock.WaitAsync();
        try
        {
        if (_publicApp is not null) return _publicApp; // double-check after acquiring lock

        // Default to the well-known client ID if called before InitializeAsync
        // (e.g. GetCachedAccountsAsync at startup). Without this, MSAL throws
        // "No ClientId was specified".
        if (string.IsNullOrWhiteSpace(_clientId))
            _clientId = WellKnownClientId;

        // WAM broker: Windows Account Manager handles authentication in a
        // sandboxed OS-level dialog. Credentials never touch the app process.
        // ListOperatingSystemAccounts = false: only show accounts from our
        // MSAL token cache, not Windows-registered accounts (avoids Windows
        // Hello / passkey prompts).
        var brokerOptions = new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
        {
            Title = "Wrapp - Sign In",
            ListOperatingSystemAccounts = false
        };

        var builder = PublicClientApplicationBuilder.Create(_clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, AadAuthorityAudience.AzureAdMultipleOrgs)
            .WithDefaultRedirectUri()
            .WithBroker(brokerOptions)
            .WithLogging(MsalLogCallback, Microsoft.Identity.Client.LogLevel.Warning, enablePiiLogging: false);

        // WAM requires a parent window handle on the PCA builder so it can
        // parent the OS-level auth dialog. Use the lazy func if available
        // (resolves the current foreground window each time), otherwise fall
        // back to the IntPtr captured during InitializeAsync.
        if (_parentWindowFunc is not null)
            builder = builder.WithParentActivityOrWindow(() => (object)_parentWindowFunc());
        else if (_parentWindow != IntPtr.Zero)
            builder = builder.WithParentActivityOrWindow(() => (object)_parentWindow);

        _publicApp = builder.Build();

        // Attach DPAPI-encrypted file cache via raw hooks
        RegisterTokenCache(_publicApp.UserTokenCache);

        return _publicApp;
        }
        finally { _initLock.Release(); }
    }

    /// <summary>
    /// System browser fallback PCA, created lazily if WAM broker is unavailable.
    /// </summary>
    private Task<IPublicClientApplication> EnsureBrowserFallbackAsync()
    {
        if (_browserFallbackApp is not null)
            return Task.FromResult(_browserFallbackApp);

        _browserFallbackApp = PublicClientApplicationBuilder.Create(_clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, AadAuthorityAudience.AzureAdMultipleOrgs)
            .WithRedirectUri("http://localhost")
            .WithLogging(MsalLogCallback, Microsoft.Identity.Client.LogLevel.Warning, enablePiiLogging: false)
            .Build();

        // Share the same DPAPI-encrypted cache
        RegisterTokenCache(_browserFallbackApp.UserTokenCache);

        return Task.FromResult(_browserFallbackApp);
    }

    // -------------------------------------------------------------------
    // DPAPI-encrypted token cache (BeforeAccess / AfterAccess hooks)
    // -------------------------------------------------------------------
    // Same approach as IntuneManagement's TokenCacheHelperEx. Using raw
    // SetBeforeAccess/SetAfterAccess ensures the full MSAL V3 cache --
    // including refresh tokens provided by WAM -- is serialized to disk.
    // MsalCacheHelper from the Extensions.Msal NuGet was not capturing
    // WAM refresh tokens, causing silent acquisition to fail after restart.
    //
    // Phase 12 hardening (S-4): the read / write callbacks now serialise
    // across processes via a named mutex derived from the cache file path.
    // Same-user concurrent processes (RDP session + local launch, main app
    // + background service) used to race on AtomicFile.WriteAllBytes; even
    // with the atomic write the encrypted cache could be torn between a
    // half-written tail and a concurrent read, surfacing as silent token
    // acquisition failures. The mutex is per-cache-file so the Intune cache
    // and the DevOps cache (which uses MsalCacheHelper, already cross-proc
    // safe) operate independently.

    private const int CacheMutexWaitMs = 5000;

    private static Mutex GetCacheMutex(string cachePath)
    {
        // Local\ prefix scopes to the current logon session, which means
        // same-user cross-process and no admin rights required to create.
        // The hash keeps the name deterministic and under the 260-char
        // kernel-object limit; lowercasing makes case-insensitive Windows
        // paths produce the same mutex name.
        var hash = Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cachePath.ToLowerInvariant())))
            .Substring(0, 16);
        return new Mutex(false, $@"Local\WrappMsalCache_{hash}");
    }

    private static void RegisterTokenCache(ITokenCache tokenCache)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CacheFilePath)!);
        tokenCache.SetBeforeAccess(BeforeAccessCallback);
        tokenCache.SetAfterAccess(AfterAccessCallback);
    }

    private static void BeforeAccessCallback(TokenCacheNotificationArgs args)
    {
        using var mutex = GetCacheMutex(CacheFilePath);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(CacheMutexWaitMs); }
            catch (AbandonedMutexException) { acquired = true; /* prior holder crashed; proceed */ }

            if (!acquired)
            {
                // Timed out waiting for another process / thread to finish.
                // Fall through and read anyway -- prefer a possibly stale
                // read over a hang. The in-process CacheLock still serializes
                // concurrent threads in this process.
                AppLogger.Warn("MSAL cache: BeforeAccess mutex timeout; reading without cross-process lock.");
            }

            lock (CacheLock)
            {
                if (File.Exists(CacheFilePath))
                {
                    try
                    {
                        // SecretProtection.UnprotectBytes recognises the v2 magic
                        // prefix and falls back to v1 (no entropy) so caches
                        // written by older builds still decrypt -- self-healing
                        // migration on the next AfterAccess write.
                        var stored    = File.ReadAllBytes(CacheFilePath);
                        var decrypted = SecretProtection.UnprotectBytes(stored);
                        args.TokenCache.DeserializeMsalV3(decrypted);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Exception("MSAL cache: failed to read", ex);
                        args.TokenCache.DeserializeMsalV3(null);
                    }
                }
                else
                {
                    args.TokenCache.DeserializeMsalV3(null);
                }
            }
        }
        finally
        {
            if (acquired)
            {
                try { mutex.ReleaseMutex(); } catch { /* not held -- nothing to release */ }
            }
        }
    }

    private static void AfterAccessCallback(TokenCacheNotificationArgs args)
    {
        if (!args.HasStateChanged) return;

        using var mutex = GetCacheMutex(CacheFilePath);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(CacheMutexWaitMs); }
            catch (AbandonedMutexException) { acquired = true; }

            if (!acquired)
            {
                AppLogger.Warn("MSAL cache: AfterAccess mutex timeout; writing without cross-process lock.");
            }

            lock (CacheLock)
            {
                try
                {
                    var data      = args.TokenCache.SerializeMsalV3();
                    // ProtectBytes always writes the v2 envelope; this is what
                    // upgrades a legacy v1 cache to v2 transparently.
                    var encrypted = SecretProtection.ProtectBytes(data);
                    AtomicFile.WriteAllBytes(CacheFilePath, encrypted);
                }
                catch (Exception ex)
                {
                    AppLogger.Exception("MSAL cache: failed to write", ex);
                }
            }
        }
        finally
        {
            if (acquired)
            {
                try { mutex.ReleaseMutex(); } catch { /* not held -- nothing to release */ }
            }
        }
    }

    // -------------------------------------------------------------------
    // WAM broker helpers
    // -------------------------------------------------------------------

    /// <summary>
    /// Executes an interactive token request via WAM broker, falling back
    /// to system browser if WAM is unavailable (DLL missing, runtime error).
    /// Serialized via _interactiveLock to prevent concurrent WAM requests
    /// (WAM crashes with ApiContractViolation if two run simultaneously).
    /// </summary>
}
