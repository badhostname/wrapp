using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Microsoft.Identity.Client;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Converts an <see cref="MsalTokenResult"/> into the three global variables
/// that IntuneWin32App expects and injects them into a PowerShell RunspacePool.
///
/// IntuneWin32App reads:
///   $Global:AccessToken          - PSObject with access_token, ExpiresOn, etc.
///   $Global:AuthenticationHeader - Hashtable with Authorization header
///   $Global:AccessTokenTenantID  - Tenant ID string
///
/// These are normally set by Connect-MSIntuneGraph, but we bypass it entirely
/// by injecting MSAL-acquired tokens directly.
/// </summary>
public static class PowerShellTokenBridge
{
    /// <summary>
    /// The PowerShell that sets the three token globals IntuneWin32App reads,
    /// from a <c>$Token</c> PSObject (see <see cref="BuildTokenPSObject"/>) and
    /// a <c>$TenantId</c> in scope. The field names here are a CONTRACT with
    /// IntuneWin32App / Wrapp.Packager -- renaming any of them silently breaks
    /// auth inside a run. Shared by every full-token injection path so the
    /// shape can never drift between them.
    /// </summary>
    internal const string TokenGlobalsBody = @"
$Global:AccessToken = [PSCustomObject]@{
    access_token = $Token.AccessToken
    AccessToken  = $Token.AccessToken
    ExpiresOn    = $Token.ExpiresOnUtc
    Scopes       = $Token.Scopes
    token_type   = 'Bearer'
    client_id    = $Token.ClientId
}
$Global:AuthenticationHeader = @{
    'Content-Type'  = 'application/json'
    'Authorization' = 'Bearer ' + $Token.AccessToken
    'ExpiresOn'     = $Token.ExpiresOnUtc
}
$Global:AccessTokenTenantID = $TenantId
";

    /// <summary>
    /// Builds the <c>$Token</c> PSObject that <see cref="TokenGlobalsBody"/>
    /// reads. The C# property names map to the PS-side reads above.
    /// </summary>
    internal static PSObject BuildTokenPSObject(MsalTokenResult token)
    {
        var o = new PSObject();
        o.Properties.Add(new PSNoteProperty("AccessToken", token.AccessToken));
        o.Properties.Add(new PSNoteProperty("ExpiresOnUtc", token.ExpiresOnUtc));
        o.Properties.Add(new PSNoteProperty("Scopes", token.Scopes));
        o.Properties.Add(new PSNoteProperty("ClientId", token.ClientId));
        return o;
    }

    /// <summary>
    /// Injects the MSAL token into a PowerShell RunspacePool by running a small
    /// script that sets the three global variables. This approach works with
    /// RunspacePool (unlike SessionStateProxy which requires a single Runspace).
    /// </summary>
    public static void InjectToken(RunspacePool pool, MsalTokenResult token)
    {
        using var ps = PowerShell.Create();
        ps.RunspacePool = pool;

        // Set the three globals from the shared injection body. Values are
        // passed as a $Token PSObject parameter to avoid string-escaping issues.
        ps.AddScript("param($Token, $TenantId)\n" + TokenGlobalsBody);
        ps.AddParameter("Token", BuildTokenPSObject(token));
        ps.AddParameter("TenantId", token.TenantId);

        ps.Invoke();

        if (ps.HadErrors)
        {
            var errors = string.Join("; ",
                ps.Streams.Error.Select(e => e.Exception?.Message ?? e.ToString()));
            throw new InvalidOperationException(
                $"Failed to inject MSAL token into PowerShell runspace: {errors}");
        }
    }

    /// <summary>
    /// Injects token globals + an opaque MSAL
    /// refresh handle into a PowerShell instance.
    ///
    /// Prior behaviour: pushed the live <see cref="IPublicClientApplication"/>
    /// into <c>$Global:WrappMsalApp</c>, the <see cref="IAccount"/> into
    /// <c>$Global:WrappMsalAccount</c>, and the scope list into
    /// <c>$Global:WrappMsalScopes</c>. Any PS script in the runspace could
    /// then call <c>$Global:WrappMsalApp.AcquireTokenSilent(...)</c> with
    /// arbitrary parameters.
    ///
    /// New behaviour: the (IPCA, account, scopes) tuple is registered with
    /// <see cref="MsalRefreshRegistry"/>; only the resulting GUID handle is
    /// exposed to PS as <c>$Global:WrappMsalRefreshHandle</c>. PS scripts
    /// invoke <c>Invoke-WrappTokenRefresh -Handle $Global:WrappMsalRefreshHandle</c>,
    /// which runs the refresh in C# and returns a typed result. The handle
    /// alone is useless to anything that doesn&#x2019;t share the in-process registry.
    /// </summary>
    /// <returns>
    /// The registry handle - the caller must pass it to
    /// <see cref="MsalRefreshRegistry.Unregister"/> in a <c>finally</c>
    /// after the PS run completes so the IPCA tuple doesn&#x2019;t leak.
    /// </returns>
    public static Guid InjectMsalApp(
        PowerShell ps,
        IPublicClientApplication app,
        IAccount? account,
        string[] scopes,
        MsalTokenResult token)
    {
        var handle = MsalRefreshRegistry.Register(app, account, scopes);

        // Same token globals as InjectToken, plus the opaque refresh handle.
        ps.AddScript(
            "param($Handle, $Token, $TenantId)\n" +
            "$Global:WrappMsalRefreshHandle = $Handle\n" +
            TokenGlobalsBody);

        ps.AddParameter("Handle", handle.ToString("D"));
        ps.AddParameter("Token", BuildTokenPSObject(token));
        ps.AddParameter("TenantId", token.TenantId);

        try
        {
            ps.Invoke();
        }
        catch
        {
            // Invoke crashed before the handle could be consumed; release it
            // immediately so the registry doesn't leak. Re-throw so the
            // caller still sees the failure.
            MsalRefreshRegistry.Unregister(handle);
            throw;
        }

        if (ps.HadErrors)
        {
            var errors = string.Join("; ",
                ps.Streams.Error.Select(e => e.Exception?.Message ?? e.ToString()));
            AppLogger.Warn($"MSAL app injection had errors: {errors}");
        }

        ps.Commands.Clear();
        return handle;
    }

    /// <summary>
    /// Injects a PER-TENANT token map for the module-owned
    /// multi-tenant loop (<c>Invoke-WrappPackaging</c>).
    ///
    /// Instead of one injection per .NET-side tenant iteration, the whole run
    /// gets a single <c>$Global:WrappTokenMap</c> (tenantId → entry) up front;
    /// the module promotes one entry into the per-tenant token globals per
    /// iteration via <c>Use-WrappTenantToken</c> and clears them via
    /// <c>Clear-WrappTenantToken</c> between tenants. Each entry carries its
    /// own opaque refresh handle (one <see cref="MsalRefreshRegistry"/>
    /// registration per tenant, since the <see cref="IAccount"/> differs).
    /// </summary>
    /// <returns>
    /// The registered refresh handles - the caller must unregister ALL of them
    /// in a <c>finally</c> after the PS run completes.
    /// </returns>
    public static List<Guid> InjectMsalTokenMap(
        PowerShell ps,
        IPublicClientApplication app,
        string[] scopes,
        IReadOnlyList<(MsalTokenResult Token, IAccount? Account)> entries)
    {
        var handles = new List<Guid>(entries.Count);
        var mapEntries = new List<PSObject>(entries.Count);

        foreach (var (token, account) in entries)
        {
            var handle = MsalRefreshRegistry.Register(app, account, scopes);
            handles.Add(handle);

            // Same fields as BuildTokenPSObject, plus the routing key and the
            // per-tenant refresh handle. Field names are a contract with
            // Use-WrappTenantToken (module side).
            var entry = BuildTokenPSObject(token);
            entry.Properties.Add(new PSNoteProperty("TenantId", token.TenantId));
            entry.Properties.Add(new PSNoteProperty("RefreshHandle", handle.ToString("D")));
            mapEntries.Add(entry);
        }

        ps.AddScript(@"
param($Entries)
$Global:WrappTokenMap = @{}
foreach ($e in $Entries) { $Global:WrappTokenMap[$e.TenantId] = $e }
");
        ps.AddParameter("Entries", mapEntries);

        try
        {
            ps.Invoke();
        }
        catch
        {
            // Invoke crashed before the map could be consumed; release every
            // handle immediately so the registry doesn't leak. Re-throw so
            // the caller still sees the failure.
            foreach (var h in handles)
                MsalRefreshRegistry.Unregister(h);
            throw;
        }

        if (ps.HadErrors)
        {
            var errors = string.Join("; ",
                ps.Streams.Error.Select(e => e.Exception?.Message ?? e.ToString()));
            AppLogger.Warn($"MSAL token map injection had errors: {errors}");
        }

        ps.Commands.Clear();
        return handles;
    }
}
