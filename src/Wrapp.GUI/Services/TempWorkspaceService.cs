using System.IO;

namespace Wrapp.Services;

/// <summary>
/// Manages temporary working directories under %TEMP%\Wrapp\ for new-package sessions.
/// Uses a FileStream-based lock to prevent multi-instance conflicts.
/// </summary>
public static class TempWorkspaceService
{
    public static readonly string RootPath = Path.Combine(Path.GetTempPath(), "Wrapp");

    private const string LockFileName = ".lock";

    private static FileStream? _currentLock;
    private static string? _currentLockPath;

    /// <summary>
    /// Creates a new temp workspace with a unique sub-directory.
    /// Acquires a lock file to protect it from cleanup by other instances.
    /// Returns the path to the Config.json file in the new workspace.
    /// </summary>
    public static async Task<string> CreateAsync()
    {
        var dir = Path.Combine(RootPath, Guid.NewGuid().ToString("N"));
        var scriptDir = BundlePaths.ScriptDir(dir);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(scriptDir);
        Directory.CreateDirectory(Path.Combine(dir, "B"));
        Directory.CreateDirectory(BundlePaths.ShortcutDir(dir));

        // Write blank Config.json into Script/
        // Scripts are NOT written here -- they're loaded on-demand from embedded
        // resources (Appease) or the bundled psadt-template/ folder (PSADT) by
        // ScriptsViewModel, and only written to disk on first Save Bundle.
        var blankConfig = "{}";
        var configPath  = BundlePaths.ConfigJson(dir);
        await File.WriteAllTextAsync(configPath, blankConfig);

        // Git repo is created lazily on the first save (CommitAllAsync calls
        // InitAsync) or when the History button is pressed, so we skip it here
        // to keep "New Bundle" fast.

        // Exclude the lock file from git tracking (prevents git add -A failures
        // and an infinite auto-commit loop when the lock is held open)
        await File.WriteAllTextAsync(Path.Combine(dir, ".gitignore"), ".lock\n");

        AcquireLock(dir);

        return configPath;
    }

    // ------------------------------------------------------------------
    // Lock file management
    // ------------------------------------------------------------------

    /// <summary>
    /// Acquires an exclusive lock on the workspace directory by holding a
    /// FileStream open on a .lock file. Releases any previously held lock first.
    /// </summary>
    public static void AcquireLock(string workspaceDir)
    {
        ReleaseLock();
        var lockPath = Path.Combine(workspaceDir, LockFileName);
        _currentLock = new FileStream(lockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        _currentLockPath = lockPath;
    }

    /// <summary>
    /// Releases the currently held workspace lock. Safe to call multiple times.
    /// </summary>
    public static void ReleaseLock()
    {
        try
        {
            _currentLock?.Dispose();
        }
        catch { /* ignore */ }
        _currentLock = null;
        _currentLockPath = null;
    }

    // ------------------------------------------------------------------
    // Workspace deletion
    // ------------------------------------------------------------------

    /// <summary>
    /// Deletes a specific temp workspace directory. Releases the lock first
    /// if this process holds it for the given directory.
    /// </summary>
    public static void DeleteWorkspace(string workspaceDir)
    {
        if (string.IsNullOrEmpty(workspaceDir)) return;
        try
        {
            // Release our lock if it belongs to this directory
            if (_currentLockPath is not null &&
                _currentLockPath.StartsWith(workspaceDir, StringComparison.OrdinalIgnoreCase))
            {
                ReleaseLock();
            }

            if (Directory.Exists(workspaceDir))
            {
                ForceDeleteDirectory(workspaceDir);
                AppLogger.Info($"TempWorkspace: deleted {workspaceDir}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"TempWorkspace: cleanup failed for {workspaceDir}: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes a directory tree, first clearing the read-only attribute from
    /// every file in it.
    /// <para>
    /// Git writes its loose objects (<c>.git/objects/**</c>) as READ-ONLY.
    /// <see cref="Directory.Delete(string, bool)"/> throws
    /// <see cref="UnauthorizedAccessException"/> on a read-only file, so a
    /// plain recursive delete of a git-initialised workspace deleted the
    /// writable working files, then died on the object store -- leaving an
    /// orphaned <c>.git</c> tree behind. Those accumulated indefinitely
    /// (observed: 267 workspaces / 5.7 GB, 99.99% of it inside .git).
    /// </para>
    /// </summary>
    private static void ForceDeleteDirectory(string dir)
    {
        ClearReadOnlyAttributes(dir);
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// Best-effort clear of the read-only attribute across a tree. Individual
    /// failures are ignored -- the subsequent delete surfaces anything fatal.
    /// </summary>
    private static void ClearReadOnlyAttributes(string dir)
    {
        try
        {
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    if (file.IsReadOnly) file.IsReadOnly = false;
                }
                catch { /* per-file best effort */ }
            }
        }
        catch { /* enumeration failed (access denied on a subdir) -- let the delete report it */ }
    }

    /// <summary>
    /// Schedules a workspace deletion on a background thread.
    /// Use when cleanup should not block the UI (e.g. after creating a new workspace).
    /// </summary>
    public static void DeleteWorkspaceBackground(string workspaceDir)
    {
        _ = Task.Run(() => DeleteWorkspace(workspaceDir));
    }

    // ------------------------------------------------------------------
    // Stale workspace cleanup
    // ------------------------------------------------------------------

    /// <summary>
    /// Deletes workspace sub-directories older than one hour that are not locked
    /// by another instance. Called at startup and at shutdown, so it is bounded
    /// by <paramref name="budget"/> (default 5s): a large backlog drains across
    /// several sessions instead of stalling launch or close. Never throws.
    /// <para>
    /// Failures are now SUMMARISED to the log. They used to be swallowed
    /// silently, which is why the read-only-git-object delete failure (see
    /// <see cref="ForceDeleteDirectory"/>) went unnoticed for months while
    /// workspaces accumulated.
    /// </para>
    /// </summary>
    public static void CleanOld(TimeSpan? budget = null)
    {
        try
        {
            if (!Directory.Exists(RootPath)) return;

            var deadline = SystemClock.UtcNow + (budget ?? TimeSpan.FromSeconds(5));
            var cutoff   = SystemClock.UtcNow.AddHours(-1);
            int deleted = 0, failed = 0, locked = 0, deferred = 0;
            string? lastError = null;

            foreach (var dir in Directory.GetDirectories(RootPath))
            {
                try
                {
                    var created = Directory.GetCreationTimeUtc(dir);
                    if (created >= cutoff) continue;

                    // Check if another instance holds the lock
                    if (IsLockedByAnotherProcess(dir)) { locked++; continue; }

                    // Time-boxed: stop deleting once the budget is spent, but
                    // keep counting so the log reports the true backlog.
                    if (SystemClock.UtcNow >= deadline) { deferred++; continue; }

                    ForceDeleteDirectory(dir);
                    deleted++;
                }
                catch (Exception ex)
                {
                    failed++;
                    lastError = ex.Message;
                }
            }

            if (deleted > 0 || failed > 0 || deferred > 0)
            {
                AppLogger.Info(
                    $"TempWorkspace: cleanup deleted {deleted}, failed {failed}, "
                    + $"locked {locked}, deferred {deferred}"
                    + (lastError is null ? "" : $" -- last error: {lastError}"));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"TempWorkspace: CleanOld failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns true if the workspace has a .lock file held open by another process.
    /// A stale lock (from a crashed process) returns false so the dir can be cleaned.
    /// </summary>
    private static bool IsLockedByAnotherProcess(string workspaceDir)
    {
        var lockPath = Path.Combine(workspaceDir, LockFileName);
        if (!File.Exists(lockPath)) return false;

        try
        {
            // Try to open exclusively -- if another process holds it, this throws
            using var fs = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            // Opened successfully: lock is stale (owning process crashed), safe to delete
            return false;
        }
        catch (IOException)
        {
            // Cannot open: another instance is actively using this workspace
            return true;
        }
        catch
        {
            // Any other error: skip this directory to be safe
            return true;
        }
    }
}
