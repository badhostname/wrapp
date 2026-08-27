using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Wrapp.Helpers;

/// <summary>
/// Attached behavior that gives a <see cref="ScrollViewer"/> a gentle, fixed
/// wheel step instead of WPF's default (which scrolls a large logical amount
/// per notch and feels jumpy on dense, pixel-scrolling content). Each wheel
/// notch scrolls <see cref="StepProperty"/> device-independent pixels,
/// proportional to the wheel delta (so high-resolution / precision wheels stay
/// smooth), and the event is marked handled so it cannot also be processed by
/// an ancestor scroller (the compounding that felt over-sensitive).
///
/// <para>Only applies to PIXEL-scrolling viewers (<c>CanContentScroll=false</c>,
/// the default for a plain ScrollViewer). It is a deliberate no-op on
/// item-scrolling viewers (a ListBox / DataGrid's internal ScrollViewer, where
/// offsets are in items, not pixels) so it is safe to attach broadly.</para>
///
/// <para>Use in XAML:
/// <code>&lt;ScrollViewer helpers:SmoothScroll.Enabled="True"/&gt;</code>
/// Optionally tune the step:
/// <code>helpers:SmoothScroll.Step="64"</code></para>
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(SmoothScroll),
            new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    /// <summary>Pixels scrolled per standard wheel notch (delta 120). Default 50.</summary>
    public static readonly DependencyProperty StepProperty =
        DependencyProperty.RegisterAttached(
            "Step", typeof(double), typeof(SmoothScroll),
            new PropertyMetadata(50.0));

    public static double GetStep(DependencyObject obj) => (double)obj.GetValue(StepProperty);
    public static void SetStep(DependencyObject obj, double value) => obj.SetValue(StepProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        if ((bool)e.NewValue)
            sv.PreviewMouseWheel += OnPreviewMouseWheel;
        else
            sv.PreviewMouseWheel -= OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;

        // Item-scrolling viewers measure offsets in items, not pixels -- a fixed
        // pixel step would be nonsensical there, so leave their default behavior.
        // EXCEPTION: a virtualized list with VirtualizingPanel.ScrollUnit="Pixel"
        // keeps CanContentScroll=true (virtualization needs it) but its offsets
        // ARE pixels - the gentle step applies there exactly like on a plain
        // viewer (the Logs / Run-log / Inventory lists).
        if (sv.CanContentScroll && !HasPixelScrollUnit(sv)) return;

        // INNER-FIRST: this handler runs during the TUNNEL phase on the OUTER
        // viewer, i.e. BEFORE any nested scrollable (a read-only preview
        // TextBox's internal viewer, a fixed-height JSON wrapper) ever sees
        // the event. Handling unconditionally made every nested preview dead
        // to the wheel (the field-reported Inventory/Settings bug). When a
        // nested scrollable between the wheel target and this viewer can
        // still move in the wheel's direction, stand aside -- it (or its own
        // SmoothScroll) takes the event; once it's exhausted, we scroll.
        if (NestedScrollableCanConsume(sv, e.OriginalSource, e.Delta)) return;

        var step = GetStep(sv);
        sv.ScrollToVerticalOffset(sv.VerticalOffset - (e.Delta / 120.0) * step);
        e.Handled = true;
    }

    /// <summary>Pixel-unit virtualized list: offsets are pixels despite
    /// CanContentScroll being true (its templated parent is the ItemsControl
    /// carrying the attached ScrollUnit).</summary>
    private static bool HasPixelScrollUnit(ScrollViewer sv)
        => sv.TemplatedParent is ItemsControl ic
           && VirtualizingPanel.GetScrollUnit(ic) == ScrollUnit.Pixel;

    /// <summary>
    /// True when a ScrollViewer strictly between <paramref name="originalSource"/>
    /// and <paramref name="outer"/> can still scroll in the wheel's direction.
    /// Walks the visual tree (logical hops for content elements like Run).
    /// </summary>
    private static bool NestedScrollableCanConsume(
        ScrollViewer outer, object originalSource, int delta)
    {
        var node = originalSource as DependencyObject;
        while (node is not null && !ReferenceEquals(node, outer))
        {
            if (node is ScrollViewer inner && inner.ScrollableHeight > 0)
            {
                var canScrollUp   = delta > 0 && inner.VerticalOffset > 0;
                var canScrollDown = delta < 0 && inner.VerticalOffset < inner.ScrollableHeight;
                if (canScrollUp || canScrollDown) return true;
            }
            node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
}
