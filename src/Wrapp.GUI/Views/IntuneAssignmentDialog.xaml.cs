using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Wrapp.Controls;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Views;

public partial class IntuneAssignmentDialog : UserControl
{
    private readonly ObservableCollection<AssignmentEntry> _assignments;
    /// <summary>App section for placeholder expansion when applying templates.</summary>
    private readonly AppSection? _app;
    private readonly string _appName;
    private readonly string _packageId;
    private readonly IntuneAssignmentDefaults _assignmentPrefs;

    // Exposed for XAML ComboBox ItemsSource bindings
    public string[] ValidAssignmentTypes { get; }
    public string[] ValidAssignmentIntents { get; }
    public string[] ValidGroupModes { get; }
    public string[] ValidIntuneNotifications { get; }
    public string[] ValidDeliveryOptimizationPriorities { get; }
    public string[] ValidFilterModes { get; }

    public IntuneAssignmentDialog(
        ObservableCollection<AssignmentEntry> assignments,
        string appName,
        ModuleDefaults defaults,
        string packageId = "",
        AppSection? app = null)
    {
        _app = app;
        _assignments = assignments;
        _appName = appName;
        _packageId = packageId;

        ValidAssignmentTypes = defaults.ValidAssignmentTypes;
        ValidAssignmentIntents = defaults.ValidAssignmentIntents;
        ValidGroupModes = defaults.ValidGroupModes;
        ValidIntuneNotifications = defaults.ValidIntuneNotifications;
        ValidDeliveryOptimizationPriorities = defaults.ValidDeliveryOptimizationPriorities;
        ValidFilterModes = defaults.ValidFilterModes;

        // Cache user-persisted assignment defaults so enabling the restart
        // grace toggle can pre-fill the three companion fields without hitting
        // disk on every click.
        _assignmentPrefs = SettingsService.Load().IntuneAssignmentDefaults;

        DataContext = this;
        InitializeComponent();

        TemplateComboBox.ItemsSource = TemplateService.GetAssignmentTemplates();
        AssignmentItems.ItemsSource = _assignments;
        UpdatePlaceholder();
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var entry = new AssignmentEntry
        {
            AppName      = _appName,
            PackageId    = _packageId,
            Type         = Pick(_assignmentPrefs.Type,         ModuleDefaultsSeed.IntuneAssignmentType),
            GroupMode    = Pick(_assignmentPrefs.GroupMode,    ModuleDefaultsSeed.IntuneAssignmentGroupMode),
            Intent       = Pick(_assignmentPrefs.Intent,       ModuleDefaultsSeed.IntuneAssignmentIntent),
            Notification = Pick(_assignmentPrefs.Notification, ModuleDefaultsSeed.IntuneAssignmentNotification),
            DeliveryOptimizationPriority =
                Pick(_assignmentPrefs.DeliveryOptimizationPriority,
                     ModuleDefaultsSeed.IntuneAssignmentDeliveryOptimizationPriority),
            AvailableTime = Pick(_assignmentPrefs.AvailableTime, ModuleDefaultsSeed.IntuneAssignmentAvailableTime),
            DeadlineTime  = _assignmentPrefs.DeadlineTime,
            FilterMode    = _assignmentPrefs.FilterMode,
            EnableRestartGracePeriod = _assignmentPrefs.EnableRestartGracePeriod,
            RestartGracePeriodInMinutes = _assignmentPrefs.EnableRestartGracePeriod
                ? _assignmentPrefs.RestartGracePeriodInMinutes.ToString()
                : string.Empty,
            RestartCountDownDisplayInMinutes = _assignmentPrefs.EnableRestartGracePeriod
                ? _assignmentPrefs.RestartCountDownDisplayInMinutes.ToString()
                : string.Empty,
            RestartNotificationSnoozeInMinutes = _assignmentPrefs.EnableRestartGracePeriod
                ? _assignmentPrefs.RestartNotificationSnoozeInMinutes.ToString()
                : string.Empty,
            IsSelected = false,
        };
        _assignments.Add(entry);
        UpdatePlaceholder();
    }

    private static string Pick(string preferred, string fallback)
        => string.IsNullOrEmpty(preferred) ? fallback : preferred;

    private void BtnDuplicateItem_Click(object sender, RoutedEventArgs e)
    {
        DialogHelpers.TryInsertCloneAfter(sender, _assignments, CloneAssignment);
    }

    /// <summary>Field-aware clone for <see cref="AssignmentEntry"/>.</summary>
    private static AssignmentEntry CloneAssignment(AssignmentEntry src) => new()
    {
        AppName                            = src.AppName,
        PackageId                          = src.PackageId,
        Label                              = src.Label,
        Type                               = src.Type,
        GroupID                            = src.GroupID,
        GroupMode                          = src.GroupMode,
        Intent                             = src.Intent,
        Notification                       = src.Notification,
        DeliveryOptimizationPriority       = src.DeliveryOptimizationPriority,
        AvailableTime                      = src.AvailableTime,
        DeadlineTime                       = src.DeadlineTime,
        UseLocalTime                       = src.UseLocalTime,
        AutoUpdateSupersededApps           = src.AutoUpdateSupersededApps,
        EnableRestartGracePeriod           = src.EnableRestartGracePeriod,
        RestartGracePeriodInMinutes        = src.RestartGracePeriodInMinutes,
        RestartCountDownDisplayInMinutes   = src.RestartCountDownDisplayInMinutes,
        RestartNotificationSnoozeInMinutes = src.RestartNotificationSnoozeInMinutes,
        FilterName                         = src.FilterName,
        FilterMode                         = src.FilterMode,
        IsSelected                         = false,
    };

    private void BtnRemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (DialogHelpers.TryRemoveTagged(sender, _assignments))
            UpdatePlaceholder();
    }

    private void BtnSaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not AssignmentEntry entry) return;

        // A plain owned Window: this dialog already occupies the app's single
        // ContentDialogHost, so a nested ContentDialog is not an option here.
        var result = SaveTemplateWindow.Prompt(
            Window.GetWindow(this),
            "Save Assignment Template",
            "Group ID, intent, notification and scheduling values are saved as-is. "
            + "App name and package linkage are never stored.",
            fields: null,
            name => TemplateService.CheckCollision(TemplateKind.Assignment, name),
            initialName: entry.Label);
        if (result is null) return;

        try
        {
            var info = TemplateService.SaveAssignmentTemplate(
                result.Value.Name, result.Value.Description, entry);
            TemplateComboBox.ItemsSource = TemplateService.GetAssignmentTemplates();
            AppLogger.Info($"Assignments: saved template '{info.Name}'");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Assignments: template save failed -- {ex.Message}");
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Save Template",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdatePlaceholder()
        => DialogHelpers.SetPlaceholderVisible(PlaceholderText, _assignments.Count);

    /// <summary>
    /// When the user turns on the Enable Restart Grace Period checkbox on an
    /// assignment row, pre-fill the three companion restart fields from the
    /// user's Intune Preferences. Any value the user already typed (non-zero)
    /// is preserved.
    /// </summary>
    private void EnableRestartGrace_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox cb) return;
        if (cb.DataContext is not AssignmentEntry entry) return;

        entry.RestartGracePeriodInMinutes =
            FillRestartField(entry.RestartGracePeriodInMinutes,
                _assignmentPrefs.RestartGracePeriodInMinutes,
                ModuleDefaultsSeed.IntuneAssignmentRestartGracePeriodInMinutes);

        entry.RestartCountDownDisplayInMinutes =
            FillRestartField(entry.RestartCountDownDisplayInMinutes,
                _assignmentPrefs.RestartCountDownDisplayInMinutes,
                ModuleDefaultsSeed.IntuneAssignmentRestartCountDownDisplayInMinutes);

        entry.RestartNotificationSnoozeInMinutes =
            FillRestartField(entry.RestartNotificationSnoozeInMinutes,
                _assignmentPrefs.RestartNotificationSnoozeInMinutes,
                ModuleDefaultsSeed.IntuneAssignmentRestartNotificationSnoozeInMinutes);
    }

    /// <summary>
    /// Returns the current field value if it already holds a positive integer;
    /// otherwise returns the user's preference (or the hard-coded seed when the
    /// preference is also unset). AssignmentEntry stores these as strings.
    /// </summary>
    private static string FillRestartField(string current, int preference, int seed)
    {
        if (int.TryParse(current, out var n) && n > 0) return current;
        var chosen = preference > 0 ? preference : seed;
        return chosen.ToString();
    }

    private bool _suppressTemplateSelection;

    private void TemplateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTemplateSelection) return;
        if (TemplateComboBox.SelectedItem is not TemplateInfo template) return;

        var entry = TemplateService.LoadAssignmentTemplate(template, _app);
        entry.AppName = _appName;
        entry.PackageId = _packageId;
        _assignments.Add(entry);
        UpdatePlaceholder();

        AppLogger.Info($"Assignments: applied template '{template.Name}'");

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
            var text = TryFindResource("Help.Intune.Assignments") as string;
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
