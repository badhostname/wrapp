using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Wrapp.Models;

namespace Wrapp;

/// <summary>
/// Converts an int to Visibility: 0 = Collapsed, > 0 = Visible.
/// Used for validation badge overlays and error count badges.
/// </summary>
public sealed class IntToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Inverts a boolean value: true -> false, false -> true.
/// Used for disabling controls while an operation is in progress.
/// </summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}

/// <summary>
/// Converts a bool to Visibility with inverse logic: true = Collapsed, false = Visible.
/// Used for disabled overlays (show overlay when feature is disabled = false).
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps ConnectionState enum to a theme-aware brush from application resources.
/// </summary>
public sealed class ConnectionStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is ConnectionState state ? state switch
        {
            ConnectionState.Connected    => "ConnectedBrush",
            ConnectionState.Disconnected => "DisconnectedBrush",
            ConnectionState.Error        => "DisconnectedBrush",
            ConnectionState.Checking     => "CheckingBrush",
            ConnectionState.Skipped      => "SkippedBrush",
            _                            => "UnknownLineBrush"
        } : "UnknownLineBrush";

        return Application.Current.TryFindResource(key) ?? System.Windows.Media.Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps PackageOutcome enum to a theme-aware brush from application resources.
/// </summary>
public sealed class PackageOutcomeToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is PackageOutcome outcome ? outcome switch
        {
            PackageOutcome.Succeeded      => "ConnectedBrush",
            PackageOutcome.Failed         => "DisconnectedBrush",
            PackageOutcome.Running        => "RunningBrush",
            PackageOutcome.PartialSuccess => "CheckingBrush",
            PackageOutcome.Cancelled      => "WarningBrush",
            _                             => "UnknownLineBrush"
        } : "UnknownLineBrush";

        return Application.Current.TryFindResource(key) ?? System.Windows.Media.Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Compares bound string value to ConverterParameter. Returns Visible on match, Collapsed otherwise.
/// Supports pipe-separated multi-match: "embedded|manual" matches either value.
/// </summary>
public sealed class StringMatchToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value?.ToString() ?? "";
        var targets = (parameter?.ToString() ?? "").Split('|');
        return targets.Any(t => t.Equals(s, StringComparison.OrdinalIgnoreCase))
            ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Returns Collapsed for null/empty strings, Visible otherwise.
/// </summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value?.ToString()) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Returns "-" for null or empty strings. Used in inventory detail fields as placeholder.
/// </summary>
public sealed class EmptyToPlaceholderConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value?.ToString();
        return string.IsNullOrWhiteSpace(s) ? "-" : s;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Selects the Markdown-stripping <see cref="DataTemplate"/> only for string
/// tooltip content. Returns <c>null</c> for any other content type (UIElement
/// tooltips set via property-element syntax — e.g. a custom TextBlock
/// inside <c>&lt;Button.ToolTip&gt;</c> — render directly without going
/// through the template). Wired to the global <c>ToolTip</c> style in
/// <c>App.xaml</c> via <c>ContentTemplateSelector</c>.
/// </summary>
public sealed class ToolTipContentTemplateSelector : System.Windows.Controls.DataTemplateSelector
{
    public System.Windows.DataTemplate? StringTemplate { get; set; }

    public override System.Windows.DataTemplate? SelectTemplate(object item, DependencyObject container)
        => item is string ? StringTemplate : null;
}

/// <summary>
/// Strips Markdown formatting characters (bold/italic asterisks, inline-code
/// backticks) so a single help-content key can power both the rich popup
/// (rendered by HelpMarkdownRenderer) and a plain tooltip without the operator
/// seeing raw `**asterisks**` floating in the tooltip text.
///
/// <para>Wired into the global <c>ToolTip</c> style in <c>App.xaml</c> so every
/// tooltip in the app gets the cleanup automatically — consumers don't need to
/// remember to apply the converter at the call-site.</para>
/// </summary>
public sealed class MarkdownToPlainTextConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return null;
        var s = value as string ?? value.ToString();
        if (string.IsNullOrEmpty(s)) return s;

        // Bold: **text** → text. Process before single-asterisk italic so we
        // don't accidentally consume the bold markers as two italic runs.
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"\*\*(.+?)\*\*", "$1",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        // Italic: *text* → text (avoids matching standalone asterisks by
        // requiring a word character on both sides of the marker pair).
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"(?<!\*)\*(?!\*)([^\*\n]+?)(?<!\*)\*(?!\*)", "$1",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        // Inline code: `text` → text
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"`([^`\n]+?)`", "$1",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        return s;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
