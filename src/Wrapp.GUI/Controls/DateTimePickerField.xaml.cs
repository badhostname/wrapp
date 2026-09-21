using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using Wrapp.Helpers;
using Wrapp.Services;

namespace Wrapp.Controls;

/// <summary>
/// TextBox with Material Design calendar + clock popup and ASAP button
/// for ISO 8601 UTC date/time entry.
/// </summary>
public partial class DateTimePickerField : UserControl
{
    /// <summary>The bound text value (ISO 8601 date/time string).</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(DateTimePickerField),
        new FrameworkPropertyMetadata(string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnTextChanged, null, false, System.Windows.Data.UpdateSourceTrigger.PropertyChanged));

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DateTimePickerField picker)
        {
            picker.UpdateAsapDisplay();
            picker.UpdatePastTimeWarning();
        }
    }

    /// <summary>
    /// The value to set when ASAP is clicked.
    /// Intune: "0001-01-01T00:00:00.000Z"
    /// SCCM: null (uses current UTC time)
    /// </summary>
    public static readonly DependencyProperty AsapValueProperty = DependencyProperty.Register(
        nameof(AsapValue), typeof(string), typeof(DateTimePickerField),
        new PropertyMetadata(null));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? AsapValue
    {
        get => (string?)GetValue(AsapValueProperty);
        set => SetValue(AsapValueProperty, value);
    }

    private CustomColorTheme? _mdixTheme;
    private bool _syncingDateBoxes;
    private bool _syncingTimeBoxes;
    private ScrollViewer? _parentScrollViewer;

    public DateTimePickerField()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // -------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        foreach (var dict in PopupBorder.Resources.MergedDictionaries)
        {
            if (dict is CustomColorTheme theme)
            {
                _mdixTheme = theme;
                break;
            }
        }
        SyncMdixTheme();
        App.ThemeChanged += OnAppThemeChanged;

        // Listen for Clock.Time changes to sync time text boxes
        var dpd = DependencyPropertyDescriptor.FromProperty(Clock.TimeProperty, typeof(Clock));
        dpd?.AddValueChanged(TimeClock, OnClockTimeChanged);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.ThemeChanged -= OnAppThemeChanged;

        var dpd = DependencyPropertyDescriptor.FromProperty(Clock.TimeProperty, typeof(Clock));
        dpd?.RemoveValueChanged(TimeClock, OnClockTimeChanged);
    }

    // -------------------------------------------------------------------
    // Theme sync
    // -------------------------------------------------------------------

    private void OnAppThemeChanged(string _) => SyncMdixTheme();

    private void SyncMdixTheme()
    {
        if (_mdixTheme is null) return;

        bool isDark = App.CurrentMonacoTheme == "vs-dark";
        _mdixTheme.BaseTheme = isDark ? BaseTheme.Dark : BaseTheme.Light;
        _mdixTheme.PrimaryColor = isDark
            ? (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#9ac9cf")!
            : (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#366372")!;
        _mdixTheme.SecondaryColor = _mdixTheme.PrimaryColor;
    }

    // -------------------------------------------------------------------
    // Scroll blocking: prevent parent ScrollViewer from scrolling while
    // the popup is open (mirrors the ComboBox scroll fix pattern).
    // -------------------------------------------------------------------

    private void CalendarPopup_Opened(object? sender, EventArgs e)
    {
        _parentScrollViewer = FindAncestor<ScrollViewer>(this);
        if (_parentScrollViewer != null)
            _parentScrollViewer.PreviewMouseWheel += BlockParentScroll;
    }

    private void CalendarPopup_Closed(object? sender, EventArgs e)
    {
        if (_parentScrollViewer != null)
        {
            _parentScrollViewer.PreviewMouseWheel -= BlockParentScroll;
            _parentScrollViewer = null;
        }
    }

    private static void BlockParentScroll(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
    }

    // Prevent mouse-wheel from leaking through popup to content behind
    private void PopupContent_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
            if (d is T t) return t;
        }
        return null;
    }

    // -------------------------------------------------------------------
    // Button handlers
    // -------------------------------------------------------------------

    private void CalendarButton_Click(object sender, RoutedEventArgs e)
    {
        if (CalendarPopup.IsOpen)
        {
            CalendarPopup.IsOpen = false;
            return;
        }

        // Pre-select from current value (skip ASAP sentinel 0001-01-01)
        if (TryParseUtc(Text, out var current) && current.Year >= 2020)
        {
            DateCalendar.SelectedDate = current.Date;
            DateCalendar.DisplayDate = current.Date;
            TimeClock.Time = new DateTime(1, 1, 1, current.Hour, current.Minute, 0);
        }
        else
        {
            var now = SystemClock.UtcNow;
            DateCalendar.SelectedDate = now.Date;
            DateCalendar.DisplayDate = now.Date;
            TimeClock.Time = new DateTime(1, 1, 1, now.Hour, now.Minute, 0);
        }

        SyncDateBoxesFromCalendar();
        SyncTimeBoxesFromClock();
        CalendarPopup.IsOpen = true;
    }

    private void AsapButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(AsapValue))
            Text = AsapValue;
        else
            Text = SystemClock.UtcNow.ToIsoUtc();
    }

    private void SetDateTime_Click(object sender, RoutedEventArgs e)
    {
        var date = DateCalendar.SelectedDate ?? SystemClock.UtcNow.Date;
        int hour24 = Get24HourFromClock();

        var dt = new DateTime(date.Year, date.Month, date.Day,
            hour24, TimeClock.Time.Minute, 0, DateTimeKind.Utc);
        Text = dt.ToIsoUtc();
        CalendarPopup.IsOpen = false;
    }

    private int Get24HourFromClock()
    {
        int h = TimeClock.Time.Hour % 12; // normalize to 0-11
        return TimeClock.IsPostMeridiem ? h + 12 : h;
    }

    // -------------------------------------------------------------------
    // Calendar <-> date text boxes sync
    // -------------------------------------------------------------------

    private void DateCalendar_SelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        SyncDateBoxesFromCalendar();
    }

    private void SyncDateBoxesFromCalendar()
    {
        if (DateCalendar.SelectedDate is not DateTime d) return;
        _syncingDateBoxes = true;
        MonthBox.Text = d.Month.ToString("D2");
        DayBox.Text = d.Day.ToString("D2");
        YearBox.Text = d.Year.ToString("D4");
        _syncingDateBoxes = false;
    }

    private void DateBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_syncingDateBoxes) return;
        if (int.TryParse(MonthBox.Text, out int m) &&
            int.TryParse(DayBox.Text, out int d) &&
            int.TryParse(YearBox.Text, out int y) &&
            m >= 1 && m <= 12 && d >= 1 && d <= 31 && y >= 2020)
        {
            try
            {
                var date = new DateTime(y, m, d);
                _syncingDateBoxes = true;
                DateCalendar.SelectedDate = date;
                DateCalendar.DisplayDate = date;
                _syncingDateBoxes = false;
            }
            catch { /* invalid date combination, ignore */ }
        }
    }

    // -------------------------------------------------------------------
    // Clock <-> time text boxes sync
    // -------------------------------------------------------------------

    private void OnClockTimeChanged(object? sender, EventArgs e)
    {
        SyncTimeBoxesFromClock();
    }

    private void SyncTimeBoxesFromClock()
    {
        _syncingTimeBoxes = true;
        int h = TimeClock.Time.Hour;
        int hour12 = h % 12;
        if (hour12 == 0) hour12 = 12;
        HourBox.Text = hour12.ToString("D2");
        MinuteBox.Text = TimeClock.Time.Minute.ToString("D2");
        _syncingTimeBoxes = false;
    }

    private void TimeBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_syncingTimeBoxes) return;
        if (int.TryParse(HourBox.Text, out int h) &&
            int.TryParse(MinuteBox.Text, out int m) &&
            h >= 1 && h <= 12 && m >= 0 && m <= 59)
        {
            _syncingTimeBoxes = true;
            TimeClock.Time = new DateTime(1, 1, 1, h, m, 0);
            _syncingTimeBoxes = false;
        }
    }

    // -------------------------------------------------------------------
    // ASAP overlay + past-time warning
    // -------------------------------------------------------------------

    private void UpdateAsapDisplay()
    {
        if (AsapOverlay is null || ValueBox is null) return;

        bool isAsap = IsCurrentValueAsap();
        AsapOverlay.Visibility = isAsap ? Visibility.Visible : Visibility.Collapsed;

        // Hide the raw ISO text behind the overlay by making it transparent
        if (isAsap)
            ValueBox.Foreground = System.Windows.Media.Brushes.Transparent;
        else
            ValueBox.ClearValue(ForegroundProperty); // revert to style default
    }

    private void UpdatePastTimeWarning()
    {
        if (PastTimeWarning is null) return;

        bool isPast = false;
        if (TryParseUtc(Text, out var dt) && dt.Year >= 2020)
            isPast = dt < SystemClock.UtcNow;

        PastTimeWarning.Visibility = isPast ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool IsCurrentValueAsap()
    {
        var text = Text;
        if (string.IsNullOrEmpty(text)) return true;
        if (!string.IsNullOrEmpty(AsapValue) && text == AsapValue) return true;
        return false;
    }

    // -------------------------------------------------------------------
    // Parsing (all values treated as UTC, no local timezone conversion)
    // -------------------------------------------------------------------

    private static bool TryParseUtc(string? value, out DateTime result)
        => DateTimeFormats.TryParseFlexible(value ?? string.Empty, out result);
}
