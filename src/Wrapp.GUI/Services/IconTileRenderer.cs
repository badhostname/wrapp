using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaterialDesignThemes.Wpf;

namespace Wrapp.Services;

/// <summary>
/// Rasterizes a Material Design vector glyph onto a colored rounded tile -
/// the "generic app icon" source for bundles with no usable installer icon.
/// Nothing is pre-rendered or shipped: the ~7,000-glyph catalogue is the
/// vector path data already inside the MaterialDesignThemes dependency
/// (Apache-2.0, safe to publish into Intune/SCCM), rasterized once at pick
/// time. 512×512 PNG output - Company Portal displays up to 256, so this
/// stays crisp on any portal surface at trivial file size.
/// </summary>
public static class IconTileRenderer
{
    public const int TileSize = 512;

    /// <summary>Glyph occupies ~70% of the tile; the rest is breathing room.</summary>
    private const double GlyphFraction = 0.70;

    /// <summary>Corner radius tuned to read as an "app icon" at portal sizes.</summary>
    private const double CornerRadius = 96;

    /// <summary>
    /// Brand-safe tile backgrounds; first entry (the app accent) is the default.
    /// </summary>
    public static readonly string[] Palette =
    {
        "#9AC9CF", "#4C8FBF", "#7A67C9", "#C95E8E",
        "#D98A0B", "#4CAF6E", "#5A6B7A", "#333A42",
    };

    /// <summary>
    /// Vector path data for a glyph. PackIcon's default style resolves from
    /// the MaterialDesignThemes assembly's Generic.xaml, so this works without
    /// the app merging any Material theme dictionaries.
    /// </summary>
    public static string GetGlyphData(PackIconKind kind)
    {
        var icon = new PackIcon { Kind = kind };
        icon.ApplyTemplate();
        return icon.Data ?? string.Empty;
    }

    /// <summary>
    /// Builds the tile as a WPF visual - shared by the on-screen preview and
    /// the PNG rasterizer so what the user sees is exactly what ships.
    /// </summary>
    public static FrameworkElement BuildTileVisual(
        PackIconKind kind, string colorHex, double size, string glyphColorHex = "#FFFFFF")
    {
        var brush = new SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex));
        brush.Freeze();
        var glyphBrush = new SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(glyphColorHex));
        glyphBrush.Freeze();

        var glyphSize = size * GlyphFraction;
        var glyph = new System.Windows.Shapes.Path
        {
            Data    = Geometry.Parse(GetGlyphData(kind)),
            Fill    = glyphBrush,
            Stretch = Stretch.Uniform,
            Width   = glyphSize,
            Height  = glyphSize,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment   = System.Windows.VerticalAlignment.Center,
        };

        return new Border
        {
            Width        = size,
            Height       = size,
            Background   = brush,
            CornerRadius = new CornerRadius(CornerRadius * (size / TileSize)),
            Child        = glyph,
        };
    }

    /// <summary>Renders the tile to PNG bytes (512×512, 96 DPI).</summary>
    public static byte[] RenderPng(PackIconKind kind, string colorHex, string glyphColorHex = "#FFFFFF")
    {
        var tile = BuildTileVisual(kind, colorHex, TileSize, glyphColorHex);
        tile.Measure(new System.Windows.Size(TileSize, TileSize));
        tile.Arrange(new Rect(0, 0, TileSize, TileSize));
        tile.UpdateLayout();

        var rtb = new RenderTargetBitmap(TileSize, TileSize, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(tile);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Renders the tile PNG to a temp file named for the app (so the bundle's
    /// icon file keeps the same naming as extracted installer icons) and
    /// returns the path. Caller feeds it through the normal apply-icon flow.
    /// </summary>
    public static string RenderToTempFile(
        PackIconKind kind, string colorHex, string appName, string glyphColorHex = "#FFFFFF")
    {
        var safe = string.Join("_",
            (string.IsNullOrWhiteSpace(appName) ? "AppIcon" : appName)
            .Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (safe.Length == 0) safe = "AppIcon";

        // Unique subfolder per render: the FILE name stays "{app}.png" (that
        // is what lands in the bundle), but the PATH differs every time so
        // WPF's URI-keyed image cache can never serve a previous render.
        var dir = Path.Combine(Path.GetTempPath(), "Wrapp", "icon-library", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{safe}.png");
        File.WriteAllBytes(path, RenderPng(kind, colorHex, glyphColorHex));
        return path;
    }
}
