using CommunityToolkit.Mvvm.ComponentModel;

namespace Wrapp.ViewModels;

/// <summary>
/// One self-contained color-picker state: HSV spectrum + darkness, hex field,
/// decimal R/G/B, and a preset swatch palette — all kept in sync, with
/// <see cref="SelectedColor"/> as the single source of truth consumers read.
/// Extracted from the icon library so the same picker can be instantiated per
/// target (tile background, glyph color, and whatever comes next).
/// </summary>
public partial class ColorPickerViewModel : ObservableObject
{
    public ColorPickerViewModel(string initialColor, string[] palette)
    {
        Palette = palette;
        _selectedColor = initialColor;
        _customHex = initialColor;
        if (TryParseHex(initialColor) is { } c)
        {
            _red = c.R; _green = c.G; _blue = c.B;
            var (h, s, v) = RgbToHsv(c.R, c.G, c.B);
            _hue = h; _saturation = s; _brightness = v;
        }
    }

    /// <summary>Preset swatches for this target (first entry is the default).</summary>
    public string[] Palette { get; }

    [ObservableProperty]
    private string _selectedColor;

    private bool _syncingColor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomHexInvalid))]
    private string _customHex;

    [ObservableProperty] private int _red;
    [ObservableProperty] private int _green;
    [ObservableProperty] private int _blue;

    /// <summary>True while the hex box holds something that isn't #RGB / #RRGGBB.</summary>
    public bool IsCustomHexInvalid => TryParseHex(CustomHex) is null;

    // Spectrum picker state (hue/saturation spectrum + darkness slider).
    [ObservableProperty] private double _hue;
    [ObservableProperty] private double _saturation;
    [ObservableProperty] private double _brightness = 1;

    partial void OnSelectedColorChanged(string value)
    {
        if (_syncingColor) return;
        _syncingColor = true;
        try
        {
            CustomHex = value;
            if (TryParseHex(value) is { } c)
            {
                Red = c.R; Green = c.G; Blue = c.B;
                SyncHsvFromRgb(c.R, c.G, c.B);
            }
        }
        finally { _syncingColor = false; }
    }

    partial void OnCustomHexChanged(string value)
    {
        if (_syncingColor) return;
        if (TryParseHex(value) is not { } c) return; // invalid: flagged, not applied
        _syncingColor = true;
        try
        {
            Red = c.R; Green = c.G; Blue = c.B;
            SyncHsvFromRgb(c.R, c.G, c.B);
            SelectedColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
        finally { _syncingColor = false; }
    }

    partial void OnRedChanged(int value)   => OnRgbChanged();
    partial void OnGreenChanged(int value) => OnRgbChanged();
    partial void OnBlueChanged(int value)  => OnRgbChanged();

    private void OnRgbChanged()
    {
        if (_syncingColor) return;
        _syncingColor = true;
        try
        {
            Red   = Math.Clamp(Red, 0, 255);
            Green = Math.Clamp(Green, 0, 255);
            Blue  = Math.Clamp(Blue, 0, 255);
            SyncHsvFromRgb((byte)Red, (byte)Green, (byte)Blue);
            SelectedColor = $"#{Red:X2}{Green:X2}{Blue:X2}";
            CustomHex = SelectedColor;
        }
        finally { _syncingColor = false; }
    }

    partial void OnHueChanged(double value)        => OnHsvChanged();
    partial void OnSaturationChanged(double value) => OnHsvChanged();
    partial void OnBrightnessChanged(double value) => OnHsvChanged();

    private void OnHsvChanged()
    {
        if (_syncingColor) return;
        _syncingColor = true;
        try
        {
            var (r, g, b) = HsvToRgb(Hue, Saturation, Brightness);
            Red = r; Green = g; Blue = b;
            SelectedColor = $"#{r:X2}{g:X2}{b:X2}";
            CustomHex = SelectedColor;
        }
        finally { _syncingColor = false; }
    }

    /// <summary>Hue is undefined for grays — keep the last hue so the marker doesn't jump.</summary>
    private void SyncHsvFromRgb(byte r, byte g, byte b)
    {
        var (h, s, v) = RgbToHsv(r, g, b);
        if (s > 0) Hue = h;
        Saturation = s;
        Brightness = v;
    }

    /// <summary>Parses #RGB or #RRGGBB (leading # optional). Null when invalid.</summary>
    internal static (byte R, byte G, byte B)? TryParseHex(string? text)
    {
        var t = (text ?? string.Empty).Trim().TrimStart('#');
        if (t.Length == 3) t = $"{t[0]}{t[0]}{t[1]}{t[1]}{t[2]}{t[2]}";
        if (t.Length != 6) return null;
        return int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v)
            ? ((byte)(v >> 16), (byte)(v >> 8 & 0xFF), (byte)(v & 0xFF))
            : null;
    }

    /// <summary>Standard HSV → RGB (h 0–360, s/v 0–1).</summary>
    internal static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r1, g1, b1) = (int)(h / 60) switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return ((byte)Math.Round((r1 + m) * 255),
                (byte)Math.Round((g1 + m) * 255),
                (byte)Math.Round((b1 + m) * 255));
    }

    /// <summary>Standard RGB → HSV.</summary>
    internal static (double H, double S, double V) RgbToHsv(byte r, byte g, byte b)
    {
        double rd = r / 255d, gd = g / 255d, bd = b / 255d;
        var max = Math.Max(rd, Math.Max(gd, bd));
        var min = Math.Min(rd, Math.Min(gd, bd));
        var delta = max - min;

        double h = 0;
        if (delta > 0)
        {
            if (max == rd)      h = 60 * ((gd - bd) / delta % 6);
            else if (max == gd) h = 60 * ((bd - rd) / delta + 2);
            else                h = 60 * ((rd - gd) / delta + 4);
            if (h < 0) h += 360;
        }
        var s = max == 0 ? 0 : delta / max;
        return (h, s, max);
    }
}
