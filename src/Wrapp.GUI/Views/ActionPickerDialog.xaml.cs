using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wrapp.Models;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using Image = System.Windows.Controls.Image;

namespace Wrapp.Views;

/// <summary>
/// Reusable card-style action picker shown inside a FluentDialog.
/// Presents a list of ActionPickerOption cards with icon, title, and description.
/// The caller reads <see cref="SelectedKey"/> after the dialog closes.
/// </summary>
public partial class ActionPickerDialog : UserControl
{
    /// <summary>The Key of the currently selected option.</summary>
    public string SelectedKey { get; private set; } = string.Empty;

    private readonly List<(Border Card, ActionPickerOption Option)> _cards = new();

    /// <summary>
    /// Creates a new action picker dialog.
    /// </summary>
    /// <param name="subtitle">Explanatory text shown above the cards.</param>
    /// <param name="options">The selectable action cards.</param>
    /// <param name="detailPanel">Optional content shown below the cards (e.g. file preview).</param>
    /// <param name="defaultKey">Key of the initially selected option. First enabled option if null.</param>
    public ActionPickerDialog(
        string subtitle,
        IReadOnlyList<ActionPickerOption> options,
        FrameworkElement? detailPanel = null,
        string? defaultKey = null)
    {
        InitializeComponent();

        SubtitleText.Text = subtitle;
        if (string.IsNullOrEmpty(subtitle))
            SubtitleText.Visibility = Visibility.Collapsed;

        foreach (var opt in options)
        {
            var card = BuildCard(opt);
            _cards.Add((card, opt));
            CardsPanel.Children.Add(card);
        }

        var defaultOpt = defaultKey is not null
            ? options.FirstOrDefault(o => o.Key == defaultKey && o.IsEnabled)
            : null;
        defaultOpt ??= options.FirstOrDefault(o => o.IsEnabled);
        if (defaultOpt is not null)
            SelectedKey = defaultOpt.Key;

        UpdateSelection();

        if (detailPanel is not null)
        {
            DetailSeparator.Visibility = Visibility.Visible;
            DetailContent.Content = detailPanel;
            DetailContent.Visibility = Visibility.Visible;
        }
    }

    private Border BuildCard(ActionPickerOption opt)
    {
        UIElement iconElement;
        if (opt.IconImage is not null)
        {
            iconElement = new Image
            {
                Source = opt.IconImage,
                Width = 32,
                Height = 32,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            RenderOptions.SetBitmapScalingMode((Image)iconElement, BitmapScalingMode.HighQuality);
        }
        else
        {
            iconElement = new TextBlock
            {
                Text = opt.Icon,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 20,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TryFindResource("AccentBgBrush") as Brush ?? Brushes.CornflowerBlue,
                Margin = new Thickness(0, 0, 12, 0)
            };
        }

        var title = new TextBlock
        {
            Text = opt.Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.White
        };

        var description = new TextBlock
        {
            Text = opt.Description,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray
        };

        var textStack = new StackPanel();
        textStack.Children.Add(title);
        textStack.Children.Add(description);
        Grid.SetColumn(textStack, 1);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(iconElement);
        grid.Children.Add(textStack);

        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(2),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 6),
            Background = TryFindResource("CardBgBrush") as Brush,
            BorderBrush = TryFindResource("AppBorderBrush") as Brush,
            Child = grid
        };

        if (opt.IsEnabled)
        {
            border.Cursor = Cursors.Hand;
            border.MouseLeftButtonDown += (_, _) =>
            {
                SelectedKey = opt.Key;
                UpdateSelection();
            };
        }
        else
        {
            border.Opacity = 0.4;
            border.Cursor = Cursors.Arrow;
            if (!string.IsNullOrEmpty(opt.DisabledReason))
            {
                border.ToolTip = opt.DisabledReason;
                ToolTipService.SetShowOnDisabled(border, true);
            }
        }

        return border;
    }

    private void UpdateSelection()
    {
        var accentBrush = TryFindResource("AccentBgBrush") as Brush
            ?? new SolidColorBrush(Color.FromRgb(0x9A, 0xC9, 0xCF));
        var normalBrush = TryFindResource("AppBorderBrush") as Brush
            ?? new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));

        foreach (var (card, opt) in _cards)
        {
            card.BorderBrush = opt.Key == SelectedKey ? accentBrush : normalBrush;
        }
    }
}
