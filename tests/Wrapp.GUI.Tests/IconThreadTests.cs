using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// An icon picked from an MSI's Icon table is decoded on a background thread
/// (the drop runs discovery off the UI thread) and saved later on the UI
/// thread. A frozen BitmapFrame straight from a decoder still reaches back to
/// that decoder when it is re-encoded, and WPF refuses the cross-thread access:
/// the Snagit save on 2026-11-10 logged "a different thread owns it" and left
/// Icon\ empty while Config.json pointed at Icon\Snagit.png.
/// </summary>
public class IconThreadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WrappTests_" + Guid.NewGuid().ToString("N"));

    public IconThreadTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static T OnStaThread<T>(Func<T> body)
    {
        T result = default!;
        Exception? failure = null;
        var t = new Thread(() => { try { result = body(); } catch (Exception ex) { failure = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
        return result;
    }

    /// <summary>A small PNG, as the MSI Icon table would hold it.</summary>
    private static byte[] PngBytes() => OnStaThread(() =>
    {
        var pixels = new byte[32 * 32 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 200; pixels[i + 1] = 80; pixels[i + 2] = 40; pixels[i + 3] = 255; }
        var src = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    });

    [Fact]
    public void An_MSI_icon_decoded_on_another_thread_can_be_saved_on_this_one()
    {
        var png = PngBytes();
        // Decoded the way MsiPropertyService.GetIcons does it, on its own thread.
        var icon = OnStaThread(() => MsiPropertyService.DecodeIcon(png)!);
        Assert.NotNull(icon);
        Assert.True(icon.IsFrozen);

        var path = Path.Combine(_root, "Icon", "Snagit.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var saved = OnStaThread(() => BundleService.SaveIconAsPngAsync(icon, path).GetAwaiter().GetResult());

        Assert.True(saved, "the save reported failure");
        Assert.True(File.Exists(path), "the PNG was not written");
        Assert.True(new FileInfo(path).Length > 0);
    }

    [Fact]
    public void A_decoder_frame_from_another_thread_is_still_written_and_the_result_says_so()
    {
        // The raw frame, as GetIcons returned it before the fix: the writer must cope too.
        var png = PngBytes();
        var frame = OnStaThread(() =>
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var f = decoder.Frames[0];
            f.Freeze();
            return (BitmapSource)f;
        });

        var path = Path.Combine(_root, "raw.png");
        var saved = OnStaThread(() => BundleService.SaveIconAsPngAsync(frame, path).GetAwaiter().GetResult());
        Assert.True(saved);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void A_failed_write_is_reported_not_announced_as_written()
    {
        var png = PngBytes();
        var icon = OnStaThread(() => MsiPropertyService.DecodeIcon(png)!);
        var path = Path.Combine(_root, "missing-folder", "x.png"); // the folder does not exist
        var saved = OnStaThread(() => BundleService.SaveIconAsPngAsync(icon, path).GetAwaiter().GetResult());
        Assert.False(saved);
        Assert.False(File.Exists(path));
    }
}
