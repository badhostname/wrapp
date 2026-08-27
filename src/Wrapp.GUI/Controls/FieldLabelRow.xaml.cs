using System.Windows;
using System.Windows.Controls;

namespace Wrapp.Controls;

/// <summary>
/// Standard field label row used across packages, deployments, assignments, and
/// Settings → Preferences. Renders three pieces of chrome around a label text:
///
/// <list type="bullet">
///   <item>A small lock glyph when <see cref="IsFieldEnabled"/> is <c>false</c>
///         (i.e., a <see cref="Wrapp.Models.FieldRule"/> has disabled the input)</item>
///   <item>The label text itself, styled with the global <c>FieldLabel</c> style</item>
///   <item>An amber warning glyph + "Required" text when <see cref="IsMissing"/>
///         is <c>true</c> (typically wired to a per-VM <c>IsXxxMissing</c> bool
///         such as <see cref="Wrapp.Models.AssignmentEntry.IsGroupIdMissing"/>)</item>
/// </list>
///
/// <para>Replaces the inline <c>StackPanel</c> + lock icon + warning icon
/// pattern that was repeated 14+ times across IntuneAssignmentDialog.xaml and
/// elsewhere. Consumers wire <see cref="IsFieldEnabled"/> to
/// <c>{Binding FieldStates[FieldName].IsEnabled}</c> on the entry/VM so the
/// chrome stays in lock-step with the underlying input's enabled state.</para>
/// </summary>
public partial class FieldLabelRow : UserControl
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(FieldLabelRow),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsFieldEnabledProperty =
        DependencyProperty.Register(nameof(IsFieldEnabled), typeof(bool), typeof(FieldLabelRow),
            new PropertyMetadata(true));

    public static readonly DependencyProperty IsMissingProperty =
        DependencyProperty.Register(nameof(IsMissing), typeof(bool), typeof(FieldLabelRow),
            new PropertyMetadata(false));

    public static readonly DependencyProperty HelpProperty =
        DependencyProperty.Register(nameof(Help), typeof(object), typeof(FieldLabelRow),
            new PropertyMetadata(null, OnHelpChanged));

    /// <summary>The label text displayed beside the chrome icons.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// Gates the lock-icon visibility. Bind to
    /// <c>{Binding FieldStates[FieldName].IsEnabled}</c>; when <c>false</c>,
    /// the lock glyph appears beside the label.
    /// </summary>
    public bool IsFieldEnabled
    {
        get => (bool)GetValue(IsFieldEnabledProperty);
        set => SetValue(IsFieldEnabledProperty, value);
    }

    /// <summary>
    /// When <c>true</c>, displays the warning glyph + "Required" text. Bind to
    /// a per-VM property such as <c>IsGroupIdMissing</c>.
    /// </summary>
    public bool IsMissing
    {
        get => (bool)GetValue(IsMissingProperty);
        set => SetValue(IsMissingProperty, value);
    }

    /// <summary>
    /// Help content (typically a <c>{StaticResource Help.Xxx.Yyy}</c> string)
    /// shown as the label's tooltip. Set as a generic <see cref="object"/> so
    /// either a string or a richer panel can be assigned.
    /// </summary>
    public object? Help
    {
        get => GetValue(HelpProperty);
        set => SetValue(HelpProperty, value);
    }

    public FieldLabelRow()
    {
        InitializeComponent();
    }

    private static void OnHelpChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FieldLabelRow row) return;
        row.LabelText.ToolTip = e.NewValue;

        // Propagate to the row's sibling input, exactly like the FieldLabel
        // style does for plain labels (App.PropagateFieldHelp). Without this,
        // every FieldLabelRow-labelled input in the app had help on the label
        // only — hovering the input itself showed nothing (0.6.318 audit).
        if (e.NewValue is null) return;
        if (row.IsLoaded)
        {
            App.PropagateFieldHelp(row, e.NewValue);
        }
        else
        {
            RoutedEventHandler? once = null;
            once = (_, _) =>
            {
                row.Loaded -= once;
                if (row.Help is not null) App.PropagateFieldHelp(row, row.Help);
            };
            row.Loaded += once;
        }
    }
}
