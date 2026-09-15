using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Orientation = System.Windows.Controls.Orientation;   // WinForms also declares this

namespace Wrapp.Services;

/// <summary>
/// The replace-placeholders confirm dialog: a token-mapping preview built from
/// <see cref="PlaceholderExpandReport.Tokens"/>. Each token found in the scope
/// gets a row - a pill badge with the token and an occurrence count, an arrow,
/// and the value it will become. Sensitive values are redacted to dots exactly
/// as in Settings &gt; Placeholders; the plaintext never enters the dialog's
/// visual tree. Tokens left as-is (empty value / unknown name) are listed
/// below the replaced ones with the reason.
/// </summary>
public static class PlaceholderPreviewDialog
{
    private const string Dots = "••••••••";

    /// <summary>Shows the preview and returns true when Replace was clicked.</summary>
    public static Task<bool> ConfirmAsync(
        string title, PlaceholderExpandReport aggregate, int changedFieldCount)
        => FluentDialog.ShowSelectAsync(
            title, BuildContent(aggregate, changedFieldCount), "Replace", "Cancel");

    /// <summary>Builds the dialog body. Internal so tests can inspect the tree.</summary>
    internal static FrameworkElement BuildContent(
        PlaceholderExpandReport aggregate, int changedFieldCount)
    {
        var root = new StackPanel { MinWidth = 380, MaxWidth = 560 };

        var header = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        header.Inlines.Add(new Run($"{aggregate.Replaced} ") { FontWeight = FontWeights.SemiBold });
        header.Inlines.Add(new Run("placeholder occurrence(s) will be replaced across "));
        header.Inlines.Add(new Run($"{changedFieldCount} ") { FontWeight = FontWeights.SemiBold });
        header.Inlines.Add(new Run("field(s)."));
        root.Children.Add(header);

        // Replaced tokens first, then left-as-is (empty, then unknown).
        var rows = new StackPanel();
        foreach (var token in aggregate.Tokens
                     .OrderBy(t => t.Outcome switch
                     {
                         PlaceholderTokenOutcome.Replaced => 0,
                         PlaceholderTokenOutcome.LeftEmpty => 1,
                         _ => 2,
                     }))
            rows.Children.Add(BuildTokenRow(token));

        var scroll = new ScrollViewer
        {
            Content = rows,
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 8, 0),
        };
        Wrapp.Helpers.SmoothScroll.SetEnabled(scroll, true);
        root.Children.Add(scroll);

        if (aggregate.TouchedSensitive)
        {
            var warn = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 0),
                Text = "Warning: sensitive value(s) " +
                       string.Join(", ", aggregate.SensitiveReplaced.Select(n => "{{" + n + "}}")) +
                       " will be written into the bundle as plaintext (Config.json / script " +
                       "content). Anyone with access to the bundle can read them.",
            };
            warn.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
            root.Children.Add(warn);
        }

        var footer = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Margin = new Thickness(0, 10, 0, 0),
            Text = "Replaced values are inserted exactly as stored; this cannot be undone " +
                   "except by editing the fields back (scripts: File History).",
        };
        footer.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        root.Children.Add(footer);

        return root;
    }

    private static FrameworkElement BuildTokenRow(PlaceholderTokenUse token)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Pill badge: {{Token}} ×N
        var badgeContent = new StackPanel { Orientation = Orientation.Horizontal };
        var nameText = new TextBlock
        {
            Text = "{{" + token.Name + "}}",
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        badgeContent.Children.Add(nameText);

        var countText = new TextBlock
        {
            Text = $"×{token.Count}",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"{token.Count} occurrence(s) found in this scope",
        };
        countText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        badgeContent.Children.Add(countText);

        var badge = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 2, 8, 2),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = badgeContent,
        };
        badge.SetResourceReference(Border.BackgroundProperty, "InputBgBrush");
        badge.SetResourceReference(Border.BorderBrushProperty, "AppBorderBrush");
        grid.Children.Add(badge);

        var arrow = new TextBlock
        {
            Text = "→",
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Grid.SetColumn(arrow, 1);
        grid.Children.Add(arrow);

        var value = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
        };
        switch (token.Outcome)
        {
            case PlaceholderTokenOutcome.Replaced when token.IsSensitive:
                value.Text = Dots;
                value.ToolTip = "Sensitive value - stored encrypted, shown redacted";
                value.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                break;
            case PlaceholderTokenOutcome.Replaced:
                value.Text = PlaceholderApplyService.TruncateValue(token.Value);
                value.FontFamily = new System.Windows.Media.FontFamily("Consolas");
                value.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                break;
            case PlaceholderTokenOutcome.LeftEmpty:
                value.Text = "no value yet - left as-is";
                value.FontStyle = FontStyles.Italic;
                value.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                break;
            default:
                value.Text = "unknown name - left as-is";
                value.FontStyle = FontStyles.Italic;
                value.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                break;
        }
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);

        return grid;
    }
}
