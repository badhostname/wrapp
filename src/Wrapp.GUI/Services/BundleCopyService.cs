using System.IO;
using Wrapp.Helpers;

namespace Wrapp.Services;

/// <summary>What the save pipeline leaves out when it copies a bundle tree.</summary>
/// <param name="BinaryFolderName">The framework's binary folder (<c>B</c> or <c>Files</c>).</param>
/// <param name="SkipInstallersInBinaryRoot">
/// Leave the installer files at the top of the binary folder behind: a new
/// installer is about to be written there and the old one would only be
/// deleted again. Sub-folders of the binary folder are always copied.
/// </param>
public sealed record BundleCopyOptions(string BinaryFolderName, bool SkipInstallersInBinaryRoot = false);

/// <summary>One file the copy could not write, after its retries.</summary>
public readonly record struct BundleCopyFailure(string RelativePath, string Error);

/// <summary>What a copy did: the files that arrived, the ones that did not, and their size.</summary>
public sealed class BundleCopyResult
{
    public List<string> Copied { get; } = new();
    public List<BundleCopyFailure> Failed { get; } = new();
    public long TotalBytes { get; set; }
    public int Planned => Copied.Count + Failed.Count;
}

/// <summary>Progress of a copy: files done so far, files planned, the file being written.</summary>
public readonly record struct BundleCopyProgress(int Done, int Total, string File, long BytesDone, long BytesTotal);

/// <summary>
/// Copies a bundle tree for the save pipeline. One enumeration, every file
/// written once, sub-folders kept, and a file that is still in use after
/// <see cref="FileRetry"/> gives up is recorded rather than thrown, so one
/// stubborn file never leaves a half-copied bundle behind.
/// <para>Never copied: <c>.git</c> at any depth and the <c>.git.backup-*</c>
/// archives (the destination gets its own repository); the temp workspace's
/// <c>.lock</c>; and <c>Config.json</c> with its <c>.bak</c>/<c>.tmp</c>
/// shadows in either layout, because the pipeline writes the destination's
/// <c>Config.json</c> from the model right after the copy, and writing the
/// same file twice within a second is what made Save As fail on a share.</para>
/// </summary>
public static class BundleCopyService
{
    /// <summary>The relative paths the copy would write, in enumeration order.</summary>
    public static List<string> Plan(string source, BundleCopyOptions options)
        => Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(source, f))
            .Where(rel => !IsSkipped(rel, options))
            .ToList();

    /// <summary>True when the pipeline never copies this file (see the class remarks).</summary>
    public static bool IsSkipped(string relativePath, BundleCopyOptions options)
    {
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var seg in segments)
        {
            if (string.Equals(seg, ".git", StringComparison.OrdinalIgnoreCase)) return true;
            if (seg.StartsWith(".git.backup-", StringComparison.OrdinalIgnoreCase)) return true;
        }

        if (segments.Length == 1 && string.Equals(segments[0], ".lock", StringComparison.OrdinalIgnoreCase))
            return true;

        // Config.json and its atomic-write shadows, at the root (legacy) or under Script\.
        var name = segments[^1];
        if (IsConfigShadow(name)
            && (segments.Length == 1
                || (segments.Length == 2 && string.Equals(segments[0], BundlePaths.ScriptFolder, StringComparison.OrdinalIgnoreCase))))
            return true;

        if (options.SkipInstallersInBinaryRoot
            && segments.Length == 2
            && string.Equals(segments[0], options.BinaryFolderName, StringComparison.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext is ".exe" or ".msi" or ".msp") return true;
        }

        return false;
    }

    private static bool IsConfigShadow(string fileName)
        => string.Equals(fileName, BundlePaths.ConfigFileName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, BundlePaths.ConfigFileName + ".bak", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, BundlePaths.ConfigFileName + ".tmp", StringComparison.OrdinalIgnoreCase);

    /// <summary>Plans and copies <paramref name="source"/> into <paramref name="destination"/>.</summary>
    public static Task<BundleCopyResult> CopyAsync(string source, string destination, BundleCopyOptions options,
        IProgress<BundleCopyProgress>? progress = null, CancellationToken ct = default)
        => Task.Run(() => CopyFiles(source, destination, Plan(source, options), progress, ct), ct);

    /// <summary>
    /// Copies the given relative paths from <paramref name="source"/> into
    /// <paramref name="destination"/>: the retry path for the files a first
    /// pass could not write.
    /// </summary>
    public static Task<BundleCopyResult> CopyFilesAsync(string source, string destination, IReadOnlyList<string> relativePaths,
        IProgress<BundleCopyProgress>? progress = null, CancellationToken ct = default)
        => Task.Run(() => CopyFiles(source, destination, relativePaths, progress, ct), ct);

    private static BundleCopyResult CopyFiles(string source, string destination, IReadOnlyList<string> files,
        IProgress<BundleCopyProgress>? progress, CancellationToken ct)
    {
        var result = new BundleCopyResult();
        if (files.Count == 0) return result;

        long totalBytes = 0;
        foreach (var rel in files)
        {
            try { totalBytes += new FileInfo(Path.Combine(source, rel)).Length; }
            catch { /* a file that vanished is reported by the copy below */ }
        }
        result.TotalBytes = totalBytes;

        foreach (var dir in files
                     .Select(f => Path.GetDirectoryName(Path.Combine(destination, f))!)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            Directory.CreateDirectory(dir);

        long bytesDone = 0;
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var rel = files[i];
            var from = Path.Combine(source, rel);
            var to = Path.Combine(destination, rel);
            try
            {
                FileRetry.Run(() => File.Copy(from, to, overwrite: true), rel);
                result.Copied.Add(rel);
                try { bytesDone += new FileInfo(to).Length; } catch { }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"BundleCopy: {rel} not copied: {ex.Message}");
                result.Failed.Add(new BundleCopyFailure(rel, ex.Message));
            }
            progress?.Report(new BundleCopyProgress(i + 1, files.Count, Path.GetFileName(rel), bytesDone, totalBytes));
        }

        return result;
    }
}
