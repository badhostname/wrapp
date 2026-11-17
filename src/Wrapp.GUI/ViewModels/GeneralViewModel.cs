using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wrapp.Helpers;
using Wrapp.Models;
using Wrapp.Services;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;

namespace Wrapp.ViewModels;

public partial class GeneralViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly IntuneWinDecryptOrchestrator? _decryptOrchestrator;
    private readonly IFeatureGate? _featureGate;
    private BackgroundJobTracker? _jobTracker;
    private AppConfigModel _config = new();
    private string _configPath = string.Empty;
    private string _installerFullPath = string.Empty;

    // Deferred installer state: when an installer is dropped on an active (saved) bundle,
    // disk writes are postponed until Save Bundle. These fields track the pending work.
    private string? _pendingInstallerPath;
    private BitmapSource? _pendingIconBitmap;
    private BitmapSource? _msiPickedIcon; // transient: set by MSI Icon table picker, consumed by ApplyInstallerFile

    /// <summary>True when an installer was dropped on an active bundle but not yet saved to disk.</summary>
    public bool HasPendingInstallerChanges => _pendingInstallerPath is not null;

    // Timer-based change detection: compares serialized model against disk snapshot
    private readonly DispatcherTimer _changeTimer;
    private string _diskSnapshot = string.Empty;   // JSON from last save/load (= what is on disk)
    private string _lastSyncedJson = string.Empty;  // last JSON pushed to ConfigChanged subscribers

    /// <summary>Raised when a config is loaded so other VMs can propagate it.</summary>
    public event EventHandler<(AppConfigModel Config, string Path)>? ConfigLoaded;

    /// <summary>Raised when the serialized model changes. Used by ConfigJsonViewModel for auto-sync.</summary>
    public event Action? ConfigChanged;

    /// <summary>Raised just before the bundle is written so subscribers can persist their data (e.g. scripts).</summary>
    public event Func<Task>? BundleSaving;

    /// <summary>Set by ScriptsViewModel when script content has been modified in the editor.</summary>
    internal bool ScriptsAreDirty { get; set; }

    // -----------------------------------------------------------------------
    // Properties bound to the General section form
    // -----------------------------------------------------------------------

    public AppSection App => _config.App;
    public AppConfigModel FullConfig => _config;
    public string ConfigPath => _configPath;

    /// <summary>Binary folder name for the current framework ("B" for Appease, "Files" for PSADT).</summary>
    private string BinFolderName => ScriptFrameworkProvider.GetBinaryFolderName(
        ScriptFrameworkProvider.Parse(_config.App.ScriptFramework));


    /// <summary>
    /// The bundle root directory (version folder) resolved from the config path.
    /// For the new layout (Config.json in Script/), this is the parent of Script/.
    /// For old layout (Config.json at root), this is the directory containing Config.json.
    /// </summary>
    public string BundleRootDir => string.IsNullOrEmpty(_configPath)
        ? string.Empty
        : BundleService.GetBundleRoot(_configPath);

    /// <summary>
    /// Directory where the per-bundle git repo lives. Previously the bundle
    /// root - now the <c>Script/</c> subfolder so git only tracks scripts +
    /// Config.json, not the installer binaries in <c>B/</c> / <c>Files/</c>
    /// that bloat diffs and the repo. Existing bundles with a legacy root
    /// <c>.git</c> folder are transparently migrated on first open via
    /// <see cref="MigrateLegacyGitRepo"/>.
    /// </summary>
    public string GitRepoDir => string.IsNullOrEmpty(_configPath)
        ? string.Empty
        : BundlePaths.ScriptDir(BundleRootDir);

    /// <summary>
    /// Silent one-time migration: if an existing bundle has <c>.git</c> at the
    /// root (the pre-0.6.0.0156 layout), archive it to
    /// <c>.git.backup-&lt;ticks&gt;/</c> so nothing is destroyed and the next
    /// <see cref="GitService.InitAsync"/> creates a fresh repo under
    /// <c>Script/</c>. Pre-migration commit history is retained in the
    /// archive but not reconstructed - pragmatically, the per-bundle git
    /// history is a local recovery tool and most users never inspect it.
    /// </summary>
    private static void MigrateLegacyGitRepo(string bundleRoot)
    {
        if (string.IsNullOrEmpty(bundleRoot)) return;
        var legacyGit = Path.Combine(bundleRoot, ".git");
        if (!Directory.Exists(legacyGit)) return;
        try
        {
            var backupName = $".git.backup-{SystemClock.Now:yyyyMMdd-HHmmss}";
            var backupPath = Path.Combine(bundleRoot, backupName);
            Directory.Move(legacyGit, backupPath);
            AppLogger.Info($"Git: migrated legacy root repo to {backupName} (git now tracks Script/ only)");
        }
        catch (Exception ex)
        {
            // Non-fatal: log and continue. The caller will still init a fresh
            // repo under Script/ alongside the orphaned root .git.
            AppLogger.Warn($"Git: legacy repo migration failed for {bundleRoot}: {ex.Message}");
        }
    }

    [ObservableProperty]
    private bool _hasConfig;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _statusText = "Drag an installer (.exe/.msi/.msp/.intunewin) or a package folder here - or use Browse.";

    [ObservableProperty]
    private bool _isImportingIntuneWin;

    /// <summary>
    /// True while a bundle save is in progress. Gates the Save command so a
    /// double-click (or a second Save triggered via another UI path) cannot
    /// launch two concurrent saves competing on the same on-disk state.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveBundleCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveBundleAsCommand))]
    private bool _isSaving;

    public bool CanSaveBundle => !IsSaving;

    /// <summary>Icon extracted from the currently loaded installer file.</summary>
    [ObservableProperty]
    private ImageSource? _installerIconSource;

    // The icon image is not in Config.json (only App.IconFile, its name), so
    // the dirty check compares pixels: _iconBaseline is the icon as last
    // opened or saved, _iconCurrent the one on screen. Replacing the icon
    // with one of the same file name - Extract Icon Only on a bundle that
    // already had Icon\App.png - used to leave the bundle "Up to date".
    private string _iconBaseline = string.Empty;
    private string _iconCurrent = string.Empty;

    /// <summary>True when the icon on screen differs from the one last opened or saved.</summary>
    public bool IconChangedSinceSave => !string.Equals(_iconBaseline, _iconCurrent, StringComparison.Ordinal);

    partial void OnInstallerIconSourceChanged(ImageSource? value)
    {
        _iconCurrent = IconFingerprint.Of(value);
        CheckForChanges();
    }

    /// <summary>The icon now on screen is what the bundle holds on disk.</summary>
    private void RebaselineIcon() => _iconBaseline = _iconCurrent;

    /// <summary>True while a large file operation (copy/delete) is running in the background.</summary>
    [ObservableProperty]
    private bool _isTransferring;

    /// <summary>Progress 0-100 for the current file transfer operation.</summary>
    [ObservableProperty]
    private double _transferProgress;

    /// <summary>Status text for the current transfer (e.g. "Copying installer... 45%").</summary>
    [ObservableProperty]
    private string _transferStatusText = string.Empty;

    // GitLastCommitText removed -- status bar no longer shows last commit info.
    // File history provides better tracking. RefreshGitStatusAsync kept as no-op
    // to avoid touching background Task.Run callers.

    public string FolderDisplayPath
        => string.IsNullOrEmpty(_configPath) ? string.Empty : BundleRootDir;

    public string InstallerDisplayPath
        => string.IsNullOrEmpty(_installerFullPath) ? string.Empty : _installerFullPath;

    /// <summary>Drives IsEnabled on the DetectRunning "Remove" button.</summary>
    public SelectionTracker<DetectRunningEntry> DetectRunningSelection { get; }
        = new(e => e.IsSelected);

    /// <summary>
    /// Applies a pre-parsed config (e.g. from JSON editor parse-back).
    /// </summary>
    public void ApplyConfig(AppConfigModel config, string path)
    {
        _config        = config;
        _configPath    = path ?? string.Empty;
        HasConfig      = true;
        IsDirty        = true;
        OnPropertyChanged(nameof(App));
        OnPropertyChanged(nameof(FullConfig));
        OnPropertyChanged(nameof(FolderDisplayPath));
        // Re-point the DetectRunning selection tracker at the new config's
        // collection. The App instance changes whenever a config is loaded.
        DetectRunningSelection.Bind(App.DetectRunning);
        ConfigLoaded?.Invoke(this, (_config, _configPath));
    }

    public GeneralViewModel(
        AppSettings settings,
        IntuneWinDecryptOrchestrator? decryptOrchestrator = null,
        IFeatureGate? featureGate = null)
    {
        _settings = settings;
        _decryptOrchestrator = decryptOrchestrator;
        _featureGate = featureGate;
        DetectRunningSelection.Bind(App.DetectRunning);

        _changeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _changeTimer.Tick += (_, _) => CheckForChanges();
        _changeTimer.Start();
    }

    /// <summary>
    /// Config edits need the main window active, so the 750ms full-config
    /// serializer pauses while Wrapp is in the background. Both transitions run
    /// one immediate check, so dirty state is exact at the boundary and
    /// background mutations are caught the moment focus returns.
    /// </summary>
    public void SetChangeTrackingActive(bool active)
    {
        CheckForChanges();
        if (active) _changeTimer.Start();
        else _changeTimer.Stop();
    }

    public void WireJobTracker(BackgroundJobTracker tracker) => _jobTracker = tracker;

    // -----------------------------------------------------------------------
    // Drop validation (called from code-behind during DragOver/DragEnter)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns true if the dropped paths represent a file we can handle:
    ///   - A folder containing Config.json
    ///   - A .exe that starts with MZ (PE header)
    ///   - A .msi or .msp that starts with OLE compound document magic bytes
    /// No file is executed - only metadata/magic bytes are read.
    /// </summary>
    public static bool ValidateDroppedPaths(string[] paths)
    {
        if (paths.Length == 0) return false;
        var path = paths[0];

        if (Directory.Exists(path))
            return BundleService.FindBundleConfig(path) is not null;

        if (!File.Exists(path)) return false;

        // Config.json itself, dropped from either layout.
        if (string.Equals(Path.GetFileName(path), BundlePaths.ConfigFileName, StringComparison.OrdinalIgnoreCase))
            return true;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".exe" => IsValidExe(path),
                ".msi" => IsValidOleFile(path),
                ".msp" => IsValidOleFile(path),
                ".intunewin" => true,
                ".png" or ".jpg" or ".jpeg" or ".ico" => true,
                _      => false
            };
        }
        catch { return false; }
    }

    // PE executable: first two bytes must be MZ (0x4D 0x5A)
    private static bool IsValidExe(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buf = new byte[2];
        return fs.Read(buf, 0, 2) == 2 && buf[0] == 0x4D && buf[1] == 0x5A;
    }

    // MSI / MSP: OLE Compound Document magic D0 CF 11 E0
    private static bool IsValidOleFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buf = new byte[4];
        return fs.Read(buf, 0, 4) == 4
            && buf[0] == 0xD0 && buf[1] == 0xCF && buf[2] == 0x11 && buf[3] == 0xE0;
    }

    // -----------------------------------------------------------------------
    // Commands
    // -----------------------------------------------------------------------

    [RelayCommand]
    private async Task LoadFolderAsync()
    {
        var folder = FileDialogService.BrowseFolder("Select the bundle folder, or any folder inside it");
        if (folder is null) return;
        var jsonPath = BundleService.FindBundleConfig(folder);
        if (jsonPath is null)
        {
            StatusText = $"No Config.json found in or above: {folder}";
            return;
        }
        await LoadFromPathAsync(jsonPath);
    }

    [RelayCommand]
    private void BrowseIcon()
    {
        var path = FileDialogService.BrowseFile(
            "Image Files|*.png;*.jpg;*.jpeg;*.ico;*.bmp|All Files|*.*",
            "Select App Icon");
        if (path is null) return;
        ApplyIconFile(path);
    }

    /// <summary>
    /// Expands {{Name}} placeholders across the App Info string fields
    /// (GUID / IconFile / ScriptFramework excluded - see
    /// <see cref="PlaceholderApplyService.GeneralFields"/>). Shows the summary
    /// confirm dialog first; mutates only on confirm.
    /// </summary>
    [RelayCommand]
    private Task ReplacePlaceholdersAsync()
        => PlaceholderApplyService.ApplyAsync(
            "General", PlaceholderApplyService.GeneralFields(App), App);

    [RelayCommand]
    private void GenerateGuid()
    {
        App.GUID = Guid.NewGuid().ToString();
        AppLogger.Info($"General: generated new GUID: {App.GUID}");
    }

    [RelayCommand]
    private void AddDetectRunning()
    {
        App.DetectRunning.Add(new DetectRunningEntry());
    }

    [RelayCommand]
    private void RemoveSelectedDetectRunning()
    {
        var selected = App.DetectRunning.Where(e => e.IsSelected).ToList();
        foreach (var entry in selected)
            App.DetectRunning.Remove(entry);
    }

    [RelayCommand(CanExecute = nameof(CanSaveBundle))]
    private async Task SaveBundleAsync()
    {
        if (IsSaving) return;             // Defensive: CanSaveBundle should already gate this.
        IsSaving = true;                  // Flip BEFORE any await so double-click can't race.

        var bundleLabel = !string.IsNullOrWhiteSpace(App.Name)
            ? $"Save bundle: {App.Name}{(string.IsNullOrWhiteSpace(App.Version) ? "" : $" {App.Version}")}"
            : "Save bundle";
        var job = _jobTracker?.BeginJob(bundleLabel, BundleRootDir) ?? default;
        try
        {
            string dir;
            string? copySource = null;
            if (!string.IsNullOrEmpty(_configPath) && !IsTempWorkspace())
            {
                // Active bundle: save back in place.
                dir = BundleRootDir;
            }
            else
            {
                // A draft's first save: the save sheet, then bring the draft's
                // binaries, icon and shortcuts along through the pipeline.
                var target = await ShowSaveSheetAsync("Save bundle", App);
                if (target is null)
                {
                    job.Complete("Save cancelled");
                    return;
                }
                dir = target;
                copySource = BundleRootDir;
                if (!await ConfirmOverwriteAsync(dir))
                {
                    job.Complete("Save cancelled (overwrite declined)");
                    return;
                }
            }

            // Diagnostic: log the inputs that drive the save target so any "my save
            // went to the wrong folder" report has ground truth in app.log.
            AppLogger.Info($"SaveBundle: _configPath='{_configPath}', BundleRootDir='{BundleRootDir}', target dir='{dir}'");

            if (await SaveBundleToDirectoryAsync(dir, copySource, "Save", raiseConfigLoaded: false))
                job.Complete("Bundle saved");
            else
                job.Fail(StatusText);
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private async Task SaveBundleAsAsync()
    {
        if (IsSaving) return;

        var dir = await ShowSaveSheetAsync("Save bundle as", App);
        if (dir is null) return;

        if (!await ConfirmOverwriteAsync(dir)) return;

        AppLogger.Info($"SaveBundleAs: saving to {dir}");
        // The current bundle (draft or saved) is the copy source: its binaries
        // with their sub-folders, shortcuts, icon and scripts all come along.
        await SaveBundleToDirectoryAsync(dir, BundleRootDir, "Save as", raiseConfigLoaded: false);
    }


    // -----------------------------------------------------------------------
    // Load from an explicit path (used by splash/New flow)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Switches this process's cross-instance bundle lock to the bundle
    /// containing <paramref name="configPath"/>. Temp workspaces release the
    /// bundle lock (they carry their own per-directory lock). Returns false --
    /// after showing the "in use" dialog -- when another Wrapp instance holds
    /// the target bundle; callers must abort and leave current state untouched.
    /// </summary>
    private async Task<bool> TrySwitchBundleLockAsync(string configPath, string action)
    {
        string root;
        try { root = BundleService.GetBundleRoot(configPath); }
        catch { root = string.Empty; }

        if (string.IsNullOrEmpty(root)
            || root.StartsWith(TempWorkspaceService.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            BundleLockService.Release();
            return true;
        }

        if (BundleLockService.TryAcquire(root)) return true;

        AppLogger.Warn($"BundleLock: {action} refused -- '{root}' is open in another Wrapp instance");
        await FluentDialog.ShowWarningAsync("Bundle In Use",
            $"This bundle is open in another Wrapp instance:\n\n{root}\n\n"
            + "Close it there first, or work on a different bundle.");
        return false;
    }

    public async Task LoadFromPathAsync(string path, AppConfigModel? preloaded = null)
    {
        AppLogger.Info($"General: loading config from {path}");

        // Refuse to open a bundle another instance is editing -- BEFORE any UI
        // state is reset, so a refusal leaves the current bundle intact.
        if (!await TrySwitchBundleLockAsync(path, "open")) return;

        try
        {
            // Reset UI state from the previous package before loading the new one
            InstallerIconSource    = null;
            _installerFullPath     = string.Empty;
            _pendingInstallerPath  = null;
            _pendingIconBitmap     = null;
            OnPropertyChanged(nameof(InstallerDisplayPath));

            _config        = preloaded ?? await ConfigFileService.LoadAsync(path);
            _configPath    = path;
            HasConfig      = true;

            // Auto-generate GUID for new bundles that don't have one yet
            if (string.IsNullOrEmpty(_config.App.GUID))
            {
                _config.App.GUID = Guid.NewGuid().ToString();
                AppLogger.Info($"General: auto-generated GUID for new bundle: {_config.App.GUID}");
            }

            TakeSnapshot();
            OnPropertyChanged(nameof(App));
            OnPropertyChanged(nameof(FullConfig));
            OnPropertyChanged(nameof(BundleRootDir));
            OnPropertyChanged(nameof(FolderDisplayPath));
            StatusText = $"Loaded: {Path.GetFileName(BundleRootDir)}";
            AppLogger.Info($"General: config loaded OK - Company={App.Company}, Name={App.Name}, Version={App.Version}");

            // Icon paths are relative to the bundle root (version folder)
            var bundleRoot = BundleRootDir;
            if (!string.IsNullOrEmpty(bundleRoot))
            {
                var iconLoaded = false;

                // Primary: use App.IconFile if set
                if (!string.IsNullOrEmpty(_config.App.IconFile))
                {
                    iconLoaded = TryLoadIconFromPath(
                        Path.Combine(bundleRoot, _config.App.IconFile));
                }

                // Fallback: search SCCM package Icon fields
                if (!iconLoaded)
                {
                    foreach (var pkg in _config.Script.SCCMPackager.Packages)
                    {
                        if (string.IsNullOrEmpty(pkg.Icon)) continue;
                        iconLoaded = TryLoadIconFromPath(
                            Path.Combine(bundleRoot, pkg.Icon));
                        if (iconLoaded)
                        {
                            _config.App.IconFile = pkg.Icon;
                            break;
                        }
                    }
                }

                // Fallback: search Intune package IconFile fields
                if (!iconLoaded)
                {
                    foreach (var pkg in _config.Script.IntunePackager.Packages)
                    {
                        if (string.IsNullOrEmpty(pkg.IconFile)) continue;
                        iconLoaded = TryLoadIconFromPath(
                            Path.Combine(bundleRoot, pkg.IconFile));
                        if (iconLoaded)
                        {
                            _config.App.IconFile = pkg.IconFile;
                            break;
                        }
                    }
                }

                // Fallback: pick first PNG/ICO in the Icon subfolder
                if (!iconLoaded)
                {
                    var iconFolder = Path.Combine(bundleRoot, _settings.IconFolderName);
                    if (Directory.Exists(iconFolder))
                    {
                        foreach (var candidate in Directory.EnumerateFiles(iconFolder)
                            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                                     || f.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                            .Take(1))
                        {
                            iconLoaded = TryLoadIconFromPath(candidate);
                            if (iconLoaded)
                            {
                                _config.App.IconFile = Path.GetRelativePath(bundleRoot, candidate);
                                break;
                            }
                        }
                    }
                }

                if (!iconLoaded)
                    AppLogger.Warn("General: no icon file found for this package");
            }

            // The snapshot above was taken before the icon was read from disk;
            // the icon now on screen is the one the bundle holds.
            RebaselineIcon();
            IsDirty = false;
            CheckForChanges();

            ConfigLoaded?.Invoke(this, (_config, path));
            ScheduleBundleNoticesRefresh();

            // Ensure a git repo exists under Script/ so saves become commits.
            // Run on a background thread so the UI is responsive immediately.
            // Legacy bundles with .git at the bundle root are silently migrated
            // (archived to .git.backup-<ticks>) before the new Script/-scoped
            // init runs.
            if (!string.IsNullOrEmpty(bundleRoot))
            {
                MigrateLegacyGitRepo(bundleRoot);
                var gitDir = BundlePaths.ScriptDir(bundleRoot);
                SafeFireAndForget.Run(async () =>
                {
                    try
                    {
                        await GitService.InitAsync(gitDir);
                        if (await GitService.HasChangesAsync(gitDir))
                        {
                            AppLogger.Info("Git: external changes detected, committing");
                            // skipIfBusy: this runs during bundle load; if a user save
                            // somehow raced, we let them win and the next poll picks
                            // up the residual changes.
                            await GitService.CommitAllAsync(gitDir, "External changes detected on load", skipIfBusy: true);
                        }
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                            () => RefreshGitStatusAsync(gitDir));
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Exception("Git: background init failed", ex);
                    }
                }, "load-git-init-and-commit");
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Error loading config: {ex.Message}";
            AppLogger.Exception("LoadFromPathAsync", ex);
        }
    }


    // -----------------------------------------------------------------------
    // Change detection (timer-based, covers ALL model sections)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Takes a snapshot of the current serialized model. Called after save/load
    /// to mark the current state as "what is on disk."
    /// </summary>
    internal void TakeSnapshot()
    {
        try
        {
            _diskSnapshot = ConfigFileService.SerializeToJson(_config);
            _lastSyncedJson = _diskSnapshot;
        }
        catch { _diskSnapshot = string.Empty; }
        RebaselineIcon();
        IsDirty = false;
    }

    /// <summary>
    /// Timer tick: serializes the model and compares against disk snapshot.
    /// Sets IsDirty and fires ConfigChanged as needed.
    /// </summary>
    private void CheckForChanges()
    {
        if (!HasConfig) return;
        try
        {
            var currentJson = ConfigFileService.SerializeToJson(_config);

            // Unsaved = the editor scripts changed, or the Config.json Wrapp
            // would write differs from the one last opened or saved, or the
            // icon on screen is not the one the bundle holds (the image is
            // not in Config.json, only its file name).
            IsDirty = ScriptsAreDirty
                || (!string.IsNullOrEmpty(_diskSnapshot) && currentJson != _diskSnapshot)
                || IconChangedSinceSave;

            if (currentJson != _lastSyncedJson)
            {
                _lastSyncedJson = currentJson;
                ConfigChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            // Transient serialize failures during rapid editing are
            // expected and stay quiet - but a PERSISTENT fault here silently
            // freezes IsDirty, and CloseGuard trusts IsDirty absolutely (a
            // close would skip the save prompt = data loss). Log the first
            // occurrence per session so the failure is diagnosable.
            if (!_dirtyCheckFaultLogged)
            {
                _dirtyCheckFaultLogged = true;
                AppLogger.Warn($"General: dirty-check serialization failed (logged once per session) -- {ex.Message}");
            }
        }
    }

    private bool _dirtyCheckFaultLogged;

    /// <summary>
    /// Returns true when the current config is in a temp workspace (draft that has never been saved).
    /// </summary>
    internal bool IsTempWorkspace()
        => !string.IsNullOrEmpty(_configPath) &&
           _configPath.StartsWith(TempWorkspaceService.RootPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when an active (non-draft, non-temp) bundle is loaded.
    /// </summary>
    public bool IsActiveBundle => HasConfig && !IsTempWorkspace();

    // -----------------------------------------------------------------------
    // Overwrite confirmation with visual summary
    // -----------------------------------------------------------------------

    /// <summary>
    /// Shows a visual summary of the existing bundle at <paramref name="targetDir"/>
    /// and asks the user to confirm overwriting. Returns true to proceed.
    /// </summary>
    private async Task<bool> ConfirmOverwriteAsync(string targetDir)
    {
        if (!Directory.Exists(targetDir)) return true;

        // Enumerate and gather metadata off the UI thread (may be slow on network paths)
        var (fileCount, totalBytes, lastModified, created,
             scriptCount, binCount, shortcutCount, iconCount, gitIgnoreCount, otherCount) =
            await Task.Run(() =>
            {
                var files = Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories)
                    .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar)
                             && !f.EndsWith(".lock"))
                    .ToList();

                if (files.Count == 0)
                    return (0, 0L, DateTime.MinValue, DateTime.MinValue, 0, 0, 0, 0, 0, 0);

                long bytes = 0;
                var lastMod = DateTime.MinValue;
                var createdTime = Directory.GetCreationTime(targetDir);
                foreach (var f in files)
                {
                    try
                    {
                        var info = new FileInfo(f);
                        bytes += info.Length;
                        if (info.LastWriteTime > lastMod) lastMod = info.LastWriteTime;
                    }
                    catch { /* skip inaccessible files */ }
                }

                var rels = files.Select(f => Path.GetRelativePath(targetDir, f)).ToList();
                int sc = rels.Count(f => f.StartsWith(BundlePaths.ScriptFolder + Path.DirectorySeparatorChar));
                int bc = rels.Count(f => f.StartsWith("B" + Path.DirectorySeparatorChar)
                    || f.StartsWith("Files" + Path.DirectorySeparatorChar));
                int shc = rels.Count(f => f.StartsWith("Shortcuts" + Path.DirectorySeparatorChar));
                int ic = rels.Count(f => f.StartsWith(_settings.IconFolderName + Path.DirectorySeparatorChar));
                int gic = rels.Count(f => f.Equals(".gitignore", StringComparison.OrdinalIgnoreCase));
                int oc = files.Count - sc - bc - shc - ic - gic;

                return (files.Count, bytes, lastMod, createdTime, sc, bc, shc, ic, gic, oc);
            });

        if (fileCount == 0) return true;

        var accent = Application.Current.TryFindResource("AccentBgBrush") as Brush;
        var primary = Application.Current.TryFindResource("TextPrimaryBrush") as Brush;
        var secondary = Application.Current.TryFindResource("TextSecondaryBrush") as Brush;
        var muted = Application.Current.TryFindResource("TextMutedBrush") as Brush;
        var warningBrush = Application.Current.TryFindResource("WarningBrush") as Brush ?? accent;

        var panel = new StackPanel { Width = 460 };

        // Folder path
        var pathRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        pathRow.Children.Add(new TextBlock
        {
            Text = "\uE8B7", FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14, Foreground = accent, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        pathRow.Children.Add(new TextBlock
        {
            Text = targetDir, FontSize = 11, FontFamily = new FontFamily("Consolas"),
            Foreground = secondary, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(pathRow);

        // Metadata grid
        var metaGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var sizeText = totalBytes < 1024 * 1024
            ? $"{totalBytes / 1024.0:F0} KB"
            : $"{totalBytes / (1024.0 * 1024.0):F1} MB";

        var createdTb = new TextBlock { FontSize = 11, Foreground = muted };
        createdTb.Inlines.Add(new System.Windows.Documents.Run("Created: ") { Foreground = muted });
        createdTb.Inlines.Add(new System.Windows.Documents.Run(created.ToString(DateTimeFormats.IsoDateOnly)) { Foreground = secondary });
        Grid.SetColumn(createdTb, 0);
        metaGrid.Children.Add(createdTb);

        var modifiedTb = new TextBlock { FontSize = 11, Foreground = muted };
        modifiedTb.Inlines.Add(new System.Windows.Documents.Run("Modified: ") { Foreground = muted });
        modifiedTb.Inlines.Add(new System.Windows.Documents.Run(lastModified.ToString(DateTimeFormats.IsoDateMinute)) { Foreground = secondary });
        Grid.SetColumn(modifiedTb, 2);
        metaGrid.Children.Add(modifiedTb);

        var sizeTb = new TextBlock { FontSize = 11, Foreground = muted };
        sizeTb.Inlines.Add(new System.Windows.Documents.Run("Size: ") { Foreground = muted });
        sizeTb.Inlines.Add(new System.Windows.Documents.Run(sizeText) { Foreground = secondary });
        Grid.SetColumn(sizeTb, 4);
        metaGrid.Children.Add(sizeTb);

        panel.Children.Add(metaGrid);

        // Content breakdown
        panel.Children.Add(new TextBlock
        {
            Text = $"Contents ({fileCount} files)",
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = primary, Margin = new Thickness(0, 0, 0, 4)
        });

        void AddCategoryRow(string icon, string label, int count)
        {
            if (count == 0) return;
            var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(8, 1, 0, 1) };
            row.Children.Add(new TextBlock
            {
                Text = icon, FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11, Foreground = muted, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0), Width = 16
            });
            row.Children.Add(new TextBlock
            {
                Text = $"{count} {label}", FontSize = 11,
                Foreground = secondary, VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(row);
        }

        AddCategoryRow("\uE7C3", scriptCount == 1 ? "script/config file" : "script/config files", scriptCount);
        AddCategoryRow("\uE74C", binCount == 1 ? "installer file" : "installer files", binCount);
        AddCategoryRow("\uE8B9", iconCount == 1 ? "icon" : "icons", iconCount);
        AddCategoryRow("\uE71B", shortcutCount == 1 ? "shortcut" : "shortcuts", shortcutCount);
        if (otherCount > 0)
            AddCategoryRow("\uE8A5", otherCount == 1 ? "other file" : "other files", otherCount);

        // Warning note
        panel.Children.Add(new TextBlock
        {
            Text = "These files will be overwritten by the new bundle.",
            FontSize = 11, Foreground = warningBrush, FontStyle = FontStyles.Italic,
            Margin = new Thickness(0, 10, 0, 0)
        });

        return await FluentDialog.ShowSelectAsync(
            "Existing Bundle Found", panel, "Overwrite", "Cancel");
    }

}
