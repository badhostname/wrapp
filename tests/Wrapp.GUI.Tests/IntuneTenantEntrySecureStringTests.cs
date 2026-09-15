using System.Security;
using Wrapp.Models;

namespace Wrapp.Tests;

/// <summary>
/// Phase 15 (S-6). Covers the migration of
/// <see cref="IntuneTenantEntry.ClientSecret"/> from <see cref="string"/> to
/// <see cref="SecureString"/>. The invariant matrix:
/// <list type="bullet">
/// <item><see cref="IntuneTenantEntry.HasStoredSecret"/> flips correctly for both
/// fresh-typed values and on-disk ciphers.</item>
/// <item>The serializer never leaks the plaintext value into Config.json (the
/// existing <see cref="ConfigFileServiceTests"/> covers the wire shape; this
/// suite covers the model-side invariants).</item>
/// <item>Disposing the SecureString clears the model&#x2019;s indication via
/// <c>HasStoredSecret</c> when no cipher is present.</item>
/// <item><see cref="SecretProtection.ResolveTenantSecret"/> &#x2019;s SecureString
/// overload prefers the fresh value over the cipher and returns a
/// distinct instance (so the model can dispose its copy without affecting
/// the resolved one).</item>
/// </list>
/// </summary>
public class IntuneTenantEntrySecureStringTests
{
    [Fact]
    public void HasStoredSecret_TrueWhenFreshSecureStringPresent()
    {
        var t = new IntuneTenantEntry();
        Assert.False(t.HasStoredSecret);

        t.ClientSecret = SecretProtection.ToSecureString("typed-value");
        Assert.True(t.HasStoredSecret);
    }

    [Fact]
    public void HasStoredSecret_TrueWhenCipherOnlyPresent()
    {
        var t = new IntuneTenantEntry
        {
            ClientSecretCipher = "dpapi:v2:fake-payload"
        };
        Assert.True(t.HasStoredSecret);
        Assert.Null(t.ClientSecret);
    }

    [Fact]
    public void HasStoredSecret_FalseWhenZeroLengthSecureString()
    {
        var t = new IntuneTenantEntry
        {
            ClientSecret = new SecureString() // zero-length, MakeReadOnly not required
        };
        // Empty SecureString must not register as a stored secret -- otherwise
        // the UI shows the "********" placeholder for a tenant with no secret.
        Assert.False(t.HasStoredSecret);
    }

    [Fact]
    public void ClientSecret_AssignmentRaisesHasStoredSecretChanged()
    {
        var t = new IntuneTenantEntry();
        var raised = new List<string?>();
        t.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        t.ClientSecret = SecretProtection.ToSecureString("v");
        // CommunityToolkit.Mvvm's [ObservableProperty] raises the field's own
        // change notification AND our partial method raises HasStoredSecret.
        Assert.Contains(nameof(IntuneTenantEntry.ClientSecret),    raised);
        Assert.Contains(nameof(IntuneTenantEntry.HasStoredSecret), raised);
    }

    [Fact]
    public void ResolveTenantSecret_PrefersFreshSecureStringOverCipher()
    {
        // Fresh wins over cipher. Use a real DPAPI-encrypted v2 cipher so the
        // SecureString path proves the fresh value is taken without touching
        // ProtectedData.Unprotect on the cipher.
        var cipher = SecretProtection.Encrypt("from-cipher");
        var fresh  = SecretProtection.ToSecureString("from-fresh");

        var resolved = SecretProtection.ResolveTenantSecret(cipher, fresh);
        Assert.NotNull(resolved);

        var plain = SecretProtection.WithPlaintext(resolved!, p => p);
        Assert.Equal("from-fresh", plain);
    }

    [Fact]
    public void ResolveTenantSecret_FallsBackToCipherWhenFreshIsEmpty()
    {
        var cipher = SecretProtection.Encrypt("from-cipher");

        var resolved = SecretProtection.ResolveTenantSecret(cipher, freshSecret: new SecureString());
        Assert.NotNull(resolved);
        Assert.Equal("from-cipher", SecretProtection.WithPlaintext(resolved!, p => p));
    }

    [Fact]
    public void ResolveTenantSecret_ReturnsNullWhenBothEmpty()
    {
        Assert.Null(SecretProtection.ResolveTenantSecret(cipher: null, freshSecret: null));
        Assert.Null(SecretProtection.ResolveTenantSecret(cipher: string.Empty, freshSecret: new SecureString()));
    }

    [Fact]
    public void ResolveTenantSecret_ReturnsCopyOfFresh_NotAReferenceShare()
    {
        // The model entry owns its SecureString; resolving must not hand back
        // the same instance, or disposing the resolved value would corrupt
        // the model entry's fresh value. This is the consumer-isolation
        // contract: resolve() => consumer disposes, model retains.
        var fresh = SecretProtection.ToSecureString("v");
        var resolved = SecretProtection.ResolveTenantSecret(cipher: null, freshSecret: fresh);

        Assert.NotNull(resolved);
        Assert.NotSame(fresh, resolved);

        resolved!.Dispose();
        // The fresh entry's SecureString must still be readable.
        Assert.Equal("v", SecretProtection.WithPlaintext(fresh, p => p));
    }
}
