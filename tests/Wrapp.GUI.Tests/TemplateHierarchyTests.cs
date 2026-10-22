using System.IO;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// The unified entry-template engine and hierarchical package templates:
/// assignment/deployment templates round-trip EVERY templatable field (the
/// former hand-maintained key lists silently dropped restart-grace,
/// UseLocalTime, ApprovalRequired, …); package templates can carry their
/// assignments/deployments as an opt-in field choice, re-linked to the target
/// package on apply with placeholder expansion; the template list reports a
/// ChildCount for the dropdown badge.
/// </summary>
// Swaps the static TemplateDir seam and exercises placeholder expansion -
// serialized with the other placeholder/template test classes.
[Collection("Placeholders")]
public class TemplateHierarchyTests : IDisposable
{
    private readonly string _dir;
    private readonly string _originalDir;

    public TemplateHierarchyTests()
    {
        _originalDir = TemplateService.TemplateDir;
        _dir = Path.Combine(Path.GetTempPath(), "wrapp-tpl-hier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        TemplateService.TemplateDir = _dir;
        PlaceholderService.CustomsSource = () => Array.Empty<(string, string?, bool)>();
    }

    public void Dispose()
    {
        TemplateService.TemplateDir = _originalDir;
        PlaceholderService.CustomsSource = () => Array.Empty<(string, string?, bool)>();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly AppSection App = new() { Name = "TestApp", Company = "Contoso" };

    // ------------------------------------------------------------------
    // Entry templates: full field fidelity
    // ------------------------------------------------------------------

    [Fact]
    public void AssignmentTemplate_RoundTripsEveryField_IncludingFormerlyDroppedOnes()
    {
        var entry = new AssignmentEntry
        {
            Label = "Pilot ring",
            Type = "Group",
            GroupID = "g-123",
            GroupMode = "exclude",
            Intent = "required",
            Notification = "hideAll",
            UseLocalTime = "true",                       // formerly dropped
            AutoUpdateSupersededApps = "enabled",        // formerly dropped
            EnableRestartGracePeriod = true,             // formerly dropped
            RestartGracePeriodInMinutes = "240",         // formerly dropped
            RestartCountDownDisplayInMinutes = "15",     // formerly dropped
            RestartNotificationSnoozeInMinutes = "60",   // formerly dropped
            FilterName = "Corp devices",
            FilterMode = "include",
            AppName = "MUST-NOT-STORE",
            PackageId = "MUST-NOT-STORE",
        };

        var info = TemplateService.SaveAssignmentTemplate("Full Fidelity", "d", entry);
        var json = File.ReadAllText(info.FilePath);
        Assert.DoesNotContain("MUST-NOT-STORE", json);

        var loaded = TemplateService.LoadAssignmentTemplate(info);
        Assert.Equal("Pilot ring", loaded.Label);
        Assert.Equal("exclude", loaded.GroupMode);
        Assert.Equal("true", loaded.UseLocalTime);
        Assert.Equal("enabled", loaded.AutoUpdateSupersededApps);
        Assert.True(loaded.EnableRestartGracePeriod);
        Assert.Equal("240", loaded.RestartGracePeriodInMinutes);
        Assert.Equal("15", loaded.RestartCountDownDisplayInMinutes);
        Assert.Equal("60", loaded.RestartNotificationSnoozeInMinutes);
        Assert.Equal(string.Empty, loaded.AppName);
        Assert.Equal(string.Empty, loaded.PackageId);
    }

    [Fact]
    public void DeploymentTemplate_RoundTripsServiceWindowOptions()
    {
        var entry = new SCCMDeploymentEntry
        {
            Collection = "All Workstations",
            DeployPurpose = "Required",
            ApprovalRequired = true,                     // formerly dropped
            OverrideServiceWindow = true,                // formerly dropped
            RebootOutsideServiceWindow = true,           // formerly dropped
            SendWakeupPacket = true,                     // formerly dropped
        };

        var info = TemplateService.SaveDeploymentTemplate("SW Options", "d", entry);
        var loaded = TemplateService.LoadDeploymentTemplate(info);

        Assert.True(loaded.ApprovalRequired);
        Assert.True(loaded.OverrideServiceWindow);
        Assert.True(loaded.RebootOutsideServiceWindow);
        Assert.True(loaded.SendWakeupPacket);
        Assert.Equal("All Workstations", loaded.Collection);
    }

    [Fact]
    public void EntryTemplate_ExpandsPlaceholders_OnlyWhenAppGiven()
    {
        var entry = new AssignmentEntry { GroupID = "g", Label = "{{Name}} ring" };
        var info = TemplateService.SaveAssignmentTemplate("Tokened", "d", entry);

        Assert.Equal("{{Name}} ring", TemplateService.LoadAssignmentTemplate(info).Label);
        Assert.Equal("TestApp ring", TemplateService.LoadAssignmentTemplate(info, App).Label);
    }

    // ------------------------------------------------------------------
    // Hierarchical package templates
    // ------------------------------------------------------------------

    private static IntunePackageEntry IntunePkgWithAssignments()
    {
        var pkg = new IntunePackageEntry { AppName = "7-Zip", InstallExperience = "system" };
        pkg.Assignments.Add(new AssignmentEntry { GroupID = "g-1", Label = "{{Name}} pilots", Intent = "required" });
        pkg.Assignments.Add(new AssignmentEntry { GroupID = "g-2", Intent = "available" });
        return pkg;
    }

    [Fact]
    public void FieldChoices_OfferAssignments_UncheckedWithCount()
    {
        var fields = TemplateService.GetPackageTemplateFields(IntunePkgWithAssignments());
        var choice = Assert.Single(fields, f => f.Name == "Assignments");
        Assert.False(choice.Checked);
        Assert.Equal("2 items", choice.Value);

        // No assignments → no choice offered.
        Assert.DoesNotContain(
            TemplateService.GetPackageTemplateFields(new IntunePackageEntry()),
            f => f.Name == "Assignments");
    }

    [Fact]
    public void HierarchicalTemplate_SavesChildren_WithoutLinkage_AndListsChildCount()
    {
        var pkg = IntunePkgWithAssignments();
        var info = TemplateService.SavePackageTemplate(
            "Intune", "Full Hierarchy", "d", pkg, new[] { "InstallExperience", "Assignments" });

        var json = File.ReadAllText(info.FilePath);
        Assert.Contains("\"Assignments\"", json);
        Assert.DoesNotContain("7-Zip", json);          // no AppName linkage
        Assert.DoesNotContain("PackageId", json);

        var listed = Assert.Single(TemplateService.GetPackageTemplates("Intune"));
        Assert.Equal(2, listed.ChildCount);            // dropdown badge source
    }

    [Fact]
    public void ApplyHierarchicalTemplate_RecreatesChildren_LinkedAndExpanded()
    {
        var info = TemplateService.SavePackageTemplate(
            "Intune", "Full Hierarchy", "d", IntunePkgWithAssignments(),
            new[] { "InstallExperience", "Assignments" });

        var target = new IntunePackageEntry { AppName = "VLC" };
        target.Assignments.Add(new AssignmentEntry { GroupID = "old" });  // replaced

        TemplateService.ApplyPackageTemplate(info, target, App);

        Assert.Equal(2, target.Assignments.Count);
        Assert.All(target.Assignments, a =>
        {
            Assert.Equal("VLC", a.AppName);                  // re-linked to target
            Assert.Equal(target.PackageId, a.PackageId);
        });
        Assert.Equal("TestApp pilots", target.Assignments[0].Label);  // token expanded
        Assert.Equal("required", target.Assignments[0].Intent);
        Assert.DoesNotContain(target.Assignments, a => a.GroupID == "old");
    }

    [Fact]
    public void FlatTemplate_LeavesExistingAssignmentsUntouched()
    {
        var info = TemplateService.SavePackageTemplate(
            "Intune", "Flat", "d", IntunePkgWithAssignments(), new[] { "InstallExperience" });

        var target = new IntunePackageEntry();
        target.Assignments.Add(new AssignmentEntry { GroupID = "keep-me" });

        TemplateService.ApplyPackageTemplate(info, target, App);

        Assert.Equal("keep-me", Assert.Single(target.Assignments).GroupID);
        Assert.Equal(0, Assert.Single(TemplateService.GetPackageTemplates("Intune")).ChildCount);
    }

    [Fact]
    public void SccmHierarchicalTemplate_RoundTripsDeployments()
    {
        var pkg = new SCCMPackageEntry { AppName = "7-Zip" };
        pkg.Deployments.Add(new SCCMDeploymentEntry
        {
            Collection = "{{Company}} Workstations", ApprovalRequired = true,
        });

        var info = TemplateService.SavePackageTemplate(
            "SCCM", "With Deployments", "d", pkg, new[] { "Deployments" });
        Assert.Equal(1, Assert.Single(TemplateService.GetPackageTemplates("SCCM")).ChildCount);

        var target = new SCCMPackageEntry { AppName = "VLC" };
        TemplateService.ApplyPackageTemplate(info, target, App);

        var dep = Assert.Single(target.Deployments);
        Assert.Equal("Contoso Workstations", dep.Collection);  // token expanded
        Assert.True(dep.ApprovalRequired);
        Assert.Equal("VLC", dep.AppName);
    }

    [Fact]
    public void PresetFromHierarchicalTemplate_KeepsAssignmentsChecked()
    {
        var pkg = IntunePkgWithAssignments();
        var info = TemplateService.SavePackageTemplate(
            "Intune", "Hier", "d", pkg, new[] { "InstallExperience", "Assignments" });

        // Quick-save flow: preset mirrors the template's keys, so the
        // hierarchy survives an update without re-checking anything.
        var fields = TemplateService.GetPackageTemplateFields(pkg, presetFrom: info);
        Assert.True(Assert.Single(fields, f => f.Name == "Assignments").Checked);
    }
}
