// Back-ported from AlwaysBlue via Unwrapped (tests/Unwrapped.GUI.Tests/Views/LongListTests.cs) - keep near-verbatim.
// Laid out off-screen in a Border, with the keys App.xaml declares read out of App.xaml itself.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Xml.Linq;
using Xunit.Abstractions;

namespace Wrapp.Tests.Views;

/// <summary>Long lists: the scroll bar thumb keeps a usable size however many rows there are.</summary>
public class LongListTests
{
    private readonly ITestOutputHelper _out;
    public LongListTests(ITestOutputHelper output) => _out = output;

    private static void Sta(Action body)
    {
        Exception? failure = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { failure = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    private static string GuiSrcDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "wrapp.sln"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("repo root not found"), "src", "Wrapp.GUI");
    }

    /// <summary>The thumb floor keys as App.xaml declares them (the vertical and the horizontal one, the same value).</summary>
    private static double AppThumbFloorKey()
    {
        var app = XDocument.Load(Path.Combine(GuiSrcDir(), "App.xaml"));
        var keys = app.Descendants().Where(e => e.Name.LocalName == "Double")
            .ToDictionary(e => e.Attributes().Single(a => a.Name.LocalName == "Key").Value, e => double.Parse(e.Value, System.Globalization.CultureInfo.InvariantCulture));
        var vertical = keys["{x:Static SystemParameters.VerticalScrollBarButtonHeightKey}"];
        Assert.Equal(vertical, keys["{x:Static SystemParameters.HorizontalScrollBarButtonWidthKey}"]);
        return vertical;
    }

    /// <summary>The app's control templates: Wpf.Ui's, as App.xaml merges them, and the thumb floor App.xaml sets.</summary>
    private static ResourceDictionary Themed()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = Application.Current;
        var res = new ResourceDictionary();
        res.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
        res.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        var floor = AppThumbFloorKey();
        res[SystemParameters.VerticalScrollBarButtonHeightKey] = floor;
        res[SystemParameters.HorizontalScrollBarButtonWidthKey] = floor;
        return res;
    }

    [Fact]
    public void The_thumb_of_a_long_list_is_never_shorter_than_32_px_and_the_arrows_keep_their_size()
    {
        Sta(() =>
        {
            Assert.Equal(64, AppThumbFloorKey()); // WPF floors the thumb at half the key
            var list = new ListBox { ItemsSource = Enumerable.Range(0, 20000).Select(i => $"Value.{i}").ToList() };
            var host = new Border { Resources = Themed(), Width = 300, Height = 320, Child = list };
            host.Measure(new Size(300, 320));
            host.Arrange(new Rect(0, 0, 300, 320));
            host.UpdateLayout();

            var bar = Descendants<ScrollBar>(host).First(b => b.Orientation == Orientation.Vertical);
            var thumb = Descendants<Thumb>(bar).First();
            var track = Descendants<Track>(bar).First();
            _out.WriteLine($"thumb {thumb.ActualWidth:0.#}x{thumb.ActualHeight:0.#}, track {track.ActualHeight:0.#}, maximum {bar.Maximum:0.#}");
            Assert.True(bar.Maximum > 1000, "the list is long enough that the thumb would be a dot by default");
            Assert.InRange(thumb.ActualHeight, 31.5, 40);
            // The key also names the arrow buttons' height in WPF's own template; the Fluent one keeps them small.
            Assert.All(Descendants<RepeatButton>(bar).Where(r => r.ActualHeight > 0 && r.ActualHeight < 40), r => Assert.True(r.ActualHeight <= 16, $"an arrow is {r.ActualHeight}px"));

            // Dragging: the halfway value puts the thumb halfway down what the track leaves it.
            bar.Value = bar.Maximum / 2;
            host.UpdateLayout();
            var top = thumb.TranslatePoint(new Point(0, 0), track).Y;
            Assert.InRange(top, (track.ActualHeight - thumb.ActualHeight) / 2 - 2, (track.ActualHeight - thumb.ActualHeight) / 2 + 2);
        });
    }

    [Fact]
    public void Without_the_key_the_thumb_of_the_same_list_is_a_dot()
    {
        Sta(() =>
        {
            var res = Themed();
            res.Remove(SystemParameters.VerticalScrollBarButtonHeightKey);
            res.Remove(SystemParameters.HorizontalScrollBarButtonWidthKey);
            var list = new ListBox { ItemsSource = Enumerable.Range(0, 20000).Select(i => $"Value.{i}").ToList() };
            var host = new Border { Resources = res, Width = 300, Height = 320, Child = list };
            host.Measure(new Size(300, 320));
            host.Arrange(new Rect(0, 0, 300, 320));
            host.UpdateLayout();
            var thumb = Descendants<Thumb>(Descendants<ScrollBar>(host).First(b => b.Orientation == Orientation.Vertical)).First();
            _out.WriteLine($"thumb without the key {thumb.ActualHeight:0.#}px");
            Assert.True(thumb.ActualHeight < 16, $"the key is what keeps the thumb usable; without it {thumb.ActualHeight:0.#}px");
        });
    }
}
