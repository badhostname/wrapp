// Back-ported from Unwrapped: XAML lint for the scroll helpers.
// SmoothScroll.Enabled is an attached behavior that hooks a ScrollViewer's
// wheel event - set on any other element (a DataGrid, a ListBox) it is
// silently a no-op, and the control keeps WPF's jumpy default step
// (Unwrapped's explorer grid shipped that way for several versions).
// Wrapp's SmoothScroll also accepts a list with its own scroller (ListBox,
// ListView, TreeView, DataGrid) and finds the list's own viewer. An implicit
// ScrollViewer style in a ListBox or TreeView's Resources does NOT work:
// Wpf.Ui templates them with a PassiveScrollViewer, which an implicit
// ScrollViewer style never reaches (Wrapp's Logs, Run-log, Git history and
// Inventory lists shipped that way until 1.0.15).
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

    private static readonly Regex ResourcesOpenRx = new(@"<(?<t>ListBox|TreeView)\.Resources>", RegexOptions.Compiled);

    /// <summary>The text so far is inside an open ListBox.Resources or TreeView.Resources.</summary>
    private static bool InsidePassiveListResources(string before)
    {
        var open = ResourcesOpenRx.Matches(before).LastOrDefault();
        return open is not null
               && before.IndexOf($"</{open.Groups["t"].Value}.Resources>", open.Index, StringComparison.Ordinal) < 0;
    }

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
                    "ScrollViewer" or "ListBox" or "ListView" or "TreeView" or "DataGrid" => true,
                    // <Setter Property="helpers:SmoothScroll.Enabled"> inside a ScrollViewer style,
                    // but not one meant for a ListBox / TreeView's own viewer
                    "Setter" => StyleTargetRx.Matches(before).LastOrDefault()?.Groups["t"].Value == "ScrollViewer"
                                && !InsidePassiveListResources(before),
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
            "helpers:SmoothScroll.Enabled works on a ScrollViewer or directly on a list (ListBox, "
            + "ListView, TreeView, DataGrid). An implicit ScrollViewer style in a ListBox / TreeView's "
            + "Resources never reaches its Wpf.Ui PassiveScrollViewer. Put the attribute on the control "
            + "itself instead:\n" + string.Join("\n", offenders));
    }
}
