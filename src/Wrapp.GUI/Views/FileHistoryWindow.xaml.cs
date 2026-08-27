using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Wrapp.Models;
using Wrapp.Services;
using Wpf.Ui.Controls;

namespace Wrapp.Views;

/// <summary>
/// Shows the git commit history for a single file with diff viewing and
/// the ability to restore a previous version. Modelled after DiffWindow.
/// </summary>
public partial class FileHistoryWindow : FluentWindow
{
    private readonly string _repoDir;
    private readonly string _relPath;
    private readonly string _language;
    private MonacoDiffService? _diffService;
    private CommitInfo? _selectedCommit;

    /// <summary>True when the user restored a historical version (caller should reload).</summary>
    public bool Restored { get; private set; }

    /// <summary>The content of the restored version. Null if not restored.</summary>
    public string? RestoredContent { get; private set; }

    private bool _restoreInProgress;

    public string WindowTitle { get; }
    public List<CommitInfo> Commits { get; private set; } = new();

    /// <param name="repoDir">Root of the git repository (bundle directory).</param>
    /// <param name="relativeFilePath">File path relative to repoDir (e.g. "DetectScript.ps1").</param>
    /// <param name="language">Monaco language id (e.g. "powershell", "json").</param>
    public FileHistoryWindow(string repoDir, string relativeFilePath, string language)
    {
        _repoDir  = repoDir;
        _relPath  = relativeFilePath;
        _language = language;

        WindowTitle = $"History - {Path.GetFileName(relativeFilePath)}";

        InitializeComponent();
        DataContext = this;

        Loaded += OnLoaded;
        WindowHelper.PreventCloseWhile(this, () => _restoreInProgress);

        // WebView2 doesn't raise SizeChanged for a DPI-only transition
        // (monitor switch, OS scale change) so Monaco's automaticLayout
        // misses it. Force a layout pass on DpiChanged, matching the
        // main editor's handling in DiffWindow + MainWindow.
        DpiChanged += async (_, __) =>
        {
            if (_diffService is not null)
                await _diffService.LayoutAsync(force: true);
        };
    }

    /// <summary>
    /// Keeps Monaco's layout in lock-step with the WebView2 host as the window
    /// resizes or the GridSplitter between the commit list and diff pane is
    /// dragged. Matches the <c>JsonWebView_SizeChanged</c> pattern used by the
    /// main editor views.
    /// </summary>
    private async void DiffWebView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_diffService is not null)
            await _diffService.LayoutAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _diffService = new MonacoDiffService(DiffWebView);

        Commits = await GitService.GetFileLogAsync(_repoDir, _relPath);
        CommitList.ItemsSource = Commits;

        if (Commits.Count == 0)
            StatusText.Text = "No history found for this file.";
        else
            StatusText.Text = $"{Commits.Count} commit(s)";
    }

    private async void CommitList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CommitList.SelectedItem is not CommitInfo commit) return;
        if (_diffService is null) return;

        _selectedCommit = commit;
        RestoreButton.IsEnabled = true;
        StatusText.Text = $"Showing: {commit.Hash} - {commit.Message}";

        // Left = historical version, Right = current file on disk
        var historical = await GitService.GetFileContentAtCommitAsync(
            _repoDir, commit.FullHash, _relPath);

        var currentPath = Path.Combine(_repoDir, _relPath);
        var current = File.Exists(currentPath)
            ? await File.ReadAllTextAsync(currentPath)
            : string.Empty;

        await _diffService.SetDiffAsync(historical, current, _language);
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCommit is null) return;

        var options = new ActionPickerOption[]
        {
            new() { Key = "restore", Icon = "\uE777", Title = "Restore to This Version",
                Description = $"Replace current content with version from commit {_selectedCommit.Hash}" },
        };
        var picker = new ActionPickerDialog(
            $"\"{_selectedCommit.Message}\"", options, null, "restore");

        var dialog = new Wpf.Ui.Controls.ContentDialog(DialogHost)
        {
            Title = "Restore from History",
            Content = picker,
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel"
        };

        // Hide WebView2 to avoid HWND airspace issue (dialog renders behind it)
        DiffWebView.Visibility = Visibility.Hidden;
        Wpf.Ui.Controls.ContentDialogResult dialogResult;
        try
        {
            dialogResult = await dialog.ShowAsync();
        }
        finally
        {
            DiffWebView.Visibility = Visibility.Visible;
        }

        if (dialogResult != Wpf.Ui.Controls.ContentDialogResult.Primary) return;

        try
        {
            _restoreInProgress = true;
            StatusText.Text = "Fetching content...";
            RestoreButton.IsEnabled = false;

            var content = await GitService.GetFileContentAtCommitAsync(
                _repoDir, _selectedCommit.FullHash, _relPath);

            RestoredContent = content;
            Restored = true;
            _restoreInProgress = false;
            AppLogger.Info($"FileHistory: restored {_relPath} to {_selectedCommit.FullHash}");

            // Auto-close so the caller can apply the content immediately
            Close();
        }
        catch (Exception ex)
        {
            _restoreInProgress = false;
            StatusText.Text = $"Restore failed: {ex.Message}";
            RestoreButton.IsEnabled = true;
            AppLogger.Warn($"FileHistory: restore failed for {_relPath}: {ex.Message}");
        }
    }
}
