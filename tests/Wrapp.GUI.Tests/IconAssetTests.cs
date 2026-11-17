using System.IO;

namespace Wrapp.Tests;

/// <summary>
/// Guards the app icon against regressing to a single-frame ICO - the root
/// cause of the taskbar icon reverting to the generic window glyph after
/// lock-unlock / DPI re-evaluation (2026-08-07 investigation). Windows
/// requires at minimum 16, 24, 32, 48 and 256px frames, 256 PNG-compressed;
/// the shell only ever scales DOWN, so missing large frames means failed icon
/// resolution at high DPI. Regenerate with scripts\Build-AppIcon.ps1.
/// </summary>
public class IconAssetTests
{
    private static string FindIcoPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Wrapp.GUI", "Assets", "burrito.ico");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent!;
        }
        throw new FileNotFoundException("burrito.ico not found walking up from test base directory");
    }

    private static List<(int Size, bool IsPng)> ParseFrames(byte[] ico)
    {
        var frames = new List<(int, bool)>();
        int count = BitConverter.ToInt16(ico, 4);
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + 16 * i;
            int dim = ico[entry];
            if (dim == 0) dim = 256;
            var offset = BitConverter.ToInt32(ico, entry + 12);
            var isPng = ico[offset] == 0x89 && ico[offset + 1] == 0x50; // PNG magic
            frames.Add((dim, isPng));
        }
        return frames;
    }

    [Fact]
    public void AppIcon_CarriesTheRequiredWindowsSizeSet()
    {
        var frames = ParseFrames(File.ReadAllBytes(FindIcoPath()));
        var sizes = frames.Select(f => f.Size).ToHashSet();

        foreach (var required in new[] { 16, 24, 32, 48, 256 })
            Assert.Contains(required, sizes);
    }

    [Fact]
    public void AppIcon_256Frame_IsPngCompressed()
    {
        var frames = ParseFrames(File.ReadAllBytes(FindIcoPath()));
        var big = frames.Single(f => f.Size == 256);
        Assert.True(big.IsPng, "the 256px frame must be PNG-compressed per Windows icon guidance");
    }
}
