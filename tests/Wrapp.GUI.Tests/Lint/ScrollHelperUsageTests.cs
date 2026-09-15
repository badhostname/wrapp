// Back-ported from Unwrapped: XAML lint for the scroll helpers.
// SmoothScroll.Enabled is an attached behavior that hooks a ScrollViewer's
// wheel event - set on any other element (a DataGrid, a ListBox) it is
// silently a no-op, and the control keeps WPF's jumpy default step
// (Unwrapped's explorer grid shipped that way for several versions). The
// supported pattern for a composite control is an implicit ScrollViewer
// style in its Resources, which reaches the internal viewer.
using System.IO;
using System.Text.RegularExpressions;

namespace Wrapp.Tests.Lint;

public class ScrollHelperUsageTests
{
    private static string GuiSrcDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Wrapp.GUI");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent!;
        }
        throw new DirectoryNotFoundException("src/Wrapp.GUI not found walking up from the test base directory");
    }

    private static readonly Regex UsageRx = new(@"helpers:SmoothScroll\.Enabled\s*=", RegexOptions.Compiled);
    private static readonly Regex OpenTagRx = new(@"<([A-Za-z_][\w:.]*)", RegexOptions.Compiled);
    private static readonly Regex StyleTargetRx = new(@"<Style\b[^>]*TargetType=""(?<t>[^""]+)""", RegexOptions.Compiled);

    [Fact]
    public void SmoothScroll_Is_Only_Attached_To_ScrollViewers()
    {
        var sep = Path.DirectorySeparatorChar;
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(GuiSrcDir(), "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}")) continue;
            var text = File.ReadAllText(file);
            foreach (Match use in UsageRx.Matches(text))
            {
                var before = text[..use.Index];
                var tag = OpenTagRx.Matches(before).LastOrDefault()?.Groups[1].Value ?? "?";
                var ok = tag switch
                {
                    "ScrollViewer" => true,
                    // <Setter Property="helpers:SmoothScroll.Enabled"> inside a ScrollViewer style
                    "Setter" => StyleTargetRx.Matches(before).LastOrDefault()?.Groups["t"].Value == "ScrollViewer",
                    _ => false,
                };
                if (!ok)
                {
                    var line = before.Count(c => c == '\n') + 1;
                    offenders.Add($"{Path.GetFileName(file)}:{line} on <{tag}>");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "helpers:SmoothScroll.Enabled does nothing outside a ScrollViewer. Put an implicit "
            + "<Style TargetType=\"ScrollViewer\" BasedOn=\"{StaticResource {x:Type ScrollViewer}}\"> "
            + "in the control's Resources instead:\n" + string.Join("\n", offenders));
    }
}
