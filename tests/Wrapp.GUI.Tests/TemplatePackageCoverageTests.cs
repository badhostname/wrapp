using System.IO;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Package-template coverage &amp; placeholder expansion
/// (docs/package-template-coverage-plan.md). Contracts under test:
/// entry collections (return codes, scope tags, categories, dependencies,
/// supersedence, install behaviors) round-trip through templates; every
/// string a template writes expands placeholders on APPLY while the saved
/// file stays verbatim; AppName travels only when explicitly chosen; keys
/// absent from a template leave the target untouched.
/// </summary>
// These tests touch TWO process-wide statics: TemplateService.TemplateDir
// (shared with TemplateServiceSaveTests) and — via ApplyPackageTemplate →
// PlaceholderService.Expand — the CustomsSource the placeholder tests swap.
// Everything touching either static lives in the single "Placeholders"
// collection; parallel runs produced intermittent failures in both directions.
[Collection("Placeholders")]
public sealed class TemplatePackageCoverageTests : IDisposable
{
    private readonly string _originalDir;
    private readonly string _originalPackDir;
    private readonly string _tempDir;

    public TemplatePackageCoverageTests()
    {
        _originalDir = TemplateService.TemplateDir;
        _originalPackDir = TemplateService.OrgTemplatePackDir;
        var baseDir = Path.Combine(Path.GetTempPath(), "WrappTests", Guid.NewGuid().ToString("N"));
        _tempDir = Path.Combine(baseDir, "Templates");
        Directory.CreateDirectory(_tempDir);
        TemplateService.TemplateDir = _tempDir;
        TemplateService.OrgTemplatePackDir = Path.Combine(baseDir, "org-templates");
    }

    public void Dispose()
    {
        TemplateService.TemplateDir = _originalDir;
        TemplateService.OrgTemplatePackDir = _originalPackDir;
        try { Directory.Delete(Path.GetDirectoryName(_tempDir)!, recursive: true); } catch { }
    }

    private static AppSection App => new() { Company = "Contoso", Name = "7zip", Version = "24.09" };

    private static IntunePackageEntry FullIntunePackage()
    {
        var pkg = new IntunePackageEntry { AppName = "{{Company}} {{Name}}", Developer = "{{Company}}" };
        pkg.CustomReturnCodes.Add(new ReturnCodeEntry { ReturnCode = 3010, Type = "softReboot" });
        pkg.ScopeTags.Add(new TagEntry { Name = "{{Company}}-Workstations" });
        pkg.Categories.Add(new TagEntry { Name = "Productivity" });
        pkg.Dependencies.Add(new DependencyEntry { AppName = "{{Company}} Runtime", AutoInstall = true });
        pkg.Supersedence.Add(new SupersedenceEntry { AppName = "{{Name}} (old)", SupersedenceType = "Replace", UninstallOldApp = true });
        return pkg;
    }

    // ------------------------------------------------------------------
    // Field choices: name + collections are offered
    // ------------------------------------------------------------------

    [Fact]
    public void FieldChoices_OfferAppName_UncheckedByDefault()
    {
        var choice = Assert.Single(
            TemplateService.GetPackageTemplateFields(new IntunePackageEntry { AppName = "X" }),
            f => f.Name == "AppName");
        Assert.False(choice.Checked);
    }

    [Fact]
    public void FieldChoices_OfferCollections_WithItemCountDisplay()
    {
        var fields = TemplateService.GetPackageTemplateFields(FullIntunePackage());

        foreach (var name in new[] { "CustomReturnCodes", "ScopeTags", "Categories", "Dependencies", "Supersedence" })
        {
            var choice = Assert.Single(fields, f => f.Name == name);
            Assert.Equal("1 item", choice.Value);
            Assert.False(choice.Checked);   // collections are opt-in like metadata
        }
    }

    // ------------------------------------------------------------------
    // Save: verbatim, collections serialized, UI state excluded
    // ------------------------------------------------------------------

    [Fact]
    public void Save_KeepsPlaceholdersVerbatim_AndSerializesCollections()
    {
        var info = TemplateService.SavePackageTemplate(
            "Intune", "Full Coverage", "test", FullIntunePackage(),
            new[] { "AppName", "Developer", "CustomReturnCodes", "ScopeTags", "Categories", "Dependencies", "Supersedence" });

        var raw = File.ReadAllText(info.FilePath);
        Assert.Contains("{{Company}} {{Name}}", raw);       // verbatim, not expanded on save
        Assert.Contains("3010", raw);
        Assert.Contains("softReboot", raw);
        Assert.Contains("{{Company}}-Workstations", raw);
        Assert.DoesNotContain("IsSelected", raw);           // UI-only state never ships
    }

    // ------------------------------------------------------------------
    // Apply: expansion + collection replace + sparse semantics
    // ------------------------------------------------------------------

    [Fact]
    public void Apply_ExpandsPlaceholders_InScalarsNameAndCollectionEntries()
    {
        var info = TemplateService.SavePackageTemplate(
            "Intune", "Expansion", "test", FullIntunePackage(),
            new[] { "AppName", "Developer", "ScopeTags", "Dependencies", "Supersedence" });

        var target = new IntunePackageEntry { AppName = "untouched" };
        TemplateService.ApplyPackageTemplate(info, target, App);

        Assert.Equal("Contoso 7zip", target.AppName);
        Assert.Equal("Contoso", target.Developer);
        Assert.Equal("Contoso-Workstations", Assert.Single(target.ScopeTags).Name);
        var dep = Assert.Single(target.Dependencies);
        Assert.Equal("Contoso Runtime", dep.AppName);
        Assert.True(dep.AutoInstall);
        var sup = Assert.Single(target.Supersedence);
        Assert.Equal("7zip (old)", sup.AppName);
        Assert.Equal("Replace", sup.SupersedenceType);
        Assert.True(sup.UninstallOldApp);
    }

    [Fact]
    public void Apply_RoundTripsReturnCodesAndCategories()
    {
        var info = TemplateService.SavePackageTemplate(
            "Intune", "Codes", "test", FullIntunePackage(),
            new[] { "CustomReturnCodes", "Categories" });

        var target = new IntunePackageEntry();
        TemplateService.ApplyPackageTemplate(info, target, App);

        var code = Assert.Single(target.CustomReturnCodes);
        Assert.Equal(3010, code.ReturnCode);
        Assert.Equal("softReboot", code.Type);
        Assert.Equal("Productivity", Assert.Single(target.Categories).Name);
    }

    [Fact]
    public void Apply_AbsentKeys_LeaveTargetUntouched()
    {
        // Template carries ONLY scope tags; the target's name, return codes,
        // and dependencies must survive the apply.
        var info = TemplateService.SavePackageTemplate(
            "Intune", "Sparse", "test", FullIntunePackage(), new[] { "ScopeTags" });

        var target = new IntunePackageEntry { AppName = "Keep Me" };
        target.CustomReturnCodes.Add(new ReturnCodeEntry { ReturnCode = 1, Type = "failed" });
        target.Dependencies.Add(new DependencyEntry { AppName = "Existing Dep" });

        TemplateService.ApplyPackageTemplate(info, target, App);

        Assert.Equal("Keep Me", target.AppName);
        Assert.Equal(1, Assert.Single(target.CustomReturnCodes).ReturnCode);
        Assert.Equal("Existing Dep", Assert.Single(target.Dependencies).AppName);
        Assert.Single(target.ScopeTags);    // the one key present did apply
    }

    [Fact]
    public void Apply_PresentCollection_ReplacesNotMerges()
    {
        var info = TemplateService.SavePackageTemplate(
            "Intune", "Replace", "test", FullIntunePackage(), new[] { "Dependencies" });

        var target = new IntunePackageEntry();
        target.Dependencies.Add(new DependencyEntry { AppName = "Old A" });
        target.Dependencies.Add(new DependencyEntry { AppName = "Old B" });

        TemplateService.ApplyPackageTemplate(info, target, App);

        Assert.Equal("Contoso Runtime", Assert.Single(target.Dependencies).AppName);
    }

    [Fact]
    public void Apply_SccmInstallBehaviors_RoundTrip()
    {
        var pkg = new SCCMPackageEntry();
        pkg.InstallBehaviors.Add(new InstallBehaviorEntry { ExeFileName = "{{Name}}.exe", DisplayName = "{{Name}}" });
        pkg.Dependencies.Add(new DependencyEntry { AppName = "{{Company}} Base" });

        var info = TemplateService.SavePackageTemplate(
            "SCCM", "Behaviors", "test", pkg, new[] { "InstallBehaviors", "Dependencies" });

        var target = new SCCMPackageEntry();
        TemplateService.ApplyPackageTemplate(info, target, App);

        var behavior = Assert.Single(target.InstallBehaviors);
        Assert.Equal("7zip.exe", behavior.ExeFileName);
        Assert.Equal("7zip", behavior.DisplayName);
        Assert.Equal("Contoso Base", Assert.Single(target.Dependencies).AppName);
    }

    // ------------------------------------------------------------------
    // Assignment / deployment expansion
    // ------------------------------------------------------------------

    [Fact]
    public void AssignmentTemplate_ExpandsPlaceholders_WhenAppGiven()
    {
        var saved = TemplateService.SaveAssignmentTemplate("Pilot", "test",
            new AssignmentEntry { Label = "{{Name}} pilot ring", Type = "Group", Intent = "required", GroupID = "g-1" });

        var plain = TemplateService.LoadAssignmentTemplate(saved);
        Assert.Equal("{{Name}} pilot ring", plain.Label);            // no app: verbatim (back-compat)

        var expanded = TemplateService.LoadAssignmentTemplate(saved, App);
        Assert.Equal("7zip pilot ring", expanded.Label);
        Assert.Equal("required", expanded.Intent);
    }

    [Fact]
    public void DeploymentTemplate_ExpandsPlaceholders_WhenAppGiven()
    {
        var saved = TemplateService.SaveDeploymentTemplate("Prod", "test",
            new SCCMDeploymentEntry { Label = "{{Company}} rollout", Collection = "{{Company}} Devices", DeployAction = "Install", DeployPurpose = "Required" });

        var expanded = TemplateService.LoadDeploymentTemplate(saved, App);
        Assert.Equal("Contoso rollout", expanded.Label);
        Assert.Equal("Contoso Devices", expanded.Collection);
    }
}
