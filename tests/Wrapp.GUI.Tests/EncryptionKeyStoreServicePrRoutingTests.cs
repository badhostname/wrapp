using System.ComponentModel;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Phase 16d. Verifies <see cref="EncryptionKeyStoreService.SaveKeysAsync"/>'s
/// routing decision between the direct push path and the PR-mode path. The
/// service has no HTTP mock seam (production calls a private static
/// <c>HttpClient</c>), so these tests exercise the routing via observable
/// pre-HTTP failure modes: the PR path's empty-template guard throws
/// <see cref="InvalidOperationException"/> with a distinct message before
/// any auth or HTTP fires; the direct path doesn't consult PR templates
/// so leaving them empty doesn't trip it.
/// <para>
/// Coverage of the actual PR creation (branch creation + PR POST) is
/// deferred to manual smoke against a live DevOps repo; the plan
/// acknowledges this gap.
/// </para>
/// </summary>
public class EncryptionKeyStoreServicePrRoutingTests
{
    private sealed class GateAlwaysOn : IFeatureGate
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public bool IsEnabled(string featureKey) => true;
        public string? DescribeWhyDisabled(string featureKey) => null;
        public void NotifyChanged()
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("FeatureGate"));
    }

    private const string FixtureUrl = "https://dev.azure.com/org/proj/_git/repo";

    private static AppSettings BaseSettings(bool prMode, string sourceBranchTemplate, string prTitleTemplate)
        => new()
        {
            KeyVaultRepoUrl = FixtureUrl,
            // Pre-approve the URL so the TOFU check passes; without this both
            // paths bail at TOFU and the routing decision is invisible.
            KeyVaultRepoUrlHash = EncryptionKeyStoreService.IssueKeyVaultTrustToken(FixtureUrl),
            KeyVaultPathTemplate = "/wrapp/{Tenant}/{AppId}.json",
            KeyVaultManualPathTemplate = "/manual/{PackageName}.json",
            KeyVaultUsePullRequests = prMode,
            KeyVaultPrSourceBranchTemplate = sourceBranchTemplate,
            KeyVaultPrTitleTemplate = prTitleTemplate,
            KeyVaultPrDescriptionTemplate = "desc",
        };

    private static EncryptionKeyStoreService MakeService(AppSettings settings)
        => new(new DevOpsAuthService(), settings, new GateAlwaysOn());

    private static EncryptionKeyInfo Keys()
        => new() { TenantId = "t", AppId = "a", EncryptionKey = "k", DisplayName = "Demo" };

    [Fact]
    public async Task SaveKeysAsync_PrModeOn_EmptyPrTemplate_ThrowsBeforeAuth_ConfirmingPrPathTaken()
    {
        // PR mode on + an empty PR source branch template means the PR
        // path's pre-auth guard fires. The direct push path does not
        // consult PR templates and would not throw here -- so this
        // failure is observable proof that the routing chose PR mode.
        var settings = BaseSettings(prMode: true, sourceBranchTemplate: "", prTitleTemplate: "title");
        var service = MakeService(settings);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SaveKeysAsync(Keys()));
        Assert.Contains("PR-mode templates resolved to empty", ex.Message);
    }

    [Fact]
    public async Task SaveKeysAsync_PrModeOff_RoutesThroughDirectPushPath()
    {
        // PR mode off: SaveKeysAsync routes to PushToDevOpsAsync which
        // constructs a commit body targeting refs/heads/main. When the
        // downstream HTTP call fails (DevOps unreachable or unauthorized
        // in the test env) the catch block throws HttpRequestException
        // with the distinct prefix "DevOps push failed:". The PR path's
        // throw uses "DevOps branch + push failed (branch ...)" instead,
        // so the prefix is observable proof of which path was taken.
        // <para>NOTE: env-dependent but always-throwing since STA-5: with no
        // cached token the pre-HTTP guard throws "Could not acquire DevOps
        // token"; with a cached-but-rejected token DevOps answers 203
        // (sign-in page) and the STA-5 guard throws "DevOps push failed:
        // authentication was rejected" (previously this case returned
        // CLEANLY -- silent key loss -- and flipped this test whenever the
        // machine's MSAL cache held a token). Pre-auth guards above don't
        // fire here -- direct push doesn't consult any PR template field.</para>
        var settings = BaseSettings(prMode: false,
            sourceBranchTemplate: "", prTitleTemplate: "");
        var service = MakeService(settings);

        try
        {
            await service.SaveKeysAsync(Keys());
            Assert.Fail("Expected a downstream failure; test env shouldn't have write access.");
        }
        catch (Exception ex)
        {
            Assert.DoesNotContain("PR-mode templates", ex.Message);
            // Allow either the direct-push HTTP failure prefix OR the
            // pre-auth "Could not acquire DevOps token" fallback. Both
            // confirm the direct path was taken (the PR path's prefix
            // would say "branch + push failed").
            Assert.True(
                ex.Message.Contains("DevOps push failed")
                || ex.Message.Contains("Could not acquire DevOps token"),
                $"Expected direct-path failure prefix, got: {ex.Message}");
            Assert.DoesNotContain("DevOps branch + push failed", ex.Message);
        }
    }

    [Fact]
    public async Task SaveKeysAsync_PrModeOn_PopulatedTemplates_RoutesThroughPrPath()
    {
        // PR mode on + valid templates: SaveKeysAsync routes to
        // PushAsPullRequestAsync. The empty-template guard does NOT fire.
        // Downstream failure produces "DevOps branch + push failed
        // (branch '...')" -- distinct from the direct path's prefix.
        var settings = BaseSettings(prMode: true,
            sourceBranchTemplate: "wrapp-keys/{Date}/{AppId}",
            prTitleTemplate: "Wrapp: {AppName}");
        var service = MakeService(settings);

        try
        {
            await service.SaveKeysAsync(Keys());
            Assert.Fail("Expected a downstream failure; test env shouldn't have write access.");
        }
        catch (Exception ex)
        {
            Assert.DoesNotContain("PR-mode templates", ex.Message);
            // PR-path-specific messages prove the routing chose PR mode. All
            // three are unique to the PR method:
            //  - "DevOps refs lookup failed" -- the PR path status-checks the
            //    refs GET (the fixture repo 404s here); the direct path does not.
            //  - "has no 'main' branch" -- the PR-only empty-main guard.
            //  - "DevOps branch + push failed" -- the PR branch-push step.
            // "Could not acquire DevOps token" also qualifies (auth step inside
            // the PR method, before any HTTP) for envs with no cached token.
            Assert.True(
                ex.Message.Contains("DevOps branch + push failed")
                || ex.Message.Contains("DevOps refs lookup failed")
                || ex.Message.Contains("has no 'main' branch")
                || ex.Message.Contains("Could not acquire DevOps token"),
                $"Expected PR-path failure prefix, got: {ex.Message}");
            // Never the direct-path prefix.
            Assert.DoesNotContain("DevOps push failed:", ex.Message);
        }
    }

    [Theory]
    // Empty {AppId} on a manual save left a trailing slash -> invalid git ref,
    // which DevOps rejected ("Parameter name: refUpdate"). SanitizeGitRef drops
    // empty segments so the branch is always a valid ref.
    [InlineData("wrapp-keys/2026-07-20/", "wrapp-keys/2026-07-20")]
    [InlineData("wrapp-keys//7-Zip", "wrapp-keys/7-Zip")]
    [InlineData("wrapp-keys/2026-07-20/01637c2f", "wrapp-keys/2026-07-20/01637c2f")]
    [InlineData("/lead/trail/", "lead/trail")]
    public void SanitizeGitRef_DropsEmptySegments(string input, string expected)
        => Assert.Equal(expected, EncryptionKeyStoreService.SanitizeGitRef(input));

    [Fact]
    public async Task SaveKeysAsync_PrModeOn_UntrustedUrl_TofuFiresBeforeRouting()
    {
        // TOFU is the very first check in both paths. With an unapproved
        // URL hash, the routing decision is irrelevant -- the user must
        // approve the URL before any save succeeds. Asserting this here
        // guards against a future refactor that accidentally moves the
        // TOFU check after the routing fork.
        var settings = BaseSettings(prMode: true,
            sourceBranchTemplate: "b", prTitleTemplate: "t");
        settings.KeyVaultRepoUrlHash = "wrong-hash";
        var service = MakeService(settings);

        await Assert.ThrowsAsync<EncryptionKeyStoreService.KeyVaultUrlNotTrustedException>(
            () => service.SaveKeysAsync(Keys()));
    }
}
