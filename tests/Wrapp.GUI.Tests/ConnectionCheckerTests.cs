using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// A tenant card says "Connected" only for a token issued for THAT tenant.
/// 1.0.7: two placeholder tenants showed Connected with the real tenant's
/// token, because an unscoped MSAL request against the cached account was
/// answered from the account's home tenant and the card never compared.
/// </summary>
public class ConnectionCheckerTests
{
    private const string Real = "d35fe7ad-0000-0000-0000-000000000001";
    private const string Fake = "11111111-aaaa-bbbb-cccc-222222222222";

    private static MsalTokenResult Token(string tenant) =>
        new("t", DateTime.UtcNow.AddHours(1), new[] { "s" }, tenant, "c", "someone@contoso.com");

    [Fact]
    public void Token_For_Another_Tenant_Is_Not_A_Connection()
    {
        var status = new ConnectionStatus { TargetName = "Placeholder", TenantId = Fake };
        ConnectionChecker.ApplyTokenStatus(status, Token(Real), graphReachable: true);
        Assert.Equal(ConnectionState.Disconnected, status.State);
        Assert.Equal("Not authenticated", status.StatusText);
        Assert.Equal("Signed in to another tenant", status.DetailLine1);
        Assert.Null(status.TokenExpiresUtc);
    }

    [Fact]
    public void Token_For_The_Tenant_Connects()
    {
        var status = new ConnectionStatus { TargetName = "Real", TenantId = Real };
        ConnectionChecker.ApplyTokenStatus(status, Token(Real.ToUpperInvariant()), graphReachable: true);
        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.Equal("someone@contoso.com", status.DetailLine1);
    }

    [Fact]
    public void A_Card_Without_A_Tenant_Does_Not_Compare()
    {
        var status = new ConnectionStatus { TargetName = "Site" };
        ConnectionChecker.ApplyTokenStatus(status, Token(Real), graphReachable: true);
        Assert.Equal(ConnectionState.Connected, status.State);
    }

    [Theory]
    [InlineData(Fake, Real, true)]
    [InlineData(Real, Real, false)]
    [InlineData("", Real, false)]            // unscoped request: discovery, not a mismatch
    [InlineData("organizations", Real, false)]
    [InlineData(Fake, "", false)]            // MSAL gave no tenant: unknown, not a mismatch
    [InlineData(Fake, null, false)]
    public void Mismatch_Needs_A_Real_Requested_Tenant_And_A_Different_Actual_One(string requested, string? actual, bool expected)
        => Assert.Equal(expected, MsalAuthService.IsTenantMismatch(requested, actual));
}
