using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Wrapp.Controls;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Views;

public partial class SCCMDeploymentDialog : UserControl
{
    private readonly ObservableCollection<SCCMDeploymentEntry> _deployments;
    /// <summary>App section for placeholder expansion when applying templates.</summary>
    private readonly AppSection? _app;
    private readonly string _appName;
    private readonly string _packageId;
    private readonly SccmDeploymentDefaults _deploymentPrefs;

    // Exposed for XAML ComboBox ItemsSource bindings
    public string[] ValidDeployActions { get; }
    public string[] ValidDeployPurposes { get; }
    public string[] ValidUserNotifications { get; }
    public string[] ValidTimeBaseOn { get; }

    public SCCMDeploymentDialog(
        ObservableCollection<SCCMDeploymentEntry> deployments,
        string appName,
        ModuleDefaults defaults,
        string packageId = "",
        AppSection? app = null)
    {
        _app = app;
        _deployments = deployments;
        _packageId = packageId;
        _appName = appName;

        ValidDeployActions = defaults.ValidDeployActions;
        ValidDeployPurposes = defaults.ValidDeployPurposes;
        ValidUserNotifications = defaults.ValidUserNotifications;
        ValidTimeBaseOn = defaults.ValidTimeBaseOn;

        _deploymentPrefs = SettingsService.Load().SccmDeploymentDefaults;

        DataContext = this;
        InitializeComponent();

        TemplateComboBox.ItemsSource = TemplateService.GetDeploymentTemplates();
        DeploymentItems.ItemsSource = _deployments;
        UpdatePlaceholder();
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var entry = new SCCMDeploymentEntry
        {
            AppName           = _appName,
            PackageId         = _packageId,
            DeployAction      = Pick(_deploymentPrefs.DeployAction,      ModuleDefaultsSeed.SccmDeployAction),
            DeployPurpose     = Pick(_deploymentPrefs.DeployPurpose,     ModuleDefaultsSeed.SccmDeployPurpose),
            UserNotification  = Pick(_deploymentPrefs.UserNotification,  ModuleDefaultsSeed.SccmUserNotification),
            TimeBaseOn        = Pick(_deploymentPrefs.TimeBaseOn,        ModuleDefaultsSeed.SccmTimeBaseOn),
            AvailableDateTime = Pick(_deploymentPrefs.AvailableDateTime, ModuleDefaultsSeed.SccmAvailableDateTime),
            DeadlineDateTime  = _deploymentPrefs.DeadlineDateTime,
            IsSelected = false,
        };
        _deployments.Add(entry);
        UpdatePlaceholder();
    }

    private static string Pick(string preferred, string fallback)
        => string.IsNullOrEmpty(preferred) ? fallback : preferred;

    private void BtnDuplicateItem_Click(object sender, RoutedEventArgs e)
    {
        DialogHelpers.TryInsertCloneAfter(sender, _deployments, CloneDeployment);
    }

    /// <summary>Field-aware clone for <see cref="SCCMDeploymentEntry"/>.</summary>
    private static SCCMDeploymentEntry CloneDeployment(SCCMDeploymentEntry src) => new()
    {
        AppName                    = src.AppName,
        PackageId                  = src.PackageId,
        Label                      = src.Label,
        Collection                 = src.Collection,
        DeployAction               = src.DeployAction,
        DeployPurpose              = src.DeployPurpose,
        UserNotification           = src.UserNotification,
        Comment                    = src.Comment,
        AvailableDateTime          = src.AvailableDateTime,
        DeadlineDateTime           = src.DeadlineDateTime,
        TimeBaseOn                 = src.TimeBaseOn,
        ApprovalRequired           = src.ApprovalRequired,
        OverrideServiceWindow      = src.OverrideServiceWindow,
        RebootOutsideServiceWindow = src.RebootOutsideServiceWindow,
        SendWakeupPacket           = src.SendWakeupPacket,
        IsSelected                 = false,
    };

    private void BtnRemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (DialogHelpers.TryRemoveTagged(sender, _deployments))
            UpdatePlaceholder();
    }

    private void BtnSaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not SCCMDeploymentEntry entry) return;

        // A plain owned Window: this dialog already occupies the app's single
        // ContentDialogHost, so a nested ContentDialog is not an option here.
        var result = SaveTemplateWindow.Prompt(
            Window.GetWindow(this),
            "Save Deployment Template",
            "Collection, purpose, notification and scheduling values are saved as-is. "
            + "App name and package linkage are never stored.",
            fields: null,
            name => TemplateService.CheckCollision(TemplateKind.Deployment, name),
            initialName: entry.Label);
        if (result is null) return;

        try
        {
            var info = TemplateService.SaveDeploymentTemplate(
                result.Value.Name, result.Value.Description, entry);
            TemplateComboBox.ItemsSource = TemplateService.GetDeploymentTemplates();
            AppLogger.Info($"Deployments: saved template '{info.Name}'");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Deployments: template save failed -- {ex.Message}");
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Save Template",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdatePlaceholder()
        => DialogHelpers.SetPlaceholderVisible(PlaceholderText, _deployments.Count);

    private bool _suppressTemplateSelection;

    private void TemplateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTemplateSelection) return;
        if (TemplateComboBox.SelectedItem is not TemplateInfo template) return;

        var entry = TemplateService.LoadDeploymentTemplate(template, _app);
        entry.AppName = _appName;
        entry.PackageId = _packageId;
        _deployments.Add(entry);
        UpdatePlaceholder();

        AppLogger.Info($"Deployments: applied template '{template.Name}'");

        _suppressTemplateSelection = true;
        TemplateComboBox.SelectedIndex = -1;
        _suppressTemplateSelection = false;
    }

    private bool _helpLoaded;

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (!_helpLoaded)
        {
            var text = TryFindResource("Help.SCCM.Deployments") as string;
            if (!string.IsNullOrEmpty(text))
            {
                var formatted = SectionHeader.BuildFormattedPanel(text, this);
                HelpContent.Children.Add(formatted);
                _helpLoaded = true;
            }
        }

        HelpPanel.Visibility = Visibility.Visible;
    }
}
