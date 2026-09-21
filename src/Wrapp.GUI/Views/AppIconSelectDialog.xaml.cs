using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Wrapp.Services;

namespace Wrapp.Views;

/// <summary>
/// Icon source picker (feature/icon-selector): a web-style drop square that
/// accepts a dragged image or opens the file browser on click, plus library
/// and remove actions. Every action completes the dialog itself - hosted via
/// <see cref="FluentDialog.ShowActionsAsync"/>; the caller reads
/// <see cref="PickedFile"/> / <see cref="Action"/> afterwards.
/// </summary>
public partial class AppIconSelectDialog : UserControl, FluentDialog.IClosableDialogContent
{
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".ico", ".bmp" };

    // File-drag hover feedback - same colors as the General view's drag
    // overlay (valid green / invalid red).
    private static readonly System.Windows.Media.Brush DragValidBrush =
        Frozen("#6FD46F");
    private static readonly System.Windows.Media.Brush DragInvalidBrush =
        Frozen("#E05C5C");

    private static System.Windows.Media.Brush Frozen(string hex)
    {
        var b = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>Full path of the dropped/browsed image, when that source was used.</summary>
    public string? PickedFile { get; private set; }

    /// <summary>"library" or "clear" when a secondary action was chosen; null otherwise.</summary>
    public string? Action { get; private set; }

    public event Action? CloseRequested;

    public AppIconSelectDialog(bool hasIcon)
    {
        InitializeComponent();
        RemoveButton.IsEnabled = hasIcon;
    }

    // ------------------------------------------------------------------
    // Drop square
    // ------------------------------------------------------------------

    private void DropZone_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var path = FileDialogService.BrowseFile(
            "Image Files|*.png;*.jpg;*.jpeg;*.ico;*.bmp|All Files|*.*",
            "Select Icon");
        if (path is null) return;
        PickedFile = path;
        CloseRequested?.Invoke();
    }

    private static bool IsImage(DragEventArgs e)
        => e.Data.GetDataPresent(DataFormats.FileDrop)
           && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files
           && ImageExtensions.Contains(Path.GetExtension(files[0]), StringComparer.OrdinalIgnoreCase);

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        var ok = IsImage(e);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        DropHint.Text    = ok ? "Drop to set the icon" : "Not an image file";
        DropSubHint.Text = ok ? Path.GetFileName(((string[])e.Data.GetData(DataFormats.FileDrop)!)[0])
                              : "Accepted: PNG, JPG, ICO, BMP";
        DropIcon.Text       = ok ? "" : "";
        DropIcon.Foreground = ok ? DragValidBrush : DragInvalidBrush;
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = IsImage(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        ResetHints();
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!IsImage(e)) { ResetHints(); return; }
        PickedFile = ((string[])e.Data.GetData(DataFormats.FileDrop)!)[0];
        CloseRequested?.Invoke();
    }

    private void ResetHints()
    {
        DropIcon.Text    = "";
        DropHint.Text    = "Drag & drop an image here";
        DropSubHint.Text = "or click to browse - PNG, JPG, ICO, BMP";
        // Restore the theme-following brush (a plain assignment would pin a
        // static color across theme switches).
        DropIcon.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "AccentBrush");
    }

    // ------------------------------------------------------------------
    // Secondary actions
    // ------------------------------------------------------------------

    private void Library_Click(object sender, RoutedEventArgs e)
    {
        Action = "library";
        CloseRequested?.Invoke();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        Action = "clear";
        CloseRequested?.Invoke();
    }
}
