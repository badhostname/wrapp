using System.Security.Cryptography;
using System.Text;
using Wrapp.Models;

namespace Wrapp.Tests;

/// <summary>
/// Round-trip + backward-compat coverage for the DPAPI envelope upgrade
/// (Phase 0 security hardening). Verifies that:
/// <list type="bullet">
/// <item>v2 ciphertext (with entropy) written by the current code path
/// decrypts back to the original plaintext.</item>
/// <item>v1 ciphertext (no entropy, written by older app versions) still
/// decrypts so existing on-disk data isn't stranded.</item>
/// <item>Legacy plaintext (no envelope) continues to pass through untouched.</item>
/// <item>Corrupted / foreign ciphertext returns empty rather than throwing.</item>
/// </list>
/// Tests run only on Windows because DPAPI is Windows-only; skipped
/// elsewhere.
/// </summary>
public class SecretProtectionTests
{
    private static bool DpapiAvailable
    {
        get
        {
            try
            {
                ProtectedData.Protect(new byte[] { 1 }, null, DataProtectionScope.CurrentUser);
                return true;
            }
            catch { return false; }
        }
    }

    // ── Guard: the crypto suite must actually run here ───────────────

    [Fact]
    public void Dpapi_IsAvailable_SoTheGuardedTestsActuallyAssert()
    {
        // Every DPAPI-dependent test below early-returns (passing, asserting
        // nothing) when DPAPI is unavailable. Wrapp targets net8.0-windows,
        // so DPAPI is ALWAYS available on a correct build/runner. This test
        // turns "the entire crypto suite silently no-opped" -- which would
        // otherwise report green -- into a single loud failure.
        Assert.True(DpapiAvailable,
            "DPAPI is unavailable: the DPAPI-guarded SecretProtection tests did not actually run.");
    }

    // ── String envelope (settings.json) ──────────────────────────────

    [Fact]
    public void Encrypt_Decrypt_RoundTrip_Succeeds()
    {
        if (!DpapiAvailable) return;
        const string secret = "ClientSecret_abc_12345";
        var cipher = SecretProtection.Encrypt(secret);
        Assert.StartsWith("dpapi:v2:", cipher);
        var plain = SecretProtection.Decrypt(cipher);
        Assert.Equal(secret, plain);
    }

    [Fact]
    public void Decrypt_LegacyV1Envelope_StillDecrypts()
    {
        if (!DpapiAvailable) return;
        // Simulate a ciphertext written by the pre-entropy code path: the
        // "dpapi:" prefix with ProtectedData.Protect(plain, null, ...).
        const string secret = "older_app_secret";
        var plainBytes = Encoding.UTF8.GetBytes(secret);
        var v1Bytes    = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        var v1Envelope = "dpapi:" + Convert.ToBase64String(v1Bytes);

        var plain = SecretProtection.Decrypt(v1Envelope);
        Assert.Equal(secret, plain);
    }

    [Fact]
    public void Decrypt_LegacyPlaintextNoEnvelope_PassesThrough()
    {
        // Values saved by the earliest app versions had no envelope at all.
        // Must still decrypt as-is so those users aren't locked out.
        Assert.Equal("plain-old-string", SecretProtection.Decrypt("plain-old-string"));
    }

    [Fact]
    public void Decrypt_Empty_ReturnsEmpty()
    {
        Assert.Equal("", SecretProtection.Decrypt(""));
        Assert.Equal("", SecretProtection.Decrypt(null));
    }

    [Fact]
    public void Decrypt_CorruptedV2_ReturnsEmpty()
    {
        if (!DpapiAvailable) return;
        // Garbage after the v2 prefix must NOT throw -- callers depend on
        // empty return so the UI can prompt for re-entry instead of crashing.
        var plain = SecretProtection.Decrypt("dpapi:v2:!!!not-base64!!!");
        Assert.Equal("", plain);
    }

    [Fact]
    public void Decrypt_ForeignV2Ciphertext_ReturnsEmpty()
    {
        if (!DpapiAvailable) return;
        // Ciphertext encrypted with DIFFERENT entropy (or by a different
        // user account) must not decrypt -- returning empty is the safe
        // failure mode.
        var foreignBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes("foreign"),
            new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
            DataProtectionScope.CurrentUser);
        var fake = "dpapi:v2:" + Convert.ToBase64String(foreignBytes);

        Assert.Equal("", SecretProtection.Decrypt(fake));
    }

    // ── Byte envelope (MSAL cache, key store payloads) ───────────────

    [Fact]
    public void ProtectBytes_UnprotectBytes_RoundTrip_Succeeds()
    {
        if (!DpapiAvailable) return;
        var plain     = Encoding.UTF8.GetBytes("binary-msal-cache-payload");
        var protected_ = SecretProtection.ProtectBytes(plain);

        // v2 magic prefix is expected at the start
        Assert.Equal(0x57, protected_[0]);
        Assert.Equal(0x52, protected_[1]);
        Assert.Equal(0x02, protected_[2]);

        var restored = SecretProtection.UnprotectBytes(protected_);
        Assert.Equal(plain, restored);
    }

    [Fact]
    public void UnprotectBytes_LegacyV1Bytes_StillDecrypts()
    {
        if (!DpapiAvailable) return;
        // Simulate a v1 MSAL cache file: raw ProtectedData.Protect bytes
        // with no magic prefix. UnprotectBytes must recognise the absence
        // of magic and fall back to the unentropied legacy path.
        var plain = Encoding.UTF8.GetBytes("legacy-msal-cache");
        var v1Bytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);

        var restored = SecretProtection.UnprotectBytes(v1Bytes);
        Assert.Equal(plain, restored);
    }

    [Fact]
    public void UnprotectBytes_EmptyInput_Throws()
    {
        // Arguments must be non-null; zero-length input lands in the v1
        // fallback path which ProtectedData.Unprotect rejects -- that's
        // fine (caller gets an exception instead of silent corruption).
        Assert.Throws<ArgumentNullException>(() => SecretProtection.UnprotectBytes(null!));
    }

    // ── SecureString variant ─────────────────────────────────────────

    [Fact]
    public void DecryptToSecureString_V2_ReturnsPopulated()
    {
        if (!DpapiAvailable) return;
        const string secret = "secure-string-source";
        var cipher = SecretProtection.Encrypt(secret);

        using var ss = SecretProtection.DecryptToSecureString(cipher);
        Assert.NotNull(ss);
        Assert.Equal(secret.Length, ss.Length);
    }

    [Fact]
    public void DecryptToSecureString_LegacyV1_StillWorks()
    {
        if (!DpapiAvailable) return;
        const string secret = "legacy-secure-source";
        var plainBytes = Encoding.UTF8.GetBytes(secret);
        var v1Bytes    = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        var v1Envelope = "dpapi:" + Convert.ToBase64String(v1Bytes);

        using var ss = SecretProtection.DecryptToSecureString(v1Envelope);
        Assert.NotNull(ss);
        Assert.Equal(secret.Length, ss.Length);
    }

    // ── WithPlaintext: secret must not leak through a thrown exception ─

    [Fact]
    public void WithPlaintext_BodyThrowsQuotingSecret_RethrownMessageIsRedacted()
    {
        // Last line of defence: if the secret-consuming body throws an
        // exception that quotes the plaintext back (e.g. "invalid secret
        // 'abc'"), the rethrown exception the caller/log sees must have the
        // plaintext scrubbed to *** -- AppLogger's token-shape redaction
        // would not catch a bare opaque secret in a free-form message.
        const string secret = "Sup3rSecret_OpaqueValue";
        using var ss = SecretProtection.ToSecureString(secret);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SecretProtection.WithPlaintext<int>(ss, plain =>
                throw new Exception($"backend rejected secret '{plain}' at line 5")));

        Assert.DoesNotContain(secret, ex.Message);
        Assert.Contains("***", ex.Message);
    }

    [Fact]
    public void WithPlaintext_BodySucceeds_ReturnsValueAndExposesPlaintext()
    {
        // The happy path: the body receives the actual plaintext (so it can do
        // its job) and its return value is passed straight back.
        const string secret = "abc123";
        using var ss = SecretProtection.ToSecureString(secret);

        var length = SecretProtection.WithPlaintext(ss, plain => plain.Length);

        Assert.Equal(secret.Length, length);
    }

    [Fact]
    public void WithPlaintext_NullSecret_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => SecretProtection.WithPlaintext<int>(null!, _ => 0));
    }
}
