using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Wrapp.Helpers;
using Wrapp.Models;
using Wrapp.Services;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;

namespace Wrapp.ViewModels;

public partial class GeneralViewModel
{
    // -----------------------------------------------------------------------
    // Bundle file-system primitives -- installer copy with progress, bin
    // folder management, icon save, bundle directory copy. Used during
    // installer drop, save, and full-clone import flows.
    // -----------------------------------------------------------------------

    [RelayCommand]
    private void SyncUnderscoreVersion()
    {
        if (!string.IsNullOrWhiteSpace(App.DotVersion))
            App.Version = App.DotVersion.Replace('.', '_');
    }

    /// <summary>
    /// Applies an image file as the app icon without treating it as an installer.
    /// Copies to icon folder with deduplication, keeps original filename.
    /// Does NOT clear metadata fields.
    /// </summary>
    private void ApplyIconFile(string path)
    {
        try
        {
            var bundleRoot = BundleRootDir;
            if (!string.IsNullOrEmpty(bundleRoot))
            {
                var relativePath = IconService.CopyToIconFolder(path, bundleRoot, _settings.IconFolderName);
                App.IconFile = relativePath;
            }

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            // WPF caches decodes by URI; re-applying a path whose CONTENT
            // changed (library re-render to the same temp file, an edited
            // image re-browsed) showed the stale first image without this.
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource   = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            InstallerIconSource = bmp;

            // Every route into here (browse, library render, image drop) is a
            // deliberate choice — protect it from silent Full-apply replacement.
            App.IconUserChosen = true;

            StatusText = $"Icon set: {Path.GetFileName(path)}";
            AppLogger.Info($"General: icon applied from image file: {path}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"General: failed to load icon from {path}: {ex.Message}");
            StatusText = $"Failed to load icon: {ex.Message}";
        }
    }

    // -----------------------------------------------------------------------
    // App icon selector (feature/icon-selector): one entry point for the three
    // icon sources -- drop/browse square, generic library, clear. Everything
    // funnels through ApplyIconFile so history/dirty/preview behave
    // identically regardless of source.
    // -----------------------------------------------------------------------

    [RelayCommand]
    private async Task SelectAppIconAsync()
    {
        var hasIcon = InstallerIconSource is not null || !string.IsNullOrEmpty(App.IconFile);

        // Web-style source picker: drop square (drag or click-to-browse) plus
        // library and remove actions -- each completes the dialog itself.
        var dialog = new Views.AppIconSelectDialog(hasIcon);
        await FluentDialog.ShowActionsAsync("Select Icon", dialog);

        if (dialog.PickedFile is not null)
        {
            ApplyIconFile(dialog.PickedFile);
            return;
        }
        switch (dialog.Action)
        {
            case "library": await PickFromLibraryAsync(); break;
            case "clear":   await ClearIconAsync(); break;
        }
    }
    private async Task PickFromLibraryAsync()
    {
        var library = new Views.IconLibraryDialog();
        var confirmed = await FluentDialog.ShowSelectAsync("Icon Library", library, "Use Icon", "Cancel");
        if (!confirmed || library.ViewModel.Selected is null) return;

        try
        {
            // Rendered to a temp file named for the app, then fed through the
            // normal apply path so the bundle sees it exactly like a browsed file.
            var rendered = IconTileRenderer.RenderToTempFile(
                library.ViewModel.Selected.Kind, library.ViewModel.SelectedColor, App.Name,
                library.ViewModel.GlyphColor);
            ApplyIconFile(rendered);
            AppLogger.Info($"General: library icon applied -- {library.ViewModel.Selected.Name} " +
                           $"({library.ViewModel.GlyphColor} on {library.ViewModel.SelectedColor})");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"General: library icon render failed: {ex.Message}");
            StatusText = $"Could not render the icon: {ex.Message}";
        }
    }

    /// <summary>
    /// Clears the app icon: blanks <c>App.IconFile</c>, drops the preview, and
    /// deletes the icon file from the bundle's icon folder (confirmed first --
    /// per-package IconFile fields referencing it surface through validation).
    /// </summary>
    [RelayCommand]
    private async Task ClearIconAsync()
    {
        var iconRel = App.IconFile;
        var confirmed = await FluentDialog.ConfirmAsync(
            "Remove icon?",
            string.IsNullOrEmpty(iconRel)
                ? "Clear the current icon preview?"
                : $"Clear the icon and delete \"{iconRel}\" from the bundle?",
            "Remove icon", "Cancel");
        if (!confirmed) return;

        try
        {
            var bundleRoot = BundleRootDir;
            if (!string.IsNullOrEmpty(iconRel) && !string.IsNullOrEmpty(bundleRoot))
            {
                var full = Path.Combine(bundleRoot, iconRel);
                if (File.Exists(full)) File.Delete(full);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"General: could not delete icon file: {ex.Message}");
        }

        App.IconFile = string.Empty;
        App.IconUserChosen = false;
        InstallerIconSource = null;
        StatusText = "Icon removed.";
        AppLogger.Info("General: app icon cleared");
    }

    /// <summary>
    /// Copies a single file with progress reporting. Small files (&lt; 50 MB) use a direct
    /// File.Copy; large files use buffered async I/O with byte-level progress.
    /// Sets IsTransferring/TransferProgress/TransferStatusText for visual feedback.
    /// </summary>
    private async Task CopyFileWithProgressAsync(string sourcePath, string destPath)
    {
        var fileName = Path.GetFileName(sourcePath);
        var fileInfo = new FileInfo(sourcePath);
        var totalBytes = fileInfo.Length;

        IsTransferring = true;
        TransferProgress = 0;
        TransferStatusText = $"Copying {fileName}...";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            if (totalBytes < 50 * 1024 * 1024)
            {
                await Task.Run(() => File.Copy(sourcePath, destPath, overwrite: true));
                TransferProgress = 100;
            }
            else
            {
                const int bufferSize = 1024 * 1024;
                long bytesCopied = 0;

                await Task.Run(async () =>
                {
                    using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
                    using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);

                    var buffer = new byte[bufferSize];
                    int bytesRead;
                    while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await dest.WriteAsync(buffer, 0, bytesRead);
                        bytesCopied += bytesRead;
                        var pct = totalBytes > 0 ? (double)bytesCopied / totalBytes * 100.0 : 0;
                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        {
                            TransferProgress = pct;
                            TransferStatusText = $"Copying {fileName}... {pct:F0}%";
                        });
                    }
                });

                TransferProgress = 100;
            }

            AppLogger.Info($"General: copied {fileName} to {destPath}");
        }
        finally
        {
            IsTransferring = false;
            TransferStatusText = string.Empty;
            TransferProgress = 0;
        }
    }

    /// <summary>
    /// Copies the dropped installer file into the B/ folder with progress reporting.
    /// </summary>
    private async Task CopyInstallerToBinAsync(string sourcePath)
    {
        var bundleRoot = BundleRootDir;
        if (string.IsNullOrEmpty(bundleRoot)) return;
        try
        {
            var binDir = BundlePaths.BinaryFolder(bundleRoot, ScriptFrameworkProvider.Parse(_config.App.ScriptFramework));
            Directory.CreateDirectory(binDir);
            var destPath = Path.Combine(binDir, Path.GetFileName(sourcePath));
            await CopyFileWithProgressAsync(sourcePath, destPath);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"General: failed to copy installer to B/: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes all files in the B/ folder off the UI thread so stale binaries
    /// from a previous drop do not accumulate in drafts.
    /// </summary>
    private async Task ClearBinFolderAsync()
    {
        var bundleRoot = BundleRootDir;
        if (string.IsNullOrEmpty(bundleRoot)) return;
        var binDir = BundlePaths.BinaryFolder(bundleRoot, ScriptFrameworkProvider.Parse(_config.App.ScriptFramework));
        if (!Directory.Exists(binDir)) return;
        try
        {
            await Task.Run(() =>
            {
                foreach (var file in Directory.EnumerateFiles(binDir))
                {
                    File.Delete(file);
                    AppLogger.Info($"General: deleted old binary {Path.GetFileName(file)} from B/");
                }
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"General: failed to clear B/ folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Saves the current installer icon as Icon/appIcon.png inside the draft workspace.
    /// Uses a filename derived from the extracted app metadata (not form fields).
    /// </summary>
    private async Task SaveIconToDraftAsync(string extractedAppName)
    {
        if (string.IsNullOrEmpty(_configPath) || InstallerIconSource is null) return;
        try
        {
            var bundleRoot = BundleRootDir;
            if (string.IsNullOrEmpty(bundleRoot)) return;

            var iconFileName = ResolveIconFileName(extractedAppName);
            var relPath = Path.Combine(_settings.IconFolderName, iconFileName);
            var iconPath = Path.Combine(bundleRoot, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
            await BundleService.SaveIconAsPngAsync(InstallerIconSource, iconPath);
            App.IconFile = relPath;
            AppLogger.Info($"General: icon saved to draft at {iconPath}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"General: failed to save icon to draft: {ex.Message}");
        }
    }

    /// <summary>
    /// Recursively copies a bundle directory to a new location, skipping .git/ and
    /// installer files (.exe/.msi/.msp) in the B/ root. Subfolders within B/ are
    /// preserved. The installer is handled separately by FlushPendingInstallerToDiskAsync.
    /// Uses a single directory enumeration pass and shows file-count progress with filenames.
    /// </summary>
    private async Task CopyBundleDirectoryAsync(string source, string destination)
    {
        // Single enumeration pass: collect files, skip .git/ and installer files in binary folder root
        var binFolder = BinFolderName;
        var files = await Task.Run(() =>
            Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(source, f))
                .Where(rel =>
                {
                    // Skip .git/ at any depth â€” since 0.6.0.0156 the per-bundle
                    // git repo lives at Script/.git; older bundles may still
                    // have .git at the root. Also filter the migration backup
                    // pattern (.git.backup-<ticks>) so archived legacy repos
                    // don't leak into new bundle copies.
                    var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    foreach (var seg in segments)
                    {
                        if (string.Equals(seg, ".git", StringComparison.OrdinalIgnoreCase))
                            return false;
                        if (seg.StartsWith(".git.backup-", StringComparison.OrdinalIgnoreCase))
                            return false;
                    }

                    // Skip installer files in the binary folder root (not subfolders)
                    var parent = Path.GetDirectoryName(rel);
                    if (string.Equals(parent, binFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        var ext = Path.GetExtension(rel).ToLowerInvariant();
                        if (ext is ".exe" or ".msi" or ".msp")
                            return false;
                    }

                    return true;
                })
                .ToList());

        if (files.Count == 0) return;

        IsTransferring = true;
        TransferProgress = 0;
        TransferStatusText = $"Copying bundle ({files.Count} files)...";

        // Registered with the background watcher so the upgrade / full-apply
        // copy is visible in the status bar + jobs pop-up like every other
        // long-running operation (the transfer overlay alone only shows while
        // the General view is on screen).
        var job = _jobTracker?.BeginJob($"Copying bundle ({files.Count} files)", destination) ?? default;
        // Detail card: where the copy is going and what it carries — the
        // to/from matters most when the destination is a fileshare.
        job.SetDetail("From", source);
        job.SetDetail("To", destination);
        job.SetDetail("Files", files.Count.ToString());
        job.SetDetail("Total size", "calculating...");

        try
        {
            await Task.Run(() =>
            {
                // Size first (facts update thread-safely once added).
                long totalBytes = 0;
                foreach (var rel in files)
                {
                    try { totalBytes += new FileInfo(Path.Combine(source, rel)).Length; }
                    catch { /* file may vanish mid-scan; the copy loop reports it */ }
                }
                job.SetDetail("Total size", totalBytes >= 1024 * 1024
                    ? $"{totalBytes / 1024.0 / 1024.0:0.0} MB"
                    : $"{totalBytes / 1024.0:0.0} KB");

                // Pre-create all needed directories in one pass
                var dirs = files
                    .Select(f => Path.GetDirectoryName(Path.Combine(destination, f))!)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var d in dirs)
                    Directory.CreateDirectory(d);

                var copied = 0;
                foreach (var rel in files)
                {
                    File.Copy(
                        Path.Combine(source, rel),
                        Path.Combine(destination, rel),
                        overwrite: true);
                    copied++;
                    var pct = (double)copied / files.Count * 100.0;
                    var fileName = Path.GetFileName(rel);
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        TransferProgress = pct;
                        TransferStatusText = $"Copying bundle... {copied}/{files.Count} - {fileName}";
                        job.SetProgress((int)pct);
                        job.SetStatus($"Copying bundle... {copied}/{files.Count} - {fileName}");
                    });
                }
            });

            TransferProgress = 100;
            job.Complete($"Copied {files.Count} file(s)");
            AppLogger.Info($"General: copied {files.Count} files from {source} to {destination} (excluding .git, installers in B/)");
        }
        catch (Exception ex)
        {
            job.SetError(ex.GetType().Name, ex.ToString());
            job.Fail(ex.Message);
            throw;
        }
        finally
        {
            IsTransferring = false;
            TransferStatusText = string.Empty;
            TransferProgress = 0;
        }
    }

    /// <summary>
    /// Flushes deferred installer changes to disk. Called during Save Bundle
    /// when an installer was dropped on an active bundle without immediate disk writes.
    /// Uses async file copy with progress for large installers.
    /// </summary>
    private async Task FlushPendingInstallerToDiskAsync(string bundleDir)
    {
        if (_pendingInstallerPath is null) return;

        // Clear stale binaries from binary folder (keep only the new one)
        var binDir = BundlePaths.BinaryFolder(bundleDir, ScriptFrameworkProvider.Parse(_config.App.ScriptFramework));
        Directory.CreateDirectory(binDir);
        try
        {
            var newFileName = Path.GetFileName(_pendingInstallerPath);
            await Task.Run(() =>
            {
                foreach (var file in Directory.EnumerateFiles(binDir))
                {
                    if (!string.Equals(Path.GetFileName(file), newFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(file);
                        AppLogger.Info($"General: cleaned stale binary {Path.GetFileName(file)} from B/");
                    }
                }
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"General: B/ cleanup warning: {ex.Message}");
        }

        // Copy new installer to B/
        try
        {
            var destPath = Path.Combine(binDir, Path.GetFileName(_pendingInstallerPath));
            await CopyFileWithProgressAsync(_pendingInstallerPath, destPath);
            AppLogger.Info($"General: flushed pending installer to {destPath}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"General: failed to flush installer to B/: {ex.Message}");
        }

        // Write icon to disk at the path set in App.IconFile
        if (_pendingIconBitmap is not null && !string.IsNullOrEmpty(App.IconFile))
        {
            try
            {
                var iconPath = Path.Combine(bundleDir, App.IconFile);
                Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
                await BundleService.SaveIconAsPngAsync(_pendingIconBitmap, iconPath);
                AppLogger.Info($"General: flushed pending icon to {iconPath}");
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"General: failed to flush icon: {ex.Message}");
            }
        }

        _pendingInstallerPath = null;
        _pendingIconBitmap = null;
    }
}
