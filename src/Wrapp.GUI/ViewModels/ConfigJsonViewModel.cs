using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wrapp.Models;
using Wrapp.Services;
using static Wrapp.Services.AppLogger;

namespace Wrapp.ViewModels;

public partial class ConfigJsonViewModel : ObservableObject
{
    private readonly GeneralViewModel _appInfoVm;

    // Set from view code-behind after WebView2 initialises
    public MonacoService? Monaco { get; set; }

    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>Path to the currently loaded Config.json (exposed for History button).</summary>
    public string ConfigPath => _appInfoVm.ConfigPath;

    /// <summary>Bundle root directory where the git repo lives.</summary>
    public string BundleRootDir => _appInfoVm.BundleRootDir;

    /// <summary>
    /// Pass-through to <see cref="TenantsViewModel"/> so the "Sync Domains"
    /// dropdown in <c>ConfigJsonView</c> can bind to the four
    /// <c>SyncDomains*</c> commands without duplicating them here.
    /// </summary>
    public TenantsViewModel Tenants { get; }

    // Holds the latest config so it can be applied when Monaco initialises (lazy tab open).
    private AppConfigModel? _pendingConfig;

    public ConfigJsonViewModel(GeneralViewModel appInfoVm, TenantsViewModel tenantsVm)
    {
        _appInfoVm = appInfoVm;
        Tenants = tenantsVm;
        _appInfoVm.ConfigLoaded += OnConfigLoaded;
        _appInfoVm.ConfigChanged += OnConfigChanged;
    }

    private async void OnConfigLoaded(object? sender, (AppConfigModel Config, string Path) e)
    {
        _pendingConfig = e.Config;
        if (Monaco is null) return;
        await ShowConfigAsync(e.Config);
    }

    private async void OnConfigChanged()
    {
        if (Monaco is null) return;
        try
        {
            var json = ConfigFileService.SerializeToJson(_appInfoVm.FullConfig);
            await Monaco.SetContentAsync(json, "json");
        }
        catch { /* swallow serialization errors during rapid typing */ }
    }

    /// <summary>Called from view code-behind after Monaco is wired for the first time.</summary>
    public async Task OnMonacoReadyAsync()
    {
        if (_pendingConfig is not null)
            await ShowConfigAsync(_pendingConfig);
    }

    private async Task ShowConfigAsync(AppConfigModel config)
    {
        try
        {
            var json = ConfigFileService.SerializeToJson(config);
            await Monaco!.SetContentAsync(json, "json");
            StatusMessage = "Showing current form state.";
            Info("ConfigJson: editor refreshed with current form state");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            Warn($"ConfigJson: failed to refresh editor: {ex.Message}");
        }
    }

    /// <summary>
    /// Apply editor JSON back into the form ViewModels (does not write to disk).
    /// </summary>
    [RelayCommand]
    private async Task ApplyChangesAsync()
    {
        if (Monaco is null) return;
        try
        {
            var json = await Monaco.GetContentAsync();
            var newConfig = ConfigFileService.DeserializeFromJson(json);
            _appInfoVm.ApplyConfig(newConfig, _appInfoVm.ConfigPath);
            StatusMessage = "Changes applied to forms.";
            Info("ConfigJson: editor changes applied to forms");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            Warn($"ConfigJson: failed to apply changes: {ex.Message}");
        }
    }
}
