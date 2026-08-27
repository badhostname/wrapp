using System.Collections.Generic;
using System.IO;
using System.Windows;
using Wrapp.Models;
using Wrapp.Services;
using Wpf.Ui.Controls;

namespace Wrapp.Views;

public partial class DiffWindow : FluentWindow
{
    private readonly CommitInfo _commit;
    private readonly List<CommitFileChange> _files;
    private readonly string _repoDir;
    private MonacoDiffService? _diffService;

    public string CommitHash    { get; }
    public string CommitMessage { get; }
    public string CommitAuthor  { get; }
    public string CommitTime    { get; }
    public List<CommitFileChange> Files { get; }
    public string StatusText { get; set; } = "";

    public DiffWindow(CommitInfo commit, List<CommitFileChange> files, string repoDir)
    {
        _commit  = commit;
        _files   = files;
        _repoDir = repoDir;

        CommitHash    = commit.Hash;
        CommitMessage = commit.Message;
        CommitAuthor  = commit.Author;
        CommitTime    = commit.RelativeTime;
        Files         = files;
        StatusText    = $"{files.Count} file(s) changed";

        InitializeComponent();
        DataContext = this;

        Loaded += OnLoaded;

        // Force a Monaco relayout when the window's DPI changes (docking,
        // undocking, OS scale change) — WebView2 doesn't raise SizeChanged
        // for a DPI-only transition, so automaticLayout stays stale.
        DpiChanged += async (_, __) =>
        {
            if (_diffService is not null)
                await _diffService.LayoutAsync(force: true);
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _diffService = new MonacoDiffService(DiffWebView);

        // Auto-select first text file
        var first = _files.Find(f => !f.IsBinary);
        if (first is not null)
            await ShowDiffForFileAsync(first);
    }

    private async void FileButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not CommitFileChange file) return;

        await ShowDiffForFileAsync(file);
    }

    /// <summary>
    /// WebView2 doesn't always propagate its own resize to the hosted Monaco
    /// instance in time — the Grid cell may grow while Monaco is still laid
    /// out against the old 0×0 container size. Matches the pattern used for
    /// the main editor (see <c>ConfigJsonView.JsonWebView_SizeChanged</c>) so
    /// the diff editor tracks window / splitter resizes reliably.
    /// </summary>
    private async void DiffWebView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_diffService is not null)
            await _diffService.LayoutAsync();
    }

    private async Task ShowDiffForFileAsync(CommitFileChange file)
    {
        if (_diffService is null) return;

        if (file.IsBinary)
        {
            await _diffService.SetDiffAsync(
                $"(binary file: {file.FilePath})",
                $"(binary file: {file.StatusLabel})",
                "plaintext");
            StatusText = $"Binary file - {file.StatusLabel}: {file.FilePath}";
            return;
        }

        var language = GetLanguageFromExtension(Path.GetExtension(file.FilePath));

        string original = "";
        string modified = "";

        if (file.Status == "A")
        {
            // Added: no original, modified is the new content
            modified = await GitService.GetFileContentAtCommitAsync(_repoDir, _commit.FullHash, file.FilePath);
        }
        else if (file.Status == "D")
        {
            // Deleted: original is parent content, no modified
            original = await GitService.GetFileContentAtCommitAsync(_repoDir, $"{_commit.FullHash}~1", file.FilePath);
        }
        else if (file.Status == "R")
        {
            // Renamed: old path at parent, new path at this commit
            original = await GitService.GetFileContentAtCommitAsync(_repoDir, $"{_commit.FullHash}~1", file.OldPath);
            modified = await GitService.GetFileContentAtCommitAsync(_repoDir, _commit.FullHash, file.FilePath);
        }
        else
        {
            // Modified: parent vs this commit
            original = await GitService.GetFileContentAtCommitAsync(_repoDir, $"{_commit.FullHash}~1", file.FilePath);
            modified = await GitService.GetFileContentAtCommitAsync(_repoDir, _commit.FullHash, file.FilePath);
        }

        await _diffService.SetDiffAsync(original, modified, language);
        StatusText = $"Showing diff for {file.FilePath}";
    }

    private static string GetLanguageFromExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".ps1"  => "powershell",
        ".json" => "json",
        ".xml"  => "xml",
        ".xaml" => "xml",
        ".cs"   => "csharp",
        ".js"   => "javascript",
        ".ts"   => "typescript",
        ".md"   => "markdown",
        ".yml"  => "yaml",
        ".yaml" => "yaml",
        ".bat"  => "bat",
        ".cmd"  => "bat",
        ".sh"   => "shell",
        ".py"   => "python",
        ".html" => "html",
        ".css"  => "css",
        _       => "plaintext"
    };
}
