using System.IO;

namespace Wrapp.Services;

/// <summary>
/// Torn-write-safe file I/O helpers. Every write goes through a temp file +
/// <see cref="File.Replace(string, string, string, bool)"/> so a crash during
/// persistence leaves either the old or new complete file on disk, never a
/// truncated one. A single .bak generation is retained for recovery.
///
/// Use for small critical-state files (settings.json, msal_cache.bin, key
/// store JSONs). NOT a drop-in replacement for large streaming writes.
/// </summary>
public static class AtomicFile
{
    private const string TempSuffix = ".tmp";
    private const string BackupSuffix = ".bak";

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/> atomically.
    /// The previous file (if any) is preserved at <c>{path}.bak</c>.
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = path + TempSuffix;
        var backupPath = path + BackupSuffix;

        File.WriteAllText(tempPath, contents);

        if (File.Exists(path))
        {
            File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
        }
        else
        {
            // Destination doesn't exist yet — File.Replace requires one — so move.
            File.Move(tempPath, path, overwrite: false);
        }
    }

    /// <summary>
    /// Binary counterpart to <see cref="WriteAllText"/>.
    /// </summary>
    public static void WriteAllBytes(string path, byte[] contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = path + TempSuffix;
        var backupPath = path + BackupSuffix;

        File.WriteAllBytes(tempPath, contents);

        if (File.Exists(path))
        {
            File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, path, overwrite: false);
        }
    }

    /// <summary>Async variant of <see cref="WriteAllText"/>.</summary>
    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = path + TempSuffix;
        var backupPath = path + BackupSuffix;

        await File.WriteAllTextAsync(tempPath, contents, ct).ConfigureAwait(false);

        if (File.Exists(path))
        {
            File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, path, overwrite: false);
        }
    }
}
