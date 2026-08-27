using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Clipboard = System.Windows.Clipboard;   // WinForms also declares this

namespace Wrapp.Controls;

/// <summary>
/// A path with the app's standard two actions (open-in-Explorer, copy) -
/// one control so every surface that exposes a file location behaves and
/// looks identical. A non-existent path shows its text but disables the
/// buttons (e.g. "no organization defaults file found").
/// </summary>
public partial class PathActionsRow : UserControl
{
    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path), typeof(string), typeof(PathActionsRow),
        new PropertyMetadata(string.Empty, (d, _) => ((PathActionsRow)d).OnPathChanged()));

    public string Path
    {
        get => (string)GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    public static readonly DependencyProperty HasRealPathProperty = DependencyProperty.Register(
        nameof(HasRealPath), typeof(bool), typeof(PathActionsRow), new PropertyMetadata(false));

    /// <summary>True when <see cref="Path"/> points at an existing file or
    /// directory - gates the action buttons.</summary>
    public bool HasRealPath
    {
        get => (bool)GetValue(HasRealPathProperty);
        private set => SetValue(HasRealPathProperty, value);
    }

    public PathActionsRow() => InitializeComponent();

    private void OnPathChanged()
    {
        try
        {
            HasRealPath = !string.IsNullOrWhiteSpace(Path)
                && (System.IO.File.Exists(Path) || System.IO.Directory.Exists(Path));
        }
        catch { HasRealPath = false; }
    }

    private void OpenClick(object sender, RoutedEventArgs e)
        => Services.FluentDialog.OpenInExplorer(Path);

    private void CopyClick(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Path); } catch { return; }
        CopiedText.Visibility = Visibility.Visible;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { CopiedText.Visibility = Visibility.Collapsed; timer.Stop(); };
        timer.Start();
    }
}
