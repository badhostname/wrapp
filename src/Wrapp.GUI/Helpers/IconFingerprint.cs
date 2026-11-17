using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wrapp.Helpers;

/// <summary>
/// What an icon looks like, as a short string: its size plus a hash of its
/// pixels. The bundle's icon image is not part of Config.json (only its file
/// name is), so the dirty check compares this instead: replacing the icon
/// with one of the same file name is a change, and putting the same picture
/// back is not.
/// </summary>
public static class IconFingerprint
{
    /// <summary>Empty for no icon; otherwise "WxH:hash". Never throws.</summary>
    public static string Of(ImageSource? icon)
    {
        if (icon is not BitmapSource bmp) return string.Empty;
        try
        {
            BitmapSource bgra = bmp.Format == PixelFormats.Bgra32 || bmp.Format == PixelFormats.Pbgra32
                ? bmp
                : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            var stride = bgra.PixelWidth * ((bgra.Format.BitsPerPixel + 7) / 8);
            var pixels = new byte[stride * bgra.PixelHeight];
            bgra.CopyPixels(pixels, stride, 0);
            return $"{bgra.PixelWidth}x{bgra.PixelHeight}:{Convert.ToHexString(SHA256.HashData(pixels))}";
        }
        catch (Exception)
        {
            // Unreadable pixels: treat every such image as different, so a
            // replaced icon is never mistaken for "no change".
            return "unreadable:" + Guid.NewGuid().ToString("N");
        }
    }
}
