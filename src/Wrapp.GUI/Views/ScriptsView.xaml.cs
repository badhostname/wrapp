using System.IO;
using System.Windows;
using System.Windows.Controls;
using Wrapp.Models;
using Wrapp.Services;
using Wrapp.ViewModels;

namespace Wrapp.Views;

public partial class ScriptsView : UserControl
{
    private bool _monacoWired;

    public ScriptsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        IsVisibleChanged += OnVisibilityChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_monacoWired) return;
        if (DataContext is not ScriptsViewModel vm) return;

        vm.TabService = new MonacoTabService(SharedEditorWebView);
        vm.FrameworkChanged += OnFrameworkChanged;
        _monacoWired = true;

        await vm.OnMonacoReadyAsync();
        RefreshTemplateList();
    }

    private void OnFrameworkChanged(object? sender, EventArgs e)
    {
        RefreshTemplateList();

        // Select the first visible tab so the user lands on a valid tab
        if (DataContext is ScriptsViewModel vm)
        {
            if (vm.Framework == ScriptFramework.PSADT)
                TabDetect.IsChecked = true;
            else
                TabInstall.IsChecked = true;
        }
    }

    private async void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (!_monacoWired || DataContext is not ScriptsViewModel vm) return;

        if (sender == TabDetect)         await vm.SwitchTabAsync(ScriptsViewModel.DetectId);
        else if (sender == TabInstall)   await vm.SwitchTabAsync(ScriptsViewModel.InstallId);
        else if (sender == TabUninstall) await vm.SwitchTabAsync(ScriptsViewModel.UninstallId);
        else if (sender == TabDeploy)    await vm.SwitchTabAsync(ScriptsViewModel.DeployId);

        RefreshTemplateList();
    }

    /// <summary>
    /// Shows/hides tabs based on the current script framework and refreshes the template list.
    /// </summary>
    private void RefreshTemplateList()
    {
        _suppressTemplateSelection = true;

        if (DataContext is ScriptsViewModel vm)
        {
            var fw = vm.Framework;
            // Toggle tab visibility based on framework
            TabInstall.Visibility   = fw == Models.ScriptFramework.Appease ? Visibility.Visible : Visibility.Collapsed;
            TabUninstall.Visibility = fw == Models.ScriptFramework.Appease ? Visibility.Visible : Visibility.Collapsed;
            TabDeploy.Visibility    = fw == Models.ScriptFramework.PSADT   ? Visibility.Visible : Visibility.Collapsed;

            // If the active tab is now hidden, switch to the first visible one
            if (fw == Models.ScriptFramework.PSADT && (TabInstall.IsChecked == true || TabUninstall.IsChecked == true))
            {
                TabDeploy.IsChecked = true;
            }
            else if (fw == Models.ScriptFramework.Appease && TabDeploy.IsChecked == true)
            {
                TabInstall.IsChecked = true;
            }
        }

        // Determine the active tab's template category
        string? category = null;
        if (DataContext is ScriptsViewModel svm)
        {
            category = ScriptFrameworkProvider.GetTemplateCategory(svm.Framework, ActiveModelId());
        }

        if (category is not null)
        {
            TemplatePanel.Visibility = Visibility.Visible;
            TemplateComboBox.ItemsSource = TemplateService.GetScriptTemplates(category);
        }
        else
        {
            TemplatePanel.Visibility = Visibility.Collapsed;
            TemplateComboBox.ItemsSource = null;
        }

        TemplateComboBox.SelectedIndex = -1;
        _suppressTemplateSelection = false;

        UpdateSaveTooltip();
    }

    private bool _suppressTemplateSelection;

    /// <summary>
    /// The template each editor tab is "working on" -- set when a template is
    /// applied or saved. The Save button overwrites this template in place;
    /// Save As always prompts for a new one.
    /// </summary>
    private readonly Dictionary<string, TemplateInfo> _currentTemplates = new();

    private async void TemplateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTemplateSelection) return;
        if (TemplateComboBox.SelectedItem is not TemplateInfo template) return;
        if (DataContext is not ScriptsViewModel vm) return;

        var options = new List<ActionPickerOption>
        {
            new() { Key = "apply", Icon = "", Title = "Apply Template",
                Description = "Replace the current script content with this template" },
        };
        if (!TemplateService.IsBuiltInTemplate(template))
        {
            options.Add(new() { Key = "delete", Icon = "", Title = "Delete Template",
                Description = "Permanently delete this custom template" });
        }
        var picker = new ActionPickerDialog($"Template: {template.Name}", options, null, "apply");
        var confirmed = await FluentDialog.ShowSelectAsync(
            "Script Template", picker, "Continue", "Cancel");

        if (!confirmed)
        {
            _suppressTemplateSelection = true;
            TemplateComboBox.SelectedIndex = -1;
            _suppressTemplateSelection = false;
            return;
        }

        if (picker.SelectedKey == "delete")
        {
            if (await FluentDialog.ConfirmAsync("Delete Template",
                    $"Delete the custom template \"{template.Name}\"? This cannot be undone.",
                    "Delete", "Cancel")
                && TemplateService.DeleteCustomTemplate(template))
            {
                vm.StatusMessage = $"Template '{template.Name}' deleted.";
                ForgetTemplate(template);
            }
            RefreshTemplateList();
            return;
        }

        var content = TemplateService.LoadScriptTemplate(template, vm.App);
        // Target the ACTIVE tab's model -- the old install/uninstall guess sent
        // PSADT templates into the hidden uninstall buffer on the Deploy tab.
        var modelId = ActiveModelId();
        await vm.TabService!.SetModelContentAsync(modelId, content, "powershell");
        vm.RefreshDirtyState();

        // This tab is now working on the applied template: Save overwrites it
        _currentTemplates[modelId] = template;
        UpdateSaveTooltip();

        AppLogger.Info($"Scripts: applied template '{template.Name}' to {modelId}");

        // Reset selection so the same template can be re-applied
        _suppressTemplateSelection = true;
        TemplateComboBox.SelectedIndex = -1;
        _suppressTemplateSelection = false;
    }

    /// <summary>Model ID of the currently checked editor tab.</summary>
    private string ActiveModelId() =>
        TabDetect.IsChecked == true ? ScriptsViewModel.DetectId
        : TabInstall.IsChecked == true ? ScriptsViewModel.InstallId
        : TabUninstall.IsChecked == true ? ScriptsViewModel.UninstallId
        : TabDeploy.IsChecked == true ? ScriptsViewModel.DeployId
        : ScriptsViewModel.InstallId;

    /// <summary>Drops a deleted template from every tab's current-template slot.</summary>
    private void ForgetTemplate(TemplateInfo template)
    {
        foreach (var key in _currentTemplates
                     .Where(kv => kv.Value.FilePath == template.FilePath)
                     .Select(kv => kv.Key).ToList())
        {
            _currentTemplates.Remove(key);
        }
    }

    /// <summary>Save tooltip reflects where a quick save would land for the active tab.</summary>
    private void UpdateSaveTooltip()
    {
        SaveTemplateTooltip.Text = CurrentTemplateForActiveTab() is { } current
            ? $"Save to template '{current.Name}'"
            : (TryFindResource("Help.Scripts.SaveTemplate") as string ?? "Save as a new template");
    }

    /// <summary>The active tab's current template, if it still exists and is custom.</summary>
    private TemplateInfo? CurrentTemplateForActiveTab()
    {
        if (!_currentTemplates.TryGetValue(ActiveModelId(), out var current)) return null;
        if (!File.Exists(current.FilePath)) return null;
        if (TemplateService.IsBuiltInTemplate(current)) return null;
        return current;
    }

    /// <summary>
    /// Captures the live editor buffer for the active tab -- NOT GetLatestContent
    /// (500ms debounced) and not the file on disk, so unsaved typing counts and
    /// the bundle need not be saved. Returns null when Monaco isn't ready, the
    /// tab has no template category (Detect), or the buffer is empty.
    /// </summary>
    private async Task<(string Category, string Content)?> CaptureActiveScriptAsync(ScriptsViewModel vm)
    {
        if (!_monacoWired || vm.TabService is null) return null;

        var activeId = ActiveModelId();
        var category = ScriptFrameworkProvider.GetTemplateCategory(vm.Framework, activeId);
        if (category is null) return null;

        var content = await vm.TabService.GetModelContentAsync(activeId);
        if (string.IsNullOrWhiteSpace(content))
        {
            vm.StatusMessage = "Nothing to save - the editor is empty.";
            return null;
        }
        return (category, content);
    }

    /// <summary>
    /// Save: overwrite the active tab's current custom template in place (the
    /// one last applied or saved). Falls back to Save As when this tab isn't
    /// working on a custom template yet.
    /// </summary>
    private async void SaveTemplateButton_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not ScriptsViewModel vm) return;

        var current = CurrentTemplateForActiveTab();
        if (current is null)
        {
            await SaveTemplateAsAsync(vm);
            return;
        }

        var captured = await CaptureActiveScriptAsync(vm);
        if (captured is null) return;

        try
        {
            TemplateService.UpdateScriptTemplate(current, captured.Value.Content);
            vm.StatusMessage = $"Saved to template '{current.Name}'.";
            RefreshTemplateList();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Scripts: template save failed -- {ex.Message}");
            await FluentDialog.ShowWarningAsync("Save Template", ex.Message);
        }
    }

    private async void SaveTemplateAsButton_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not ScriptsViewModel vm) return;
        await SaveTemplateAsAsync(vm);
    }

    private async Task SaveTemplateAsAsync(ScriptsViewModel vm)
    {
        var captured = await CaptureActiveScriptAsync(vm);
        if (captured is null) return;
        var (category, content) = captured.Value;

        var result = SaveTemplateWindow.Prompt(
            Window.GetWindow(this),
            $"Save {category} Script Template",
            "Content is saved exactly as shown. Tokens like {{Name}}, {{Version}} or {{Company}} "
            + "stay literal in the template and expand when it is applied to a bundle.",
            fields: null,
            name => TemplateService.CheckCollision(TemplateKind.Script, name, category));
        if (result is null) return;

        try
        {
            var info = TemplateService.SaveScriptTemplate(
                category, result.Value.Name, result.Value.Description, content);
            vm.StatusMessage = $"Template '{info.Name}' saved.";
            // Future quick saves on this tab go to the new template
            _currentTemplates[ActiveModelId()] = info;
            RefreshTemplateList();
            await FluentDialog.ShowExportedAsync("Template saved",
                $"Script template \"{info.Name}\" was saved.", info.FilePath);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Scripts: template save failed -- {ex.Message}");
            await FluentDialog.ShowWarningAsync("Save Template", ex.Message);
        }
    }

    private async void TokenHelpButton_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var content = TryFindResource("Help.Scripts.TemplateTokens") as string;
        if (string.IsNullOrEmpty(content)) return;

        var panel = Controls.SectionHeader.BuildFormattedPanel(content, this);
        await FluentDialog.ShowScrollableContentAsync("Template Tokens", panel, "Close");
    }

    private async void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || !_monacoWired || DataContext is not ScriptsViewModel vm) return;
        if (vm.TabService is not null) await vm.TabService.LayoutAsync();
    }

    private async void EditorHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_monacoWired || DataContext is not ScriptsViewModel vm) return;
        if (vm.TabService is not null) await vm.TabService.LayoutAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_monacoWired || DataContext is not ScriptsViewModel vm || vm.TabService is null) return;
        AppLogger.Info("ScriptsView: Refresh Editor clicked");
        try
        {
            vm.StatusMessage = "Refreshing Monaco editor...";
            await vm.TabService.RefreshAsync();
            vm.StatusMessage = "Monaco editor refreshed.";
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"ScriptsView: Monaco refresh failed -- {ex.Message}");
            vm.StatusMessage = $"Refresh failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Workstream P (P3): expands {{Name}} placeholders in the ACTIVE tab's
    /// live Monaco buffer (unsaved typing counts). Shares the confirm-first
    /// summary flow with the field-map scopes; File History provides rollback.
    /// </summary>
    private async void ReplacePlaceholdersButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_monacoWired || DataContext is not ScriptsViewModel vm || vm.TabService is null) return;

        var modelId = ActiveModelId();
        var fileName = vm.ModelIdToFileName(modelId) ?? "script";
        var content = await vm.TabService.GetModelContentAsync(modelId);
        if (string.IsNullOrEmpty(content))
        {
            vm.StatusMessage = "Nothing to replace - the editor is empty.";
            return;
        }

        var expanded = PlaceholderService.Expand(content, vm.App, out var report);
        if (string.Equals(expanded, content, StringComparison.Ordinal))
        {
            await FluentDialog.ShowInfoAsync(
                $"Replace placeholders — {fileName}",
                PlaceholderApplyService.BuildNothingToReplaceMessage(report));
            return;
        }

        var confirmed = await PlaceholderPreviewDialog.ConfirmAsync(
            $"Replace placeholders — {fileName}", report, changedFieldCount: 1);
        if (!confirmed) return;

        await vm.TabService.SetModelContentAsync(modelId, expanded, "powershell");
        vm.RefreshDirtyState();
        vm.StatusMessage = $"Replaced {report.Replaced} placeholder occurrence(s) in {fileName}.";
        AppLogger.Info($"Scripts: replaced {report.Replaced} placeholder occurrence(s) in {fileName}");
    }

    private async void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ScriptsViewModel vm) return;

        var configDir = vm.ConfigDir;
        if (string.IsNullOrEmpty(configDir))
        {
            vm.StatusMessage = "No package loaded. Open or save a bundle first.";
            return;
        }

        // Git repo lives in Script/ (where ConfigDir points). Script-only
        // tracking keeps the repo small and prevents installer binaries in
        // B/ / Files/ from bloating diffs.
        var repoDir = configDir;
        if (string.IsNullOrEmpty(repoDir)) return;

        // Initialise the git repo if it doesn't exist yet (first-time use)
        if (!GitService.IsGitRepo(repoDir))
            await GitService.InitAsync(repoDir);

        // Resolve active tab to model ID and filename
        var modelId = TabDetect.IsChecked == true  ? "detect"
                    : TabInstall.IsChecked == true ? "install"
                    : "uninstall";
        var fileName = vm.ModelIdToFileName(modelId);
        if (fileName is null) return;

        // Relative path from repo root (Script/) â€” just the filename.
        var relPath = fileName;

        var window = new FileHistoryWindow(repoDir, relPath, "powershell")
        {
            Owner = Window.GetWindow(this)
        };
        var restored = false;
        string? restoredContent = null;

        window.ShowDialog();

        restored = window.Restored;
        restoredContent = window.RestoredContent;

        AppLogger.Info($"Scripts: history window closed. Restored={restored}, ContentLen={restoredContent?.Length ?? -1}");

        if (restored && !string.IsNullOrEmpty(restoredContent))
        {
            try
            {
                AppLogger.Info($"Scripts: applying restored content ({restoredContent.Length} chars) to {modelId}");
                await vm.TabService!.SetModelContentAsync(modelId, restoredContent, "powershell");
                await vm.SwitchTabAsync(modelId);
                vm.RefreshDirtyState();
                AppLogger.Info($"Scripts: restore applied to editor successfully");
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Scripts: restore failed to apply: {ex.Message}");
                AppLogger.Exception("Scripts restore", ex);
            }
        }
    }
}
