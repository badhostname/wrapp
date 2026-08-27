using System.ComponentModel;
using System.Windows.Controls;
using Wrapp.Services;
using Wrapp.ViewModels;

namespace Wrapp.Views;

/// <summary>
/// Generic-icon library picker: search over the Material glyph catalogue,
/// paged results, and one full color picker per target (tile background and
/// glyph color) in tabs under the live preview — which is built by the SAME
/// renderer that produces the shipped PNG. Hosted by
/// <see cref="Services.FluentDialog"/>; the caller reads
/// <see cref="ViewModel"/>.Selected / .SelectedColor / .GlyphColor after
/// "Use Icon". Picker interaction lives in <see cref="Controls.ColorPickerPanel"/>.
/// </summary>
public partial class IconLibraryDialog : UserControl
{
    public IconLibraryViewModel ViewModel { get; } = new();

    public IconLibraryDialog()
    {
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += OnVmPropertyChanged;
        ViewModel.Background.PropertyChanged += OnColorChanged;
        ViewModel.Glyph.PropertyChanged += OnColorChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IconLibraryViewModel.Selected))
            UpdatePreview();
    }

    private void OnColorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ColorPickerViewModel.SelectedColor))
            UpdatePreview();
    }

    private void UpdatePreview()
    {
        PreviewHost.Content = ViewModel.Selected is null
            ? null
            : IconTileRenderer.BuildTileVisual(
                ViewModel.Selected.Kind, ViewModel.SelectedColor, 120, ViewModel.GlyphColor);
    }
}
