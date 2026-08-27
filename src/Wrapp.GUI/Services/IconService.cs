using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wrapp.Services;

/// <summary>
/// Shared helpers for copying icons to the icon folder with content-based
/// deduplication and loading icon images from relative paths.
/// </summary>
public static class IconService
{
    /// <summary>
    /// Copies an icon file to the icon folder, deduplicating by content.
    /// If an identical file already exists, returns the existing relative path.
    /// Dragged/browsed images keep their original filename.
    /// Returns a relative path like "Icon/myicon.png".
    /// </summary>
    public static string CopyToIconFolder(string sourcePath, string configDir, string iconFolderName = "Icon")
    {
        var iconFolder = Path.Combine(configDir, iconFolderName);
        Directory.CreateDirectory(iconFolder);

        var sourceInfo = new FileInfo(sourcePath);
        byte[]? sourceBytes = null;

        // Check for content-identical file already in the folder
        if (Directory.Exists(iconFolder))
        {
            foreach (var existing in Directory.EnumerateFiles(iconFolder))
            {
                var existingInfo = new FileInfo(existing);
                if (existingInfo.Length != sourceInfo.Length) continue;

                sourceBytes ??= File.ReadAllBytes(sourcePath);
                if (File.ReadAllBytes(existing).AsSpan().SequenceEqual(sourceBytes))
                {
                    AppLogger.Info($"IconService: duplicate detected, reusing {Path.GetFileName(existing)}");
                    return Path.Combine(iconFolderName, Path.GetFileName(existing));
                }
            }
        }

        // Copy with original filename
        var destFileName = Path.GetFileName(sourcePath);
        var destPath = Path.Combine(iconFolder, destFileName);

        // Handle name collision (different content, same name)
        if (File.Exists(destPath))
        {
            var nameWithoutExt = Path.GetFileNameWithoutExtension(sourcePath);
            var ext = Path.GetExtension(sourcePath);
            int i = 1;
            do
            {
                destFileName = $"{nameWithoutExt}_{i}{ext}";
                destPath = Path.Combine(iconFolder, destFileName);
                i++;
            } while (File.Exists(destPath));
        }

        File.Copy(sourcePath, destPath);
        AppLogger.Info($"IconService: copied {Path.GetFileName(sourcePath)} -> {destFileName}");
        return Path.Combine(iconFolderName, destFileName);
    }

    /// <summary>
    /// Loads a BitmapImage from a relative icon path under configDir.
    /// Returns null if the file does not exist or cannot be loaded.
    /// </summary>
    public static ImageSource? LoadIcon(string configDir, string relativeIconPath)
    {
        if (string.IsNullOrEmpty(relativeIconPath)) return null;
        var fullPath = Path.Combine(configDir, relativeIconPath);
        if (!File.Exists(fullPath)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(fullPath, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns true if two files have identical content.
    /// Checks file size first for a fast-path rejection, then compares in 64KB chunks.
    /// </summary>
    public static bool FilesAreIdentical(string pathA, string pathB)
    {
        if (!File.Exists(pathA) || !File.Exists(pathB)) return false;
        var infoA = new FileInfo(pathA);
        var infoB = new FileInfo(pathB);
        if (infoA.Length != infoB.Length) return false;

        const int bufferSize = 65536;
        var bufA = new byte[bufferSize];
        var bufB = new byte[bufferSize];

        using var streamA = new FileStream(pathA, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var streamB = new FileStream(pathB, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        int readA;
        while ((readA = streamA.Read(bufA, 0, bufferSize)) > 0)
        {
            var readB = streamB.Read(bufB, 0, bufferSize);
            if (readA != readB) return false;
            if (!bufA.AsSpan(0, readA).SequenceEqual(bufB.AsSpan(0, readB))) return false;
        }
        return true;
    }
}
