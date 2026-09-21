using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Characterization tests for <see cref="PowerShellTokenBridge"/> token
/// injection. These pin the EXACT shape of the `$Global:` variables that
/// IntuneWin32App / Wrapp.Packager read by name &mdash; if a refactor renamed a
/// field or dropped a global, auth would silently break inside a packaging run.
/// The test runs the real injection into a pooled runspace and reads the
/// globals back out, so it asserts observable behavior, not implementation.
/// </summary>
public class PowerShellTokenInjectionTests
{
    private static MsalTokenResult SampleToken() => new(
        AccessToken:       "eyJ0eXAiOiJKV1QifQ.payload.sig",
        ExpiresOnUtc:      new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Scopes:            new[] { "https://graph.microsoft.com/.default" },
        TenantId:          "11111111-2222-3333-4444-555555555555",
        ClientId:          "app-client-id-42",
        UserPrincipalName: null);

    [Fact]
    public void InjectToken_SetsAllExpectedGlobals_AsReadFromTheRunspace()
    {
        var token = SampleToken();

        // min=max=1 -> a single shared runspace, so $Global: state persists
        // across the inject call and the read-back call (the whole reason the
        // bridge targets a pool rather than SessionStateProxy).
        using var pool = RunspaceFactory.CreateRunspacePool(1, 1);
        pool.Open();

        PowerShellTokenBridge.InjectToken(pool, token);

        using var ps = PowerShell.Create();
        ps.RunspacePool = pool;
        ps.AddScript(@"
[PSCustomObject]@{
    AccessToken_snake = $Global:AccessToken.access_token
    AccessToken_pascal = $Global:AccessToken.AccessToken
    TokenType         = $Global:AccessToken.token_type
    ClientId          = $Global:AccessToken.client_id
    Authorization     = $Global:AuthenticationHeader.Authorization
    ContentType       = $Global:AuthenticationHeader.'Content-Type'
    TenantId          = $Global:AccessTokenTenantID
}");
        var result = ps.Invoke();
        Assert.False(ps.HadErrors);
        var o = Assert.Single(result);

        // The two casings IntuneWin32App accepts for the bearer value
        Assert.Equal(token.AccessToken, o.Properties["AccessToken_snake"].Value);
        Assert.Equal(token.AccessToken, o.Properties["AccessToken_pascal"].Value);
        Assert.Equal("Bearer", o.Properties["TokenType"].Value);
        Assert.Equal(token.ClientId, o.Properties["ClientId"].Value);
        Assert.Equal($"Bearer {token.AccessToken}", o.Properties["Authorization"].Value);
        Assert.Equal("application/json", o.Properties["ContentType"].Value);
        Assert.Equal(token.TenantId, o.Properties["TenantId"].Value);
    }
}
