using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Wrapp.Models;
using Wrapp.Services;
using Wrapp.ViewModels;

namespace Wrapp.Views;

public partial class IntuneView : UserControl
{
    private readonly DragOverlayService.DragOverlayState _dragState = new();

    public IntuneView()
    {
        InitializeComponent();
        PackageTemplateComboBox.ItemsSource = TemplateService.GetPackageTemplates("Intune");
    }

    /// <summary>
    /// The ONLY path that writes IsEnabled from the Package State radios.
    /// Their bindings use UpdateSourceTrigger=Explicit, so the radio group's
    /// mutual-uncheck during selection changes can never push through a stale
    /// binding chain (the bug that silently re-enabled a disabled package).
    /// A Checked raised by a transition target-update writes back the value
    /// it just read — a no-op; only a real toggle changes state.
    /// </summary>
    private void PackageStateRadio_Checked(object sender, RoutedEventArgs e)
        => ((System.Windows.Controls.Primitives.ToggleButton)sender)
            .GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)
            ?.UpdateSource();

    /// <summary>
    /// Opens the Sync button's ContextMenu on click — gives the button a
    /// dropdown affordance without needing a custom DropDownButton control.
    /// </summary>
    private void IntuneTenantsSyncButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.ContextMenu is null) return;
        btn.ContextMenu.PlacementTarget = btn;
        btn.ContextMenu.Placement       = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        btn.ContextMenu.IsOpen          = true;
    }

    private async void IntuneTenantsHelpButton_Click(object sender, RoutedEventArgs e)
        => await ShowHelpAsync("Help.Intune.TenantsPanel", "Intune Tenants");

    private async Task ShowHelpAsync(string resourceKey, string title)
    {
        var content = TryFindResource(resourceKey) as string;
        if (string.IsNullOrEmpty(content)) return;
        var panel = Controls.SectionHeader.BuildFormattedPanel(content, this);
        await FluentDialog.ShowScrollableContentAsync(title, panel, "Close");
    }

    private bool _suppressPkgTemplate;

    private async void PackageTemplateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPkgTemplate) return;
        if (PackageTemplateComboBox.SelectedItem is not TemplateInfo template) return;
        if (DataContext is not IntuneViewModel vm) return;

        if (vm.SelectedPackage is not null)
        {
            var options = new List<ActionPickerOption>
            {
                new() { Key = "apply", Icon = "\uE74E", Title = "Apply to Current Package",
                    Description = $"Overwrite settings on \"{vm.SelectedPackage.AppName}\" with template values" },
                new() { Key = "create", Icon = "\uE710", Title = "Create New Package",
                    Description = "Add a new package with the template pre-applied" },
            };
            if (!TemplateService.IsBuiltInTemplate(template))
            {
                options.Add(new() { Key = "delete", Icon = "\uE74D", Title = "Delete Template",
                    Description = "Permanently delete this custom template" });
            }
            var picker = new ActionPickerDialog(
                $"Template: {template.Name}\n{template.Description}", options, null, "apply");
            var confirmed = await FluentDialog.ShowSelectAsync(
                "Apply Package Template", picker, "Continue", "Cancel");

            if (confirmed && picker.SelectedKey == "delete")
            {
                if (await FluentDialog.ConfirmAsync("Delete Template",
                        $"Delete the custom template \"{template.Name}\"? This cannot be undone.",
                        "Delete", "Cancel"))
                {
                    TemplateService.DeleteCustomTemplate(template);
                    if (_currentTemplate?.FilePath == template.FilePath) _currentTemplate = null;
                    PackageTemplateComboBox.ItemsSource = TemplateService.GetPackageTemplates("Intune");
                    UpdateSaveTooltip();
                }
            }
            else if (confirmed && picker.SelectedKey == "apply")
            {
                TemplateService.ApplyPackageTemplate(template, vm.SelectedPackage, vm.App);
                SetCurrentTemplate(template);
                AppLogger.Info($"Intune: applied package template '{template.Name}' to '{vm.SelectedPackage.AppName}'");
            }
            else if (confirmed && picker.SelectedKey == "create")
            {
                vm.AddPackageCommand.Execute(null);
                if (vm.SelectedPackage is not null)
                {
                    TemplateService.ApplyPackageTemplate(template, vm.SelectedPackage, vm.App);
                    SetCurrentTemplate(template);
                    AppLogger.Info($"Intune: created new package from template '{template.Name}'");
                }
            }
        }
        else
        {
            vm.AddPackageCommand.Execute(null);
            if (vm.SelectedPackage is not null)
            {
                TemplateService.ApplyPackageTemplate(template, vm.SelectedPackage, vm.App);
                SetCurrentTemplate(template);
                AppLogger.Info($"Intune: created new package from template '{template.Name}'");
            }
        }

        _suppressPkgTemplate = true;
        PackageTemplateComboBox.SelectedIndex = -1;
        _suppressPkgTemplate = false;
    }

    /// <summary>
    /// The template this view is "working on" -- set when a template is applied
    /// or saved. Save overwrites it in place; Save As always prompts.
    /// </summary>
    private TemplateInfo? _currentTemplate;

    private void SetCurrentTemplate(TemplateInfo template)
    {
        _currentTemplate = template;
        UpdateSaveTooltip();
    }

    /// <summary>Save tooltip reflects where a quick save would land.</summary>
    private void UpdateSaveTooltip()
    {
        SaveTemplateTooltip.Text = ValidCurrentTemplate() is { } current
            ? $"Save package settings to template '{current.Name}'"
            : (TryFindResource("Help.Intune.Toolbar.SaveTemplate") as string ?? "Save package settings as a new template");
    }

    /// <summary>The current template, if it still exists and is custom.</summary>
    private TemplateInfo? ValidCurrentTemplate()
    {
        if (_currentTemplate is null) return null;
        if (!System.IO.File.Exists(_currentTemplate.FilePath)) return null;
        if (TemplateService.IsBuiltInTemplate(_currentTemplate)) return null;
        return _currentTemplate;
    }

    /// <summary>
    /// Save: overwrite the current custom template with the selected package's
    /// values, reusing the template's existing field set, name and description.
    /// Falls back to Save As when no custom template is in use.
    /// </summary>
    private async void SavePackageTemplate_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not IntuneViewModel vm) return;
        if (vm.SelectedPackage is null)
        {
            await FluentDialog.ShowInfoAsync("Save Package Template",
                "Select a package first, then save its settings as a template.");
            return;
        }

        var current = ValidCurrentTemplate();
        if (current is null)
        {
            await SavePackageTemplateAsAsync(vm.SelectedPackage);
            return;
        }

        try
        {
            // Keep the template's shape: refresh the values of the keys it
            // already stores, nothing more.
            var fields = TemplateService.GetPackageTemplateFields(vm.SelectedPackage, presetFrom: current)
                .Where(f => f.Checked).Select(f => f.Name).ToList();
            TemplateService.UpdatePackageTemplate(
                current, current.Name, current.Description, vm.SelectedPackage, fields);
            PackageTemplateComboBox.ItemsSource = TemplateService.GetPackageTemplates("Intune");
            AppLogger.Info($"Intune: saved package template '{current.Name}' from '{vm.SelectedPackage.AppName}'");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Intune: package template save failed -- {ex.Message}");
            await FluentDialog.ShowWarningAsync("Save Template", ex.Message);
        }
    }

    private async void SavePackageTemplateAs_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not IntuneViewModel vm) return;
        if (vm.SelectedPackage is null)
        {
            await FluentDialog.ShowInfoAsync("Save Package Template",
                "Select a package first, then save its settings as a template.");
            return;
        }
        await SavePackageTemplateAsAsync(vm.SelectedPackage);
    }

    private async Task SavePackageTemplateAsAsync(IPackageEntry pkg)
    {
        var fields = TemplateService.GetPackageTemplateFields(pkg);
        var result = SaveTemplateWindow.Prompt(
            Window.GetWindow(this),
            "Save Intune Package Template",
            "Templates are sparse: only checked fields are stored, and applying the template "
            + "overwrites just those fields on a package. Unchecked fields are left alone.",
            fields,
            name => TemplateService.CheckCollision(TemplateKind.Package, name, "Intune"));
        if (result is null) return;

        try
        {
            var info = TemplateService.SavePackageTemplate(
                "Intune", result.Value.Name, result.Value.Description, pkg, result.Value.Fields);
            PackageTemplateComboBox.ItemsSource = TemplateService.GetPackageTemplates("Intune");
            SetCurrentTemplate(info);
            AppLogger.Info($"Intune: saved package template '{info.Name}' ({result.Value.Fields.Count} fields)");
            await FluentDialog.ShowExportedAsync("Template saved",
                $"Package template \"{info.Name}\" was saved ({result.Value.Fields.Count} field(s)).",
                info.FilePath);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Intune: package template save failed -- {ex.Message}");
            await FluentDialog.ShowWarningAsync("Save Template", ex.Message);
        }
    }

    private void UserControl_DragEnter(object sender, DragEventArgs e)
        => DragOverlayService.HandlePackageDragEnter(e, HasSelectedPackage,
            DragOverlay, MainContent, DragValidIcon, DragInvalidIcon,
            DragOverlayHint, DragOverlaySubHint, _dragState);

    private void UserControl_DragOver(object sender, DragEventArgs e)
        => DragOverlayService.HandlePackageDragOver(e, HasSelectedPackage, _dragState);

    private void UserControl_DragLeave(object sender, DragEventArgs e)
        => DragOverlayService.HandlePackageDragLeave(e, this, DragOverlay, MainContent, _dragState);

    private void UserControl_Drop(object sender, DragEventArgs e)
        => DragOverlayService.HandlePackageDrop(e, DragOverlay, MainContent,
            path => { if (DataContext is IntuneViewModel vm) vm.ApplyDroppedIcon(path); },
            _dragState);

    private bool HasSelectedPackage()
        => DataContext is IntuneViewModel vm && vm.HasSelectedPackage;

    private async void BrowseIntuneDependency_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not IntuneViewModel vm || vm.SelectedPackage is null) return;
        var svc = App.InventoryService;
        if (svc is null) return;

        var tenantId = vm.SelectedPackage.TenantId;
        if (string.IsNullOrEmpty(tenantId))
        {
            await FluentDialog.ShowInfoAsync("No Tenant Selected",
                "Select a target tenant for this package before browsing inventory.");
            return;
        }
        if (svc.GetCachedIntuneApps(tenantId) is null)
        {
            await FluentDialog.ShowInfoAsync("No Inventory Data",
                "Load the Intune inventory first via the Inventory tab, then use Browse.");
            return;
        }

        var picker = new AppPickerDialog("Intune", tenantId, svc);
        var confirmed = await FluentDialog.ShowSelectAsync(
            "Select Dependencies", picker, "Add Selected", "Cancel");
        if (!confirmed) return;

        foreach (var app in picker.SelectedApps)
        {
            vm.SelectedPackage.Dependencies.Add(new DependencyEntry { AppName = app.DisplayName, AutoInstall = true });
        }
        AppLogger.Info($"Intune: added {picker.SelectedApps.Count} dependency(ies) from browse");
    }

    private async void BrowseIntuneSupersedence_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not IntuneViewModel vm || vm.SelectedPackage is null) return;
        var svc = App.InventoryService;
        if (svc is null) return;

        var tenantId = vm.SelectedPackage.TenantId;
        if (string.IsNullOrEmpty(tenantId))
        {
            await FluentDialog.ShowInfoAsync("No Tenant Selected",
                "Select a target tenant for this package before browsing inventory.");
            return;
        }
        if (svc.GetCachedIntuneApps(tenantId) is null)
        {
            await FluentDialog.ShowInfoAsync("No Inventory Data",
                "Load the Intune inventory first via the Inventory tab, then use Browse.");
            return;
        }

        var picker = new AppPickerDialog("Intune", tenantId, svc);
        var confirmed = await FluentDialog.ShowSelectAsync(
            "Select Supersedence Targets", picker, "Add Selected", "Cancel");
        if (!confirmed) return;

        foreach (var app in picker.SelectedApps)
        {
            vm.SelectedPackage.Supersedence.Add(new SupersedenceEntry { AppName = app.DisplayName });
        }
        AppLogger.Info($"Intune: added {picker.SelectedApps.Count} supersedence(s) from browse");
    }
}
