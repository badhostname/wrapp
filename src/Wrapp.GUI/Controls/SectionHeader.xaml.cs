using System.Windows;
using System.Windows.Controls;
using Wrapp.Services;

namespace Wrapp.Controls;

public partial class SectionHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SectionHeader),
            new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty HelpKeyProperty =
        DependencyProperty.Register(nameof(HelpKey), typeof(string), typeof(SectionHeader),
            new PropertyMetadata(string.Empty, OnHelpKeyChanged));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string HelpKey
    {
        get => (string)GetValue(HelpKeyProperty);
        set => SetValue(HelpKeyProperty, value);
    }

    public SectionHeader()
    {
        InitializeComponent();
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SectionHeader sh)
            sh.TitleText.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnHelpKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SectionHeader sh)
            sh.InfoButton.Visibility = string.IsNullOrEmpty(e.NewValue as string)
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    private async void InfoButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(HelpKey)) return;

        var content = TryFindResource(HelpKey) as string;
        if (string.IsNullOrEmpty(content))
        {
            await FluentDialog.ShowInfoAsync($"Help: {Title}", $"No help content found for key: {HelpKey}");
            return;
        }

        var panel = BuildFormattedPanel(content, this);
        // Scrollable wrapper = the app's gentle wheel behavior; a bare
        // ShowContentAsync scrolls via the ContentDialog's own template
        // viewer, which has no SmoothScroll and feels fast and choppy.
        await FluentDialog.ShowScrollableContentAsync($"Help: {Title}", panel, "Close");
    }

    public static StackPanel BuildFormattedPanel(string text, FrameworkElement resourceSource)
        => HelpMarkdownRenderer.Render(text, resourceSource);
}
