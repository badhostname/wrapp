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
/// (always — its internal scrollviewer captures wheel regardless of the
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
    /// WPF class handler — for types where the attached-property route would
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
    /// Wheel behaviour while a ComboBox dropdown is open:
    /// <list type="bullet">
    ///   <item>over the LIST — scrolls the list (its popup is a separate HWND,
    ///     so those wheel events never reach the window handler below);</item>
    ///   <item>anywhere ELSE — closes the dropdown, then the page scrolls
    ///     normally.</item>
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

    private static void OnDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox cb) return;
        _openDropDown = cb;
        if (Window.GetWindow(cb) is not { } window) return;
        window.PreviewMouseWheel -= CloseDropDownOnWheel;
        window.PreviewMouseWheel += CloseDropDownOnWheel;
    }

    private static void OnDropDownClosed(object? sender, EventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox cb) return;
        if (ReferenceEquals(_openDropDown, cb)) _openDropDown = null;
        if (Window.GetWindow(cb) is { } window)
            window.PreviewMouseWheel -= CloseDropDownOnWheel;
    }

    /// <summary>
    /// Wheel outside the popup: close the list and let the scroll continue, so
    /// one gesture both dismisses the dropdown and moves the page. Closing
    /// raises DropDownClosed, which unhooks this handler.
    /// </summary>
    private static void CloseDropDownOnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_openDropDown is not { IsDropDownOpen: true } cb) return;

        // A Popup renders in its own window but stays LOGICALLY parented to the
        // ComboBox, so wheel events over the list still route through here.
        // Those must scroll the list, not close it.
        if (e.OriginalSource is DependencyObject src && IsWithin(src, cb)) return;

        cb.IsDropDownOpen = false;
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
        // Check if any ComboBox within this element has its dropdown open.
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
