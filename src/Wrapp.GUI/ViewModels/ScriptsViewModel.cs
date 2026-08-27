using System.IO;
using System.Reflection;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.ViewModels;

public partial class ScriptsViewModel : ObservableObject
{
    private readonly GeneralViewModel _generalVm;

    // Single multi-model Monaco service - set from view code-behind
    public MonacoTabService? TabService { get; set; }

    // Legacy constants kept for ScriptsView tab binding compatibility
    internal const string DetectId    = "detect";
    internal const string InstallId   = "install";
    internal const string UninstallId = "uninstall";
    internal const string DeployId    = "deploy";

    private string _configDir = string.Empty;
    private bool _monacoReady;

    /// <summary>Bundle directory containing the scripts (parent of Config.json).</summary>
    public string ConfigDir => _configDir;

    /// <summary>Bundle root directory where the git repo lives.</summary>
    public string BundleRootDir => _generalVm.BundleRootDir;

    /// <summary>App section for template token replacement.</summary>
    public AppSection App => _generalVm.App;

    /// <summary>Current script framework (resolved from App.ScriptFramework).</summary>
    public ScriptFramework Framework => ScriptFrameworkProvider.Parse(_generalVm.App.ScriptFramework);

    // Per-script disk snapshots keyed by model ID
    private readonly Dictionary<string, string> _snapshots = new();

    /// <summary>Raised after a config load changes the active framework, so the view can refresh tabs.</summary>
    public event EventHandler? FrameworkChanged;

    [ObservableProperty] private string _statusMessage = string.Empty;

    public ScriptsViewModel(GeneralViewModel generalVm)
    {
        _generalVm = generalVm;
        _generalVm.ConfigLoaded += OnConfigLoaded;
        _generalVm.BundleSaving += OnBundleSavingAsync;
    }

    private async void OnConfigLoaded(object? sender, (AppConfigModel Config, string Path) e)
    {
        _configDir = System.IO.Path.GetDirectoryName(e.Path) ?? string.Empty;
        await AutoLoadAllAsync();
        _generalVm.ScriptsAreDirty = false;
        FrameworkChanged?.Invoke(this, EventArgs.Empty);
    }

    // Called from code-behind after MonacoTabService is wired.
    public async Task OnMonacoReadyAsync()
    {
        _monacoReady = true;
        if (TabService is null) return;

        // Create all four models (both frameworks may need any of these)
        await TabService.CreateModelAsync(DetectId, "", "powershell");
        await TabService.CreateModelAsync(InstallId, "", "powershell");
        await TabService.CreateModelAsync(UninstallId, "", "powershell");
        await TabService.CreateModelAsync(DeployId, "", "powershell");

        // Switch to the first relevant tab for the current framework
        var firstTab = ScriptFrameworkProvider.GetEditorTabs(Framework);
        var defaultTab = firstTab.Length > 1 ? firstTab[1].Id : firstTab[0].Id;
        await TabService.SwitchModelAsync(defaultTab);

        await AutoLoadAllAsync();
        _generalVm.ScriptsAreDirty = false;
        SubscribeToContentChanges();
    }

    /// <summary>Switch the active editor tab (called from view code-behind).</summary>
    public async Task SwitchTabAsync(string modelId)
    {
        if (TabService is null) return;
        await TabService.SwitchModelAsync(modelId);
    }

    // -----------------------------------------------------------------------
    // Content-change tracking: compare editor content against disk snapshot
    // -----------------------------------------------------------------------

    private void SubscribeToContentChanges()
    {
        if (TabService is not null)
            TabService.ContentChanged += OnScriptContentChanged;
    }

    private void OnScriptContentChanged(object? sender, (string ModelId, string Content) e)
        => RefreshDirtyState();

    /// <summary>
    /// Recalculates the dirty state by comparing current editor content against disk snapshots.
    /// Call after programmatic content changes (e.g. template injection) since Monaco
    /// suppresses the change event for programmatic setValue calls.
    /// </summary>
    public void RefreshDirtyState()
    {
        var dirty = false;
        foreach (var tab in ScriptFrameworkProvider.GetEditorTabs(Framework))
        {
            var snapshot = _snapshots.TryGetValue(tab.Id, out var s) ? s : string.Empty;
            if (!string.Equals(GetNormalizedLatest(tab.Id), snapshot))
            {
                dirty = true;
                break;
            }
        }
        _generalVm.ScriptsAreDirty = dirty;
    }

    private string GetNormalizedLatest(string modelId)
    {
        if (TabService is null) return string.Empty;
        return Normalize(TabService.GetLatestContent(modelId));
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    // -----------------------------------------------------------------------
    // Save scripts to disk (called via BundleSaving event)
    // -----------------------------------------------------------------------

    private async Task OnBundleSavingAsync()
    {
        if (!_monacoReady || TabService is null) return;

        // Use the latest config path from GeneralViewModel (may have changed on first save)
        var scriptDir = Path.GetDirectoryName(_generalVm.ConfigPath);
        if (string.IsNullOrEmpty(scriptDir)) return;
        _configDir = scriptDir;

        var bundleRoot = _generalVm.BundleRootDir;
        foreach (var tab in ScriptFrameworkProvider.GetEditorTabs(Framework))
        {
            // PSADT deploy script lives at bundle root, not Script/
            var saveDir = (tab.Id == DeployId && !string.IsNullOrEmpty(bundleRoot))
                ? bundleRoot : scriptDir;
            await SaveScriptAsync(saveDir, tab.FileName, tab.Id);
        }

        foreach (var tab in ScriptFrameworkProvider.GetEditorTabs(Framework))
            _snapshots[tab.Id] = Normalize(await TabService.GetModelContentAsync(tab.Id));

        AppLogger.Info("Scripts: saved all scripts to disk");
    }

    private async Task SaveScriptAsync(string dir, string fileName, string modelId)
    {
        if (TabService is null) return;
        var content = await TabService.GetModelContentAsync(modelId);
        if (string.IsNullOrEmpty(content))
        {
            // Monaco empty means either (a) the user hasn't opened the
            // editor for this tab so it never loaded, or (b) Monaco
            // wasn't ready when AutoLoad ran. Preserve whatever's on
            // disk -- a recent upgrade/full save may have just copied
            // the real script in from the source bundle, and stomping
            // it with an empty Monaco buffer would lose user code.
            AppLogger.Info($"Scripts: skipped {fileName} (Monaco buffer empty -- preserving disk content)");
            return;
        }
        var destPath = Path.Combine(dir, fileName);
        await File.WriteAllTextAsync(destPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        AppLogger.Info($"Scripts: wrote {fileName} ({content.Length} chars from Monaco) to {dir}");
    }

    // -----------------------------------------------------------------------
    // Auto-load scripts: use bundle file if it exists, else embedded template
    // -----------------------------------------------------------------------

    private async Task AutoLoadAllAsync()
    {
        foreach (var tab in ScriptFrameworkProvider.GetEditorTabs(Framework))
            await AutoLoadAsync(tab.FileName, tab.Id);
    }

    /// <summary>
    /// Resolves where a script file lives on disk. Most scripts live in the Script/
    /// folder (_configDir), but PSADT's Invoke-AppDeployToolkit.ps1 lives at the bundle root.
    /// </summary>
    private string? ResolveScriptPath(string fileName)
    {
        if (string.IsNullOrEmpty(_configDir)) return null;

        // Check Script/ folder first (normal case)
        var scriptPath = Path.Combine(_configDir, fileName);
        if (File.Exists(scriptPath)) return scriptPath;

        // Check bundle root (PSADT deploy script lives there)
        var bundleRoot = _generalVm.BundleRootDir;
        if (!string.IsNullOrEmpty(bundleRoot))
        {
            var rootPath = Path.Combine(bundleRoot, fileName);
            if (File.Exists(rootPath)) return rootPath;
        }

        return null;
    }

    private async Task AutoLoadAsync(string fileName, string modelId)
    {
        if (TabService is null) return;

        string content;
        var path = ResolveScriptPath(fileName);

        if (path is not null && File.Exists(path))
        {
            try   { content = await File.ReadAllTextAsync(path); }
            catch { content = string.Empty; }
        }
        else
        {
            content = string.Empty;
        }

        // No file on disk: load from the bundled framework template folder
        if (string.IsNullOrEmpty(content))
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var templateFolder = ScriptFrameworkProvider.GetTemplateFolderName(Framework);
            var bundledPath = Path.Combine(baseDir, templateFolder, fileName);

            // Fall back to appease-template for shared scripts (e.g. DetectScript.ps1)
            // that exist in the Appease template but not in the PSADT template
            if (!File.Exists(bundledPath) && Framework != ScriptFramework.Appease)
                bundledPath = Path.Combine(baseDir, "appease-template", fileName);

            if (File.Exists(bundledPath))
            {
                content = await File.ReadAllTextAsync(bundledPath);
                content = TemplateService.ApplyTokens(content, _generalVm.App);
            }
        }

        await TabService.SetModelContentAsync(modelId, content, "powershell");

        _snapshots[modelId] = Normalize(content);
    }

    /// <summary>
    /// Re-reads a script from disk into the editor. Called after a file
    /// history restore overwrites the file.
    /// </summary>
    public async Task ReloadTabAsync(string modelId)
    {
        var fileName = ScriptFrameworkProvider.ModelIdToFileName(Framework, modelId);
        if (fileName is null || TabService is null) return;

        await AutoLoadAsync(fileName, modelId);
        _generalVm.ScriptsAreDirty = false;
    }

    /// <summary>Maps a tab model ID to its filename on disk for the current framework.</summary>
    public string? ModelIdToFileName(string modelId)
        => ScriptFrameworkProvider.ModelIdToFileName(Framework, modelId);

}
