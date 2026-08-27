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
    // Confidential client builder (client-secret + client-cert flows),
    // certificate-store lookup, MSAL result mapping, and MSAL log callback.
    // -----------------------------------------------------------------------

    private void EnsureConfidentialClient()
    {
        if (_confidentialApp is not null) return;

        var builder = ConfidentialClientApplicationBuilder.Create(_clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, ConfidentialTenantId)
            .WithLogging(MsalLogCallback, Microsoft.Identity.Client.LogLevel.Warning, enablePiiLogging: false);

        if (_authFlow == AuthFlow.ClientCert)
        {
            var cert = LoadCertificate(_certThumbprint!);
            builder = builder.WithCertificate(cert);
        }
        else
        {
            // Unwrap the SecureString into a plaintext .NET string only for the
            // duration of this builder call; the BSTR is zero-freed on exit.
            // MSAL keeps its own internal copy we can't zero, but at least our
            // side of the boundary holds no long-lived plaintext field.
            builder = SecretProtection.WithPlaintext(
                _clientSecret!, plain => builder.WithClientSecret(plain));
        }

        _confidentialApp = builder.Build();
    }

    /// <summary>
    /// Constant-time-ish equality for two <see cref="SecureString"/>s without
    /// ever materializing both plaintexts as managed <see cref="string"/>s.
    /// Unwraps each to an unmanaged BSTR, compares char-by-char, then zeroes
    /// the interop buffers. Both nulls are equal; lengths differ fast-fail.
    /// Used only by InitializeAsync to decide whether to rebuild the CCA.
    /// </summary>
    private static bool SecureStringsEqual(SecureString? a, SecureString? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Length != b.Length) return false;

        IntPtr ba = IntPtr.Zero, bb = IntPtr.Zero;
        try
        {
            ba = Marshal.SecureStringToBSTR(a);
            bb = Marshal.SecureStringToBSTR(b);
            for (int i = 0; i < a.Length; i++)
            {
                var ca = Marshal.ReadInt16(ba, i * 2);
                var cb = Marshal.ReadInt16(bb, i * 2);
                if (ca != cb) return false;
            }
            return true;
        }
        finally
        {
            if (ba != IntPtr.Zero) Marshal.ZeroFreeBSTR(ba);
            if (bb != IntPtr.Zero) Marshal.ZeroFreeBSTR(bb);
        }
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static X509Certificate2 LoadCertificate(string thumbprint)
    {
        // Normalize thumbprint: strip spaces, dashes, colons in a single pass
        // (vs the old triple-Replace, which allocated three intermediate
        // strings). Thumbprints are short (40 chars SHA1 / 64 chars SHA256)
        // so a stack buffer covers every case without heap traffic.
        Span<char> buf = stackalloc char[thumbprint.Length];
        int len = 0;
        foreach (var c in thumbprint)
            if (c is not ' ' and not '-' and not ':')
                buf[len++] = c;
        var clean = new string(buf[..len]);

        var cert = FindCertInStore(clean, StoreLocation.CurrentUser)
                ?? FindCertInStore(clean, StoreLocation.LocalMachine);

        if (cert is null)
            throw new InvalidOperationException(
                $"Certificate with thumbprint '{clean}' not found in "
                + "CurrentUser\\My or LocalMachine\\My stores.");

        if (!cert.HasPrivateKey)
            throw new InvalidOperationException(
                $"Certificate '{clean}' does not have a private key. "
                + "The private key is required for client certificate authentication.");

        return cert;
    }

    private static X509Certificate2? FindCertInStore(string thumbprint, StoreLocation location)
    {
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly);
        // validOnly:true rejects expired / not-yet-valid / revoked certificates.
        // Expired client certs must not be used to authenticate -- if an operator
        // renews a cert and forgets to update the thumbprint, we'd rather fail
        // hard here than silently keep using the expired one.
        var found = store.Certificates
            .Find(X509FindType.FindByThumbprint, thumbprint, validOnly: true);
        return found.Count > 0 ? found[0] : null;
    }

    private MsalTokenResult ToResult(AuthenticationResult result)
    {
        // Always track the tenant ID from the auth result. For interactive flows
        // (which use /organizations authority), this discovers which tenant the
        // user actually signed into. For confidential flows, it confirms the
        // configured tenant.
        var resolvedTenant = result.TenantId;
        if (!string.IsNullOrEmpty(resolvedTenant))
        {
            if (resolvedTenant != _tenantId)
                AppLogger.Info($"Tenant from auth result: {resolvedTenant}");
            _tenantId = resolvedTenant;
        }

        var tokenResult = new MsalTokenResult(
            AccessToken: result.AccessToken,
            ExpiresOnUtc: result.ExpiresOn.UtcDateTime,
            Scopes: result.Scopes?.ToArray() ?? Array.Empty<string>(),
            TenantId: _tenantId,
            ClientId: _clientId,
            UserPrincipalName: result.Account?.Username,
            IdToken: result.IdToken,
            CorrelationId: result.CorrelationId.ToString()
        );
        TokenAcquired?.Invoke(tokenResult);
        return tokenResult;
    }

    private static bool _silentFailureLogged;

    /// <summary>Resets the MSAL silent-failure dedup flag so the next occurrence is logged.</summary>
    internal static void ResetSilentFailureLog() => _silentFailureLogged = false;

    private static void MsalLogCallback(Microsoft.Identity.Client.LogLevel level, string message, bool containsPii)
    {
        if (containsPii) return;

        // --- Benign noise: suppress entirely ---
        // WAM provider probes, empty cache probes, broker availability checks
        if (message.Contains("MsaDeviceOperationProvider")
            || message.Contains("MsaPassthroughHandler")
            || message.Contains("WAM broker plugin")
            || message.Contains("Checking broker availability")
            || message.Contains("Found 0 broker accounts")
            || message.Contains("is not found in the cache")
            || message.Contains("No Access Token found"))
            return;

        // MsalUiRequiredException / silent failure -- expected when not signed in.
        // MSAL fires this callback multiple times per silent attempt; log only once
        // per refresh cycle to avoid flooding the log (was 572 lines in one session).
        if (message.Contains("MsalUiRequiredException")
            || message.Contains("failed_to_acquire_token_silently")
            || message.Contains("UiRequiredException")
            || message.Contains("InteractionRequired"))
        {
            if (!_silentFailureLogged)
            {
                _silentFailureLogged = true;
                AppLogger.Info("[MSAL] Silent token acquisition failed (interaction required)");
            }
            return;
        }

        // --- Useful info: surface as Info ---
        // Token expiry, refresh, cache hits
        if (message.Contains("token expires on")
            || message.Contains("Access token expired")
            || message.Contains("Refresh token expired")
            || message.Contains("token was found in the cache")
            || message.Contains("Refreshing access token")
            || message.Contains("returned from broker"))
        {
            // Extract just the meaningful part (MSAL prefixes with timestamps/correlation IDs)
            AppLogger.Info($"[MSAL] {TrimMsalPrefix(message)}");
            return;
        }

        // --- Real errors: log as warnings ---
        if (level == Microsoft.Identity.Client.LogLevel.Error)
        {
            AppLogger.Warn($"[MSAL] {TrimMsalPrefix(message)}");
            return;
        }

        // Skip remaining Warning-level noise (dozens of lines per call)
    }

    /// <summary>
    /// Strips the verbose MSAL prefix (timestamps, correlation IDs) to keep log lines concise.
    /// MSAL messages typically start with "[timestamp - correlationId] message".
    /// </summary>
    private static string TrimMsalPrefix(string message)
    {
        // MSAL format: "[2026-03-16 ...] Some message" or "False - CorrelationId - Some message"
        var idx = message.IndexOf("] ", StringComparison.Ordinal);
        if (idx >= 0 && idx < 80)
            return message[(idx + 2)..];
        return message;
    }

}
