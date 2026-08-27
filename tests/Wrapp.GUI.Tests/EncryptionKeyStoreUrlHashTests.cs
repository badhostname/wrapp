using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Covers the DevOps key-vault TOFU (trust-on-first-use) anchor after the SEC-1
/// (2026-07 audit) hardening: the approved-URL record is now a machine-bound
/// DPAPI trust token (<see cref="EncryptionKeyStoreService.IssueKeyVaultTrustToken"/>)
/// verified by <see cref="EncryptionKeyStoreService.IsKeyVaultUrlTrusted"/>,
/// replacing the previous forgeable plain SHA-256 hash.
/// <para>
/// The security-relevant invariants:
///   - normalisation stays lenient (trailing slash / case / whitespace) so a
///     benign edit doesn't lock the operator out;
///   - a substituted host/org/repo is NOT trusted;
///   - crucially, a value an attacker could write into settings.json WITHOUT
///     DPAPI-protecting it as the current user (a legacy hex hash, or the
///     plaintext normalised URL) does NOT validate - this is the whole point
///     of the fix. Relies on DPAPI being available for the current user
///     (see SecretProtectionTests).
/// </para>
/// </summary>
public class EncryptionKeyStoreUrlHashTests
{
    private const string LegitUrl = "https://dev.azure.com/org/proj/_git/keys";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IssueTrustToken_EmptyUrl_ReturnsEmpty(string? url)
    {
        Assert.Equal(string.Empty, EncryptionKeyStoreService.IssueKeyVaultTrustToken(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsTrusted_EmptyStoredToken_ReturnsFalse(string? token)
    {
        // An empty stored token must never "match" a real URL.
        Assert.False(EncryptionKeyStoreService.IsKeyVaultUrlTrusted(LegitUrl, token));
    }

    [Fact]
    public void IssuedToken_TrustsSameUrl_RoundTrip()
    {
        var token = EncryptionKeyStoreService.IssueKeyVaultTrustToken(LegitUrl);
        Assert.False(string.IsNullOrEmpty(token));
        Assert.True(EncryptionKeyStoreService.IsKeyVaultUrlTrusted(LegitUrl, token));
    }

    [Fact]
    public void TrailingSlash_StillTrusted()
    {
        var token = EncryptionKeyStoreService.IssueKeyVaultTrustToken(LegitUrl);
        Assert.True(EncryptionKeyStoreService.IsKeyVaultUrlTrusted(LegitUrl + "/", token));
    }

    [Fact]
    public void CaseAndSurroundingWhitespace_StillTrusted()
    {
        var token = EncryptionKeyStoreService.IssueKeyVaultTrustToken("https://dev.azure.com/Org/Proj/_git/Keys");
        Assert.True(EncryptionKeyStoreService.IsKeyVaultUrlTrusted(
            "  HTTPS://DEV.AZURE.COM/org/proj/_git/keys  ", token));
    }

    [Fact]
    public void DifferentUrl_NotTrusted()
    {
        // The whole point of TOFU: a substituted host/org/repo must NOT pass.
        var token = EncryptionKeyStoreService.IssueKeyVaultTrustToken(LegitUrl);
        Assert.False(EncryptionKeyStoreService.IsKeyVaultUrlTrusted(
            "https://dev.azure.com/attacker/proj/_git/keys", token));
    }

    [Fact]
    public void LegacyPlainHash_NotTrusted_ForcesReApproval()
    {
        // A pre-SEC-1 stored value was uppercase SHA-256 hex. It is not a DPAPI
        // envelope, so it must fail verification (the operator re-approves once).
        var legacyHash = "A1B2C3D4E5F6A1B2C3D4E5F6A1B2C3D4E5F6A1B2C3D4E5F6A1B2C3D4E5F6A1B2";
        Assert.False(EncryptionKeyStoreService.IsKeyVaultUrlTrusted(LegitUrl, legacyHash));
    }

    [Fact]
    public void AttackerWrittenPlaintextUrl_NotTrusted()
    {
        // The core SEC-1 fix: an attacker who can write settings.json but cannot
        // DPAPI-protect as the user might try storing the plaintext normalised
        // URL as the "token". DecryptAuthentic has no plaintext passthrough, so
        // this must NOT be accepted.
        var plaintextForgery = "https://dev.azure.com/org/proj/_git/keys"; // normalised, no dpapi: prefix
        Assert.False(EncryptionKeyStoreService.IsKeyVaultUrlTrusted(LegitUrl, plaintextForgery));
    }
}
