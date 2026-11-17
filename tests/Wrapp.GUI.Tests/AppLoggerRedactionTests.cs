using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Redaction regex coverage for <see cref="AppLogger"/>. Phase-0 security
/// hardening: every delimiter pattern that can surround a token in a real
/// log line must truncate the capture cleanly AND the token body must be
/// replaced before it reaches disk.
/// </summary>
public class AppLoggerRedactionTests
{
    private const string RealisticJwt =
        "eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9." +
        "eyJzdWIiOiJ1c2VyQGV4YW1wbGUuY29tIiwiaWF0IjoxNzE0OTk5OTk5fQ." +
        "abcdef1234567890XYZ";

    // ── Bearer scheme ────────────────────────────────────────────────

    [Fact]
    public void Redact_BearerInHeader_ReplacesToken()
    {
        var input  = $"Authorization: Bearer {RealisticJwt}";
        var output = AppLogger.Redact(input);
        Assert.DoesNotContain(RealisticJwt, output);
        Assert.Contains("Bearer ***REDACTED***", output);
    }

    [Fact]
    public void Redact_BearerInJsonPayload_TruncatesAtClosingQuote()
    {
        // Regression for the audit concern: JSON-quoted tokens must not leak
        // after the closing quote. Character-class boundary stops at ".
        var input  = $"{{\"Authorization\":\"Bearer {RealisticJwt}\",\"other\":\"data\"}}";
        var output = AppLogger.Redact(input);
        Assert.DoesNotContain(RealisticJwt, output);
        Assert.Contains("\"other\":\"data\"", output);  // payload around it survives
    }

    [Theory]
    [InlineData("bearer")]
    [InlineData("BEARER")]
    [InlineData("BeArEr")]
    public void Redact_BearerCaseInsensitive_AllVariantsRedacted(string scheme)
    {
        // RFC 7235: auth schemes are case-insensitive. Case variations are
        // common in server-side logs (lowercase in nginx, mixed in verbose
        // HTTP traces).
        var input  = $"{scheme} {RealisticJwt}";
        var output = AppLogger.Redact(input);
        Assert.DoesNotContain(RealisticJwt, output);
    }

    [Fact]
    public void Redact_BearerTerminatorVariants_AllHandled()
    {
        // Closing quote, brace, bracket, semicolon, comma, newline -- each
        // must cleanly terminate the capture without leaking into the next
        // field.
        var terminators = new[] { "\"", "}", "]", ",", ";", "\n" };
        foreach (var term in terminators)
        {
            var input  = $"Bearer {RealisticJwt}{term}next-field";
            var output = AppLogger.Redact(input);
            Assert.DoesNotContain(RealisticJwt, output);
            Assert.Contains("next-field", output);
        }
    }

    // ── Bare JWT (no Bearer prefix) ──────────────────────────────────

    [Fact]
    public void Redact_BareJwtInJsonBody_Redacted()
    {
        // JWTs sometimes appear in logged response bodies without a Bearer
        // prefix (e.g. token endpoint response JSON). The JWT-shape regex
        // catches them as a second line of defence.
        var input  = $"{{\"access_token\":\"{RealisticJwt}\",\"expires_in\":3600}}";
        var output = AppLogger.Redact(input);
        Assert.DoesNotContain(RealisticJwt, output);
    }

    // ── ClientSecret ─────────────────────────────────────────────────

    [Theory]
    [InlineData("ClientSecret=abcd1234xyz")]
    [InlineData("ClientSecret: abcd1234xyz")]
    [InlineData("\"ClientSecret\": \"abcd1234xyz\"")]
    [InlineData("clientSecret=abcd1234xyz")]         // camelCase
    [InlineData("client_secret=abcd1234xyz")]        // OAuth form encoding
    [InlineData("CLIENT_SECRET=abcd1234xyz")]        // screaming
    [InlineData("client-secret: abcd1234xyz")]       // hyphenated header-style
    public void Redact_ClientSecretCasings_AllRedacted(string input)
    {
        var output = AppLogger.Redact(input);
        Assert.DoesNotContain("abcd1234xyz", output);
        Assert.Contains("***REDACTED***", output);
    }

    // ── OAuth form-encoded tokens ────────────────────────────────────

    [Theory]
    [InlineData("access_token")]
    [InlineData("refresh_token")]
    [InlineData("id_token")]
    public void Redact_OAuthFormTokens_AllRedacted(string tokenName)
    {
        // Form-encoded token-endpoint bodies include these fields; if any
        // HTTP debug middleware logs a request/response body, this keeps
        // tokens out of the on-disk log. The capture stops at &, so
        // surrounding form fields survive.
        var input  = $"grant_type=refresh_token&{tokenName}={RealisticJwt}&scope=openid";
        var output = AppLogger.Redact(input);
        Assert.DoesNotContain(RealisticJwt, output);
        Assert.Contains("grant_type=refresh_token", output);
        Assert.Contains("scope=openid", output);
    }

    // ── Basic auth (HTTP Basic + Azure DevOps PATs encoded as Basic) ─

    [Fact]
    public void Redact_BasicAuthHeader_ReplacesCredential()
    {
        // Azure DevOps PAT encoded as base64(":<PAT>") -- typical Basic auth
        // shape for DevOps REST calls. The 56-char body below is the base64
        // representation of a colon plus a 41-byte arbitrary string.
        const string credential = "OnRoaXMtaXMtbm90LXJlYWxseS1hLXBhdC1idXQtbG9va3MtbGlrZS1vbmU=";
        var input  = $"Authorization: Basic {credential}";
        var output = AppLogger.Redact(input);
        Assert.DoesNotContain(credential, output);
        Assert.Contains("Basic ***REDACTED***", output);
    }

    [Theory]
    [InlineData("Basic")]
    [InlineData("basic")]
    [InlineData("BASIC")]
    [InlineData("BaSiC")]
    public void Redact_BasicCaseInsensitive_AllVariantsRedacted(string scheme)
    {
        const string credential = "OnZhbHVl";
        var input  = $"Authorization: {scheme} {credential}";
        var output = AppLogger.Redact(input);
        Assert.DoesNotContain(credential, output);
    }

    [Fact]
    public void Redact_PatQueryString_Redacted()
    {
        // Azure DevOps occasionally embeds PATs in query strings; not the
        // common path but seen in some Microsoft sample code.
        const string pat = "abc123def456ghi789jkl012mno345pqr678stu901vwx234yz";
        var input  = $"https://dev.azure.com/org/_apis/wit?api-version=7.0&pat={pat}";
        var output = AppLogger.Redact(input);
        Assert.DoesNotContain(pat, output);
        Assert.Contains("pat=***REDACTED***", output);
    }

    // ── Clean inputs survive untouched ───────────────────────────────

    [Fact]
    public void Redact_CleanMessage_Unchanged()
    {
        const string input = "User signed in at 2026-04-22; tenant=contoso.onmicrosoft.com";
        Assert.Equal(input, AppLogger.Redact(input));
    }

    [Fact]
    public void Redact_EmptyAndNull_Safe()
    {
        Assert.Equal("", AppLogger.Redact(""));
        Assert.Null(AppLogger.Redact(null!));
    }
}
