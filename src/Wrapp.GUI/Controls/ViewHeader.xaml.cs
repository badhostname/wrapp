using System.Windows;
using System.Windows.Controls;
using Wrapp.Services;

namespace Wrapp.Controls;

/// <summary>
/// View-level header used at the top of full-screen views (Settings, Detection,
/// Git History, etc.). Mirrors the inline header in <c>LogsView.xaml</c>: a
/// large title on the left, an info button that opens the associated help
/// popup, an optional right-side content slot for view-specific actions (path
/// display, explorer shortcut, refresh button), and a bottom divider that
/// separates the header from the view body.
///
/// <para>Distinct from <see cref="SectionHeader"/>, which is a smaller inline
/// header used within form cards. Both use the same Markdown help-popup path
/// (<see cref="SectionHeader.BuildFormattedPanel"/>) so one help key drives
/// both surfaces.</para>
/// </summary>
public partial class ViewHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(ViewHeader),
            new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty HelpKeyProperty =
        DependencyProperty.Register(nameof(HelpKey), typeof(string), typeof(ViewHeader),
            new PropertyMetadata(string.Empty, OnHelpKeyChanged));

    public static readonly DependencyProperty RightContentProperty =
        DependencyProperty.Register(nameof(RightContent), typeof(object), typeof(ViewHeader),
            new PropertyMetadata(null));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>
    /// Resource key in <c>HelpContent.xaml</c> for this view's help text.
    /// When empty, the info button is hidden.
    /// </summary>
    public string HelpKey
    {
        get => (string)GetValue(HelpKeyProperty);
        set => SetValue(HelpKeyProperty, value);
    }

    /// <summary>
    /// Optional content rendered on the right side of the header (after the
    /// flexible spacer). Typical use: a path display + explorer shortcut
    /// button, mirroring the <c>LogsView</c> header.
    /// </summary>
    public object? RightContent
    {
        get => GetValue(RightContentProperty);
        set => SetValue(RightContentProperty, value);
    }

    public ViewHeader()
    {
        InitializeComponent();
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ViewHeader vh)
            vh.TitleText.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnHelpKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ViewHeader vh)
            vh.InfoButton.Visibility = string.IsNullOrEmpty(e.NewValue as string)
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

        var panel = SectionHeader.BuildFormattedPanel(content, this);
        // Scrollable wrapper for the gentle wheel (see SectionHeader.ShowHelp).
        await FluentDialog.ShowScrollableContentAsync($"Help: {Title}", panel, "Close");
    }
}
