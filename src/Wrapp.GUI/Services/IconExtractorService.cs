using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wrapp.Services;

/// <summary>
/// Extracts the best available resolution icon from an executable or installer file.
/// Uses Shell32 SHDefExtractIcon which can return 256x256 PNG icons from modern EXEs,
/// falling back to 48x48 and 32x32 before the legacy GDI API.
/// </summary>
public static class IconExtractorService
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIcon(
        string pszIconFile, int iIndex, uint uFlags,
        out IntPtr phiconLarge, out IntPtr phiconSmall,
        uint nIconSize);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static uint MakeLong(int low, int high)
        => (uint)((low & 0xFFFF) | ((high & 0xFFFF) << 16));

    /// <summary>
    /// Extracts the highest available resolution icon for the given file.
    /// Tries 256x256, then 48x48, then 32x32, then falls back to GDI legacy.
    /// Returns null if no icon could be extracted.
    /// </summary>
    public static BitmapSource? Extract(string filePath)
    {
        foreach (var size in new[] { 256, 48, 32 })
        {
            var bmp = TryExtractAtSize(filePath, size);
            if (bmp is not null)
            {
                AppLogger.Info($"IconExtractor: extracted {size}x{size} icon from {System.IO.Path.GetFileName(filePath)}");
                return bmp;
            }
        }

        // Final fallback: legacy GDI icon
        var fallback = TryLegacyExtract(filePath);
        if (fallback is not null)
            AppLogger.Info($"IconExtractor: extracted icon via legacy GDI from {System.IO.Path.GetFileName(filePath)}");
        else
            AppLogger.Warn($"IconExtractor: no icon found in {System.IO.Path.GetFileName(filePath)}");

        return fallback;
    }

    private static BitmapSource? TryExtractAtSize(string filePath, int size)
    {
        try
        {
            // SHDefExtractIcon nIconSize = MAKELONG(largeSz, smallSz)
            // We request the large icon at the target size; small at 16px (not used).
            var hr = SHDefExtractIcon(
                filePath, 0, 0,
                out IntPtr hIconLarge, out IntPtr hIconSmall,
                MakeLong(size, 16));

            // Always destroy the small icon - we only use the large one.
            if (hIconSmall != IntPtr.Zero) DestroyIcon(hIconSmall);

            if (hr != 0 || hIconLarge == IntPtr.Zero) return null;

            try
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(
                    hIconLarge,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bmp?.Freeze();
                return bmp;
            }
            finally
            {
                DestroyIcon(hIconLarge);
            }
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? TryLegacyExtract(string filePath)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(filePath);
            if (icon is null) return null;
            var bmp = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bmp?.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
