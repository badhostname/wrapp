using System.IO;
using System.Reflection;
using Wrapp.Models;
using Wrapp.ViewModels;

namespace Wrapp.Tests;

/// <summary>
/// The scripts view's directory is derived from GeneralViewModel's live
/// config path, never cached. A draft's first save moves it out of the
/// temp workspace and deletes the workspace; a copy taken at load time
/// pointed History at the deleted folder (1.0.6: DirectoryNotFoundException
/// from the History button when the editor had not been opened before the
/// save, so the save hook that used to refresh the copy returned early).
/// </summary>
public class ScriptsViewModelTests
{
    private static readonly FieldInfo ConfigPathField =
        typeof(GeneralViewModel).GetField("_configPath", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("GeneralViewModel._configPath not found");

    [Fact]
    public void ConfigDir_FollowsTheLiveConfigPath_WithoutAnyLoadOrSaveEvent()
    {
        var general = new GeneralViewModel(new AppSettings());
        var scripts = new ScriptsViewModel(general);

        Assert.Equal(string.Empty, scripts.ConfigDir);

        var draft = Path.Combine(Path.GetTempPath(), "Wrapp", "draft", "Script", "Config.json");
        ConfigPathField.SetValue(general, draft);
        Assert.Equal(Path.GetDirectoryName(draft), scripts.ConfigDir);

        // The first save re-points the path and raises no ConfigLoaded; the
        // editor may never have been opened, so no save hook runs either.
        var saved = Path.Combine(@"Z:\Applications\test\1_0", "Script", "Config.json");
        ConfigPathField.SetValue(general, saved);
        Assert.Equal(Path.GetDirectoryName(saved), scripts.ConfigDir);
    }
}
