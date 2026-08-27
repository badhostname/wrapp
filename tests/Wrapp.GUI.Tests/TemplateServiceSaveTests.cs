using System.IO;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Save-path tests for TemplateService (custom templates from the UI).
/// Each test redirects TemplateDir to a fresh temp directory and restores
/// it afterwards, so nothing touches the real %LOCALAPPDATA% templates.
/// </summary>
[Collection("Placeholders")]   // one collection for every test that swaps the TemplateDir or CustomsSource statics
public sealed class TemplateServiceSaveTests : IDisposable
{
    private readonly string _originalDir;
    private readonly string _originalPackDir;
    private readonly string _tempDir;
    private readonly string _packDir;

    public TemplateServiceSaveTests()
    {
        _originalDir = TemplateService.TemplateDir;
        _originalPackDir = TemplateService.OrgTemplatePackDir;
        var baseDir = Path.Combine(Path.GetTempPath(), "WrappTests", Guid.NewGuid().ToString("N"));
        _tempDir = Path.Combine(baseDir, "Templates");
        _packDir = Path.Combine(baseDir, "org-templates");
        Directory.CreateDirectory(_tempDir);
        TemplateService.TemplateDir = _tempDir;
        TemplateService.OrgTemplatePackDir = _packDir;
    }

    public void Dispose()
    {
        TemplateService.TemplateDir = _originalDir;
        TemplateService.OrgTemplatePackDir = _originalPackDir;
        try { Directory.Delete(Path.GetDirectoryName(_tempDir)!, recursive: true); } catch { /* best effort */ }
    }

    private void WritePackFile(string relative, string content)
    {
        var path = Path.Combine(_packDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // -----------------------------------------------------------------------
    // Workstream O: org template pack
    // -----------------------------------------------------------------------

    [Fact]
    public void OrgPack_SeedsTemplates_AsProtectedBuiltIns()
    {
        WritePackFile(Path.Combine("Scripts", "Install", "Contoso_Install.ps1"), "# Org standard install\nInstall-Org");
        TemplateService.EnsureOrgTemplatePack();

        var listed = Assert.Single(TemplateService.GetScriptTemplates("Install"),
            t => t.Name == "Contoso Install");
        Assert.Equal("Org standard install", listed.Description);
        Assert.True(TemplateService.IsBuiltInTemplate(listed));

        // Protected: cannot be overwritten by a custom save or deleted
        Assert.Equal(TemplateCollision.BuiltIn,
            TemplateService.CheckCollision(TemplateKind.Script, "Contoso Install", "Install"));
        Assert.False(TemplateService.DeleteCustomTemplate(listed));
    }

    [Fact]
    public void OrgPack_RefreshesUneditedCopy_OnPackUpdate()
    {
        var rel = Path.Combine("Scripts", "Install", "Contoso_Install.ps1");
        WritePackFile(rel, "# v1");
        TemplateService.EnsureOrgTemplatePack();

        WritePackFile(rel, "# v2");
        TemplateService.EnsureOrgTemplatePack();

        Assert.Equal("# v2", File.ReadAllText(Path.Combine(_tempDir, rel)));
    }

    [Fact]
    public void OrgPack_PreservesTechnicianEditedCopy_OnPackUpdate()
    {
        var rel = Path.Combine("Scripts", "Install", "Contoso_Install.ps1");
        WritePackFile(rel, "# v1");
        TemplateService.EnsureOrgTemplatePack();

        // Technician customizes their copy
        File.WriteAllText(Path.Combine(_tempDir, rel), "# my edits");

        WritePackFile(rel, "# v2");
        TemplateService.EnsureOrgTemplatePack();

        Assert.Equal("# my edits", File.ReadAllText(Path.Combine(_tempDir, rel)));
    }

    [Fact]
    public void OrgPack_Reset_RestoresEditedCopies()
    {
        var rel = Path.Combine("Scripts", "Install", "Contoso_Install.ps1");
        WritePackFile(rel, "# org content");
        TemplateService.EnsureOrgTemplatePack();
        File.WriteAllText(Path.Combine(_tempDir, rel), "# my edits");

        TemplateService.ResetBuiltInTemplates();

        Assert.Equal("# org content", File.ReadAllText(Path.Combine(_tempDir, rel)));
    }

    [Fact]
    public void OrgPack_MissingFolder_IsANoOp()
    {
        TemplateService.EnsureOrgTemplatePack(); // _packDir never created
        Assert.Empty(TemplateService.GetScriptTemplates("Install"));
    }

    // -----------------------------------------------------------------------
    // Filename sanitization
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("My EXE Install", "My_EXE_Install")]
    [InlineData("  spaced  out  ", "spaced_out")]
    [InlineData("a/b\\c:d", "a_b_c_d")]
    [InlineData("dots...", "dots")]
    [InlineData("__lead_trail__", "lead_trail")]
    public void SanitizeTemplateFileName_ProducesSafeNames(string input, string expected)
    {
        Assert.Equal(expected, TemplateService.SanitizeTemplateFileName(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("___")]
    public void SanitizeTemplateFileName_RejectsUnusableNames(string input)
    {
        Assert.Throws<ArgumentException>(() => TemplateService.SanitizeTemplateFileName(input));
    }

    [Fact]
    public void SanitizeTemplateFileName_CannotEscapeTemplateDir()
    {
        var result = TemplateService.SanitizeTemplateFileName(@"..\..\evil");
        Assert.DoesNotContain(Path.DirectorySeparatorChar, result);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, result);
        Assert.False(result.StartsWith('.'));
    }

    // -----------------------------------------------------------------------
    // Script templates
    // -----------------------------------------------------------------------

    [Fact]
    public void SaveScriptTemplate_RoundTripsThroughListAndLoad()
    {
        var content = "Install-Thing -Name \"{{Name}}\" -Version {{Version}}";
        var info = TemplateService.SaveScriptTemplate("Install", "My Winget Install", "Installs via winget", content);

        var listed = Assert.Single(TemplateService.GetScriptTemplates("Install"),
            t => t.Name == "My Winget Install");
        Assert.Equal("Installs via winget", listed.Description);

        // Tokens stay literal in the file and expand only on apply
        var raw = File.ReadAllText(listed.FilePath);
        Assert.Contains("{{Name}}", raw);

        var app = new AppSection { Name = "7-Zip", Version = "2409" };
        var loaded = TemplateService.LoadScriptTemplate(listed, app);
        Assert.Contains("-Name \"7-Zip\" -Version 2409", loaded);
        Assert.DoesNotContain("{{Name}}", loaded);

        Assert.Equal(info.FilePath, listed.FilePath);
    }

    [Fact]
    public void SaveScriptTemplate_RegistersCustomManifestEntry()
    {
        TemplateService.SaveScriptTemplate("Uninstall", "Cleanup", "", "Remove-Thing");

        var manifest = File.ReadAllText(Path.Combine(_tempDir, "manifest.json"));
        Assert.Contains("Cleanup.ps1", manifest);
        Assert.Contains("\"BuiltIn\": false", manifest);
    }

    [Fact]
    public void SaveScriptTemplate_RefusesBuiltInOverwrite()
    {
        // Simulate a built-in entry the way EnsureBuiltInTemplates records one
        var relPath = Path.Combine("Scripts", "Install", "MSI_Silent.ps1");
        SeedManifestEntry(relPath, builtIn: true);

        Assert.Throws<InvalidOperationException>(() =>
            TemplateService.SaveScriptTemplate("Install", "MSI Silent", "", "# custom"));
    }

    [Fact]
    public void CheckCollision_DistinguishesNoneCustomBuiltIn()
    {
        Assert.Equal(TemplateCollision.None,
            TemplateService.CheckCollision(TemplateKind.Script, "Fresh Name", "Install"));

        TemplateService.SaveScriptTemplate("Install", "Mine", "", "# x");
        Assert.Equal(TemplateCollision.Custom,
            TemplateService.CheckCollision(TemplateKind.Script, "Mine", "Install"));

        SeedManifestEntry(Path.Combine("Scripts", "Install", "MSI_Silent.ps1"), builtIn: true);
        Assert.Equal(TemplateCollision.BuiltIn,
            TemplateService.CheckCollision(TemplateKind.Script, "MSI Silent", "Install"));
    }

    // -----------------------------------------------------------------------
    // Package templates (sparse overlay)
    // -----------------------------------------------------------------------

    [Fact]
    public void GetPackageTemplateFields_ExcludesIdentityAndPreChecksBehavioral()
    {
        var pkg = new IntunePackageEntry { AppName = "7-Zip", TenantId = "guid", Developer = "Igor" };
        var fields = TemplateService.GetPackageTemplateFields(pkg);

        // AppName is offered (opt-in via checkbox — package-template-coverage
        // plan) but never pre-checked; hard identity stays excluded.
        Assert.False(fields.Single(f => f.Name == "AppName").Checked);
        Assert.DoesNotContain(fields, f => f.Name == "TenantId");
        Assert.DoesNotContain(fields, f => f.Name == "PackageId");
        Assert.DoesNotContain(fields, f => f.Name == "InstallCommand");

        Assert.True(fields.Single(f => f.Name == "InstallExperience").Checked);
        Assert.True(fields.Single(f => f.Name == "MaximumInstallationTimeInMinutes").Checked);
        // Metadata is offered but NOT pre-checked (per-app values shouldn't travel by accident)
        Assert.False(fields.Single(f => f.Name == "Developer").Checked);
    }

    [Fact]
    public void SavePackageTemplate_OnlyCheckedFieldsApplyBack()
    {
        var source = new IntunePackageEntry
        {
            AppName = "7-Zip",
            InstallExperience = "user",
            MaximumInstallationTimeInMinutes = 30,
            Architecture = "x64",
            Developer = "Igor Pavlov",
        };

        TemplateService.SavePackageTemplate("Intune", "User Install 30m", "Half-hour user install",
            source, new[] { "InstallExperience", "MaximumInstallationTimeInMinutes", "Architecture" });

        var listed = Assert.Single(TemplateService.GetPackageTemplates("Intune"));
        Assert.Equal("User Install 30m", listed.Name);

        // Unchecked field never reaches the file
        Assert.DoesNotContain("Igor Pavlov", File.ReadAllText(listed.FilePath));

        var target = new IntunePackageEntry
        {
            AppName = "Notepad++",
            Developer = "Don Ho",
            RestartBehavior = "suppress",
        };
        TemplateService.ApplyPackageTemplate(listed, target, new AppSection());

        // Chosen fields transferred
        Assert.Equal("user", target.InstallExperience);
        Assert.Equal(30, target.MaximumInstallationTimeInMinutes);
        Assert.Equal("x64", target.Architecture);
        // Sparse: everything else untouched
        Assert.Equal("Notepad++", target.AppName);
        Assert.Equal("Don Ho", target.Developer);
        Assert.Equal("suppress", target.RestartBehavior);
    }

    // -----------------------------------------------------------------------
    // Assignment / deployment templates
    // -----------------------------------------------------------------------

    [Fact]
    public void SaveAssignmentTemplate_RoundTripsThroughLoad()
    {
        var entry = new AssignmentEntry
        {
            Label = "Pilot ring",
            Type = "Group",
            GroupID = "11111111-2222-3333-4444-555555555555",
            GroupMode = "include",
            Intent = "required",
            Notification = "showReboot",
            DeliveryOptimizationPriority = "foreground",
            AvailableTime = "2026-01-01T00:00:00.000Z",
            DeadlineTime = "2026-02-01T00:00:00.000Z",
            AppName = "7-Zip",           // must NOT be stored
            PackageId = "some-guid",     // must NOT be stored
        };

        TemplateService.SaveAssignmentTemplate("Pilot Required", "Pilot ring required install", entry);

        var listed = Assert.Single(TemplateService.GetAssignmentTemplates(),
            t => t.Name == "Pilot Required");
        var loaded = TemplateService.LoadAssignmentTemplate(listed);

        Assert.Equal("Pilot ring", loaded.Label);
        Assert.Equal("Group", loaded.Type);
        Assert.Equal(entry.GroupID, loaded.GroupID);
        Assert.Equal("required", loaded.Intent);
        Assert.Equal("showReboot", loaded.Notification);
        Assert.Equal(entry.AvailableTime, loaded.AvailableTime);
        Assert.Equal(entry.DeadlineTime, loaded.DeadlineTime);

        Assert.Empty(loaded.AppName);
        Assert.Empty(loaded.PackageId);
    }

    [Fact]
    public void SaveDeploymentTemplate_RoundTripsThroughLoad()
    {
        var entry = new SCCMDeploymentEntry
        {
            Label = "Prod rollout",
            Collection = "All Workstations",
            DeployAction = "Install",
            DeployPurpose = "Required",
            UserNotification = "DisplaySoftwareCenterOnly",
            Comment = "Standard prod push",
            TimeBaseOn = "UTC",
            AvailableDateTime = "2026-01-01T00:00:00.000Z",
            DeadlineDateTime = "2026-01-15T00:00:00.000Z",
            AppName = "7-Zip",           // must NOT be stored
            PackageId = "some-guid",     // must NOT be stored
        };

        TemplateService.SaveDeploymentTemplate("Prod Required", "Prod push", entry);

        var listed = Assert.Single(TemplateService.GetDeploymentTemplates(),
            t => t.Name == "Prod Required");
        var loaded = TemplateService.LoadDeploymentTemplate(listed);

        Assert.Equal("Prod rollout", loaded.Label);
        Assert.Equal("All Workstations", loaded.Collection);
        Assert.Equal("Required", loaded.DeployPurpose);
        Assert.Equal("UTC", loaded.TimeBaseOn);
        Assert.Equal(entry.DeadlineDateTime, loaded.DeadlineDateTime);

        Assert.Empty(loaded.AppName);
        Assert.Empty(loaded.PackageId);
    }

    // -----------------------------------------------------------------------
    // Update in place
    // -----------------------------------------------------------------------

    [Fact]
    public void UpdateScriptTemplate_OverwritesInPlace_KeepsNameAndDescription()
    {
        var original = TemplateService.SaveScriptTemplate("Install", "Tweakable", "My tweak target", "Install-Old");

        var updated = TemplateService.UpdateScriptTemplate(original, "Install-New -Flag");

        Assert.Equal(original.FilePath, updated.FilePath);
        var listed = Assert.Single(TemplateService.GetScriptTemplates("Install"),
            t => t.Name == "Tweakable");
        Assert.Equal("My tweak target", listed.Description);

        var raw = File.ReadAllText(listed.FilePath);
        Assert.Contains("Install-New -Flag", raw);
        Assert.DoesNotContain("Install-Old", raw);
        Assert.StartsWith("# My tweak target", raw);
    }

    [Fact]
    public void UpdateScriptTemplate_RefusesBuiltIn()
    {
        var relPath = Path.Combine("Scripts", "Install", "MSI_Silent.ps1");
        SeedManifestEntry(relPath, builtIn: true);
        var filePath = Path.Combine(_tempDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, "# built-in");

        var info = new TemplateInfo("MSI Silent", "", filePath, "Install");
        Assert.Throws<InvalidOperationException>(() =>
            TemplateService.UpdateScriptTemplate(info, "# hacked"));
        Assert.Equal("# built-in", File.ReadAllText(filePath));
    }

    [Fact]
    public void UpdatePackageTemplate_RenamesInPlaceWithoutNewFile()
    {
        var pkg = new IntunePackageEntry { InstallExperience = "user" };
        var original = TemplateService.SavePackageTemplate(
            "Intune", "User Install 30m", "v1", pkg, new[] { "InstallExperience" });

        pkg.InstallExperience = "system";
        var updated = TemplateService.UpdatePackageTemplate(
            original, "User Install 45m", "v2", pkg, new[] { "InstallExperience" });

        // Same file, new display name -- update must never fork a second template
        Assert.Equal(original.FilePath, updated.FilePath);
        var listed = Assert.Single(TemplateService.GetPackageTemplates("Intune"));
        Assert.Equal("User Install 45m", listed.Name);
        Assert.Equal("v2", listed.Description);
        Assert.Contains("\"InstallExperience\": \"system\"", File.ReadAllText(listed.FilePath));
    }

    [Fact]
    public void GetPackageTemplateFields_PresetFromTemplate_MirrorsItsKeys()
    {
        var pkg = new IntunePackageEntry();
        var saved = TemplateService.SavePackageTemplate(
            "Intune", "Sparse", "", pkg, new[] { "InstallExperience", "Architecture" });

        var fields = TemplateService.GetPackageTemplateFields(pkg, presetFrom: saved);

        Assert.True(fields.Single(f => f.Name == "InstallExperience").Checked);
        Assert.True(fields.Single(f => f.Name == "Architecture").Checked);
        // Core field NOT in the template: must be unchecked in update mode
        // (default save mode would pre-check it)
        Assert.False(fields.Single(f => f.Name == "RestartBehavior").Checked);
    }

    // -----------------------------------------------------------------------
    // Delete
    // -----------------------------------------------------------------------

    [Fact]
    public void DeleteCustomTemplate_RemovesFileAndManifestEntry()
    {
        var info = TemplateService.SaveScriptTemplate("Install", "Doomed", "", "# bye");
        Assert.True(File.Exists(info.FilePath));

        Assert.True(TemplateService.DeleteCustomTemplate(info));
        Assert.False(File.Exists(info.FilePath));
        Assert.DoesNotContain("Doomed.ps1", File.ReadAllText(Path.Combine(_tempDir, "manifest.json")));
    }

    [Fact]
    public void DeleteCustomTemplate_RefusesBuiltIn()
    {
        var relPath = Path.Combine("Scripts", "Install", "MSI_Silent.ps1");
        SeedManifestEntry(relPath, builtIn: true);
        var filePath = Path.Combine(_tempDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, "# built-in");

        var info = new TemplateInfo("MSI Silent", "", filePath, "Install");
        Assert.True(TemplateService.IsBuiltInTemplate(info));
        Assert.False(TemplateService.DeleteCustomTemplate(info));
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void DeleteCustomTemplate_RefusesPathsOutsideTemplateDir()
    {
        var outside = Path.Combine(Path.GetTempPath(), "WrappTests", "outside.ps1");
        var info = new TemplateInfo("Outside", "", outside, "Install");
        Assert.False(TemplateService.DeleteCustomTemplate(info));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>Writes a manifest entry the same shape EnsureBuiltInTemplates produces.</summary>
    private void SeedManifestEntry(string relativePath, bool builtIn)
    {
        var manifestPath = Path.Combine(_tempDir, "manifest.json");
        var escaped = relativePath.Replace("\\", "\\\\");
        var json = $$"""
        {
          "Version": "test",
          "Templates": {
            "{{escaped}}": { "BuiltIn": {{(builtIn ? "true" : "false")}}, "Hash": "ABC" }
          }
        }
        """;
        File.WriteAllText(manifestPath, json);
    }
}
