using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using Wrapp.Helpers;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.ViewModels;

public partial class GeneralViewModel
{
    // -----------------------------------------------------------------------
    // The one save pipeline. Save, Save As, Upgrade and Full apply all end
    // here; only how they pick the directory differs. Order matters:
    //
    //   lock -> copy the source tree (when saving somewhere else) -> write
    //   Config.json, missing scripts, shortcuts and icon -> installer and
    //   icon flush -> editor scripts -> state, git, temp cleanup.
    //
    // Each file is written once. The copy leaves Config.json out because the
    // next step writes it from the model; the editor writes a script only
    // when its text differs from the file the copy just put there. Writing
    // the same file twice within a second is what produced "file in use" on
    // shares with an on-access scanner. A file that is still in use after
    // the retries is listed at the end with Retry; the save itself completes.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Saves the current bundle to <paramref name="dir"/>. When
    /// <paramref name="copySourceRoot"/> names another folder, that bundle's
    /// files are copied first (binaries with their sub-folders, shortcuts,
    /// icon, scripts). <paramref name="raiseConfigLoaded"/> re-announces the
    /// config to every view afterwards, which the upgrade and full-apply
    /// flows need because they changed package metadata on the way.
    /// </summary>
    private async Task<bool> SaveBundleToDirectoryAsync(string dir, string? copySourceRoot, string commitPrefix,
        bool raiseConfigLoaded = true)
    {
        if (!await TrySwitchBundleLockAsync(BundlePaths.ConfigJson(dir), commitPrefix))
            return false;

        // Decided before _configPath moves: where we came from, and whether it was a draft.
        var previousRoot = BundleRootDir;
        var oldTempDir   = IsTempWorkspace() ? previousRoot : null;
        var sourceRoot   = copySourceRoot is not null
                           && !SameDirectory(copySourceRoot, dir)
                           && Directory.Exists(copySourceRoot)
            ? copySourceRoot
            : null;

        AppLogger.Info($"{commitPrefix}: saving to '{dir}' (from '{previousRoot}', copy source '{sourceRoot ?? "none"}')");

        var wasSaving = IsSaving;
        IsSaving = true;
        var failedCopies = new List<BundleCopyFailure>();
        var scriptsFailed = false;
        try
        {
            if (sourceRoot is not null)
            {
                var copy = await CopyBundleDirectoryAsync(sourceRoot, dir);
                failedCopies.AddRange(copy.Failed);
                await CopyLegacyLayoutScriptsAsync(sourceRoot, dir, commitPrefix);
            }

            // Where the .intunewin goes, from Settings or the bundle's own choice,
            // written as a final path so a CLI run reads it from Config.json.
            IntuneWinOutput.Apply(_settings, _config);

            await BundleService.CreateBundleAsync(_config, _settings, dir, InstallerIconSource);
            _configPath = BundlePaths.ConfigJson(dir);

            if (sourceRoot is not null)
                await EnsureInstallerArrivedAsync(dir, commitPrefix);

            await FlushPendingInstallerToDiskAsync(dir);

            scriptsFailed = !await TryWriteEditorScriptsAsync(commitPrefix);

            HasConfig       = true;
            ScriptsAreDirty = false;
            TakeSnapshot();
            OnPropertyChanged(nameof(BundleRootDir));
            OnPropertyChanged(nameof(FolderDisplayPath));
            StatusText = $"{commitPrefix} saved: {dir}";
            AppLogger.Info($"{commitPrefix}: completed -> {dir}");

            MigrateLegacyGitRepo(dir);
            var gitDir = BundlePaths.ScriptDir(dir);
            var gitMsg = $"{commitPrefix}: {App.Company} {App.Name} {App.Version}".Trim();
            var tempDir = oldTempDir is not null && !SameDirectory(oldTempDir, dir) ? oldTempDir : null;
            SafeFireAndForget.Run(async () =>
            {
                try
                {
                    await GitService.CommitAllAsync(gitDir, gitMsg);
                    await Application.Current.Dispatcher.InvokeAsync(() => RefreshGitStatusAsync(gitDir));
                }
                catch (Exception ex)
                {
                    AppLogger.Exception($"Git: background {commitPrefix} commit failed", ex);
                }

                if (tempDir is not null)
                    TempWorkspaceService.DeleteWorkspace(tempDir);
            }, $"{commitPrefix}-git-commit");

            if (raiseConfigLoaded)
                ConfigLoaded?.Invoke(this, (_config, _configPath));
            ScheduleBundleNoticesRefresh();

            if (failedCopies.Count > 0 || scriptsFailed)
                await OfferRetryForFilesInUseAsync(dir, sourceRoot, failedCopies, scriptsFailed, commitPrefix);

            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"{commitPrefix} failed: {ex.Message}";
            AppLogger.Exception($"SaveBundleToDirectoryAsync ({commitPrefix})", ex);
            return false;
        }
        finally
        {
            IsSaving = wasSaving;
        }
    }

    /// <summary>The icon sub-folder name from Settings, for the package views' Browse and drop handlers.</summary>
    internal string IconFolderName => _settings.IconFolderName;


    private static bool SameDirectory(string a, string b)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Copies the source bundle tree into <paramref name="destination"/> with
    /// the status-bar overlay and a background job, so a copy to a share is
    /// visible like every other long operation. Returns what arrived and what
    /// did not; nothing here throws for a single file.
    /// </summary>
    private async Task<BundleCopyResult> CopyBundleDirectoryAsync(string source, string destination)
    {
        var options = new BundleCopyOptions(BinFolderName, SkipInstallersInBinaryRoot: _pendingInstallerPath is not null);
        var files = await Task.Run(() => BundleCopyService.Plan(source, options));
        if (files.Count == 0) return new BundleCopyResult();

        IsTransferring = true;
        TransferProgress = 0;
        TransferStatusText = $"Copying bundle ({files.Count} files)...";

        var job = _jobTracker?.BeginJob($"Copying bundle ({files.Count} files)", destination) ?? default;
        job.SetDetail("From", source);
        job.SetDetail("To", destination);
        job.SetDetail("Files", files.Count.ToString());
        var sizeShown = false;

        var progress = UiProgress.For<BundleCopyProgress>(p =>
        {
            if (!sizeShown)
            {
                job.SetDetail("Total size", p.BytesTotal >= 1024 * 1024
                    ? $"{p.BytesTotal / 1024.0 / 1024.0:0.0} MB"
                    : $"{p.BytesTotal / 1024.0:0.0} KB");
                sizeShown = true;
            }
            var pct = (double)p.Done / p.Total * 100.0;
            TransferProgress = pct;
            TransferStatusText = $"Copying bundle... {p.Done}/{p.Total} - {p.File}";
            job.SetProgress((int)pct);
            job.SetStatus($"Copying bundle... {p.Done}/{p.Total} - {p.File}");
        });

        try
        {
            var result = await BundleCopyService.CopyFilesAsync(source, destination, files, progress);
            TransferProgress = 100;
            if (result.Failed.Count == 0)
                job.Complete($"Copied {result.Copied.Count} file(s)");
            else
            {
                job.SetError("FilesInUse", string.Join("\n", result.Failed.Select(f => $"{f.RelativePath}: {f.Error}")));
                job.Fail($"{result.Failed.Count} of {files.Count} file(s) could not be copied");
            }
            AppLogger.Info($"General: copied {result.Copied.Count} of {files.Count} files from {source} to {destination}");
            return result;
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
    /// A source made before the Script\ layout keeps its scripts at the
    /// root. The tree copy preserves relative paths, so they would land at
    /// the destination root and CreateBundleAsync would write templates into
    /// Script\. Copy them to Script\ first. Sources in the current layout
    /// were handled by the tree copy and are left alone.
    /// </summary>
    private async Task CopyLegacyLayoutScriptsAsync(string sourceRoot, string dir, string commitPrefix)
    {
        var fw = ScriptFrameworkProvider.Parse(_config.App.ScriptFramework);
        var destScriptDir = BundlePaths.ScriptDir(dir);
        foreach (var scriptName in ScriptFrameworkProvider.GetBundleScripts(fw))
        {
            var newLayoutSrc = Path.Combine(BundlePaths.ScriptDir(sourceRoot), scriptName);
            var oldLayoutSrc = Path.Combine(sourceRoot, scriptName);
            if (File.Exists(newLayoutSrc) || !File.Exists(oldLayoutSrc)) continue;

            Directory.CreateDirectory(destScriptDir);
            var destFile = Path.Combine(destScriptDir, scriptName);
            await FileRetry.RunAsync(() => Task.Run(() => File.Copy(oldLayoutSrc, destFile, overwrite: true)), scriptName);
            AppLogger.Info($"{commitPrefix}: legacy layout - copied {scriptName} from the source root to Script\\");
        }
    }

    /// <summary>
    /// Save As from a draft whose binary folder never got the installer (the
    /// file was dropped but not yet copied): put the known installer into the
    /// new binary folder so the saved bundle is complete.
    /// </summary>
    private async Task EnsureInstallerArrivedAsync(string dir, string commitPrefix)
    {
        if (_pendingInstallerPath is not null) return;
        if (string.IsNullOrEmpty(_installerFullPath) || !File.Exists(_installerFullPath)) return;

        var dstBin = BundlePaths.BinaryFolder(dir, ScriptFrameworkProvider.Parse(_config.App.ScriptFramework));
        var dest = Path.Combine(dstBin, Path.GetFileName(_installerFullPath));
        if (File.Exists(dest) || SameDirectory(Path.GetDirectoryName(_installerFullPath)!, dstBin)) return;

        Directory.CreateDirectory(dstBin);
        await CopyFileWithProgressAsync(_installerFullPath, dest);
        AppLogger.Info($"{commitPrefix}: copied installer {Path.GetFileName(_installerFullPath)} into the binary folder");
    }

    /// <summary>Writes the editor buffers; false when that failed (logged, reported with Retry).</summary>
    private async Task<bool> TryWriteEditorScriptsAsync(string commitPrefix)
    {
        if (BundleSaving is null) return true;
        try
        {
            await BundleSaving.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Exception($"{commitPrefix}: editor scripts were not written", ex);
            return false;
        }
    }

    /// <summary>
    /// The save is done; these files are not. Offer Retry until everything
    /// arrived or the operator chooses to carry on.
    /// </summary>
    private async Task OfferRetryForFilesInUseAsync(string dir, string? sourceRoot, List<BundleCopyFailure> failedCopies,
        bool scriptsFailed, string commitPrefix)
    {
        while (failedCopies.Count > 0 || scriptsFailed)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"The bundle was saved to:\n{dir}\n");
            sb.AppendLine("These files could not be written because another program had them open:\n");
            foreach (var f in failedCopies.Take(12))
                sb.AppendLine($"  - {f.RelativePath}  ({f.Error})");
            if (failedCopies.Count > 12)
                sb.AppendLine($"  - and {failedCopies.Count - 12} more (see app.log)");
            if (scriptsFailed)
                sb.AppendLine($"  - the scripts open in the editor (see app.log)");
            sb.AppendLine("\nRetry now, or continue and copy them yourself later.");

            var retry = await FluentDialog.ConfirmAsync("Some files are still in use", sb.ToString(), "Retry", "Continue");
            if (!retry)
            {
                AppLogger.Warn($"{commitPrefix}: operator continued with {failedCopies.Count} file(s) not copied"
                               + (scriptsFailed ? " and editor scripts not written" : ""));
                return;
            }

            if (failedCopies.Count > 0 && sourceRoot is not null)
            {
                var again = await BundleCopyService.CopyFilesAsync(sourceRoot, dir, failedCopies.Select(f => f.RelativePath).ToList());
                failedCopies.Clear();
                failedCopies.AddRange(again.Failed);
            }
            if (scriptsFailed)
                scriptsFailed = !await TryWriteEditorScriptsAsync(commitPrefix);
        }
    }
}
