using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wrapp.Views;

public partial class MsiIconPickerDialog : UserControl
{
    private readonly List<MsiIconEntry> _entries = new();

    /// <summary>The icon the user selected, or null if none.</summary>
    public BitmapSource? SelectedIcon { get; private set; }

    public MsiIconPickerDialog(List<(string Name, BitmapSource Icon)> icons)
    {
        InitializeComponent();

        foreach (var (name, icon) in icons)
            _entries.Add(new MsiIconEntry { Name = name, Icon = icon });

        // Pre-select first icon
        if (_entries.Count > 0)
        {
            _entries[0].IsSelected = true;
            SelectedIcon = _entries[0].Icon;
        }

        IconList.ItemsSource = _entries;

        // Defer initial highlight until layout is ready
        Loaded += (_, _) => UpdateBorders();
    }

    private void IconItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not MsiIconEntry clicked) return;

        foreach (var entry in _entries)
            entry.IsSelected = false;

        clicked.IsSelected = true;
        SelectedIcon = clicked.Icon;
        UpdateBorders();
    }

    private void UpdateBorders()
    {
        var accentBrush = TryFindResource("AccentBgBrush") as System.Windows.Media.Brush
            ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9A, 0xC9, 0xCF));
        var normalBrush = TryFindResource("AppBorderBrush") as System.Windows.Media.Brush
            ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x3A));

        for (int i = 0; i < _entries.Count; i++)
        {
            var container = IconList.ItemContainerGenerator.ContainerFromIndex(i) as ContentPresenter;
            if (container is null) continue;

            var border = FindChild<Border>(container);
            if (border is not null)
                border.BorderBrush = _entries[i].IsSelected ? accentBrush : normalBrush;
        }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var result = FindChild<T>(child);
            if (result is not null) return result;
        }
        return null;
    }
}

internal class MsiIconEntry
{
    public string Name { get; init; } = string.Empty;
    public BitmapSource Icon { get; init; } = null!;
    public bool IsSelected { get; set; }
}
