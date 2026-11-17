using System.IO;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Phase 13 (D-1). Covers the central path helper that replaced ~14 inline
/// <c>Path.Combine(root, "Script", "Config.json")</c> sites. The shape and
/// behavior must remain stable - the lint rule (see SourceLintTests)
/// forbids inline literals outside <see cref="BundlePaths"/>, so any
/// regression here propagates instantly across the GUI.
/// </summary>
public class BundlePathsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "bundle-paths-test");

    [Fact]
    public void ConfigJson_ComposesScriptSubfolder()
    {
        var actual = BundlePaths.ConfigJson(Root);
        var expected = Path.Combine(Root, "Script", "Config.json");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ScriptDir_ReturnsScriptSubfolder()
    {
        var actual = BundlePaths.ScriptDir(Root);
        var expected = Path.Combine(Root, "Script");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ShortcutDir_ReturnsShortcutsSubfolder()
    {
        var actual = BundlePaths.ShortcutDir(Root);
        var expected = Path.Combine(Root, "Shortcuts");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BinaryFolder_AppeaseReturnsBSubfolder()
    {
        var actual = BundlePaths.BinaryFolder(Root, ScriptFramework.Appease);
        var expected = Path.Combine(Root, "B");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BinaryFolder_PsadtReturnsFilesSubfolder()
    {
        var actual = BundlePaths.BinaryFolder(Root, ScriptFramework.PSADT);
        var expected = Path.Combine(Root, "Files");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Constants_MatchExpectedLayoutNames()
    {
        Assert.Equal("Script",      BundlePaths.ScriptFolder);
        Assert.Equal("Config.json", BundlePaths.ConfigFileName);
        Assert.Equal("Shortcuts",   BundlePaths.ShortcutFolder);
    }
}
