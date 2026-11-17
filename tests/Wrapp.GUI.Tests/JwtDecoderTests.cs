using System.Text;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Covers <see cref="JwtDecoder"/>, the presentation-only JWT payload
/// inspector used by the account flyout. The contract that matters: it must
/// NEVER throw on hostile/malformed input (it feeds UI), and the few claim
/// transformations it does (Unix-timestamp -> date, arrays -> joined, scp
/// -> newlines) must actually fire. Builds real base64url tokens so the
/// decode path is exercised end-to-end, not stubbed.
/// </summary>
public class JwtDecoderTests
{
    // Builds a syntactically real JWT (header.payload.signature) whose
    // payload is the given JSON. Only the payload segment is ever decoded.
    private static string TokenWithPayload(string payloadJson)
        => "eyJhbGciOiJub25lIn0." + B64Url(payloadJson) + ".sig";

    private static string B64Url(string s)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── Robustness: must never throw, returns empty/null ───────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DecodePayload_NullEmptyWhitespace_ReturnsEmpty(string? jwt)
    {
        Assert.Empty(JwtDecoder.DecodePayloadForDisplay(jwt));
    }

    [Fact]
    public void DecodePayload_FewerThanTwoSegments_ReturnsEmpty()
    {
        // A token with no '.' has only one segment -> nothing to decode.
        Assert.Empty(JwtDecoder.DecodePayloadForDisplay("onlyoneheaderpart"));
    }

    [Fact]
    public void DecodePayload_PayloadIsNotValidBase64_ReturnsEmptyDoesNotThrow()
    {
        // '@' is not a base64 character -> Convert.FromBase64String throws,
        // which the decoder must swallow and return empty (it feeds UI).
        var token = "header.@@@not-base64@@@.sig";
        var result = JwtDecoder.DecodePayloadForDisplay(token);
        Assert.Empty(result);
    }

    [Fact]
    public void DecodePayload_PayloadIsNotJson_ReturnsEmptyDoesNotThrow()
    {
        // Valid base64url, but the decoded bytes aren't JSON.
        var token = "header." + B64Url("this is not json at all") + ".sig";
        Assert.Empty(JwtDecoder.DecodePayloadForDisplay(token));
    }

    // ── Claim transformations actually fire ────────────────────────

    [Fact]
    public void DecodePayload_StringClaim_ReturnedVerbatim()
    {
        var token = TokenWithPayload("""{"upn":"alice@contoso.com"}""");
        var claims = JwtDecoder.DecodePayloadForDisplay(token);
        Assert.Contains(claims, c => c.Key == "upn" && c.Value == "alice@contoso.com");
    }

    [Fact]
    public void DecodePayload_ExpClaim_RenderedAsDateNotRawEpoch()
    {
        // exp is a Unix timestamp; the decoder must convert it to a readable
        // local datetime, NOT leave it as the raw integer. Compute the
        // expectation the same way production does so the test is timezone
        // independent while still proving the conversion fired.
        long epoch = 1_700_000_000;
        var token = TokenWithPayload($$"""{"exp":{{epoch}}}""");

        var claims = JwtDecoder.DecodePayloadForDisplay(token);
        var exp = Assert.Single(claims, c => c.Key == "exp");

        var expected = DateTimeOffset.FromUnixTimeSeconds(epoch)
            .LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        Assert.Equal(expected, exp.Value);
        Assert.NotEqual(epoch.ToString(), exp.Value);   // guards against "no conversion" regression
    }

    [Fact]
    public void DecodePayload_ArrayClaim_JoinedWithSemicolons()
    {
        var token = TokenWithPayload("""{"roles":["Reader","Writer","Owner"]}""");
        var claims = JwtDecoder.DecodePayloadForDisplay(token);
        var roles = Assert.Single(claims, c => c.Key == "roles");
        Assert.Equal("Reader; Writer; Owner", roles.Value);
    }

    [Fact]
    public void DecodePayload_ScpClaim_SpacesBecomeNewlines()
    {
        // Scope strings are space-delimited; the UI shows them one per line.
        var token = TokenWithPayload("""{"scp":"User.Read Mail.Read Files.ReadWrite"}""");
        var claims = JwtDecoder.DecodePayloadForDisplay(token);
        var scp = Assert.Single(claims, c => c.Key == "scp");
        Assert.Equal("User.Read\nMail.Read\nFiles.ReadWrite", scp.Value);
    }

    // ── GetClaimForDisplay ─────────────────────────────────────────

    [Fact]
    public void GetClaim_PresentClaim_ReturnsValue()
    {
        var token = TokenWithPayload("""{"tid":"11111111-2222-3333-4444-555555555555"}""");
        Assert.Equal("11111111-2222-3333-4444-555555555555",
            JwtDecoder.GetClaimForDisplay(token, "tid"));
    }

    [Fact]
    public void GetClaim_AbsentClaim_ReturnsNull()
    {
        var token = TokenWithPayload("""{"tid":"abc"}""");
        Assert.Null(JwtDecoder.GetClaimForDisplay(token, "oid"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("noseconddot")]
    [InlineData("header.@@@bad@@@.sig")]
    public void GetClaim_NullMalformedOrSingleSegment_ReturnsNull(string? jwt)
    {
        Assert.Null(JwtDecoder.GetClaimForDisplay(jwt, "anything"));
    }
}
