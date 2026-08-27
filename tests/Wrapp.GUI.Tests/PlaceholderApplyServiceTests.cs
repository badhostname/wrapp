using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Workstream P (P3): the shared replace-in-place core — report aggregation,
/// confirm-gating (decision path factored so it is testable without UI), and
/// the explicit per-scope field maps (every mapped property round-trips
/// through the resolver).
/// </summary>
// Serialized with the other placeholder test classes: both swap the static
// PlaceholderService.CustomsSource hook, and parallel classes cross-talk.
[Collection("Placeholders")]
public class PlaceholderApplyServiceTests : IDisposable
{
    private static readonly AppSection App = new()
    {
        Company = "Contoso", Name = "TestApp", DotVersion = "1.2.3", Version = "1_2_3",
    };

    public PlaceholderApplyServiceTests()
    {
        PlaceholderService.CustomsSource = () => Array.Empty<(string, string?, bool)>();
    }

    public void Dispose()
    {
        PlaceholderService.CustomsSource = () => Array.Empty<(string, string?, bool)>();
    }

    private static void SetCustoms(params (string Name, string? Value, bool Sensitive)[] customs)
        => PlaceholderService.CustomsSource = () => customs;

    /// <summary>A settable fake field for exercising the core without any model.</summary>
    private sealed class FakeField
    {
        public string Value;
        public readonly PlaceholderFieldAccessor Accessor;
        public FakeField(string label, string value)
        {
            Value = value;
            Accessor = new PlaceholderFieldAccessor(label, () => Value, v => Value = v);
        }
    }

    // ------------------------------------------------------------------
    // Aggregation
    // ------------------------------------------------------------------

    [Fact]
    public void ComputeChanges_AggregatesAcrossFields_AndDeduplicatesNames()
    {
        SetCustoms(("grp", "gid-1", false), ("emptyone", "", false));
        var f1 = new FakeField("f1", "a={{grp}} b={{grp}} c={{emptyone}} d={{nope}}");
        var f2 = new FakeField("f2", "x={{Name}} y={{emptyone}} z={{nope}}");
        var f3 = new FakeField("f3", "no tokens here");
        var f4 = new FakeField("f4", "");

        var changes = PlaceholderApplyService.ComputeChanges(
            new[] { f1.Accessor, f2.Accessor, f3.Accessor, f4.Accessor }, App, out var report);

        Assert.Equal(3, report.Replaced);                     // grp x2 + Name x1
        Assert.Equal(new[] { "emptyone" }, report.LeftEmpty); // deduped across fields
        Assert.Equal(new[] { "nope" }, report.LeftUnknown);   // deduped across fields
        Assert.False(report.TouchedSensitive);

        // Only the fields whose text actually changes are in the change set.
        Assert.Equal(2, changes.Count);
        Assert.Equal("a=gid-1 b=gid-1 c={{emptyone}} d={{nope}}", changes[0].NewValue);
        Assert.Equal("x=TestApp y={{emptyone}} z={{nope}}", changes[1].NewValue);

        // Pure: nothing mutated yet.
        Assert.StartsWith("a={{grp}}", f1.Value);
    }

    [Fact]
    public void ComputeChanges_ReportsSensitiveTouches()
    {
        SetCustoms(("secretid", "s3cret", true));
        var f = new FakeField("f", "id={{secretid}}");
        PlaceholderApplyService.ComputeChanges(new[] { f.Accessor }, App, out var report);
        Assert.True(report.TouchedSensitive);
        Assert.Contains("secretid", report.SensitiveReplaced);
    }

    // ------------------------------------------------------------------
    // Summary text (what the confirm dialog shows)
    // ------------------------------------------------------------------

    [Fact]
    public void SummaryMarkdown_ListsCountsBucketsAndSensitiveWarning()
    {
        var report = new PlaceholderExpandReport { Replaced = 5 };
        report.LeftEmpty.Add("pilot");
        report.LeftUnknown.Add("mystery");
        report.SensitiveReplaced.Add("vaultpath");

        var text = PlaceholderApplyService.BuildSummaryMarkdown(report, changedFieldCount: 3);

        Assert.Contains("**5**", text);
        Assert.Contains("**3**", text);
        Assert.Contains("pilot", text);
        Assert.Contains("mystery", text);
        // The sensitive warning is bold and names the placeholder.
        Assert.Contains("**Warning", text);
        Assert.Contains("vaultpath", text);
        Assert.Contains("plaintext", text);
    }

    [Fact]
    public void SummaryMarkdown_OmitsSensitiveWarning_WhenNothingSensitive()
    {
        var report = new PlaceholderExpandReport { Replaced = 1 };
        var text = PlaceholderApplyService.BuildSummaryMarkdown(report, 1);
        Assert.DoesNotContain("Warning", text);
    }

    [Fact]
    public void NothingToReplaceMessage_ExplainsWhy()
    {
        var report = new PlaceholderExpandReport();
        report.LeftEmpty.Add("pilot");
        report.LeftUnknown.Add("mystery");
        var text = PlaceholderApplyService.BuildNothingToReplaceMessage(report);
        Assert.Contains("{{pilot}}", text);
        Assert.Contains("{{mystery}}", text);
    }

    // ------------------------------------------------------------------
    // Confirm gating: dialog BEFORE mutation; decline = no mutation
    // ------------------------------------------------------------------

    [Fact]
    public async Task Apply_MutatesOnlyAfterConfirm()
    {
        SetCustoms(("grp", "gid-9", false));
        var f = new FakeField("f", "id={{grp}}");
        string? seenBody = null;

        var applied = await PlaceholderApplyService.ApplyAsync(
            "test", new[] { f.Accessor }, App,
            confirm: (_, body) =>
            {
                seenBody = body;
                // The dialog fires BEFORE any mutation.
                Assert.Equal("id={{grp}}", f.Value);
                return Task.FromResult(true);
            });

        Assert.True(applied);
        Assert.Equal("id=gid-9", f.Value);
        Assert.Contains("**1**", seenBody);
    }

    [Fact]
    public async Task Apply_Declined_LeavesEverythingUntouched()
    {
        SetCustoms(("grp", "gid-9", false));
        var f = new FakeField("f", "id={{grp}}");

        var applied = await PlaceholderApplyService.ApplyAsync(
            "test", new[] { f.Accessor }, App,
            confirm: (_, _) => Task.FromResult(false));

        Assert.False(applied);
        Assert.Equal("id={{grp}}", f.Value);
    }

    [Fact]
    public async Task Apply_NothingToReplace_ShowsInfoNotConfirm()
    {
        var f = new FakeField("f", "no tokens, and {{unknownname}} stays");
        var confirmCalled = false;
        var infoCalled = false;

        var applied = await PlaceholderApplyService.ApplyAsync(
            "test", new[] { f.Accessor }, App,
            confirm: (_, _) => { confirmCalled = true; return Task.FromResult(true); },
            info: (_, body) =>
            {
                infoCalled = true;
                Assert.Contains("{{unknownname}}", body);
                return Task.CompletedTask;
            });

        Assert.False(applied);
        Assert.False(confirmCalled);
        Assert.True(infoCalled);
        Assert.Equal("no tokens, and {{unknownname}} stays", f.Value);
    }

    // ------------------------------------------------------------------
    // Per-scope field maps: every mapped property round-trips through the
    // resolver; excluded fields stay untouched.
    // ------------------------------------------------------------------

    private const string Token = "pre-{{scopeval}}-post";
    private const string Expanded = "pre-RESOLVED-post";

    private static Task<bool> AlwaysConfirm(string t, string b) => Task.FromResult(true);

    [Fact]
    public async Task GeneralMap_CoversAllStringFields_AndSkipsExclusions()
    {
        SetCustoms(("scopeval", "RESOLVED", false));
        var app = new AppSection
        {
            Company = Token, Name = Token, Comment = Token, Language = Token,
            EXEFile = Token, MSIFile = Token, URL = Token, DotVersion = Token, Version = Token,
            GUID = Token, IconFile = Token, ScriptFramework = Token,
        };

        await PlaceholderApplyService.ApplyAsync(
            "General", PlaceholderApplyService.GeneralFields(app), app, AlwaysConfirm);

        Assert.Equal(Expanded, app.Company);
        Assert.Equal(Expanded, app.Name);
        Assert.Equal(Expanded, app.Comment);
        Assert.Equal(Expanded, app.Language);
        Assert.Equal(Expanded, app.EXEFile);
        Assert.Equal(Expanded, app.MSIFile);
        Assert.Equal(Expanded, app.URL);
        Assert.Equal(Expanded, app.DotVersion);
        Assert.Equal(Expanded, app.Version);
        // Excluded on purpose: identity, icon path, framework selector.
        Assert.Equal(Token, app.GUID);
        Assert.Equal(Token, app.IconFile);
        Assert.Equal(Token, app.ScriptFramework);
    }

    [Fact]
    public async Task GeneralMap_CoversTableRows()
    {
        SetCustoms(("scopeval", "RESOLVED", false));
        var app = new AppSection();
        app.DetectRunning.Add(new DetectRunningEntry
            { DisplayName = Token, ExeFileName = Token, Process = Token });
        app.Dependencies.Add(Token);

        await PlaceholderApplyService.ApplyAsync(
            "General", PlaceholderApplyService.GeneralFields(app), app, AlwaysConfirm);

        Assert.Equal(Expanded, app.DetectRunning[0].DisplayName);
        Assert.Equal(Expanded, app.DetectRunning[0].ExeFileName);
        Assert.Equal(Expanded, app.DetectRunning[0].Process);
        Assert.Equal(Expanded, app.Dependencies[0]);
    }

    [Fact]
    public async Task IntuneMap_CoversTableRows()
    {
        SetCustoms(("scopeval", "RESOLVED", false));
        var pkg = new IntunePackageEntry();
        pkg.Categories.Add(new TagEntry { Name = Token });
        pkg.ScopeTags.Add(new TagEntry { Name = Token });
        pkg.Dependencies.Add(new DependencyEntry { AppName = Token });
        pkg.Supersedence.Add(new SupersedenceEntry { AppName = Token });

        await PlaceholderApplyService.ApplyAsync(
            "Intune", PlaceholderApplyService.IntunePackageFields(pkg), new AppSection(), AlwaysConfirm);

        Assert.Equal(Expanded, pkg.Categories[0].Name);
        Assert.Equal(Expanded, pkg.ScopeTags[0].Name);
        Assert.Equal(Expanded, pkg.Dependencies[0].AppName);
        Assert.Equal(Expanded, pkg.Supersedence[0].AppName);
    }

    [Fact]
    public async Task SccmMap_CoversTableRows()
    {
        SetCustoms(("scopeval", "RESOLVED", false));
        var pkg = new SCCMPackageEntry();
        pkg.InstallBehaviors.Add(new InstallBehaviorEntry { ExeFileName = Token, DisplayName = Token });
        pkg.Dependencies.Add(new DependencyEntry { AppName = Token });
        pkg.Supersedence.Add(new SupersedenceEntry { AppName = Token });

        await PlaceholderApplyService.ApplyAsync(
            "SCCM", PlaceholderApplyService.SccmPackageFields(pkg), new AppSection(), AlwaysConfirm);

        Assert.Equal(Expanded, pkg.InstallBehaviors[0].ExeFileName);
        Assert.Equal(Expanded, pkg.InstallBehaviors[0].DisplayName);
        Assert.Equal(Expanded, pkg.Dependencies[0].AppName);
        Assert.Equal(Expanded, pkg.Supersedence[0].AppName);
    }

    [Fact]
    public async Task IntuneMap_CoversPackageAndAssignmentFields()
    {
        SetCustoms(("scopeval", "RESOLVED", false));
        var pkg = new IntunePackageEntry
        {
            AppName = Token, Comment = Token, InstallCommand = Token,
            UninstallCommand = Token, Developer = Token, Owner = Token,
            InformationURL = Token, PrivacyURL = Token, PackageOption = Token,
            ExistingAppID = Token,
            IconFile = Token, // excluded: icon path
        };
        var assignment = new AssignmentEntry
        {
            AppName = Token, GroupID = Token, FilterName = Token,
            AvailableTime = Token, DeadlineTime = Token, Label = Token,
            PackageId = "keep-me", // excluded: internal link id
        };
        pkg.Assignments.Add(assignment);

        await PlaceholderApplyService.ApplyAsync(
            "Intune", PlaceholderApplyService.IntunePackageFields(pkg), App, AlwaysConfirm);

        Assert.Equal(Expanded, pkg.AppName);
        Assert.Equal(Expanded, pkg.Comment);
        Assert.Equal(Expanded, pkg.InstallCommand);
        Assert.Equal(Expanded, pkg.UninstallCommand);
        Assert.Equal(Expanded, pkg.Developer);
        Assert.Equal(Expanded, pkg.Owner);
        Assert.Equal(Expanded, pkg.InformationURL);
        Assert.Equal(Expanded, pkg.PrivacyURL);
        Assert.Equal(Expanded, pkg.PackageOption);
        Assert.Equal(Expanded, pkg.ExistingAppID);
        Assert.Equal(Token, pkg.IconFile);

        Assert.Equal(Expanded, assignment.AppName);
        Assert.Equal(Expanded, assignment.GroupID);
        Assert.Equal(Expanded, assignment.FilterName);
        Assert.Equal(Expanded, assignment.AvailableTime);
        Assert.Equal(Expanded, assignment.DeadlineTime);
        Assert.Equal(Expanded, assignment.Label);
        Assert.Equal("keep-me", assignment.PackageId);
    }

    [Fact]
    public async Task SccmMap_CoversPackageAndDeploymentFields()
    {
        SetCustoms(("scopeval", "RESOLVED", false));
        var pkg = new SCCMPackageEntry
        {
            AppName = Token, AppComment = Token, Publisher = Token,
            SoftwareVersion = Token, Owner = Token, SupportContact = Token,
            Description = Token, ReleaseDate = Token, LocalizedName = Token,
            LocalizedDescription = Token, Keywords = Token, PrivacyUrl = Token,
            UserDocumentation = Token, LinkText = Token, Name = Token,
            Comment = Token, PackageOption = Token, InstallCommand = Token,
            UninstallCommand = Token, RepairCommand = Token,
            Icon = Token,           // excluded: icon path
            SiteCode = "CB1",       // excluded: site key
        };
        var deployment = new SCCMDeploymentEntry
        {
            AppName = Token, Collection = Token, Label = Token, Comment = Token,
            PackageId = "keep-me",  // excluded: internal link id
        };
        pkg.Deployments.Add(deployment);

        await PlaceholderApplyService.ApplyAsync(
            "SCCM", PlaceholderApplyService.SccmPackageFields(pkg), App, AlwaysConfirm);

        Assert.Equal(Expanded, pkg.AppName);
        Assert.Equal(Expanded, pkg.AppComment);
        Assert.Equal(Expanded, pkg.Publisher);
        Assert.Equal(Expanded, pkg.SoftwareVersion);
        Assert.Equal(Expanded, pkg.Owner);
        Assert.Equal(Expanded, pkg.SupportContact);
        Assert.Equal(Expanded, pkg.Description);
        Assert.Equal(Expanded, pkg.ReleaseDate);
        Assert.Equal(Expanded, pkg.LocalizedName);
        Assert.Equal(Expanded, pkg.LocalizedDescription);
        Assert.Equal(Expanded, pkg.Keywords);
        Assert.Equal(Expanded, pkg.PrivacyUrl);
        Assert.Equal(Expanded, pkg.UserDocumentation);
        Assert.Equal(Expanded, pkg.LinkText);
        Assert.Equal(Expanded, pkg.Name);
        Assert.Equal(Expanded, pkg.Comment);
        Assert.Equal(Expanded, pkg.PackageOption);
        Assert.Equal(Expanded, pkg.InstallCommand);
        Assert.Equal(Expanded, pkg.UninstallCommand);
        Assert.Equal(Expanded, pkg.RepairCommand);
        Assert.Equal(Token, pkg.Icon);
        Assert.Equal("CB1", pkg.SiteCode);

        Assert.Equal(Expanded, deployment.AppName);
        Assert.Equal(Expanded, deployment.Collection);
        Assert.Equal(Expanded, deployment.Label);
        Assert.Equal(Expanded, deployment.Comment);
        Assert.Equal("keep-me", deployment.PackageId);
    }

    [Fact]
    public async Task BuiltIns_ResolveFromTheActiveAppSection_NotTheMutatedOne()
    {
        // {{Name}} inside App.Company must resolve to the PRE-apply Name even
        // though Name itself is also being replaced in the same pass — all
        // values are computed before any setter runs.
        SetCustoms(("scopeval", "RESOLVED", false));
        var app = new AppSection { Name = "{{scopeval}}", Company = "by {{Name}}" };

        await PlaceholderApplyService.ApplyAsync(
            "General", PlaceholderApplyService.GeneralFields(app), app, AlwaysConfirm);

        Assert.Equal("RESOLVED", app.Name);
        Assert.Equal("by {{scopeval}}", app.Company);
    }
}
