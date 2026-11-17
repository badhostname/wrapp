using System.IO;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.Input;
using Wrapp.ViewModels;

namespace Wrapp.Tests;

/// <summary>
/// The window's keyboard shortcuts: each gesture is bound, each bound
/// command exists on the view model, and the help text tells the user.
/// </summary>
public class KeyboardShortcutTests
{
    private static readonly (string Gesture, string Command)[] Expected =
    {
        ("Ctrl+S",     "SaveShortcutCommand"),
        ("Ctrl+Alt+S", "SaveAsShortcutCommand"),
        ("Ctrl+O",     "OpenShortcutCommand"),
        ("Ctrl+N",     "NewShortcutCommand"),
    };

    private static string GuiSrcDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "wrapp.sln"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("repo root not found"), "src", "Wrapp.GUI");
    }

    [Fact]
    public void The_main_window_binds_the_four_gestures_to_the_shortcut_commands()
    {
        var xaml = XDocument.Load(Path.Combine(GuiSrcDir(), "Views", "MainWindow.xaml"));
        var bindings = xaml.Descendants().Where(e => e.Name.LocalName == "KeyBinding")
            .ToDictionary(
                e => e.Attribute("Gesture")!.Value,
                e => e.Attribute("Command")!.Value);

        foreach (var (gesture, command) in Expected)
        {
            Assert.True(bindings.TryGetValue(gesture, out var bound), $"{gesture} is not bound in MainWindow.xaml");
            Assert.Equal($"{{Binding {command}}}", bound);
        }
        Assert.Equal(Expected.Length, bindings.Count);
    }

    [Fact]
    public void Every_shortcut_command_exists_on_the_main_view_model()
    {
        foreach (var (_, command) in Expected)
        {
            var prop = typeof(MainViewModel).GetProperty(command);
            Assert.NotNull(prop);
            Assert.True(typeof(IAsyncRelayCommand).IsAssignableFrom(prop!.PropertyType), $"{command} is not an async command");
        }
    }

    [Fact]
    public void The_help_names_each_shortcut_where_its_button_is_explained()
    {
        var help = File.ReadAllText(Path.Combine(GuiSrcDir(), "Help", "HelpContent.xaml"));
        Assert.Contains("Help.Main.Toolbar.NewBundle\">", help);
        Assert.Matches("Help\\.Main\\.Toolbar\\.NewBundle\"[^<]*Ctrl\\+N", help);
        Assert.Matches("Help\\.Main\\.Toolbar\\.OpenBundle\"[^<]*Ctrl\\+O", help);
        Assert.Matches("Help\\.Main\\.Toolbar\\.SaveBundle\"[^<]*Ctrl\\+S", help);
        Assert.Matches("Help\\.Main\\.Toolbar\\.SaveBundleAs\"[^<]*Ctrl\\+Alt\\+S", help);
        Assert.Matches("Help\\.Settings\\.SaveSettings\"[^<]*Ctrl\\+S", help);
        Assert.Contains("## Keyboard shortcuts", help);
    }
}
