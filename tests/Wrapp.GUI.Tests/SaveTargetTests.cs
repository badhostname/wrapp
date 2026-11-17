using System.IO;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// The save sheet's model: where it opens, what the convention suggests,
/// what it resolves to, and what it refuses or warns about. Real temp
/// folders stand in for the share.
/// </summary>
public class SaveTargetTests : IDisposable
{
    private readonly string _root;

    public SaveTargetTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "WrappTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Dir(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    private static AppSection Ricoh() => new()
    {
        Company = "Ricoh", Name = "Universal Print Driver PCL6", Version = "4_45_0_0", DotVersion = "4.45.0.0", Language = "EN",
    };

    private string Bundle(string relative, string name = "Some App", string dotVersion = "1.0.0")
    {
        var root = Dir(relative);
        Directory.CreateDirectory(Path.Combine(root, "Script"));
        File.WriteAllText(Path.Combine(root, "Script", "Config.json"),
            $$"""{ "App": { "Name": "{{name}}", "DotVersion": "{{dotVersion}}" } }""");
        return root;
    }

    // ── Where the sheet opens ────────────────────────────────────────

    [Fact]
    public void The_sheet_opens_at_the_last_location_used_on_this_computer_when_it_still_exists()
    {
        var last = Dir("last");
        var settings = new AppSettings { LastBundleOutputRoot = last, BundleOutputRoot = @"\\dist\packages$" };
        var (location, source) = SaveTarget.StartingLocation(settings, @"D:\Bundles\App\1_0_0");
        Assert.Equal(last, location);
        Assert.Contains("last used", source);
    }

    [Fact]
    public void Then_at_the_organisations_default_then_at_the_current_bundles_parent_then_at_documents()
    {
        var settings = new AppSettings { LastBundleOutputRoot = Path.Combine(_root, "gone"), BundleOutputRoot = @"\\dist\packages$" };
        Assert.Equal((@"\\dist\packages$", "the organisation's default location, set in Settings"), SaveTarget.StartingLocation(settings, null));

        settings.BundleOutputRoot = string.Empty;
        Assert.Equal(@"D:\Bundles\App", SaveTarget.StartingLocation(settings, @"D:\Bundles\App\1_0_0\").Location);

        // A draft in the temp workspace has no parent worth offering.
        var temp = Path.Combine(TempWorkspaceService.RootPath, "abc");
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), SaveTarget.StartingLocation(settings, temp).Location);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), SaveTarget.StartingLocation(settings, null).Location);
    }

    // ── What it resolves to ──────────────────────────────────────────

    [Fact]
    public void Convention_mode_suggests_the_format_with_the_spaces_rule_and_resolves_under_the_location()
    {
        var settings = new AppSettings { FolderNameSpaces = BundleService.FolderSpacesUnderscore, LastBundleOutputRoot = _root };
        var target = SaveTarget.For(settings, Ricoh(), null);

        Assert.Equal(SaveTargetMode.Convention, target.Mode);
        Assert.Equal(@"Ricoh\Universal_Print_Driver_PCL6\4_45_0_0", target.RelativePath);
        Assert.Equal(target.RelativePath, target.SuggestedRelativePath);
        Assert.Equal(Path.Combine(_root, @"Ricoh\Universal_Print_Driver_PCL6\4_45_0_0"), target.ResolvedPath);
        Assert.Null(target.Error);
        Assert.Empty(target.Warnings);
    }

    [Fact]
    public void Direct_mode_resolves_to_the_location_itself_and_follows_the_setting()
    {
        var mine = Dir("mine");
        var settings = new AppSettings { BundleSaveMode = BundleService.SaveModeDirect, LastBundleOutputRoot = mine };
        var target = SaveTarget.For(settings, Ricoh(), null);

        Assert.Equal(SaveTargetMode.Direct, target.Mode);
        Assert.Equal(mine, target.ResolvedPath);
        Assert.Null(target.Error);
    }

    [Fact]
    public void An_edited_relative_path_is_kept_and_forward_slashes_are_accepted()
    {
        var settings = new AppSettings { LastBundleOutputRoot = _root };
        var target = SaveTarget.For(settings, Ricoh(), null);
        target.RelativePath = "Drivers/Ricoh/PCL6 4.45";
        target.Refresh(settings, Ricoh(), null);

        Assert.Null(target.Error);
        Assert.Equal(Path.Combine(_root, @"Drivers\Ricoh\PCL6 4.45"), target.ResolvedPath);
        Assert.NotEqual(target.SuggestedRelativePath, target.RelativePath);
    }

    [Fact]
    public void Mode_setting_round_trips_and_an_unknown_value_means_convention()
    {
        Assert.Equal(SaveTargetMode.Direct, SaveTarget.ModeFrom("direct"));
        Assert.Equal(SaveTargetMode.Convention, SaveTarget.ModeFrom("Convention"));
        Assert.Equal(SaveTargetMode.Convention, SaveTarget.ModeFrom(""));
        Assert.Equal(SaveTargetMode.Convention, SaveTarget.ModeFrom("whatever"));
        Assert.Equal(BundleService.SaveModeDirect, SaveTarget.ModeSetting(SaveTargetMode.Direct));
        Assert.Equal(BundleService.SaveModeConvention, SaveTarget.ModeSetting(SaveTargetMode.Convention));
    }

    // ── What it refuses ──────────────────────────────────────────────

    [Fact]
    public void A_blank_or_relative_location_is_an_error()
    {
        var settings = new AppSettings();
        var target = SaveTarget.For(settings, Ricoh(), null);
        target.Location = "";
        target.Refresh(settings, Ricoh(), null);
        Assert.Equal("Choose a location.", target.Error);

        target.Location = @"Bundles\Ricoh";
        target.Refresh(settings, Ricoh(), null);
        Assert.Contains("not a full folder path", target.Error);
    }

    [Theory]
    [InlineData(@"\Ricoh\PCL6")]
    [InlineData(@"C:\Ricoh\PCL6")]
    [InlineData(@"Ricoh\..\PCL6")]
    [InlineData(@"Ricoh\\PCL6")]
    [InlineData(@"Ricoh\PCL6\")]
    [InlineData(@"Ricoh\PC:L6")]
    public void Convention_mode_refuses_folders_that_would_not_stay_under_the_location(string relative)
    {
        var settings = new AppSettings { LastBundleOutputRoot = _root };
        var target = SaveTarget.For(settings, Ricoh(), null);
        target.RelativePath = relative;
        target.Refresh(settings, Ricoh(), null);
        Assert.NotNull(target.Error);
        Assert.StartsWith("The folders to create", target.Error);
    }

    [Fact]
    public void Convention_mode_with_an_empty_field_the_format_uses_is_an_error_until_edited_or_saved_directly()
    {
        var settings = new AppSettings { LastBundleOutputRoot = _root };
        var app = Ricoh();
        app.Company = "";
        var target = SaveTarget.For(settings, app, null);

        Assert.Equal(new[] { "Company" }, target.MissingFields);
        Assert.NotNull(target.Error);
        Assert.Contains("Company", target.Error);

        // The operator writes the folders themselves: their path stands, the hole is still listed.
        target.RelativePath = @"Ricoh\Universal Print Driver PCL6\4_45_0_0";
        target.Refresh(settings, app, null);
        Assert.Null(target.Error);
        Assert.Equal(new[] { "Company" }, target.MissingFields);

        // Or saves directly: nothing is required.
        target.Mode = SaveTargetMode.Direct;
        target.Refresh(settings, app, null);
        Assert.Null(target.Error);
        Assert.Empty(target.MissingFields);
    }

    [Fact]
    public void Required_fields_follow_the_format_not_the_app()
    {
        var settings = new AppSettings { DirectoryFormat = @"{Name}\{Version}", LastBundleOutputRoot = _root };
        var app = Ricoh();
        app.Company = "";
        app.Language = "";
        var target = SaveTarget.For(settings, app, null);
        Assert.Empty(target.MissingFields);
        Assert.Null(target.Error);
    }

    // ── What it warns about ──────────────────────────────────────────

    [Fact]
    public void Warns_when_the_folder_already_holds_a_bundle_and_names_it()
    {
        var existing = Bundle(@"Ricoh\PCL6\4_45_0_0", "Universal Print Driver PCL6", "4.44.0.0");
        var settings = new AppSettings { BundleSaveMode = BundleService.SaveModeDirect, LastBundleOutputRoot = existing };
        var target = SaveTarget.For(settings, Ricoh(), null);

        var warning = Assert.Single(target.Warnings);
        Assert.Contains("already holds a bundle (Universal Print Driver PCL6 4.44.0.0)", warning);
    }

    [Fact]
    public void Warns_when_the_folder_is_inside_another_bundle_or_is_not_empty_or_is_the_temp_workspace()
    {
        var outer = Bundle(@"Outer\1_0_0");
        var inside = Dir(@"Outer\1_0_0\B\sub");
        var settings = new AppSettings { BundleSaveMode = BundleService.SaveModeDirect, LastBundleOutputRoot = inside };
        var target = SaveTarget.For(settings, Ricoh(), null);
        Assert.Contains(target.Warnings, w => w.Contains("inside another bundle") && w.Contains(outer));

        var busy = Dir("busy");
        File.WriteAllText(Path.Combine(busy, "notes.txt"), "x");
        settings.LastBundleOutputRoot = busy;
        target = SaveTarget.For(settings, Ricoh(), null);
        Assert.Contains("This folder is not empty.", target.Warnings);

        settings.LastBundleOutputRoot = _root;
        target = SaveTarget.For(settings, Ricoh(), null);
        target.Location = Path.Combine(TempWorkspaceService.RootPath, "draft");
        target.Refresh(settings, Ricoh(), null);
        Assert.Contains(target.Warnings, w => w.Contains("temporary workspace"));
    }

    [Fact]
    public void Warns_about_a_drive_root_and_about_the_current_bundles_own_folder()
    {
        var settings = new AppSettings { BundleSaveMode = BundleService.SaveModeDirect, LastBundleOutputRoot = _root };
        var target = SaveTarget.For(settings, Ricoh(), null);
        target.Location = Path.GetPathRoot(_root)!;
        target.Refresh(settings, Ricoh(), null);
        Assert.Contains("This is the root of a drive or share.", target.Warnings);

        var current = Bundle(@"Current\1_0_0");
        target.Location = current;
        target.Refresh(settings, Ricoh(), current);
        Assert.Contains(target.Warnings, w => w.Contains("current bundle's own folder"));
        Assert.DoesNotContain(target.Warnings, w => w.Contains("already holds a bundle"));
    }

    [Fact]
    public void An_error_hides_the_warnings_and_a_clean_target_has_neither()
    {
        var settings = new AppSettings { LastBundleOutputRoot = _root };
        var target = SaveTarget.For(settings, Ricoh(), null);
        Assert.Null(target.Error);
        Assert.Empty(target.Warnings);

        target.RelativePath = @"..\escape";
        target.Refresh(settings, Ricoh(), null);
        Assert.NotNull(target.Error);
        Assert.Empty(target.Warnings);
    }
}
