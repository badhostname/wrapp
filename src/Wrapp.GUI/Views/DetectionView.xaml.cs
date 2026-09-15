using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Wrapp.Models;
using Wrapp.Services;
using Wrapp.ViewModels;

namespace Wrapp.Views;

public partial class DetectionView : UserControl
{
    public DetectionView() { InitializeComponent(); }

    private void BrowsePathClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not DetectionTest test) return;
        var path = FileDialogService.BrowseFile("All Files|*.*", "Select file for detection");
        if (path is null) return;

        test.Command = string.Empty;
        test.Path = path;
        test.IsPathLocked = true;
        test.Property = "Exists";
        // Build the property list dynamically from what's actually populated on
        // this specific file. Only properties that will return a non-empty value
        // at runtime (via Get-ItemProperty in DetectScript.ps1) are offered.
        test.PropertyValues = GetFilePropertyValues(path);
        test.AvailableProperties = new ObservableCollection<string>(test.PropertyValues.Keys);
        test.Value = "True";
    }

    private async void BrowseRegistryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not DetectionTest test) return;

        var dialog = new RegistryBrowserDialog();
        var confirmed = await FluentDialog.ShowSelectAsync(
            "Browse Registry", dialog, "Select", "Cancel");

        if (!confirmed || !dialog.HasSelection || dialog.SelectedPath is null) return;

        test.Command = string.Empty;
        test.Path = dialog.SelectedPath;
        test.Property = dialog.SelectedProperty;
        test.IsPathLocked = true;
        test.AvailableProperties = new ObservableCollection<string>(dialog.ValueNames);
        test.PropertyValues = dialog.ValueData;
        if (test.PropertyValues.TryGetValue(test.Property, out var val))
            test.Value = val;
    }

    private void UnlockPathClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not DetectionTest test) return;
        // Capture property before clearing the ComboBox source
        var savedProperty = test.Property;
        // Clear dropdown items and value map first (while ComboBox is still visible)
        test.AvailableProperties = new ObservableCollection<string>();
        test.PropertyValues = new Dictionary<string, string>();
        // Now switch from ComboBox to TextBox and restore the value
        test.IsPathLocked = false;
        test.Property = savedProperty;
    }

    private void PropertyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox cb || cb.DataContext is not DetectionTest test) return;
        if (string.IsNullOrEmpty(test.Property)) return;
        if (test.PropertyValues.TryGetValue(test.Property, out var val))
            test.Value = val;
    }

    /// <summary>
    /// Build a map of property name -> sample value for the selected file.
    /// Names must match exactly what PowerShell's Get-ItemProperty returns,
    /// since the detection script uses them verbatim in:
    /// (Get-ItemProperty $TestPath).$Property
    ///
    /// VersionInfo is a nested FileVersionInfo; PowerShell's dotted-path
    /// access resolves "VersionInfo.FileVersion" at runtime, so that literal
    /// string works as the Property value. Only properties with an actual
    /// value on this file are included, so the dropdown never offers one
    /// that would evaluate to $null.
    /// </summary>
    private static Dictionary<string, string> GetFilePropertyValues(string filePath)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Exists"] = "True"
        };
        try
        {
            var fi = new FileInfo(filePath);
            // FileInfo members -- these always have values for a file that exists
            values["Length"] = fi.Length.ToString();
            values["LastWriteTime"] = fi.LastWriteTime.ToString("o");
            values["CreationTime"] = fi.CreationTime.ToString("o");

            // VersionInfo nested members -- only add those populated on this file
            var fvi = FileVersionInfo.GetVersionInfo(filePath);
            if (!string.IsNullOrEmpty(fvi.FileVersion))
                values["VersionInfo.FileVersion"] = fvi.FileVersion;
            if (!string.IsNullOrEmpty(fvi.ProductVersion))
                values["VersionInfo.ProductVersion"] = fvi.ProductVersion;
            if (!string.IsNullOrEmpty(fvi.FileDescription))
                values["VersionInfo.FileDescription"] = fvi.FileDescription;
            if (!string.IsNullOrEmpty(fvi.CompanyName))
                values["VersionInfo.CompanyName"] = fvi.CompanyName;
            if (!string.IsNullOrEmpty(fvi.ProductName))
                values["VersionInfo.ProductName"] = fvi.ProductName;
            if (!string.IsNullOrEmpty(fvi.InternalName))
                values["VersionInfo.InternalName"] = fvi.InternalName;
            if (!string.IsNullOrEmpty(fvi.OriginalFilename))
                values["VersionInfo.OriginalFilename"] = fvi.OriginalFilename;
            if (!string.IsNullOrEmpty(fvi.LegalCopyright))
                values["VersionInfo.LegalCopyright"] = fvi.LegalCopyright;
        }
        // must-stay-silent: best-effort version-info scrape. A failure means
        // the binary lacks a VERSIONINFO resource (or the file is missing);
        // returning the partially-populated dictionary is the documented
        // contract for this picker, and surfacing the exception would scare
        // users with errors during the normal "no metadata" case.
        catch { }
        return values;
    }
}
