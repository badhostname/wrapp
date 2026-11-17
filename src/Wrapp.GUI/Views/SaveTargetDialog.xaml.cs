using System.Windows;
using System.Windows.Controls;
using Wrapp.Models;
using Wrapp.Services;
using Wrapp.Services.Policy;

namespace Wrapp.Views;

/// <summary>
/// The save sheet: where a bundle goes. One sheet for Save, Save As,
/// Upgrade › Save as and Full apply › Save as; only the pre-filled values
/// differ. Edits a <see cref="SaveTarget"/> in place and shows what it
/// resolves to; the host reads <see cref="Target"/> after the dialog closes.
/// Browse is the only way to set the location: nothing is typed.
/// </summary>
public partial class SaveTargetDialog : UserControl
{
    private readonly AppSettings _settings;
    private readonly AppSection _app;
    private readonly string? _currentBundleRoot;
    private bool _rendering;

    public SaveTarget Target { get; }

    /// <summary>True when policy fixes the save mode; the radios are then locked.</summary>
    public bool ModeIsManaged { get; }

    public SaveTargetDialog(SaveTarget target, AppSettings settings, AppSection app, string? currentBundleRoot)
    {
        InitializeComponent();
        Target = target;
        _settings = settings;
        _app = app;
        _currentBundleRoot = currentBundleRoot;
        ModeIsManaged = PolicyService.Current.Mandatory.ContainsKey(nameof(AppSettings.BundleSaveMode));
        if (ModeIsManaged)
        {
            ConventionRadio.IsEnabled = false;
            DirectRadio.IsEnabled = false;
            ConventionRadio.ToolTip = DirectRadio.ToolTip = "Managed by your organization";
            ManagedNote.Visibility = Visibility.Visible;
        }
        Render();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var folder = FileDialogService.BrowseFolder("Choose where to save the bundle");
        if (string.IsNullOrEmpty(folder)) return;
        Target.Location = folder;
        Target.LocationSource = "chosen for this save";
        Refresh();
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (_rendering) return;
        Target.Mode = ConventionRadio.IsChecked == true ? SaveTargetMode.Convention : SaveTargetMode.Direct;
        Refresh();
    }

    private void Relative_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_rendering) return;
        Target.RelativePath = RelativeBox.Text;
        Refresh();
    }

    private void Refresh()
    {
        Target.Refresh(_settings, _app, _currentBundleRoot);
        Render();
    }

    private void Render()
    {
        _rendering = true;
        try
        {
            LocationBox.Text = Target.Location;
            LocationHint.Text = string.IsNullOrEmpty(Target.LocationSource)
                ? string.Empty
                : $"{Target.LocationSource} · the organisation default is set in Settings > Bundle";

            var convention = Target.Mode == SaveTargetMode.Convention;
            ConventionRadio.IsChecked = convention;
            DirectRadio.IsChecked = !convention;
            ConventionPanel.IsEnabled = convention;
            if (!string.Equals(RelativeBox.Text, Target.RelativePath, StringComparison.Ordinal))
                RelativeBox.Text = Target.RelativePath;

            var format = BundleService.EffectiveDirectoryFormat(_settings);
            RelativeHint.Text = string.Equals(Target.RelativePath.Trim(), Target.SuggestedRelativePath, StringComparison.Ordinal)
                ? $"from {format} · edit for this save only"
                : $"edited for this save · the convention would make {Target.SuggestedRelativePath}";

            if (convention && Target.MissingFields.Count > 0)
            {
                MissingText.Text = $"The convention needs {string.Join(", ", Target.MissingFields)} (empty in App Info).";
                MissingText.Visibility = Visibility.Visible;
            }
            else MissingText.Visibility = Visibility.Collapsed;

            ResolvedText.Text = Target.ResolvedPath;

            ErrorText.Text = Target.Error ?? string.Empty;
            ErrorText.Visibility = Target.Error is null ? Visibility.Collapsed : Visibility.Visible;

            WarningsList.ItemsSource = Target.Error is null ? Target.Warnings.ToList() : new List<string>();
        }
        finally
        {
            _rendering = false;
        }
    }
}
