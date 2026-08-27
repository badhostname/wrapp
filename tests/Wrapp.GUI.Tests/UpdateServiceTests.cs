using Wrapp.Models;
using Wrapp.Services;
using Wrapp.Services.Gates;

namespace Wrapp.Tests;

/// <summary>
/// Workstream D: update-channel policy layer. The security-relevant invariants
/// mirror the Key Vault TOFU suite (<see cref="EncryptionKeyStoreUrlHashTests"/>):
/// the feed delivers executable code, so a URL written into settings.json /
/// defaults.local.json by anything other than the user approving it in-app
/// must never validate, and non-HTTPS transports are rejected outright.
/// </summary>
public class UpdateServiceTests
{
    private const string LegitFeed = "https://updates.contoso.com/wrapp";

    // ---- URL validation ----

    [Theory]
    [InlineData("https://updates.contoso.com/wrapp")]
    [InlineData("  https://updates.contoso.com/wrapp/  ")]
    [InlineData(@"\\fileserver\share\wrapp-updates")]
    [InlineData(@"C:\wrapp\releases")]                // local rooted folder (testing / offline feeds)
    [InlineData("D:/feeds/wrapp")]
    public void IsValidFeedUrl_AcceptsHttpsUncAndLocalFolders(string url)
    {
        Assert.True(UpdateService.IsValidFeedUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://updates.contoso.com/wrapp")] // plain HTTP: spoofable transport
    [InlineData("ftp://updates.contoso.com/wrapp")]
    [InlineData(@"relative\path")]                   // not rooted
    [InlineData("C:")]                               // drive without root
    [InlineData("not a url")]
    public void IsValidFeedUrl_RejectsEverythingElse(string? url)
    {
        Assert.False(UpdateService.IsValidFeedUrl(url));
    }

    [Fact]
    public void NormalizeFeedUrl_TrimsSlashesWhitespaceAndCase()
    {
        Assert.Equal("https://updates.contoso.com/wrapp",
            UpdateService.NormalizeFeedUrl("  HTTPS://Updates.Contoso.com/wrapp/  "));
        Assert.Equal(@"\\fileserver\share",
            UpdateService.NormalizeFeedUrl(@"\\FILESERVER\Share\"));
    }

    // ---- Trust tokens (SEC-1 pattern) ----

    [Fact]
    public void IssuedToken_TrustsSameUrl_RoundTrip()
    {
        var token = UpdateService.IssueFeedTrustToken(LegitFeed);
        Assert.False(string.IsNullOrEmpty(token));
        Assert.True(UpdateService.IsFeedTrusted(LegitFeed, token));
        // Lenient normalisation: benign edits don't lock the user out.
        Assert.True(UpdateService.IsFeedTrusted("  " + LegitFeed.ToUpperInvariant() + "/  ", token));
    }

    [Fact]
    public void DifferentUrl_NotTrusted()
    {
        var token = UpdateService.IssueFeedTrustToken(LegitFeed);
        Assert.False(UpdateService.IsFeedTrusted("https://updates.attacker.com/wrapp", token));
    }

    [Fact]
    public void PlaintextForgery_NotTrusted()
    {
        // An attacker who can write settings.json but cannot DPAPI-protect as
        // the user stores the plaintext normalised URL as the "token".
        Assert.False(UpdateService.IsFeedTrusted(LegitFeed, "https://updates.contoso.com/wrapp"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyToken_NotTrusted(string? token)
    {
        Assert.False(UpdateService.IsFeedTrusted(LegitFeed, token));
    }

    [Fact]
    public void EmptyUrl_IssuesEmptyToken()
    {
        Assert.Equal(string.Empty, UpdateService.IssueFeedTrustToken("  "));
    }

    // ---- Advisory gate ----

    [Fact]
    public void Gate_NotPending_WhenNoFeedConfigured()
    {
        var gate = new UpdateFeedApprovalGate();
        Assert.False(gate.IsPending(new AppSettings()));
    }

    [Fact]
    public void Gate_Pending_ForConfiguredButUnapprovedFeed()
    {
        var gate = new UpdateFeedApprovalGate();
        var settings = new AppSettings { UpdateFeedUrl = LegitFeed };
        Assert.True(gate.IsPending(settings));

        settings.UpdateFeedTrustToken = UpdateService.IssueFeedTrustToken(LegitFeed);
        Assert.False(gate.IsPending(settings));

        // URL changed after approval -> pending again.
        settings.UpdateFeedUrl = "https://updates.contoso.com/wrapp-v2";
        Assert.True(gate.IsPending(settings));
    }
}
