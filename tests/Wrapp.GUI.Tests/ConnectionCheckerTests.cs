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

    // ── SCCM: the site card's twin of the token rule ─────────────────
    // A site card says "Connected" only when ITS site answered through its
    // drive. 1.0.10: the Run view compared the configured site code with the
    // one the console had recorded in the registry, which exists only once
    // the console has completed a connection under this Windows account, so
    // a console-only workstation showed "Not reachable" while the inventory
    // view, which builds the drive itself, worked.

    private static readonly SccmConnectivityResult ConsoleFound = new(true, null, "Module found", "cm01.example.test");

    [Fact]
    public void A_Site_That_Answers_Connects_And_Shows_Its_Server()
    {
        var status = new ConnectionStatus { TenantId = "CB1" };
        ConnectionChecker.ApplySccmSiteStatus(status, new SccmSiteResult(true, "", "Primary", "cm01.example.test"), ConsoleFound);
        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.Equal("Connected", status.StatusText);
        Assert.Equal("cm01.example.test", status.DetailLine1);
        Assert.Equal("Primary", status.DetailLine2);
        Assert.Equal(string.Empty, status.ErrorMessage);
    }

    [Fact]
    public void A_Site_That_Does_Not_Answer_Carries_The_Probe_Reason()
    {
        var status = new ConnectionStatus { TenantId = "CB1", DetailLine1 = "stale" };
        ConnectionChecker.ApplySccmSiteStatus(status, new SccmSiteResult(false, "Could not connect to ConfigMgr site CB1 through 'cm01'", null, null), ConsoleFound);
        Assert.Equal(ConnectionState.Disconnected, status.State);
        Assert.Equal("Not reachable", status.StatusText);
        Assert.Equal(string.Empty, status.DetailLine1);
        Assert.StartsWith("Could not connect", status.ErrorMessage);

        ConnectionChecker.ApplySccmSiteStatus(status, null, ConsoleFound);
        Assert.Equal("Not checked", status.ErrorMessage);
    }

    [Fact]
    public void No_Console_Fails_Every_Site_With_That_Reason()
    {
        var status = new ConnectionStatus { TenantId = "CB1" };
        ConnectionChecker.ApplySccmSiteStatus(status,
            new SccmSiteResult(true, "", "Primary", "cm01"),
            new SccmConnectivityResult(false, "SMS_ADMIN_UI_PATH not set", null, null));
        Assert.Equal(ConnectionState.Disconnected, status.State);
        Assert.Equal("Console not found", status.StatusText);
        Assert.Equal("SMS_ADMIN_UI_PATH not set", status.ErrorMessage);
    }

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
