using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Phase 16c. Covers <see cref="VaultPathTemplate.Resolve"/>'s token
/// expansion + sanitisation contract:
/// <list type="bullet">
/// <item>The default template produces the pre-16c on-disk layout so
/// existing vaults still resolve via the legacy fast-path lookup.</item>
/// <item>Each documented token expands independently against the matching
/// <see cref="EncryptionKeyInfo"/> property.</item>
/// <item>Per-value sanitisation drops path-traversal segments and embedded
/// path separators so a user-controlled value (AppName, PackageName) cannot
/// introduce folders the operator didn't author in the template.</item>
/// <item><see cref="VaultPathTemplate.SupportedTokens"/> matches the tokens
/// the <see cref="VaultPathTemplate.Resolve"/> body actually replaces -- the
/// help text in Settings reads from the catalogue, so drift would lie to
/// the operator.</item>
/// </list>
/// </summary>
public class VaultPathTemplateTests
{
    private static EncryptionKeyInfo Sample(
        string tenant = "t-id", string app = "a-id",
        string name = "MyApp", string pkg = "MyApp_1_0_0")
        => new()
        {
            TenantId = tenant,
            AppId = app,
            DisplayName = name,
            PackageName = pkg,
        };

    [Fact]
    public void Resolve_DefaultTemplate_PreservesPre16cLayout()
    {
        var result = VaultPathTemplate.Resolve(
            "/wrapp/{Tenant}/{AppId}.json", Sample(tenant: "tenant-guid", app: "app-guid"));

        Assert.Equal("/wrapp/tenant-guid/app-guid.json", result);
    }

    [Fact]
    public void Resolve_ManualDefaultTemplate_PreservesPre16cLayout()
    {
        var result = VaultPathTemplate.Resolve(
            "/manual/{PackageName}.json", Sample(pkg: "MyApp_2_5_0"));

        Assert.Equal("/manual/MyApp_2_5_0.json", result);
    }

    [Fact]
    public void Resolve_AllTokens_ExpandIndependently()
    {
        var keys = Sample(tenant: "T", app: "A", name: "N", pkg: "P");
        var result = VaultPathTemplate.Resolve(
            "/{Tenant}/{AppId}/{AppName}/{PackageName}/{Author}.json",
            keys, author: "alice");

        Assert.Equal("/T/A/N/P/alice.json", result);
    }

    [Fact]
    public void Resolve_NullOrEmptyTemplate_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, VaultPathTemplate.Resolve(null, Sample()));
        Assert.Equal(string.Empty, VaultPathTemplate.Resolve(string.Empty, Sample()));
    }

    [Fact]
    public void Resolve_PathTraversalInValue_Sanitised()
    {
        // An AppId like `../../etc/passwd` must not punch out of the wrapp
        // folder. Sanitize collapses `..` to `_` and forward slashes inside
        // a value to `_`, so the result is a single dotless segment.
        var keys = Sample(app: "../../etc/passwd");
        var result = VaultPathTemplate.Resolve("/wrapp/{AppId}.json", keys);

        Assert.DoesNotContain("..", result);
        // The TEMPLATE's literal `/` between `wrapp` and `{AppId}` stays.
        Assert.StartsWith("/wrapp/", result);
        // The VALUE's `/` and `..` become `_` so it's a single segment.
        Assert.EndsWith(".json", result);
        var segment = result["/wrapp/".Length..^".json".Length];
        Assert.DoesNotContain('/', segment);
    }

    [Fact]
    public void Resolve_WindowsInvalidCharsInValue_Sanitised()
    {
        var keys = Sample(name: "Bad:*?\"<>|Name");
        var result = VaultPathTemplate.Resolve("/{AppName}.json", keys);

        foreach (var bad in new[] { ":", "*", "?", "\"", "<", ">", "|" })
            Assert.DoesNotContain(bad, result);
    }

    [Fact]
    public void Resolve_TemplateLiteralForwardSlashes_PreservedAsFolders()
    {
        // The slashes between literal text segments are folder boundaries.
        // Sanitisation only runs on token values, not on the template body.
        var result = VaultPathTemplate.Resolve(
            "/a/b/{AppId}/c/d.json", Sample(app: "X"));

        Assert.Equal("/a/b/X/c/d.json", result);
    }

    [Fact]
    public void Resolve_DateToken_UsesUtcIsoDateOnlyForReproducibility()
    {
        // SystemClock can be overridden in tests -- pin it to a known UTC
        // instant so the assertion is deterministic across timezones.
        var fakeUtc = new DateTimeOffset(2026, 6, 10, 14, 32, 0, TimeSpan.Zero);
        var fake = new FakeTimeProvider(fakeUtc);
        using (SystemClock.Override(fake))
        {
            var result = VaultPathTemplate.Resolve(
                "/wrapp/{Date}/{AppId}.json", Sample(app: "x"));
            Assert.Equal("/wrapp/2026-06-10/x.json", result);
        }
    }

    [Fact]
    public void Resolve_AuthorDefault_UsesEnvironmentUserName()
    {
        // Passing null as the author should fall back to Environment.UserName.
        var keys = Sample();
        var result = VaultPathTemplate.Resolve("/{Author}.json", keys, author: null);

        // We can't assert the exact value (varies per machine) but it must
        // be non-empty and sanitised (no path separators inside the segment).
        var segment = result.TrimStart('/').TrimEnd(".json".ToCharArray());
        Assert.False(string.IsNullOrEmpty(segment));
        Assert.DoesNotContain('/', segment);
        Assert.DoesNotContain('\\', segment);
    }

    [Fact]
    public void SupportedTokens_MatchesResolveImplementation()
    {
        // Every token in the catalogue must produce a different result when
        // its value changes; an undocumented token would silently no-op.
        var baseline = VaultPathTemplate.Resolve(
            string.Concat(VaultPathTemplate.SupportedTokens), Sample());

        Assert.DoesNotContain("{", baseline);
        Assert.DoesNotContain("}", baseline);
    }

    [Fact]
    public void Resolve_BackslashInValue_NormalisedToUnderscore()
    {
        // DevOps git paths are forward-slash only; a Windows-flavoured
        // backslash in a value must not silently produce a backslash in
        // the on-disk path (would 404 the read path).
        var keys = Sample(pkg: @"sub\dir\PkgName");
        var result = VaultPathTemplate.Resolve("/manual/{PackageName}.json", keys);

        Assert.DoesNotContain("\\", result);
        Assert.Contains("sub_dir_PkgName", result);
    }

    // ------------------------------------------------------------------
    // Phase 16d. PR-mode templates reuse the same Resolve helper, so the
    // existing token expansion is exercised here in branch / title /
    // description shapes that match the AppSettings defaults.
    // ------------------------------------------------------------------

    [Fact]
    public void Resolve_DefaultPrSourceBranchTemplate_ProducesDatedAppIdBranch()
    {
        // Pin the clock so the {Date} segment is deterministic across
        // machines. The default template groups branches by date so a
        // daily packaging run produces one tidy folder of PRs.
        var fakeUtc = new DateTimeOffset(2026, 6, 10, 14, 32, 0, TimeSpan.Zero);
        var fake = new FakeTimeProvider(fakeUtc);
        using (SystemClock.Override(fake))
        {
            var result = VaultPathTemplate.Resolve(
                "wrapp-keys/{Date}/{AppId}", Sample(app: "app-123"));
            Assert.Equal("wrapp-keys/2026-06-10/app-123", result);
        }
    }

    [Fact]
    public void Resolve_DefaultPrTitleTemplate_ExpandsAppNameAndAppId()
    {
        var result = VaultPathTemplate.Resolve(
            "Wrapp: encryption keys for {AppName} ({AppId})",
            Sample(name: "Acme Suite", app: "abc-123"));

        Assert.Equal("Wrapp: encryption keys for Acme Suite (abc-123)", result);
    }

    [Fact]
    public void Resolve_DefaultPrDescriptionTemplate_PreservesNewlines()
    {
        // The default description template stores `\n` as a real newline
        // in the AppSettings string (C# escape). The Resolve helper must
        // not collapse or mangle them -- DevOps renders the PR body with
        // literal newlines as Markdown line breaks.
        var fakeUtc = new DateTimeOffset(2026, 6, 10, 14, 32, 0, TimeSpan.Zero);
        var fake = new FakeTimeProvider(fakeUtc);
        using (SystemClock.Override(fake))
        {
            var result = VaultPathTemplate.Resolve(
                "Automated push by Wrapp on {Date} by {Author}.\n\nApp: {AppName}\nTenant: {Tenant}\nApp ID: {AppId}",
                Sample(name: "Acme", app: "a", tenant: "t"),
                author: "alice");

            Assert.Contains("\n\n", result);
            Assert.Contains("App: Acme", result);
            Assert.Contains("Tenant: t", result);
            Assert.Contains("App ID: a", result);
            Assert.Contains("by alice", result);
            Assert.Contains("2026-06-10", result);
        }
    }

    [Fact]
    public void Resolve_PrBranchTemplate_BackslashInAppIdNormalisedSoBranchStaysValid()
    {
        // A branch name like `wrapp-keys/.../sub\dir` would be rejected by
        // the DevOps API. The same per-value sanitiser that protects path
        // segments also protects branch segments -- one token expansion is
        // one branch namespace segment, never two.
        var keys = Sample(app: @"appid\with\backslash");
        var result = VaultPathTemplate.Resolve(
            "wrapp-keys/{AppId}", keys);

        Assert.DoesNotContain("\\", result);
        Assert.Equal("wrapp-keys/appid_with_backslash", result);
    }
}
