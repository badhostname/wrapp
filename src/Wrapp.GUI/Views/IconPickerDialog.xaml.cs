using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Wrapp.Views;

public partial class IconPickerDialog : UserControl
{
    /// <summary>True if the user selected the old (current) icon; false if the new one.</summary>
    public bool SelectedOld { get; private set; } = true;

    public IconPickerDialog(ImageSource? oldIcon, ImageSource? newIcon, string appName)
    {
        InitializeComponent();

        OldIconImage.Source = oldIcon;
        NewIconImage.Source = newIcon;
        AppNameLabel.Text = appName;

        UpdateSelection();
    }

    private void OldIcon_Click(object sender, MouseButtonEventArgs e)
    {
        SelectedOld = true;
        UpdateSelection();
    }

    private void NewIcon_Click(object sender, MouseButtonEventArgs e)
    {
        SelectedOld = false;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var accentBrush = TryFindResource("AccentBgBrush") as System.Windows.Media.Brush
            ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9A, 0xC9, 0xCF));
        var normalBrush = TryFindResource("AppBorderBrush") as System.Windows.Media.Brush
            ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x3A));

        OldIconBorder.BorderBrush = SelectedOld ? accentBrush : normalBrush;
        NewIconBorder.BorderBrush = SelectedOld ? normalBrush : accentBrush;
    }
}
