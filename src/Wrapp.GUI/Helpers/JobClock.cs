// Back-ported from AlwaysBlue via Unwrapped (src/Unwrapped.GUI/Foundation/Helpers/JobClock.cs) - keep near-verbatim.
// Job cards' clocks tick only while the cards are on screen. Set JobClock.Tracker on the element that shows them
// (the jobs list); while it is visible it holds BackgroundJobTracker.Watch, and lets go when it is hidden or leaves
// the window.
using System.Windows;
using Wrapp.Services;

namespace Wrapp.Helpers;

public static class JobClock
{
    public static readonly DependencyProperty TrackerProperty = DependencyProperty.RegisterAttached(
        "Tracker", typeof(BackgroundJobTracker), typeof(JobClock), new PropertyMetadata(null, OnTrackerChanged));

    public static BackgroundJobTracker? GetTracker(DependencyObject d) => (BackgroundJobTracker?)d.GetValue(TrackerProperty);
    public static void SetTracker(DependencyObject d, BackgroundJobTracker? value) => d.SetValue(TrackerProperty, value);

    private static readonly DependencyProperty WatchProperty = DependencyProperty.RegisterAttached(
        "Watch", typeof(IDisposable), typeof(JobClock));

    private static void OnTrackerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.IsVisibleChanged -= Element_IsVisibleChanged;
        Release(element);
        if (e.NewValue is null) return;
        element.IsVisibleChanged += Element_IsVisibleChanged;
        Update(element);
    }

    private static void Element_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => Update((UIElement)sender);

    private static void Update(UIElement element)
    {
        if (element.IsVisible && GetTracker(element) is { } tracker)
        {
            if (element.GetValue(WatchProperty) is null) element.SetValue(WatchProperty, tracker.Watch());
        }
        else Release(element);
    }

    private static void Release(UIElement element)
    {
        if (element.GetValue(WatchProperty) is IDisposable watch)
        {
            element.ClearValue(WatchProperty);
            watch.Dispose();
        }
    }
}
