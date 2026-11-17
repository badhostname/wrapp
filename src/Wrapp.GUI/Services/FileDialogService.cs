using System.Windows.Forms;

namespace Wrapp.Services;

/// <summary>
/// Thin wrapper over WPF/WinForms dialogs to keep dialog logic out of ViewModels.
/// </summary>
public static class FileDialogService
{
    public static string? BrowseFile(string filter = "All Files|*.*", string title = "Open File")
    {
        var dlg = new OpenFileDialog
        {
            Filter = filter,
            Title  = title
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public static string? BrowseFolder(string description = "Select Folder")
    {
        var dlg = new FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true
        };
        return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? dlg.SelectedPath
            : null;
    }

    /// <summary>Save-as picker. Returns the chosen path, or null if cancelled.</summary>
    public static string? SaveFile(string filter = "All Files|*.*", string title = "Save File",
        string? defaultFileName = null, string? defaultExt = null)
    {
        var dlg = new SaveFileDialog
        {
            Filter = filter,
            Title = title,
            FileName = defaultFileName ?? string.Empty,
            DefaultExt = defaultExt ?? string.Empty,
            AddExtension = !string.IsNullOrEmpty(defaultExt),
            OverwritePrompt = true,
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}
