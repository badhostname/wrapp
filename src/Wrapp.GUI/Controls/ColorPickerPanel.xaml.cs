using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Wrapp.ViewModels;

namespace Wrapp.Controls;

/// <summary>
/// Reusable HSV color picker: spectrum square (hue × saturation), darkness
/// slider, preset swatches, and synced hex / decimal R-G-B fields. Binds to a
/// <see cref="ColorPickerViewModel"/> as its DataContext — instantiate one
/// panel per color target (the icon library uses two: tile background and
/// glyph color). All color math lives in the view-model; this code-behind
/// only translates mouse drags to HSV values and positions the markers.
/// </summary>
public partial class ColorPickerPanel : UserControl
{
    private ColorPickerViewModel? Vm => DataContext as ColorPickerViewModel;

    public ColorPickerPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdatePickerVisuals();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ColorPickerViewModel oldVm) oldVm.PropertyChanged -= OnVmPropertyChanged;
            if (e.NewValue is ColorPickerViewModel newVm) newVm.PropertyChanged += OnVmPropertyChanged;
            UpdatePickerVisuals();
        };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ColorPickerViewModel.Hue)
                           or nameof(ColorPickerViewModel.Saturation)
                           or nameof(ColorPickerViewModel.Brightness)
                           or nameof(ColorPickerViewModel.SelectedColor))
            UpdatePickerVisuals();
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not null && sender is System.Windows.Controls.Button { Tag: string color })
            Vm.SelectedColor = color;
    }

    // ------------------------------------------------------------------
    // Spectrum square + darkness slider drag handling
    // ------------------------------------------------------------------

    private bool _draggingSpectrum;
    private bool _draggingDarkness;

    private void Spectrum_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _draggingSpectrum = true;
        SpectrumSquare.CaptureMouse();
        ApplySpectrumPoint(e.GetPosition(SpectrumSquare));
    }

    private void Spectrum_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_draggingSpectrum) ApplySpectrumPoint(e.GetPosition(SpectrumSquare));
    }

    private void Spectrum_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _draggingSpectrum = false;
        SpectrumSquare.ReleaseMouseCapture();
    }

    private void ApplySpectrumPoint(System.Windows.Point p)
    {
        if (Vm is null) return;
        var x = Math.Clamp(p.X / SpectrumSquare.ActualWidth, 0, 1);
        var y = Math.Clamp(p.Y / SpectrumSquare.ActualHeight, 0, 1);
        Vm.Hue = x * 360;
        Vm.Saturation = y; // top = white (0), bottom = full (1)
    }

    private void Darkness_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _draggingDarkness = true;
        DarknessStrip.CaptureMouse();
        ApplyDarknessPoint(e.GetPosition(DarknessStrip));
    }

    private void Darkness_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_draggingDarkness) ApplyDarknessPoint(e.GetPosition(DarknessStrip));
    }

    private void Darkness_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _draggingDarkness = false;
        DarknessStrip.ReleaseMouseCapture();
    }

    private void ApplyDarknessPoint(System.Windows.Point p)
    {
        if (Vm is null) return;
        var x = Math.Clamp(p.X / DarknessStrip.ActualWidth, 0, 1);
        Vm.Brightness = 1 - x; // left = full color, right = black
    }

    /// <summary>Repositions both markers and refreshes the darkness gradient.</summary>
    private void UpdatePickerVisuals()
    {
        if (Vm is null || !IsLoaded || SpectrumSquare.ActualWidth <= 0) return;

        var mx = Vm.Hue / 360 * SpectrumSquare.ActualWidth;
        var my = Vm.Saturation * SpectrumSquare.ActualHeight;
        Canvas.SetLeft(SpectrumMarker, mx - SpectrumMarker.Width / 2);
        Canvas.SetTop(SpectrumMarker, my - SpectrumMarker.Height / 2);

        var dx = (1 - Vm.Brightness) * DarknessStrip.ActualWidth;
        Canvas.SetLeft(DarknessMarker, dx - DarknessMarker.Width / 2);

        // Darkness gradient starts at the CURRENT hue/saturation at full
        // brightness so the strip always previews this color's range.
        var (r, g, b) = ColorPickerViewModel.HsvToRgb(Vm.Hue, Vm.Saturation, 1);
        DarknessStart.Color = System.Windows.Media.Color.FromRgb(r, g, b);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdatePickerVisuals();
    }
}
