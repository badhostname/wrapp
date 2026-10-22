using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Wrapp.Helpers;

/// <summary>
/// Attached behavior that forwards mouse-wheel events from a child control
/// UP to the nearest ancestor <see cref="ScrollViewer"/>, so page-level
/// scrolling keeps working when the cursor is over a composite control
/// whose internal <c>ScrollViewer</c> swallows wheel events.
///
/// <para>Typical offenders: <see cref="DataGrid"/> (over column headers /
/// empty rows), <see cref="System.Windows.Controls.FlowDocumentScrollViewer"/>
/// (always - its internal scrollviewer captures wheel regardless of the
/// <c>VerticalScrollBarVisibility</c> setting), WebView2,
/// <see cref="System.Windows.Controls.RichTextBox"/> in readonly configs.</para>
///
/// <para>Use in XAML (see global DataGrid style in <c>App.xaml</c>):
/// <code>&lt;Setter Property="helpers:ScrollBubbling.BubbleScroll" Value="True"/&gt;</code>
/// Use in code-behind for dynamically-created elements:
/// <code>ScrollBubbling.SetBubbleScroll(viewer, true);</code></para>
///
/// <para>This is the app-wide pattern for "wheel should scroll the page,
/// not the child control". If the inner control's own scroll should respond
/// to the wheel instead, manipulate that ScrollViewer's offset directly
/// rather than using this behavior.</para>
/// </summary>
public static class ScrollBubbling
{
    public static readonly DependencyProperty BubbleScrollProperty =
        DependencyProperty.RegisterAttached(
            "BubbleScroll", typeof(bool), typeof(ScrollBubbling),
            new PropertyMetadata(false, OnBubbleScrollChanged));

    public static bool GetBubbleScroll(DependencyObject obj) => (bool)obj.GetValue(BubbleScrollProperty);
    public static void SetBubbleScroll(DependencyObject obj, bool value) => obj.SetValue(BubbleScrollProperty, value);

    /// <summary>
    /// Applies bubbling to EVERY instance of a control type app-wide via a
    /// WPF class handler - for types where the attached-property route would
    /// need an implicit style, and an implicit style (even with BasedOn)
    /// shadows a theme's dynamically-applied implicit style. ComboBox is the
    /// canonical case: a style-based hookup stripped the Wpf.Ui dropdown
    /// theming. Call once at startup.
    /// </summary>
    public static void RegisterClassHandler(Type controlType)
        => EventManager.RegisterClassHandler(controlType, UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel));

    // ------------------------------------------------------------------
    // Open-dropdown scroll guard
    // ------------------------------------------------------------------

    /// <summary>
    /// Behaviour while a ComboBox dropdown is open:
    /// <list type="bullet">
    ///   <item>wheel over the LIST - scrolls the list only (the page's
    ///     SmoothScroll stands aside via <see cref="WheelIsOverOpenDropDown"/>);</item>
    ///   <item>wheel anywhere ELSE - closes the dropdown, then the page scrolls
    ///     normally in the same gesture;</item>
    ///   <item>any other scroll of a page the field sits in (scrollbar,
    ///     keyboard, touch, code) - closes the dropdown.</item>
    /// </list>
    /// A WPF Popup lives in its own top-level window and cannot follow the
    /// field it belongs to, so leaving it open during a page scroll strands the
    /// list mid-screen, detached from its control. Closing on the way out is
    /// the browser-like behaviour and keeps the page scrollable in one gesture.
    /// <para>Registered ONCE for every ComboBox in the app, hooked via the
    /// routed Loaded event because DropDownOpened / DropDownClosed are plain
    /// CLR events and cannot take class handlers.</para>
    /// </summary>
    public static void RegisterDropDownScrollGuard()
        => EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ComboBox),
            FrameworkElement.LoadedEvent, new RoutedEventHandler(OnComboBoxLoaded));

    private static void OnComboBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox cb) return;
        // Loaded can fire more than once for the same instance (re-templating,
        // virtualized containers) -- keep the subscription idempotent.
        cb.DropDownOpened -= OnDropDownOpened;
        cb.DropDownOpened += OnDropDownOpened;
        cb.DropDownClosed -= OnDropDownClosed;
        cb.DropDownClosed += OnDropDownClosed;
    }

    // Only one dropdown can be open at a time (opening one closes any other),
    // so a single tracked reference is enough to close it from the handler.
    private static System.Windows.Controls.ComboBox? _openDropDown;

    // Ancestor scrollers of the open dropdown, watched so that ANY scroll of
    // the page it sits in (scrollbar drag, keyboard, touch, programmatic)
    // dismisses it - not only the wheel.
    private static readonly List<ScrollViewer> _watchedScrollers = new();

    private static void OnDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox cb) return;
        _openDropDown = cb;
        if (Window.GetWindow(cb) is { } window)
        {
            window.PreviewMouseWheel -= CloseDropDownOnWheel;
            window.PreviewMouseWheel += CloseDropDownOnWheel;
        }

        // Watch the ancestor scrollers once the open has settled: focusing the
        // ComboBox can bring it into view, which is a scroll of its own and
        // must not dismiss the list the user just opened.
        cb.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if (!ReferenceEquals(_openDropDown, cb) || !cb.IsDropDownOpen) return;
            UnwatchScrollers();
            for (var cur = VisualTreeHelper.GetParent(cb); cur is not null; cur = VisualTreeHelper.GetParent(cur))
            {
                if (cur is not ScrollViewer sv) continue;
                sv.ScrollChanged += CloseDropDownOnScroll;
                _watchedScrollers.Add(sv);
            }
        });
    }

    private static void OnDropDownClosed(object? sender, EventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox cb) return;
        if (ReferenceEquals(_openDropDown, cb)) _openDropDown = null;
        UnwatchScrollers();
        if (Window.GetWindow(cb) is { } window)
            window.PreviewMouseWheel -= CloseDropDownOnWheel;
    }

    private static void UnwatchScrollers()
    {
        foreach (var sv in _watchedScrollers) sv.ScrollChanged -= CloseDropDownOnScroll;
        _watchedScrollers.Clear();
    }

    /// <summary>
    /// The page under an open dropdown moved: the popup cannot follow (it is a
    /// separate top-level window positioned once, at open), so dismiss it
    /// rather than leave it hanging where the field used to be.
    /// </summary>
    private static void CloseDropDownOnScroll(object sender, ScrollChangedEventArgs e)
    {
        // ScrollChanged BUBBLES. The list inside the popup has its own
        // ScrollViewer, and scrolling it raises ScrollChanged up through the
        // Popup into every ancestor viewer watched here - so only a change
        // reported by the watched viewer ITSELF counts as "the page moved".
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        if (e.VerticalChange == 0 && e.HorizontalChange == 0) return;
        if (_openDropDown is { IsDropDownOpen: true } cb)
            cb.IsDropDownOpen = false;
    }

    /// <summary>
    /// Wheel outside the popup: close the list and let the scroll continue, so
    /// one gesture both dismisses the dropdown and moves the page. Closing
    /// raises DropDownClosed, which unhooks this handler.
    /// </summary>
    private static void CloseDropDownOnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_openDropDown is not { IsDropDownOpen: true } cb) return;
        if (WheelIsOverOpenDropDown(e)) return;
        cb.IsDropDownOpen = false;
    }

    /// <summary>
    /// True when a wheel event is over the OPEN dropdown list itself, i.e. the
    /// wheel should scroll the list and nothing else.
    /// <para>An open ComboBox holds the mouse captured for its subtree
    /// (WPF's <c>Mouse.Capture(comboBox, CaptureMode.SubTree)</c>). While it
    /// does, a wheel anywhere OUTSIDE the popup is delivered with the ComboBox
    /// itself as OriginalSource - so "is the source a descendant of the
    /// ComboBox" is true for every wheel and cannot tell the page from the list
    /// (the 0.6.321 guard used exactly that test and let the page scroll away
    /// under an open list). Only a source inside the ComboBox's own Popup means
    /// the pointer is over the list.</para>
    /// </summary>
    public static bool WheelIsOverOpenDropDown(MouseWheelEventArgs e)
    {
        if (_openDropDown is not { IsDropDownOpen: true } cb) return false;
        if (e.OriginalSource is not DependencyObject src) return false;
        var popup = FindPopup(src);
        // The Popup must be the ComboBox's own (a template part below it), not
        // some popup the ComboBox happens to live in (account flyout, filters).
        return popup is not null && IsWithin(popup, cb);
    }

    /// <summary>
    /// Nearest Popup above <paramref name="node"/>. Popup content is a visual
    /// child of the popup's own root (which has no visual parent) and a
    /// LOGICAL child of the Popup, so the logical parent is checked at every
    /// step of the visual walk - waiting for the visual chain to run out
    /// would end at the popup root and never reach the Popup.
    /// </summary>
    private static System.Windows.Controls.Primitives.Popup? FindPopup(DependencyObject node)
    {
        for (var cur = node; cur is not null;
             cur = VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur))
        {
            if (cur is System.Windows.Controls.Primitives.Popup p) return p;
            if (LogicalTreeHelper.GetParent(cur) is System.Windows.Controls.Primitives.Popup lp) return lp;
        }
        return null;
    }

    /// <summary>Visual-then-logical ancestor walk (crosses the popup boundary).</summary>
    private static bool IsWithin(DependencyObject node, DependencyObject ancestor)
    {
        for (var cur = node; cur is not null;
             cur = VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur))
        {
            if (ReferenceEquals(cur, ancestor)) return true;
        }
        return false;
    }

    private static void OnBubbleScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        if ((bool)e.NewValue)
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        else
            element.PreviewMouseWheel -= OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject d) return;

        // When a ComboBox dropdown is open, WM_MOUSEWHEEL can fall through
        // from the Popup HWND to the main window if the dropdown doesn't
        // consume it. The OriginalSource ends up in the host's visual tree
        // (not the Popup's), so we check the ComboBox state directly and
        // bail so the wheel scrolls the dropdown, not the page.
        if (sender is UIElement element && HasOpenComboBox(element))
            return;

        var parent = FindParent<ScrollViewer>(d);
        if (parent is null) return;

        var ev = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender
        };
        parent.RaiseEvent(ev);
        e.Handled = true;
    }

    private static bool HasOpenComboBox(DependencyObject parent)
    {
        if (parent is System.Windows.Controls.ComboBox { IsDropDownOpen: true })
            return true;

        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            if (HasOpenComboBox(VisualTreeHelper.GetChild(parent, i)))
                return true;
        }
        return false;
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(child);
        while (current is not null)
        {
            if (current is T t) return t;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
