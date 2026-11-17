using System.IO;
using Wrapp.Helpers;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// The notices the General view shows when the real folder and the config
/// disagree. Real temp folders; nothing is stored anywhere.
/// </summary>
public class BundleNoticesTests : IDisposable
{
    private readonly string _root;

    public BundleNoticesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "WrappTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private (string Root, string ConfigPath) Bundle(string relative, bool legacy = false)
    {
        var root = Path.Combine(_root, relative);
        var configDir = legacy ? root : Path.Combine(root, "Script");
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, "Config.json");
        File.WriteAllText(configPath, "{}");
        return (root, configPath);
    }

    private static AppConfigModel Ricoh(string dotVersion = "4.45.0.0")
    {
        var c = new AppConfigModel();
        c.App.Company = "Ricoh";
        c.App.Name = "Universal Print Driver PCL6";
        c.App.DotVersion = dotVersion;
        c.App.Version = dotVersion.Replace('.', '_');
        return c;
    }

    private static AppSettings Underscores() => new()
    {
        FolderNameSpaces = BundleService.FolderSpacesUnderscore,
        BundleSaveMode = BundleService.SaveModeConvention,
    };

    [Theory]
    [InlineData("4_45_0_0", "4.45.0.0")]
    [InlineData("4.45.0.0", "4.45.0.0")]
    [InlineData("1_0", "1.0")]
    [InlineData("IMC3010", null)]
    [InlineData("4_45_0_0 hotfix", null)]
    [InlineData("1", null)]
    [InlineData("", null)]
    public void A_folder_name_is_a_version_when_it_is_digits_joined_by_dots_or_underscores(string folder, string? expected)
    {
        Assert.Equal(expected, BundleNotices.VersionFromFolderName(folder));
    }

    [Fact]
    public void No_bundle_or_a_temp_draft_has_no_notices()
    {
        Assert.Empty(BundleNotices.Evaluate(null, null, Ricoh(), Underscores()));
        var temp = Path.Combine(TempWorkspaceService.RootPath, "draft", "4_44_0_0");
        Assert.Empty(BundleNotices.Evaluate(temp, Path.Combine(temp, "Script", "Config.json"), Ricoh(), Underscores()));
    }

    [Fact]
    public void A_conventional_bundle_whose_folder_matches_its_version_has_no_notices()
    {
        var (root, cfg) = Bundle(@"share\Ricoh\Universal_Print_Driver_PCL6\4_45_0_0");
        Assert.Empty(BundleNotices.Evaluate(root, cfg, Ricoh(), Underscores()));
    }

    [Fact]
    public void Warns_when_the_folder_is_named_after_another_version_and_offers_to_take_it()
    {
        var (root, cfg) = Bundle(@"share\Ricoh\Universal_Print_Driver_PCL6\4_44_0_0");
        var notice = Assert.Single(BundleNotices.Evaluate(root, cfg, Ricoh("4.45.0.0"), Underscores()).Where(n => n.Id == "version-folder"));
        Assert.True(notice.IsWarning);
        Assert.Equal(BundleNoticeFix.SetVersionFromFolder, notice.Fix);
        Assert.Equal("4.44.0.0", notice.Data);
        Assert.Contains("4.44.0.0", notice.Title);
        Assert.Contains("4.45.0.0", notice.Title);
    }

    [Fact]
    public void A_folder_that_is_not_a_version_says_nothing_about_versions()
    {
        var (root, cfg) = Bundle(@"share\Ricoh\Universal_Print_Driver_PCL6\IMC3010");
        Assert.DoesNotContain(BundleNotices.Evaluate(root, cfg, Ricoh(), Underscores()), n => n.Id == "version-folder");
    }

    [Fact]
    public void Says_when_the_folder_does_not_follow_the_convention_but_only_in_convention_mode()
    {
        var (root, cfg) = Bundle(@"share\Drivers\Ricoh PCL6\4_45_0_0");
        var settings = Underscores();
        var notice = Assert.Single(BundleNotices.Evaluate(root, cfg, Ricoh(), settings).Where(n => n.Id == "convention"));
        Assert.True(notice.IsInfo);
        Assert.Equal(BundleNoticeFix.CopyToConventionalFolder, notice.Fix);
        Assert.Contains(@"Ricoh\Universal_Print_Driver_PCL6\4_45_0_0", notice.Detail);

        settings.BundleSaveMode = BundleService.SaveModeDirect;
        Assert.DoesNotContain(BundleNotices.Evaluate(root, cfg, Ricoh(), settings), n => n.Id == "convention");

        // The spaces rule is the operator's: a tree with spaces is conventional under the Spaces setting.
        var spaces = new AppSettings { FolderNameSpaces = BundleService.FolderSpacesKeep };
        var (spaced, spacedCfg) = Bundle(@"share\Ricoh\Universal Print Driver PCL6\4_45_0_0");
        Assert.DoesNotContain(BundleNotices.Evaluate(spaced, spacedCfg, Ricoh(), spaces), n => n.Id == "convention");
    }

    [Fact]
    public void FollowsConvention_compares_the_tail_segment_by_segment()
    {
        Assert.True(BundleNotices.FollowsConvention(@"\\share\x\Ricoh\UPD\4_45_0_0", @"ricoh\upd\4_45_0_0"));
        Assert.True(BundleNotices.FollowsConvention(@"D:\Bundles\UPD\4_45_0_0\", @"UPD\4_45_0_0"));
        Assert.False(BundleNotices.FollowsConvention(@"D:\UPD\4_45_0_0", @"Ricoh\UPD\4_45_0_0"));
        Assert.False(BundleNotices.FollowsConvention(@"D:\Bundles\Ricoh\UPD\4_45_0_0", @"Ricoh\UPD\4_46_0_0"));
        Assert.False(BundleNotices.FollowsConvention(@"D:\Bundles\x", ""));
    }

    [Fact]
    public void Reports_a_legacy_layout_as_information_that_the_next_save_handles()
    {
        var (root, cfg) = Bundle(@"share\Ricoh\Universal_Print_Driver_PCL6\4_45_0_0", legacy: true);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        Directory.CreateDirectory(Path.Combine(root, "Shortcuts"));
        File.WriteAllText(Path.Combine(root, "Shortcuts", "Install.cmd"), "rem");

        var notice = Assert.Single(BundleNotices.Evaluate(root, cfg, Ricoh(), Underscores()).Where(n => n.Id == "legacy-layout"));
        Assert.True(notice.IsInfo);
        Assert.False(notice.HasFix);
        Assert.Contains("Config.json", notice.Detail);
        Assert.Contains(".git", notice.Detail);
        Assert.Contains(".cmd", notice.Detail);
    }

    [Fact]
    public void Says_when_the_stored_output_folder_differs_from_what_settings_now_give()
    {
        var (root, cfg) = Bundle(@"share\Ricoh\Universal_Print_Driver_PCL6\4_45_0_0");
        var config = Ricoh();
        config.Script.IntunePackager.Output.Path = @"..\Output";
        var settings = Underscores();
        Assert.Empty(BundleNotices.Evaluate(root, cfg, config, settings));

        settings.IntuneWinOutputMode = IntuneWinOutput.ModeCentral;
        settings.IntuneWinOutputRoot = Path.Combine(_root, "central");
        var notice = Assert.Single(BundleNotices.Evaluate(root, cfg, config, settings).Where(n => n.Id == "output-changes"));
        Assert.True(notice.IsInfo);
        Assert.Contains(@"..\Output", notice.Detail);
        Assert.Contains(Path.Combine(_root, "central"), notice.Detail);
    }

    [Fact]
    public void Warns_when_the_output_folders_root_is_not_reachable_and_points_at_the_output_card()
    {
        var missing = Enumerable.Range('A', 26).Reverse().Select(c => $"{(char)c}:\\").FirstOrDefault(d => !Directory.Exists(d));
        if (missing is null) return; // every drive letter is in use on this machine

        var (root, cfg) = Bundle(@"share\Ricoh\Universal_Print_Driver_PCL6\4_45_0_0");
        var config = Ricoh();
        config.Script.IntunePackager.Output.Source = IntuneWinOutput.SourceCustom;
        config.Script.IntunePackager.Output.Root = Path.Combine(missing, "intunewin");
        config.Script.IntunePackager.Output.Layout = IntuneWinOutput.LayoutDirect;
        config.Script.IntunePackager.Output.Path = Path.Combine(missing, "intunewin");

        var notice = Assert.Single(BundleNotices.Evaluate(root, cfg, config, Underscores()).Where(n => n.Id == "output-unreachable"));
        Assert.True(notice.IsWarning);
        Assert.Equal(BundleNoticeFix.OpenIntuneOutput, notice.Fix);
        Assert.Contains(IntuneWinOutput.BesideBundle(root), notice.Detail);
    }
}
