// Back-ported from Unwrapped: the view header's layout contract. The right
// content (the toolbar) is never pushed off the edge by a long title - the
// title trims first, and the toolbar is measured with the width it really has.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Wrapp.Controls;

namespace Wrapp.Tests.Controls;

public class HeaderLayoutTests
{
    private static void Sta(Action body)
    {
        Exception? failure = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { failure = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static Rect Slot(UIElement e) => LayoutInformation.GetLayoutSlot((FrameworkElement)e);

    private static void Layout(UIElement e, double width)
    {
        e.Measure(new Size(width, 40));
        e.Arrange(new Rect(0, 0, width, 40));
    }

    [Fact]
    public void Long_Title_Yields_To_The_Right_Content()
    {
        Sta(() =>
        {
            var panel = new HeaderPanel { TitleMinWidth = 140 };
            var title = new TextBlock { Text = new string('T', 400), TextTrimming = TextTrimming.CharacterEllipsis };
            var info = new Border { Width = 24, Height = 20 };
            var right = new Border { Width = 500, Height = 30 };
            panel.Children.Add(title);
            panel.Children.Add(info);
            panel.Children.Add(right);

            Layout(panel, 800);
            var t = Slot(title); var i = Slot(info); var r = Slot(right);
            Assert.Equal(500, r.Width, 0.5);
            Assert.Equal(800, r.Right, 0.5);              // toolbar hugs the right edge, fully visible
            Assert.True(t.Right <= i.X + 0.5 && i.Right <= r.X + 0.5, "no overlap: title | info | right");
            // The title got what was left (an ellipsis lands on a glyph boundary, so a few px under).
            Assert.InRange(t.Width, 200, 800 - 24 - 500 + 0.5);
        });
    }

    [Fact]
    public void Right_Content_Is_Measured_With_The_Width_It_Really_Has()
    {
        Sta(() =>
        {
            var panel = new HeaderPanel { TitleMinWidth = 150 };
            var title = new TextBlock { Text = "Git History — a bundle with a long name" };
            var info = new Border { Width = 20 };
            var probe = new MeasureProbe();
            panel.Children.Add(title);
            panel.Children.Add(info);
            panel.Children.Add(probe);

            Layout(panel, 700);
            Assert.Equal(700 - 20 - 150, probe.LastConstraint, 0.5);
        });
    }

    private sealed class MeasureProbe : FrameworkElement
    {
        public double LastConstraint { get; private set; } = double.NaN;
        protected override Size MeasureOverride(Size availableSize)
        {
            LastConstraint = availableSize.Width;
            return new Size(Math.Min(300, availableSize.Width), 20);
        }
    }
}
