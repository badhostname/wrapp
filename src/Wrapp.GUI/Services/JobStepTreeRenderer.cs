using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using Wrapp.Models;
using Binding = System.Windows.Data.Binding;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;

namespace Wrapp.Services;

/// <summary>
/// Renders a <see cref="JobStepTree"/> into a WPF <see cref="FrameworkElement"/>
/// for the Background Jobs popup's expanded card area. Generic counterpart to
/// <see cref="DeploymentPlanRenderer"/> (which stays specialised to the
/// packaging-run shape). Wired in via <c>JobContextRendererConverter</c>.
///
/// <para>Each step renders a row with a coloured status glyph, the step
/// title, a muted status message, and an elapsed-time label. Sub-steps
/// render indented beneath their parent. Every visual element binds
/// directly to the observable step so running jobs refresh live without
/// the renderer re-running.</para>
/// </summary>
public static class JobStepTreeRenderer
{
    public static FrameworkElement Render(JobStepTree tree)
    {
        var panel = new StackPanel { MinWidth = 520 };

        foreach (var step in tree.Steps)
            panel.Children.Add(BuildStepRow(step, indent: 0));

        return panel;
    }

    // ─── Per-step row (recursive for sub-steps) ─────────────────────

    private static FrameworkElement BuildStepRow(JobStep step, int indent)
    {
        var primary   = TryBrush("TextPrimaryBrush");
        var secondary = TryBrush("TextSecondaryBrush");
        var muted     = TryBrush("TextMutedBrush");

        var outer = new StackPanel();

        // The step row itself: [glyph] [title / status] [duration]
        var row = new Grid
        {
            Margin = new Thickness(indent * 18, 3, 0, 3),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // glyph
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) }); // gap
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // title/message
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // duration

        // ── Glyph ──
        var glyph = new TextBlock
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize   = 12,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
        // Bind both the glyph string AND its colour to State -- switches
        // when the step transitions live.
        glyph.SetBinding(TextBlock.TextProperty,
            new Binding(nameof(JobStep.State)) { Source = step, Converter = StateToGlyphConverter.Instance });
        glyph.SetBinding(TextBlock.ForegroundProperty,
            new Binding(nameof(JobStep.State)) { Source = step, Converter = StateToBrushConverter.Instance });
        Grid.SetColumn(glyph, 0);
        row.Children.Add(glyph);

        // ── Title + status message ──
        var titleBlock = new StackPanel { Orientation = Orientation.Vertical };
        titleBlock.Children.Add(new TextBlock
        {
            Text       = step.Title,
            FontSize   = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = primary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var msg = new TextBlock
        {
            FontSize   = 11,
            Foreground = secondary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        msg.SetBinding(TextBlock.TextProperty,
            new Binding(nameof(JobStep.StatusMessage)) { Source = step });
        // Collapse the second line when no message.
        msg.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(JobStep.StatusMessage))
        {
            Source = step,
            Converter = EmptyStringToCollapsedConverter.Instance,
        });
        titleBlock.Children.Add(msg);
        Grid.SetColumn(titleBlock, 2);
        row.Children.Add(titleBlock);

        // ── Duration ──
        var duration = new TextBlock
        {
            FontSize   = 11,
            Foreground = muted,
            FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin     = new Thickness(8, 0, 0, 0),
        };
        duration.SetBinding(TextBlock.TextProperty,
            new Binding(nameof(JobStep.DurationDisplay)) { Source = step });
        Grid.SetColumn(duration, 3);
        row.Children.Add(duration);

        outer.Children.Add(row);

        // ── Sub-steps (recursive) ──
        if (step.SubSteps.Count > 0)
        {
            foreach (var child in step.SubSteps)
                outer.Children.Add(BuildStepRow(child, indent + 1));
        }

        return outer;
    }

    // ─── Helpers ───────────────────────────────────────────────────

    private static Brush TryBrush(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    // ─── Value converters (instances reused across every row) ──────

    /// <summary>Step-state → Segoe MDL2 glyph.</summary>
    private sealed class StateToGlyphConverter : IValueConverter
    {
        public static readonly StateToGlyphConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => value switch
            {
                StepState.Pending   => "\uE91F", // Circle
                StepState.Running   => "\uE895", // SyncFolder-ish / spinner glyph we treat as "in progress"
                StepState.Succeeded => "\uE930", // Completed
                StepState.Failed    => "\uEA39", // ErrorBadge12
                StepState.Skipped   => "\uE711", // Cancel / skipped
                _                   => "\uE91F",
            };
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c)
            => throw new NotSupportedException();
    }

    /// <summary>Step-state → foreground brush (theme-aware where possible).</summary>
    private sealed class StateToBrushConverter : IValueConverter
    {
        public static readonly StateToBrushConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => value switch
            {
                StepState.Pending   => TryBrush("TextMutedBrush"),
                StepState.Running   => TryBrush("RunningBrush"),
                StepState.Succeeded => TryBrush("ConnectedBrush"),
                StepState.Failed    => TryBrush("DisconnectedBrush"),
                StepState.Skipped   => TryBrush("SkippedBrush"),
                _                   => TryBrush("TextMutedBrush"),
            };
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c)
            => throw new NotSupportedException();
    }

    /// <summary>Empty string → Collapsed so the status-message TextBlock
    /// doesn't reserve space on rows that have no message yet.</summary>
    private sealed class EmptyStringToCollapsedConverter : IValueConverter
    {
        public static readonly EmptyStringToCollapsedConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c)
            => throw new NotSupportedException();
    }
}
