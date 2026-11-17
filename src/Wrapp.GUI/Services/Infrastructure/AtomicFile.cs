using System.IO;
using Wrapp.Helpers;

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

        FileRetry.Run(() => File.WriteAllText(tempPath, contents), tempPath);
        FileRetry.Run(() => Swap(tempPath, path, backupPath), path);
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

        FileRetry.Run(() => File.WriteAllBytes(tempPath, contents), tempPath);
        FileRetry.Run(() => Swap(tempPath, path, backupPath), path);
    }

    /// <summary>Async variant of <see cref="WriteAllText"/>.</summary>
    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = path + TempSuffix;
        var backupPath = path + BackupSuffix;

        await FileRetry.RunAsync(() => File.WriteAllTextAsync(tempPath, contents, ct), tempPath).ConfigureAwait(false);
        await FileRetry.RunAsync(() => Task.Run(() => Swap(tempPath, path, backupPath), ct), path).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts the finished temp file in place: <see cref="File.Replace(string, string, string, bool)"/>
    /// over an existing file (keeping one <c>.bak</c>), a move when there is
    /// none yet. Retried by the callers: the file just written is exactly the
    /// one an on-access scanner is still reading.
    /// </summary>
    private static void Swap(string tempPath, string path, string backupPath)
    {
        if (File.Exists(path))
            File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
        else
            File.Move(tempPath, path, overwrite: false);
    }
}
