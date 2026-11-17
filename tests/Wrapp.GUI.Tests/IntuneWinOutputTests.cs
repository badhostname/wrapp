using System.IO;
using Wrapp.Helpers;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Where the .intunewin goes: what Settings resolve to, what a bundle's own
/// choice resolves to, what is written into Config.json at save, and how a
/// stored path is read back relative to the bundle.
/// </summary>
public class IntuneWinOutputTests
{
    private static AppSection Ricoh() => new()
    {
        Company = "Ricoh", Name = "Universal Print Driver PCL6", Version = "4_45_0_0", DotVersion = "4.45.0.0", Language = "EN",
    };

    [Fact]
    public void Beside_the_bundle_is_the_default_and_is_stored_relative()
    {
        var settings = new AppSettings();
        Assert.Equal(IntuneWinOutput.ModeBesideBundle, settings.IntuneWinOutputMode);
        Assert.Equal(@"..\Output", IntuneWinOutput.FromSettings(settings, Ricoh()));
        Assert.Equal("Beside the bundle", IntuneWinOutput.ModeLabel(settings));
    }

    [Fact]
    public void A_relative_path_resolves_to_the_parents_Output_folder_so_the_example_from_the_plan_holds()
    {
        var bundle = @"\\isdist01z\prod$\HP\Drivers\Printer universal\PCL6\1.0.0.0";
        Assert.Equal(@"\\isdist01z\prod$\HP\Drivers\Printer universal\PCL6\Output", IntuneWinOutput.Resolve(@"..\Output", bundle));
        Assert.Equal(@"\\isdist01z\prod$\HP\Drivers\Printer universal\PCL6\Output", IntuneWinOutput.BesideBundle(bundle));
        Assert.Equal(@"\\central\intunewin$\Ricoh", IntuneWinOutput.Resolve(@"\\central\intunewin$\Ricoh", bundle));
        Assert.Equal(@"..\Output", IntuneWinOutput.Resolve(@"..\Output", null)); // a draft: shown as is
        Assert.Equal(string.Empty, IntuneWinOutput.Resolve("", bundle));
    }

    [Fact]
    public void Central_resolves_under_the_root_with_the_sub_folder_format_and_the_output_spaces_rule()
    {
        var settings = new AppSettings
        {
            IntuneWinOutputMode = IntuneWinOutput.ModeCentral,
            IntuneWinOutputRoot = @"\\central\intunewin$",
        };
        Assert.Equal(@"\\central\intunewin$\Ricoh\Universal_Print_Driver_PCL6\4_45_0_0", IntuneWinOutput.FromSettings(settings, Ricoh()));
        Assert.Equal("Central folder", IntuneWinOutput.ModeLabel(settings));

        settings.IntuneWinFolderNameSpaces = BundleService.FolderSpacesKeep;
        Assert.Equal(@"\\central\intunewin$\Ricoh\Universal Print Driver PCL6\4_45_0_0", IntuneWinOutput.FromSettings(settings, Ricoh()));

        settings.IntuneWinSubFolderFormat = "{Name}/{DotVersion}";
        Assert.Equal(@"\\central\intunewin$\Universal Print Driver PCL6\4.45.0.0", IntuneWinOutput.FromSettings(settings, Ricoh()));

        settings.IntuneWinSubFolderFormat = "";
        Assert.Equal(@"\\central\intunewin$", IntuneWinOutput.FromSettings(settings, Ricoh()));
    }

    [Fact]
    public void Central_without_a_root_and_a_broken_sub_folder_format_degrade_rather_than_break()
    {
        var noRoot = new AppSettings { IntuneWinOutputMode = IntuneWinOutput.ModeCentral, IntuneWinOutputRoot = "  " };
        Assert.Equal(@"..\Output", IntuneWinOutput.FromSettings(noRoot, Ricoh()));

        var brokenFormat = new AppSettings
        {
            IntuneWinOutputMode = IntuneWinOutput.ModeCentral,
            IntuneWinOutputRoot = @"D:\IntuneWin",
            IntuneWinSubFolderFormat = @"{Company}\..\{Name}",
        };
        Assert.Equal(@"D:\IntuneWin", IntuneWinOutput.FromSettings(brokenFormat, Ricoh()));

        // An empty token drops its folder; the rest of the format still applies.
        var emptyToken = new AppSettings { IntuneWinOutputMode = IntuneWinOutput.ModeCentral, IntuneWinOutputRoot = @"D:\IntuneWin" };
        var app = Ricoh();
        app.Company = "";
        Assert.Equal(@"D:\IntuneWin\Universal_Print_Driver_PCL6\4_45_0_0", IntuneWinOutput.FromSettings(emptyToken, app));
        app.Name = "";
        Assert.Equal(@"D:\IntuneWin\4_45_0_0", IntuneWinOutput.FromSettings(emptyToken, app));
    }

    [Fact]
    public void A_bundles_custom_folder_is_nested_with_the_settings_format_or_used_directly()
    {
        var settings = new AppSettings();
        Assert.Equal(@"\\share\Intune\Packages\Ricoh\Universal_Print_Driver_PCL6\4_45_0_0",
            IntuneWinOutput.FromCustom(settings, Ricoh(), @"\\share\Intune\Packages", IntuneWinOutput.LayoutNested));
        Assert.Equal(@"\\share\Intune\Packages",
            IntuneWinOutput.FromCustom(settings, Ricoh(), @"\\share\Intune\Packages\", IntuneWinOutput.LayoutDirect).TrimEnd('\\'));
    }

    [Fact]
    public void Apply_writes_the_path_from_settings_or_the_bundles_choice_and_repairs_a_custom_without_a_root()
    {
        var settings = new AppSettings();
        var config = new AppConfigModel();
        config.App.Company = "Ricoh"; config.App.Name = "Universal Print Driver PCL6"; config.App.Version = "4_45_0_0";
        var output = config.Script.IntunePackager.Output;

        IntuneWinOutput.Apply(settings, config);
        Assert.Equal(IntuneWinOutput.SourceSettings, output.Source);
        Assert.Equal(@"..\Output", output.Path);

        output.Source = IntuneWinOutput.SourceCustom;
        output.Root = @"\\share\Intune\Packages";
        output.Layout = IntuneWinOutput.LayoutNested;
        IntuneWinOutput.Apply(settings, config);
        Assert.Equal(@"\\share\Intune\Packages\Ricoh\Universal_Print_Driver_PCL6\4_45_0_0", output.Path);

        output.Layout = IntuneWinOutput.LayoutDirect;
        IntuneWinOutput.Apply(settings, config);
        Assert.Equal(@"\\share\Intune\Packages", output.Path);

        // A version bump at the next save moves a nested custom path with it.
        output.Layout = IntuneWinOutput.LayoutNested;
        config.App.Version = "4_46_0_0";
        IntuneWinOutput.Apply(settings, config);
        Assert.EndsWith(@"\4_46_0_0", output.Path);

        // Custom with nothing browsed is not a choice.
        output.Root = "";
        IntuneWinOutput.Apply(settings, config);
        Assert.Equal(IntuneWinOutput.SourceSettings, output.Source);
        Assert.Equal(@"..\Output", output.Path);
    }

    [Fact]
    public void The_output_block_round_trips_through_Config_json_and_is_optional_when_reading()
    {
        var original = new AppConfigModel();
        original.Script.IntunePackager.Output.Path = @"\\share\Intune\Packages\Ricoh";
        original.Script.IntunePackager.Output.Source = IntuneWinOutput.SourceCustom;
        original.Script.IntunePackager.Output.Root = @"\\share\Intune\Packages";
        original.Script.IntunePackager.Output.Layout = IntuneWinOutput.LayoutDirect;

        var json = ConfigFileService.SerializeToJson(original);
        Assert.Contains("\"Output\"", json);
        var restored = ConfigFileService.DeserializeFromJson(json);
        Assert.Equal(@"\\share\Intune\Packages\Ricoh", restored.Script.IntunePackager.Output.Path);
        Assert.Equal(IntuneWinOutput.SourceCustom, restored.Script.IntunePackager.Output.Source);
        Assert.Equal(@"\\share\Intune\Packages", restored.Script.IntunePackager.Output.Root);
        Assert.Equal(IntuneWinOutput.LayoutDirect, restored.Script.IntunePackager.Output.Layout);

        // A bundle saved by an older Wrapp has no block: defaults, nothing thrown.
        var older = ConfigFileService.DeserializeFromJson("""{ "App": { "Name": "Old" }, "Script": { "IntunePackager": { "Tag": "x", "Packages": [] } } }""");
        Assert.Equal(string.Empty, older.Script.IntunePackager.Output.Path);
        Assert.Equal(IntuneWinOutput.SourceSettings, older.Script.IntunePackager.Output.Source);
        Assert.Equal(IntuneWinOutput.LayoutNested, older.Script.IntunePackager.Output.Layout);
    }

    [Fact]
    public void Root_reachability_is_judged_by_the_drive_or_share_root()
    {
        Assert.True(IntuneWinOutput.RootReachable(Path.Combine(Path.GetTempPath(), "does-not-exist-yet", "sub")));
        var missing = Enumerable.Range('Z', 1).Select(c => $"{(char)c}:\\").Concat(Enumerable.Range('A', 26).Reverse().Select(c => $"{(char)c}:\\"))
            .FirstOrDefault(d => !Directory.Exists(d));
        if (missing is not null)
            Assert.False(IntuneWinOutput.RootReachable(Path.Combine(missing, "intunewin")));
        Assert.False(IntuneWinOutput.RootReachable(""));
    }
}
