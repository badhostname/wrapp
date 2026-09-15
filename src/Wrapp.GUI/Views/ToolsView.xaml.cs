using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using Wrapp.ViewModels;

namespace Wrapp.Views;

public partial class ToolsView : UserControl
{
    private static readonly HashSet<string> AcceptedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".intunewin" };

    // Counter tracks nested DragEnter/DragLeave from child elements.
    // Overlay stays visible as long as _dragDepth > 0.
    private int _dragDepth;

    public ToolsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The active tool tab's stable Tag ("decrypt" | "inspect" | "batch").
    /// Drag-drop routes by this, NOT by tab index: the tabs were reordered in
    /// 0.6.0.0145 (Decrypt moved to first) but the index-based router wasn't
    /// updated, so drops on the Decrypt tab silently ran the Inspect path and
    /// left the Decrypt fields empty (button greyed). Fixed 0.6.0.0277.
    /// </summary>
    private string SelectedTab => (ToolTabs.SelectedItem as TabItem)?.Tag as string ?? "";

    /// <summary>
    /// True while any tool operation is running. Drags are refused during it
    /// so a second drop can't interleave with a decrypt/inspect/scan in
    /// flight (mirrors GeneralView's busy-lock behaviour).
    /// </summary>
    private bool VmBusy => DataContext is ToolsViewModel vm
        && (vm.IsDecrypting || vm.IsInspecting || vm.IsBatchScanning || vm.IsBatchSaving);

    // -----------------------------------------------------------------------
    // Drag-and-drop (same pattern as GeneralView)
    // -----------------------------------------------------------------------

    private void UserControl_DragEnter(object sender, DragEventArgs e)
    {
        if (VmBusy)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files is null || files.Length == 0) return;

        _dragDepth++;

        var first = files[0];
        var isFolder = Directory.Exists(first);
        var ext = isFolder ? "" : Path.GetExtension(first);
        var valid = isFolder ? SelectedTab == "batch" : AcceptedExtensions.Contains(ext);

        MainContent.Effect = new BlurEffect { Radius = 10 };
        DragOverlay.Visibility = Visibility.Visible;

        if (valid && isFolder)
        {
            DragIcon.Text = "\uE8B7"; // folder
            DragIcon.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
            DragText.Text = "Drop folder to batch scan";
            DragSubtext.Text = "Will scan all .intunewin files in the folder";
        }
        else if (valid)
        {
            DragIcon.Text = "\uE896"; // download
            DragIcon.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
            DragText.Text = "Drop .intunewin file";
            var tab = SelectedTab switch
            {
                "decrypt" => "Decrypt",
                "inspect" => "Inspect",
                _         => "Batch Inspect",
            };
            DragSubtext.Text = $"Will open in {tab} tab";
        }
        else
        {
            DragIcon.Text = "\uE711"; // cancel
            DragIcon.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xE0, 0x5C, 0x5C));
            DragText.Text = isFolder ? "Switch to Batch Inspect tab to drop folders" : "Only .intunewin files accepted";
            DragSubtext.Text = isFolder ? "" : $"Got: {ext}";
        }

        e.Effects = valid ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void UserControl_DragOver(object sender, DragEventArgs e)
    {
        if (VmBusy || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files is not { Length: > 0 }) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        var isFolder = Directory.Exists(files[0]);
        var valid = isFolder ? SelectedTab == "batch" : AcceptedExtensions.Contains(Path.GetExtension(files[0]));
        e.Effects = valid ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void UserControl_DragLeave(object sender, DragEventArgs e)
    {
        _dragDepth--;
        if (_dragDepth <= 0)
        {
            _dragDepth = 0;
            MainContent.Effect = null;
            DragOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private async void UserControl_Drop(object sender, DragEventArgs e)
    {
        _dragDepth = 0;
        MainContent.Effect = null;
        DragOverlay.Visibility = Visibility.Collapsed;

        if (VmBusy) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files is not { Length: > 0 }) return;

        var path = files[0];
        if (DataContext is not ToolsViewModel vm) return;

        // Folder drop on Batch Inspect tab
        if (Directory.Exists(path))
        {
            if (SelectedTab == "batch")
            {
                vm.BatchFolderPath = path;
                vm.ScanBatchCommand.Execute(null);
            }
            return;
        }

        if (!AcceptedExtensions.Contains(Path.GetExtension(path))) return;

        switch (SelectedTab)
        {
            case "decrypt":
                // Auto-detect embedded keys and populate the decrypt fields.
                await vm.HandleDecryptDropAsync(path);
                SyncRadioToKeySource(vm.DecryptKeySource);
                break;

            case "inspect":
                vm.InspectFilePath = path;
                vm.InspectPackageCommand.Execute(null);
                break;

            case "batch":
                // Single file on the batch tab: scan its containing folder.
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder))
                {
                    vm.BatchFolderPath = folder;
                    vm.ScanBatchCommand.Execute(null);
                }
                break;
        }
    }

    // -----------------------------------------------------------------------
    // Key source radio buttons
    // -----------------------------------------------------------------------

    private void KeySource_Changed(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ToolsViewModel vm) return;
        if (KeySourceEmbedded.IsChecked == true) { vm.DecryptKeySource = "embedded"; vm.EmbeddedKeysReadOnly = true; }
        else if (KeySourceManual.IsChecked == true) { vm.DecryptKeySource = "manual"; vm.EmbeddedKeysReadOnly = false; }
        else if (KeySourceVault.IsChecked == true) vm.DecryptKeySource = "vault";
        else if (KeySourceBruteForce.IsChecked == true) vm.DecryptKeySource = "bruteforce";
        else if (KeySourceCsv.IsChecked == true) vm.DecryptKeySource = "csv";
    }

    private void SyncRadioToKeySource(string source)
    {
        switch (source)
        {
            case "embedded": KeySourceEmbedded.IsChecked = true; break;
            case "manual": KeySourceManual.IsChecked = true; break;
            case "vault": KeySourceVault.IsChecked = true; break;
            case "bruteforce": KeySourceBruteForce.IsChecked = true; break;
            case "csv": KeySourceCsv.IsChecked = true; break;
        }
    }
}
