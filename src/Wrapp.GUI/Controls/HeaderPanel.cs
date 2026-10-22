// Back-ported from Unwrapped: layout panel for a view header. Children in
// order: [title] [fixed... e.g. the info button] [right content]. The right
// content is measured FIRST, with the width left after the fixed children
// and a reserve for the title; the title takes whatever remains and trims.
// So a long title never pushes the toolbar off the edge, and the toolbar
// sees the real width it has instead of the infinite width a Grid's Auto
// column hands out.
using System.Windows;
using System.Windows.Controls;

namespace Wrapp.Controls;

public sealed class HeaderPanel : System.Windows.Controls.Panel
{
    /// <summary>Width reserved for the title before the right content is asked to shrink.</summary>
    public static readonly DependencyProperty TitleMinWidthProperty =
        DependencyProperty.Register(nameof(TitleMinWidth), typeof(double), typeof(HeaderPanel),
            new FrameworkPropertyMetadata(140.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double TitleMinWidth
    {
        get => (double)GetValue(TitleMinWidthProperty);
        set => SetValue(TitleMinWidthProperty, value);
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
    {
        var children = InternalChildren;
        int n = children.Count;
        if (n == 0) return new System.Windows.Size(0, 0);

        var unbounded = new System.Windows.Size(double.PositiveInfinity, availableSize.Height);
        double height = 0;

        // Fixed middle children first (unbounded - they're small and rigid).
        double fixedWidth = 0;
        for (int i = 1; i < n - 1; i++)
        {
            children[i].Measure(unbounded);
            fixedWidth += children[i].DesiredSize.Width;
            height = Math.Max(height, children[i].DesiredSize.Height);
        }

        var title = children[0];
        UIElement? right = n >= 2 ? children[n - 1] : null;
        bool bounded = !double.IsPositiveInfinity(availableSize.Width);

        double rightWidth = 0;
        if (right is not null)
        {
            var rightAvail = bounded
                ? Math.Max(0, availableSize.Width - fixedWidth - TitleMinWidth)
                : double.PositiveInfinity;
            right.Measure(new System.Windows.Size(rightAvail, availableSize.Height));
            rightWidth = right.DesiredSize.Width;
            height = Math.Max(height, right.DesiredSize.Height);
        }

        var titleAvail = bounded
            ? Math.Max(0, availableSize.Width - fixedWidth - rightWidth)
            : double.PositiveInfinity;
        title.Measure(new System.Windows.Size(titleAvail, availableSize.Height));
        height = Math.Max(height, title.DesiredSize.Height);

        var width = title.DesiredSize.Width + fixedWidth + rightWidth;
        return new System.Windows.Size(bounded ? Math.Min(width, availableSize.Width) : width, height);
    }

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size finalSize)
    {
        var children = InternalChildren;
        int n = children.Count;
        if (n == 0) return finalSize;

        double x = 0;
        // Title, then the fixed children, left to right.
        for (int i = 0; i < Math.Max(1, n - 1); i++)
        {
            var w = children[i].DesiredSize.Width;
            children[i].Arrange(new Rect(x, 0, w, finalSize.Height));
            x += w;
        }
        if (n >= 2)
        {
            // Right content hugs the right edge, never overlapping what's left of it.
            var right = children[n - 1];
            var w = right.DesiredSize.Width;
            var rx = Math.Max(x, finalSize.Width - w);
            right.Arrange(new Rect(rx, 0, w, finalSize.Height));
        }
        return finalSize;
    }
}
