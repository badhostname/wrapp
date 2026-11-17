using System.Diagnostics;
using System.IO;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Thin wrapper around the git CLI for package change-history tracking.
/// All methods swallow exceptions and log via AppLogger so a missing git
/// installation is gracefully handled.
/// </summary>
public static class GitService
{
    /// <summary>
    /// Lazily resolved path to the git executable. Prefers the bundled MinGit
    /// in the mingit/ subdirectory, falls back to system "git" on PATH.
    /// </summary>
    private static readonly Lazy<string> _gitPath = new(ResolveGitPath);
    private static bool _pathLogged;

    /// <summary>
    /// Serialises write operations (init, add, commit) so concurrent callers
    /// on slow network paths don't pile up and create index.lock collisions.
    /// Non-blocking: callers that can't acquire the lock skip silently.
    /// </summary>
    private static readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Initialises a git repo in <paramref name="dir"/> and creates an initial commit.
    /// No-op if git is not installed or the directory is already a repo.
    /// </summary>
    public static async Task InitAsync(string dir)
    {
        if (IsGitRepo(dir))
        {
            await EnsureInitialCommitAsync(dir);
            return;
        }

        AppLogger.Info($"Git: initialising repo in {dir}");
        var windowsUser = Environment.UserName;
        await RunAsync(dir, "init");
        await RunAsync(dir, "config", "user.email", $"{windowsUser}@local");
        await RunAsync(dir, "config", "user.name", windowsUser);

        // Ensure .lock is always excluded from tracking (held open by TempWorkspaceService)
        var gitignorePath = Path.Combine(dir, ".gitignore");
        if (!File.Exists(gitignorePath))
            await File.WriteAllTextAsync(gitignorePath, ".lock\n");
        else if (!File.ReadAllText(gitignorePath).Contains(".lock"))
            await File.AppendAllTextAsync(gitignorePath, "\n.lock\n");

        await RunAsync(dir, "add", "-A");
        await RunAsync(dir, "commit", "--allow-empty", "-m", "Initial workspace");
        AppLogger.Info("Git: initial commit created");
    }

    /// <summary>
    /// If the repo exists but has no commits (e.g. previous init was interrupted),
    /// creates the initial commit so subsequent operations don't error.
    /// </summary>
    private static async Task EnsureInitialCommitAsync(string dir)
    {
        var hash = await RunAsync(dir, "rev-parse", "--verify", "--quiet", "HEAD");
        if (!string.IsNullOrWhiteSpace(hash)) return;

        AppLogger.Info("Git: repo exists but has no commits, creating initial commit");
        CleanStaleLock(dir);
        var windowsUser = Environment.UserName;
        await RunAsync(dir, "config", "user.email", $"{windowsUser}@local");
        await RunAsync(dir, "config", "user.name", windowsUser);
        await RunAsync(dir, "add", "-A");
        await RunAsync(dir, "commit", "--allow-empty", "-m", "Initial workspace");
    }

    /// <summary>
    /// Stages all changes and creates a commit with <paramref name="message"/>.
    /// No-op if git is not installed or there is no repo in <paramref name="dir"/>.
    ///
    /// <para>By default (<paramref name="skipIfBusy"/> = false) this blocks until
    /// the shared write lock is available - use for user-initiated saves that
    /// must not silently disappear.</para>
    ///
    /// <para>With <paramref name="skipIfBusy"/> = true the call returns immediately
    /// when another write is already in progress - use for pollers and
    /// external-change auto-commit paths where a missed tick is fine and
    /// dog-piling on the repo is worse than dropping.</para>
    /// </summary>
    public static async Task CommitAllAsync(
        string dir,
        string message,
        CancellationToken ct = default,
        bool skipIfBusy = false)
    {
        if (skipIfBusy)
        {
            if (!await _writeLock.WaitAsync(0, ct))
            {
                AppLogger.Info($"Git: skip-if-busy commit dropped (contention) \"{message}\"");
                return;
            }
        }
        else
        {
            await _writeLock.WaitAsync(ct);
        }

        try
        {
            if (!IsGitRepo(dir))
            {
                await InitAsync(dir);
            }
            else
            {
                await EnsureInitialCommitAsync(dir);
            }

            CleanStaleLock(dir);
            AppLogger.Info($"Git: committing changes in {dir} - \"{message}\"");
            await RunAsync(dir, "add", "-A");

            if (!await HasChangesAsync(dir))
            {
                AppLogger.Info("Git: no changes to commit, skipping");
                return;
            }

            // Pre-commit secret scan: refuse to commit if any staged file contains
            // a non-empty, non-sentinel ClientSecret. Defence-in-depth - the
            // serializer writes a sentinel, but a malicious third-party tool or
            // a hand-edit could reintroduce plaintext.
            if (await StagedSecretsDetectedAsync(dir))
            {
                AppLogger.Warn("Git: commit aborted - plaintext ClientSecret detected in staged diff");
                // Unstage to leave the repo in a clean state; the user's on-disk
                // files are untouched.
                await RunAsync(dir, "reset", "HEAD");
                throw new InvalidOperationException(
                    "Commit aborted: a staged file contains a plaintext ClientSecret. " +
                    "Wrapp serialises tenant secrets as a sentinel and resolves them from " +
                    "%LOCALAPPDATA%\\Wrapp\\settings.json at runtime. Re-save the bundle " +
                    "and try again. If this message persists, please report a bug.");
            }

            // ArgumentList passes the message as a single argv entry, so quotes
            // and other metacharacters in the commit message are safe as-is --
            // no manual escaping needed.
            var output = await RunAsync(dir, "commit", "-m", message);
            if (!string.IsNullOrWhiteSpace(output))
                AppLogger.Info("Git: commit created");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Removes a stale index.lock file (older than 30 seconds) that was left
    /// behind by a crashed or interrupted git process on a network path.
    /// </summary>
    private static void CleanStaleLock(string dir)
    {
        var lockFile = Path.Combine(dir, ".git", "index.lock");
        if (!File.Exists(lockFile)) return;
        try
        {
            var age = SystemClock.UtcNow - File.GetLastWriteTimeUtc(lockFile);
            if (age > TimeSpan.FromSeconds(30))
            {
                File.Delete(lockFile);
                AppLogger.Info("Git: removed stale index.lock");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Git: could not remove index.lock: {ex.Message}");
        }
    }

    /// <summary>Returns true when <paramref name="dir"/> contains a .git sub-directory.</summary>
    public static bool IsGitRepo(string dir)
        => Directory.Exists(Path.Combine(dir, ".git"));

    /// <summary>
    /// Regex that matches a plaintext ClientSecret JSON value, excluding the
    /// sentinel <c>"ref:settings"</c> (with optional whitespace / escapes).
    /// Kept permissive on the value to catch any non-empty non-sentinel string.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex PlaintextClientSecretRegex = new(
        @"""ClientSecret""\s*:\s*""(?!ref:settings""|"")(?:[^""\\]|\\.)+""",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Runs <c>git diff --cached -U0</c> and returns true when the staged content
    /// contains a ClientSecret value other than the sentinel or the empty string.
    /// </summary>
    private static async Task<bool> StagedSecretsDetectedAsync(string dir)
    {
        var diff = await RunAsync(dir, "diff", "--cached", "-U0");
        if (string.IsNullOrWhiteSpace(diff)) return false;

        foreach (var line in diff.Split('\n'))
        {
            // Only scan added lines (start with '+' but not '+++' header)
            if (line.Length < 2 || line[0] != '+' || line.StartsWith("+++")) continue;
            if (PlaintextClientSecretRegex.IsMatch(line)) return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true when the repo at <paramref name="dir"/> has uncommitted changes
    /// (staged or unstaged).  Returns false if git is not installed or there is no repo.
    /// </summary>
    public static async Task<bool> HasChangesAsync(string dir)
    {
        if (!IsGitRepo(dir)) return false;
        var output = await RunAsync(dir, "status", "--porcelain");
        return !string.IsNullOrWhiteSpace(output);
    }

    // -----------------------------------------------------------------------
    // History / diff
    // -----------------------------------------------------------------------

    /// <summary>Returns the full hash of the latest commit, or empty if none.</summary>
    public static async Task<string> GetLatestCommitHashAsync(string dir)
    {
        if (!IsGitRepo(dir)) return string.Empty;
        // --verify --quiet avoids error output on repos with no commits
        var output = await RunAsync(dir, "rev-parse", "--verify", "--quiet", "HEAD");
        return output.Trim();
    }

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msp", ".dll", ".ico", ".png", ".jpg", ".jpeg",
        ".gif", ".bmp", ".zip", ".7z", ".cab", ".intunewin", ".pdf"
    };

    /// <summary>
    /// Returns commits that touched a specific file (follows renames).
    /// Used by the per-file history / restore feature.
    /// </summary>
    public static async Task<List<CommitInfo>> GetFileLogAsync(string dir, string filePath, int count = 100)
    {
        if (!IsGitRepo(dir)) return new List<CommitInfo>();

        var gitPath = filePath.Replace('\\', '/');
        var output = await RunAsync(dir,
            "log", "--follow", "--format=COMMIT_DELIM%h|%H|%an|%ar|%s", $"-{count}", "--", gitPath);
        var results = new List<CommitInfo>();
        if (string.IsNullOrWhiteSpace(output)) return results;

        var chunks = output.Split("COMMIT_DELIM", StringSplitOptions.RemoveEmptyEntries);
        foreach (var chunk in chunks)
        {
            var line = chunk.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var parts = line.Split('|', 5);
            if (parts.Length < 5) continue;

            results.Add(new CommitInfo
            {
                Hash         = parts[0],
                FullHash     = parts[1],
                Author       = parts[2],
                RelativeTime = parts[3],
                Message      = parts[4]
            });
        }

        if (results.Count > 0)
        {
            results[0].IsFirst = true;
            results[results.Count - 1].IsLast = true;
        }

        return results;
    }

    /// <summary>Returns the last <paramref name="count"/> commits with per-file change details.</summary>
    public static async Task<List<CommitInfo>> GetCommitLogAsync(string dir, int count = 100)
    {
        if (!IsGitRepo(dir)) return new List<CommitInfo>();

        // --name-status gives per-file status (A/M/D/R).
        // Note: --shortstat cannot be combined with --name-status (git drops the stat line).
        var output = await RunAsync(dir,
            "log", "--format=COMMIT_DELIM%h|%H|%an|%ar|%s", "--name-status", $"-{count}");
        var results = new List<CommitInfo>();
        if (string.IsNullOrWhiteSpace(output)) return results;

        var chunks = output.Split("COMMIT_DELIM", StringSplitOptions.RemoveEmptyEntries);

        foreach (var chunk in chunks)
        {
            var lines = chunk.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) continue;

            var parts = lines[0].Trim().Split('|', 5);
            if (parts.Length < 5) continue;

            var commit = new CommitInfo
            {
                Hash         = parts[0],
                FullHash     = parts[1],
                Author       = parts[2],
                RelativeTime = parts[3],
                Message      = parts[4]
            };

            // Parse per-file name-status lines: "M\tpath" or "R100\told\tnew"
            for (int i = 1; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                var fileParts = trimmed.Split('\t');
                if (fileParts.Length < 2) continue;

                var statusCode = fileParts[0];
                string status;
                string filePath;
                string oldPath = "";

                if (statusCode.StartsWith("R"))
                {
                    status = "R";
                    oldPath = fileParts.Length > 1 ? fileParts[1] : "";
                    filePath = fileParts.Length > 2 ? fileParts[2] : fileParts[1];
                }
                else
                {
                    status = statusCode.Length > 0 ? statusCode.Substring(0, 1) : statusCode;
                    filePath = fileParts[1];
                }

                var ext = Path.GetExtension(filePath);
                commit.FileChanges.Add(new CommitFileChange
                {
                    Status   = status,
                    FilePath = filePath,
                    OldPath  = oldPath,
                    IsBinary = BinaryExtensions.Contains(ext)
                });
            }

            commit.FilesChanged = commit.FileChanges.Count;

            results.Add(commit);
        }

        // Mark first/last for graph line rendering
        if (results.Count > 0)
        {
            results[0].IsFirst = true;
            results[results.Count - 1].IsLast = true;
        }

        return results;
    }

    /// <summary>Returns the list of files changed in a specific commit.</summary>
    public static async Task<List<CommitFileChange>> GetCommitFilesAsync(string dir, string commitHash)
    {
        if (!IsGitRepo(dir)) return new List<CommitFileChange>();

        // --root handles the initial commit (no parent)
        var output = await RunAsync(dir, "diff-tree", "--root", "--no-commit-id", "-r", "--name-status", commitHash);
        var results = new List<CommitFileChange>();
        if (string.IsNullOrWhiteSpace(output)) return results;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            var parts = trimmed.Split('\t');
            if (parts.Length < 2) continue;

            var statusCode = parts[0];
            string status;
            string filePath;
            string oldPath = "";

            if (statusCode.StartsWith("R"))
            {
                // Rename: R100\told\tnew
                status = "R";
                oldPath = parts.Length > 1 ? parts[1] : "";
                filePath = parts.Length > 2 ? parts[2] : parts[1];
            }
            else
            {
                status = statusCode.Substring(0, 1);
                filePath = parts[1];
            }

            var ext = Path.GetExtension(filePath);
            results.Add(new CommitFileChange
            {
                Status   = status,
                FilePath = filePath,
                OldPath  = oldPath,
                IsBinary = BinaryExtensions.Contains(ext)
            });
        }
        return results;
    }

    /// <summary>Returns the content of a file at a specific commit.</summary>
    public static async Task<string> GetFileContentAtCommitAsync(string dir, string commitRef, string filePath)
    {
        if (!IsGitRepo(dir)) return string.Empty;
        // Normalize to forward slashes; ArgumentList handles spaces/quoting.
        var gitPath = filePath.Replace('\\', '/');
        var output = await RunAsync(dir, "show", $"{commitRef}:{gitPath}");
        return output;
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private static string ResolveGitPath()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "mingit", "cmd", "git.exe");
        if (File.Exists(bundled))
            return bundled;

        // Fall back to system git (resolved via PATH by Process.Start)
        return "git";
    }

    private static Task<string> RunAsync(string workingDir, params string[] args)
    {
        // Whole body runs on the thread pool so `Process.Start` (synchronous on
        // Windows - blocks the calling thread while the OS + antivirus prepare
        // the new process image) never hits the dispatcher. Without Task.Run,
        // the 5-second git-history poll would freeze the UI for 1-3s per tick
        // while MinGit spawns. Callers that already await this method see no
        // observable difference beyond the missing UI freeze.
        return Task.Run(async () =>
        {
            try
            {
                var gitExe = _gitPath.Value;

                if (!_pathLogged)
                {
                    _pathLogged = true;
                    AppLogger.Info($"Git: using executable: {gitExe}");
                }

                // Whitelist the working directory to avoid "dubious ownership" errors
                // on network paths mapped with different credentials.
                var safePath = workingDir.Replace('\\', '/');

                var psi = new ProcessStartInfo(gitExe)
                {
                    WorkingDirectory       = workingDir,
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true
                };

                // SECURITY: add each token to ArgumentList
                // so the OS receives discrete argv entries. The previous approach
                // interpolated args into one string that Windows re-parsed, which
                // allowed git-option injection through attacker-influenced values
                // (imported-bundle filenames, commit refs, file paths). ArgumentList
                // performs the Win32 quoting per-argument -- no value can add a flag.
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add($"safe.directory={safePath}");
                foreach (var arg in args)
                    psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi);
                if (process is null) return string.Empty;

                var output = await process.StandardOutput.ReadToEndAsync();
                var error  = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(error))
                    AppLogger.Warn($"Git [{string.Join(' ', args)}] exit {process.ExitCode}: {error.Trim()}");

                return output;
            }
            catch (Exception ex)
            {
                AppLogger.Exception("Git: command failed", ex);
                return string.Empty;
            }
        });
    }
}
