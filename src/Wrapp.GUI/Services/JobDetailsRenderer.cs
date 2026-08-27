using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Wrapp.Models;
using Binding = System.Windows.Data.Binding;      // WinForms also declares these
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using TextBox = System.Windows.Controls.TextBox;

namespace Wrapp.Services;

/// <summary>
/// Renders the general-purpose <see cref="JobDetails"/> context inside the
/// Background Jobs pop-up's expanded card: a two-column facts grid (label /
/// value, live-updating) and — when present — an error section with the
/// short code and the raw payload in a scrollable monospace box. The
/// structural sibling of <c>JobStepTreeRenderer</c>; wired in via
/// <c>JobContextRendererConverter</c>.
/// </summary>
public static class JobDetailsRenderer
{
    public static FrameworkElement Render(JobDetails details)
    {
        var root = new StackPanel { Margin = new Thickness(4, 6, 4, 2) };

        // Facts: ItemsControl so late-added facts appear live.
        var facts = new ItemsControl { ItemsSource = details.Facts };
        facts.ItemTemplate = BuildFactTemplate();
        root.Children.Add(facts);

        // Error section (code + raw body), visible only when populated.
        var errorPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        errorPanel.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(JobDetails.HasError))
        {
            Source = details,
            Converter = new System.Windows.Controls.BooleanToVisibilityConverter(),
        });

        var codeText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        };
        codeText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
        codeText.SetBinding(TextBlock.TextProperty, new Binding(nameof(JobDetails.ErrorCode))
        {
            Source = details,
            StringFormat = "Error: {0}",
        });
        errorPanel.Children.Add(codeText);

        var bodyText = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 11,
            MaxHeight = 140,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(1),
        };
        bodyText.SetResourceReference(Control.BorderBrushProperty, "AppBorderBrush");
        bodyText.SetResourceReference(Control.BackgroundProperty, "InputBgBrush");
        bodyText.SetBinding(TextBox.TextProperty, new Binding(nameof(JobDetails.ErrorBody))
        {
            Source = details,
            Mode = BindingMode.OneWay,
        });
        errorPanel.Children.Add(bodyText);

        root.Children.Add(errorPanel);
        return root;
    }

    private static DataTemplate BuildFactTemplate()
    {
        // <Grid cols="110,*,auto,auto"><Label/><Value/><Open/><Copy/></Grid>
        // The two icon buttons are the SAME pair the title bar uses (E838
        // open-in-Explorer + E8C8 copy) and appear only on path-like values.
        var grid = new FrameworkElementFactory(typeof(Grid));
        var c0 = new FrameworkElementFactory(typeof(ColumnDefinition));
        c0.SetValue(ColumnDefinition.WidthProperty, new GridLength(110));
        var c1 = new FrameworkElementFactory(typeof(ColumnDefinition));
        c1.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        var c2 = new FrameworkElementFactory(typeof(ColumnDefinition));
        c2.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        var c3 = new FrameworkElementFactory(typeof(ColumnDefinition));
        c3.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        grid.AppendChild(c0);
        grid.AppendChild(c1);
        grid.AppendChild(c2);
        grid.AppendChild(c3);
        grid.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 1));

        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(JobFact.Label)));
        label.SetValue(TextBlock.FontSizeProperty, 11.0);
        label.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        grid.AppendChild(label);

        var value = new FrameworkElementFactory(typeof(TextBlock));
        value.SetValue(Grid.ColumnProperty, 1);
        value.SetBinding(TextBlock.TextProperty, new Binding(nameof(JobFact.Value)));
        value.SetValue(TextBlock.FontSizeProperty, 11.0);
        value.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        value.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        grid.AppendChild(value);

        grid.AppendChild(PathIconButton(column: 2, glyph: "",
            tooltip: "Open in Explorer", OnOpenPathClick));
        grid.AppendChild(PathIconButton(column: 3, glyph: "",
            tooltip: "Copy path", OnCopyPathClick));

        return new DataTemplate { VisualTree = grid };
    }

    private static readonly System.Windows.Controls.BooleanToVisibilityConverter BoolToVis = new();

    private static FrameworkElementFactory PathIconButton(
        int column, string glyph, string tooltip, RoutedEventHandler onClick)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.TextProperty, glyph);
        text.SetValue(TextBlock.FontFamilyProperty, new System.Windows.Media.FontFamily("Segoe MDL2 Assets"));
        text.SetValue(TextBlock.FontSizeProperty, 10.0);
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var btn = new FrameworkElementFactory(typeof(Button));
        btn.SetValue(Grid.ColumnProperty, column);
        btn.SetValue(Control.PaddingProperty, new Thickness(4, 2, 4, 2));
        btn.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 0, 0));
        btn.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        btn.SetValue(FrameworkElement.ToolTipProperty, tooltip);
        btn.SetResourceReference(FrameworkElement.StyleProperty, "ToolbarBtn");
        btn.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(JobFact.IsPathValue))
        {
            Converter = BoolToVis,
        });
        btn.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, onClick);
        btn.AppendChild(text);
        return btn;
    }

    private static void OnOpenPathClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is JobFact fact && fact.IsPathValue)
            FluentDialog.OpenInExplorer(fact.Value);
    }

    private static void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not JobFact fact || !fact.IsPathValue) return;
        try { System.Windows.Clipboard.SetText(fact.Value); }
        catch { /* foreign clipboard owner */ }
    }
}
