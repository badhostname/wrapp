using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Pins the security contract for how an Intune tenant's ClientSecret crosses
/// the bundle Config.json boundary. The bundle's Config.json is git-committed,
/// so the cardinal rule is: a secret value (fresh plaintext OR DPAPI cipher)
/// must NEVER be written there -- only the "ref:settings" sentinel, which the
/// loader normalises back to null (the real value is re-hydrated from the
/// DPAPI-encrypted settings.json separately).
/// <para>
/// The existing RoundTrip_IntuneTenant test covers the fresh-SecureString
/// disjunct; these cover the two branches it doesn't: a tenant whose only
/// secret is a stored CIPHER, and a tenant with no secret at all -- plus the
/// legacy in-bundle-plaintext load path.
/// </para>
/// </summary>
public class TenantSecretPersistenceTests
{
    private static IntuneTenantEntry Tenant() => new()
    {
        Key      = "PROD",
        Domain   = "contoso.com",
        ClientID = "abc-123",
        AuthFlow = AuthFlow.ClientSecret,
    };

    [Fact]
    public void Serialize_TenantWithStoredCipherOnly_WritesSentinelNeverTheCipher()
    {
        // The cipher-only branch: a tenant loaded from settings.json carries a
        // ClientSecretCipher but no freshly-typed ClientSecret. It still has a
        // secret, so the sentinel must be emitted -- and the cipher bytes must
        // NOT leak into the git-committed bundle file.
        const string cipher = "dpapi:v2:QUJDREVGMTIzNDU2Nzg5MA==";
        var model = new AppConfigModel();
        var t = Tenant();
        t.ClientSecretCipher = cipher;     // no fresh ClientSecret set
        model.IntuneTenants.Add(t);

        var json = ConfigFileService.SerializeToJson(model);

        Assert.Contains(ConfigFileService.ClientSecretSentinel, json);
        Assert.DoesNotContain(cipher, json);
        Assert.DoesNotContain("dpapi:", json);   // not even the prefix
    }

    [Fact]
    public void Serialize_TenantWithNoSecret_WritesEmptyNotSentinel()
    {
        // "No secret at all" must be distinguishable on disk from "secret lives
        // elsewhere" -- so a secret-less tenant must NOT emit the sentinel.
        var model = new AppConfigModel();
        model.IntuneTenants.Add(Tenant());     // no ClientSecret, no cipher

        var json = ConfigFileService.SerializeToJson(model);

        Assert.DoesNotContain(ConfigFileService.ClientSecretSentinel, json);
    }

    [Fact]
    public void Load_SentinelClientSecret_ResolvesToNull()
    {
        // The sentinel is bundle-internal bookkeeping; it must never survive
        // into the in-memory model as a literal "secret".
        var json = $$"""
            { "IntuneTenant": { "PROD": { "Domain": "c.com", "ClientSecret": "{{ConfigFileService.ClientSecretSentinel}}" } } }
            """;

        var t = Assert.Single(ConfigFileService.DeserializeFromJson(json).IntuneTenants);
        Assert.Null(t.ClientSecret);
    }

    [Fact]
    public void Load_LegacyInBundlePlaintext_IsWrappedAsSecureString()
    {
        // Rare legacy bundles carried a real plaintext ClientSecret in
        // Config.json. Those must still open (wrapped into a SecureString) so
        // the next save can re-encrypt them -- not silently dropped.
        var json = """
            { "IntuneTenant": { "PROD": { "Domain": "c.com", "ClientSecret": "legacy-plaintext-value" } } }
            """;

        var t = Assert.Single(ConfigFileService.DeserializeFromJson(json).IntuneTenants);
        Assert.NotNull(t.ClientSecret);
        Assert.Equal("legacy-plaintext-value".Length, t.ClientSecret!.Length);
    }
}
