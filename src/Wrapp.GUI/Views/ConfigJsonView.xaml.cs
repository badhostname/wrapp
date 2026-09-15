using System.IO;
using System.Windows;
using System.Windows.Controls;
using Wrapp.Services;
using Wrapp.ViewModels;

namespace Wrapp.Views;

public partial class ConfigJsonView : UserControl
{
    private bool _monacoWired;

    public ConfigJsonView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        IsVisibleChanged += OnVisibilityChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_monacoWired) return;
        if (DataContext is not ConfigJsonViewModel vm) return;
        vm.Monaco = new MonacoService(JsonWebView);
        _monacoWired = true;
        await vm.OnMonacoReadyAsync();
    }

    private async void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && _monacoWired && DataContext is ConfigJsonViewModel vm && vm.Monaco is not null)
            await vm.Monaco.LayoutAsync();
    }

    private async void JsonWebView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_monacoWired && DataContext is ConfigJsonViewModel vm && vm.Monaco is not null)
            await vm.Monaco.LayoutAsync();
    }

    /// <summary>
    /// Anchors the Sync Domains button's ContextMenu below the button on click,
    /// matching the "Sync with..." button pattern used in IntuneView / SCCMView
    /// / TenantsView. Placement is manual so the dropdown appears directly
    /// under the button rather than at the cursor.
    /// </summary>
    private void SyncDomainsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.ContextMenu is not null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_monacoWired || DataContext is not ConfigJsonViewModel vm || vm.Monaco is null) return;
        AppLogger.Info("ConfigJsonView: Refresh Editor clicked");
        try
        {
            vm.StatusMessage = "Refreshing Monaco editor...";
            await vm.Monaco.RefreshAsync();
            vm.StatusMessage = "Monaco editor refreshed.";
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"ConfigJsonView: Monaco refresh failed -- {ex.Message}");
            vm.StatusMessage = $"Refresh failed: {ex.Message}";
        }
    }

    private async void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigJsonViewModel vm) return;

        var configPath = vm.ConfigPath;
        if (string.IsNullOrEmpty(configPath))
        {
            vm.StatusMessage = "No package loaded. Open or save a bundle first.";
            return;
        }

        // Git repo lives in Script/ (the directory containing Config.json) so
        // diffs only cover scripts + Config.json, not installer binaries.
        var repoDir = System.IO.Path.GetDirectoryName(configPath) ?? string.Empty;
        if (string.IsNullOrEmpty(repoDir)) return;

        if (!GitService.IsGitRepo(repoDir))
            await GitService.InitAsync(repoDir);

        // Relative path from repo root (Script/) - just the filename.
        var relPath = System.IO.Path.GetFileName(configPath);

        var window = new FileHistoryWindow(repoDir, relPath, "json")
        {
            Owner = Window.GetWindow(this)
        };
        window.ShowDialog();

        if (window.Restored && window.RestoredContent is not null)
        {
            // Inject restored content into the editor (not saved to disk until Save Bundle)
            try
            {
                if (vm.Monaco is not null)
                    await vm.Monaco.SetContentAsync(window.RestoredContent, "json");
                vm.StatusMessage = "Restored from history. Save to apply to disk.";
            }
            catch (Exception ex)
            {
                vm.StatusMessage = $"Restore failed: {ex.Message}";
            }
        }
    }
}
