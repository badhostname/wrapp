using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Wrapp.Helpers;

namespace Wrapp.Tests.Views;

/// <summary>
/// Touchpad-friendly wheel scrolling: a small wheel delta moves a small
/// distance (SmoothScroll's step, in proportion), in dropdown lists, in lists
/// with their own scroller, and in tables capped by MaxHeight before the page.
/// </summary>
public class WheelScrollTests
{
    private static void Sta(Action body)
    {
        Exception? failure = null;
        // A dispatcher context, as the app has: Wpf.Ui's scrollers resume
        // timers on it, and without one they resume on a pool thread and throw.
        var t = new Thread(() => { try { SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext()); body(); } catch (Exception ex) { failure = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static ResourceDictionary Themed()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = Application.Current;
        var res = new ResourceDictionary();
        res.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
        res.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        return res;
    }

    private static void Layout(FrameworkElement e, double w, double h)
    {
        e.Measure(new Size(w, h));
        e.Arrange(new Rect(0, 0, w, h));
        e.UpdateLayout();
    }

    private static T First<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            if (TryFirst<T>(child) is { } found) return found;
        }
        throw new InvalidOperationException($"no {typeof(T).Name} under {root.GetType().Name}");
    }

    private static T? TryFirst<T>(DependencyObject root) where T : DependencyObject
    {
        try { return First<T>(root); } catch (InvalidOperationException) { return null; }
    }

    /// <summary>One wheel event, tunnelling from the root down to <paramref name="target"/> as input does.</summary>
    private static void Wheel(UIElement target, int delta)
    {
        var e = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        target.RaiseEvent(e);
        (target as FrameworkElement)?.UpdateLayout();
    }

    [Fact]
    public void A_dropdown_list_moves_a_small_step_for_a_small_touchpad_delta()
    {
        Sta(() =>
        {
            var cb = new ComboBox { ItemsSource = Enumerable.Range(0, 60).Select(i => $"Property{i}").ToList() };
            var host = new Border { Resources = Themed(), Width = 300, Height = 40, Child = cb };
            Layout(host, 300, 40);

            ScrollBubbling.SmoothDropDownList(cb);
            var popup = First<Popup>(cb);
            var content = (FrameworkElement)popup.Child;
            Layout(content, 300, 300);
            var list = First<ScrollViewer>(content);
            Assert.True(SmoothScroll.GetEnabled(list));
            Assert.True(list.ScrollableHeight > 200, "the list is long enough to scroll");

            var item = First<ComboBoxItem>(content);
            Wheel(item, -12);                 // one small touchpad event
            Assert.Equal(5, list.VerticalOffset, 1);
            Wheel(item, -120);                // one mouse notch
            Assert.Equal(55, list.VerticalOffset, 1);
            Wheel(item, 60);
            Assert.Equal(30, list.VerticalOffset, 1);
        });
    }

    [Fact]
    public void A_list_with_its_own_scroller_scrolls_in_pixels_and_keeps_virtualizing()
    {
        Sta(() =>
        {
            // As the XAML lists do it: the attribute on the list itself.
            var list = new ListBox { ItemsSource = Enumerable.Range(0, 2000).Select(i => $"App {i}").ToList() };
            SmoothScroll.SetEnabled(list, true);
            var host = new Border { Resources = Themed(), Width = 300, Height = 200, Child = list };
            Layout(host, 300, 200);
            list.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); // off-screen, nothing raises it

            Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(list));
            var sv = SmoothScroll.OwnViewer(list)!;
            Assert.True(SmoothScroll.GetEnabled(sv));
            Assert.True(First<VirtualizingStackPanel>(list).Children.Count < 100, "rows are still virtualized");

            Wheel(First<ListBoxItem>(list), -24);
            Assert.Equal(10, sv.VerticalOffset, 1);
        });
    }

    [Fact]
    public void An_implicit_ScrollViewer_style_never_reaches_a_Wpf_Ui_ListBox_viewer()
    {
        // Why the lists take the attribute directly (and the lint rejects the
        // old pattern): Wpf.Ui's ListBox scroller is a PassiveScrollViewer.
        Sta(() =>
        {
            var res = Themed();
            var list = new ListBox { ItemsSource = Enumerable.Range(0, 200).Select(i => $"Log {i}").ToList() };
            var smooth = new Style(typeof(ScrollViewer), res[typeof(ScrollViewer)] as Style);
            smooth.Setters.Add(new Setter(SmoothScroll.EnabledProperty, true));
            list.Resources.Add(typeof(ScrollViewer), smooth);
            var host = new Border { Resources = res, Width = 300, Height = 200, Child = list };
            Layout(host, 300, 200);

            var sv = SmoothScroll.OwnViewer(list)!;
            Assert.IsType<Wpf.Ui.Controls.PassiveScrollViewer>(sv);
            Assert.False(SmoothScroll.GetEnabled(sv));
        });
    }

    [Fact]
    public void A_table_capped_by_max_height_scrolls_its_rows_before_the_page()
    {
        Sta(() =>
        {
            // The detection tests table: a pixel-scrolling list under a page
            // with SmoothScroll, forwarding to the page through BubbleScroll.
            var table = new ListBox
            {
                MaxHeight = 100,
                ItemsSource = Enumerable.Range(0, 30).Select(i => $"Test {i}").ToList(),
            };
            ScrollViewer.SetCanContentScroll(table, false);
            ScrollBubbling.SetBubbleScroll(table, true);
            var stack = new StackPanel();
            stack.Children.Add(table);
            stack.Children.Add(new Border { Height = 600 });
            var page = new ScrollViewer { Content = stack };
            SmoothScroll.SetEnabled(page, true);
            var host = new Border { Resources = Themed(), Width = 300, Height = 200, Child = page };
            Layout(host, 300, 200);

            var rows = SmoothScroll.OwnViewer(table)!;
            var row = First<ListBoxItem>(table);
            Wheel(row, -120);
            Assert.Equal(50, rows.VerticalOffset, 1);
            Assert.Equal(0, page.VerticalOffset, 1);

            rows.ScrollToBottom();
            host.UpdateLayout();
            Wheel(row, -120);
            Assert.Equal(50, page.VerticalOffset, 1);
        });
    }
}
