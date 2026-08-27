using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;
using Wpf.Ui.Controls;
using Wrapp.Helpers;

namespace Wrapp.Services;

/// <summary>
/// Static helpers for showing Wpf.Ui ContentDialog popups that match the app theme.
/// All methods require a <see cref="ContentDialogHost"/> placed in the visual tree.
/// </summary>
public static class FluentDialog
{
    private static ContentDialogHost? _host;

    /// <summary>
    /// Sets the global dialog host. Called once from MainWindow on load.
    /// </summary>
    public static void SetHost(ContentDialogHost host) => _host = host;

    /// <summary>
    /// Shows an informational dialog with a single OK/Close button.
    /// </summary>
    public static async Task ShowInfoAsync(string title, string message, string closeText = "OK")
    {
        if (_host is null) return;
        var dialog = new ContentDialog(_host)
        {
            Title   = title,
            Content = CreateContent(message),
            CloseButtonText = closeText
        };
        await ShowWithAirspaceFixAsync(dialog);
    }

    /// <summary>
    /// Shows a warning dialog. Currently identical to <see cref="ShowInfoAsync"/>
    /// (the theme provides no severity affordance yet); kept as a separate name
    /// so call sites express intent and a future severity treatment lands in
    /// one place.
    /// </summary>
    public static Task ShowWarningAsync(string title, string message, string closeText = "OK")
        => ShowInfoAsync(title, message, closeText);

    /// <summary>
    /// Shows a Yes/No confirmation dialog. Returns true if Primary (Yes) was clicked.
    /// </summary>
    public static async Task<bool> ConfirmAsync(string title, string message,
        string yesText = "Yes", string noText = "No")
    {
        if (_host is null) return false;
        var dialog = new ContentDialog(_host)
        {
            Title             = title,
            Content           = CreateContent(message),
            PrimaryButtonText = yesText,
            CloseButtonText   = noText
        };
        var result = await ShowWithAirspaceFixAsync(dialog);
        return result == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Renders <paramref name="markdown"/> through the shared
    /// <see cref="HelpMarkdownRenderer"/> (same themed formatting as the help
    /// dialogs) and shows it in a primary/close dialog inside a bounded viewport.
    /// Returns true if the primary button was clicked. Use for long themed
    /// content (terms of use, license, release notes).
    /// </summary>
    public static async Task<bool> ConfirmMarkdownAsync(string title, string markdown,
        FrameworkElement resourceSource, string yesText = "Yes", string noText = "No")
    {
        if (_host is null) return false;
        var panel = HelpMarkdownRenderer.Render(markdown, resourceSource);
        return await ShowSelectAsync(title, WrapScrollable(panel), yesText, noText);
    }

    /// <summary>
    /// Shows arbitrary <paramref name="content"/> with a single Close button in
    /// a bounded, gently-scrolling, padded viewport (keeps text clear of the
    /// scrollbar). Use for long content popups such as the About card.
    /// </summary>
    public static async Task<ContentDialogResult> ShowScrollableContentAsync(
        string title, object content, string closeText = "Close")
        => await ShowContentAsync(title, WrapScrollable(content), closeText);

    /// <summary>
    /// Wraps <paramref name="content"/> in a bounded ScrollViewer with the app's
    /// gentle wheel behavior (<see cref="SmoothScroll"/>) and right padding so
    /// text doesn't crowd the scrollbar. Shared by the long-content dialogs so
    /// scrolling feels the same everywhere.
    /// </summary>
    private static System.Windows.Controls.ScrollViewer WrapScrollable(object content, double maxHeight = 520)
    {
        var scroll = new System.Windows.Controls.ScrollViewer
        {
            Content                       = content,
            VerticalScrollBarVisibility   = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
            MaxHeight                     = maxHeight,
            Padding                       = new Thickness(0, 0, 8, 0),
        };
        SmoothScroll.SetEnabled(scroll, true);
        return scroll;
    }

    /// <summary>
    /// Shows a Save/Don't Save/Cancel dialog. Returns "Save", "Discard", or "Cancel".
    /// </summary>
    public static async Task<string> SaveDiscardCancelAsync(string title, string message,
        string saveText = "Save", string discardText = "Don't Save", string cancelText = "Cancel")
    {
        if (_host is null) return "Cancel";
        var dialog = new ContentDialog(_host)
        {
            Title               = title,
            Content             = CreateContent(message),
            PrimaryButtonText   = saveText,
            SecondaryButtonText = discardText,
            CloseButtonText     = cancelText
        };
        var result = await ShowWithAirspaceFixAsync(dialog);
        return result switch
        {
            ContentDialogResult.Primary   => "Save",
            ContentDialogResult.Secondary => "Discard",
            _                             => "Cancel"
        };
    }

    // NOTE: dialog footer buttons are left entirely to the theme. Two attempts
    // to size them here both broke this dialog: mutating them on Opened (after
    // the dialog had already measured itself) left them clipped by its bottom
    // edge, and an implicit Style without BasedOn stripped the themed look,
    // because such a style REPLACES the theme's rather than extending it (the
    // same trap that stripped every ComboBox). If a label ever clips, shorten
    // the label rather than restyling the footer.

    /// <summary>
    /// Shows a ContentDialog hosting arbitrary UI content (e.g. a UserControl).
    /// </summary>
    public static async Task<ContentDialogResult> ShowContentAsync(
        string title, object content, string closeText = "Close")
    {
        if (_host is null) return ContentDialogResult.None;
        var dialog = new ContentDialog(_host)
        {
            Title          = title,
            Content        = content,
            CloseButtonText = closeText
        };
        return await ShowWithAirspaceFixAsync(dialog);
    }

    /// <summary>
    /// Shows an error dialog with Copy Details + Open Log Folder + OK buttons.
    /// Re-shows the dialog after Copy/Open so the user can read while acting.
    /// Falls back to MessageBox when the ContentDialogHost isn't initialized yet
    /// (e.g. early startup exceptions).
    /// </summary>
    public static async Task ShowExceptionAsync(string context, Exception exception)
    {
        var title        = $"Error \u2014 {context}";
        var shortMessage = $"An error occurred while {context}.\n\n" +
                           $"{exception.GetType().Name}: {exception.Message}\n\n" +
                           $"Log: {AppLogger.LogPath}";
        var fullDetails  = $"Context: {context}\n" +
                           $"Time:    {SystemClock.Now:u}\n\n" +
                           exception.ToString();

        if (_host is null)
        {
            MessageBox.Show(
                shortMessage + "\n\nDetails:\n" + fullDetails,
                title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        while (true)
        {
            var dialog = new ContentDialog(_host)
            {
                Title               = title,
                Content             = CreateContent(shortMessage),
                PrimaryButtonText   = "Copy details",
                SecondaryButtonText = "Open log folder",
                CloseButtonText     = "OK",
            };
            var result = await ShowWithAirspaceFixAsync(dialog);
            if (result == ContentDialogResult.Primary)
            {
                try { System.Windows.Clipboard.SetText(fullDetails); } catch { /* foreign clipboard owner */ }
                continue;
            }
            if (result == ContentDialogResult.Secondary)
            {
                try
                {
                    var logDir = System.IO.Path.GetDirectoryName(AppLogger.LogPath);
                    if (!string.IsNullOrEmpty(logDir))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                            "explorer.exe", $"\"{logDir}\"") { UseShellExecute = true });
                    }
                }
                catch { /* explorer launch failed — non-critical */ }
                continue;
            }
            break;
        }
    }

    /// <summary>
    /// THE unified "something was exported/saved to disk" prompt — every
    /// export flow in the app funnels here so the experience is identical:
    /// a contextual blurb, the destination path in the app's path styling
    /// (Consolas, secondary brush — same as the title bar and Run-view log
    /// path), and the two standard path actions: Copy path and Open in
    /// Explorer (which SELECTS the file rather than just opening the
    /// folder). Re-shows after either action, mirroring
    /// <see cref="ShowExceptionAsync"/>.
    /// </summary>
    public static async Task ShowExportedAsync(string title, string blurb, string path)
    {
        if (_host is null) return;

        var panel = new System.Windows.Controls.StackPanel { MinWidth = 420, MaxWidth = 560 };
        var blurbText = new System.Windows.Controls.TextBlock
        {
            Text = blurb,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        blurbText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextPrimaryBrush");
        panel.Children.Add(blurbText);

        // Path row mirrors the title bar exactly: single-line ellipsed path
        // (full path in the tooltip) with the SAME two icon buttons — E838
        // open-in-explorer + E8C8 copy, ToolbarBtn style — so the icons stay
        // visible no matter how long the path is.
        var row = new System.Windows.Controls.Grid();
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
        { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
        { Width = System.Windows.GridLength.Auto });
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
        { Width = System.Windows.GridLength.Auto });
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
        { Width = System.Windows.GridLength.Auto });

        var pathText = new System.Windows.Controls.TextBlock
        {
            Text = path,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            ToolTip = path,
            MinWidth = 0,
            Margin = new Thickness(0, 0, 6, 0),
        };
        pathText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextSecondaryBrush");
        row.Children.Add(pathText);

        System.Windows.Controls.Button IconButton(string glyph, string tooltip)
        {
            var text = new System.Windows.Controls.TextBlock
            {
                Text = glyph,
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
            text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextSecondaryBrush");
            var btn = new System.Windows.Controls.Button
            {
                Content = text,
                Padding = new Thickness(4, 4, 4, 4),
                Margin = new Thickness(2, 0, 0, 0),
                ToolTip = tooltip,
            };
            if (_host.TryFindResource("ToolbarBtn") is Style toolbarStyle)
                btn.Style = toolbarStyle;
            return btn;
        }

        var openBtn = IconButton("", "Open in Explorer");
        openBtn.Click += (_, _) => OpenInExplorer(path);
        System.Windows.Controls.Grid.SetColumn(openBtn, 1);
        row.Children.Add(openBtn);

        var copiedText = new System.Windows.Controls.TextBlock
        {
            Text = "Copied!",
            FontSize = 11,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        copiedText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ConnectedBrush");

        var copyBtn = IconButton("", "Copy path");
        copyBtn.Click += (_, _) =>
        {
            try { System.Windows.Clipboard.SetText(path); } catch { /* foreign clipboard owner */ }
            copiedText.Visibility = Visibility.Visible;
        };
        System.Windows.Controls.Grid.SetColumn(copyBtn, 2);
        row.Children.Add(copyBtn);
        System.Windows.Controls.Grid.SetColumn(copiedText, 3);
        row.Children.Add(copiedText);

        panel.Children.Add(row);

        var dialog = new ContentDialog(_host)
        {
            Title           = title,
            Content         = panel,
            CloseButtonText = "Close",
        };
        await ShowWithAirspaceFixAsync(dialog);
    }

    /// <summary>Opens Explorer AT the path: files are selected in their
    /// folder; directories open directly.</summary>
    public static void OpenInExplorer(string path)
    {
        try
        {
            var args = System.IO.Directory.Exists(path)
                ? $"\"{path}\""
                : $"/select,\"{path}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", args) { UseShellExecute = true });
        }
        catch { /* explorer launch failed — non-critical */ }
    }

    /// <summary>
    /// Three-outcome choice: primary, secondary, or cancel (the close button /
    /// ESC). Use when BOTH named actions have side effects — with the two-button
    /// <see cref="ShowSelectAsync"/>, ESC is indistinguishable from the "no"
    /// action, so a dismissal would silently trigger it.
    /// </summary>
    public static async Task<ContentDialogResult> ShowChoiceAsync(
        string title, object content,
        string primaryText, string secondaryText, string cancelText = "Cancel")
    {
        if (_host is null) return ContentDialogResult.None;
        var dialog = new ContentDialog(_host)
        {
            // String content gets the same styled body as SaveDiscardCancelAsync;
            // prebuilt panels pass through untouched.
            Title               = title,
            Content             = content is string s ? CreateContent(s) : content,
            PrimaryButtonText   = primaryText,
            SecondaryButtonText = secondaryText,
            CloseButtonText     = cancelText,
        };
        return await ShowWithAirspaceFixAsync(dialog);
    }

    public static async Task<bool> ShowSelectAsync(
        string title, object content,
        string selectText = "Select", string cancelText = "Cancel",
        bool primaryEnabled = true)
    {
        if (_host is null) return false;
        var dialog = new ContentDialog(_host)
        {
            Title                = title,
            Content              = content,
            PrimaryButtonText    = selectText,
            CloseButtonText      = cancelText,
            IsPrimaryButtonEnabled = primaryEnabled
        };
        var result = await ShowWithAirspaceFixAsync(dialog);
        return result == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Shows content whose actions complete the dialog themselves (e.g. a
    /// drop zone that applies on drop) — the chrome only offers a close
    /// button. When the content implements <see cref="IClosableDialogContent"/>,
    /// its <c>CloseRequested</c> hides the dialog.
    /// </summary>
    public static async Task ShowActionsAsync(string title, object content, string closeText = "Cancel")
    {
        if (_host is null) return;
        var dialog = new ContentDialog(_host)
        {
            Title           = title,
            Content         = content,
            CloseButtonText = closeText,
        };
        if (content is IClosableDialogContent closable)
            closable.CloseRequested += () => dialog.Hide();
        await ShowWithAirspaceFixAsync(dialog);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var grandchild in FindVisualChildren<T>(child))
                yield return grandchild;
        }
    }

    /// <summary>
    /// Shows a ContentDialog while temporarily hiding all WebView2 controls
    /// to work around the HWND airspace issue where WebView2 renders on top of WPF overlays.
    /// </summary>
    private static async Task<ContentDialogResult> ShowWithAirspaceFixAsync(ContentDialog dialog)
    {
        UiTrace.Log($"dialog: \"{dialog.Title}\"");
        var hidden = new List<WebView2>();
        var window = Application.Current.MainWindow;
        if (window is not null)
        {
            foreach (var wv in FindVisualChildren<WebView2>(window))
            {
                if (wv.Visibility == Visibility.Visible)
                {
                    wv.Visibility = Visibility.Collapsed;
                    hidden.Add(wv);
                }
            }
        }
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            foreach (var wv in hidden)
                wv.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Dialog content that can complete the dialog from inside (see <see cref="ShowActionsAsync"/>).</summary>
    public interface IClosableDialogContent
    {
        event Action? CloseRequested;
    }

    /// <summary>
    /// Body text for the message dialogs.
    /// <para><see cref="MinWidth"/> is load-bearing: a ContentDialog sizes
    /// itself to its CONTENT, and the footer buttons then have to fit whatever
    /// width that produced. A short message ("You have unsaved changes.") made
    /// the dialog narrow enough to clip "Don't Save" — the button labels were
    /// never the problem, the dialog was. Widening the body is the fix that
    /// leaves the theme's own footer layout untouched.</para>
    /// </summary>
    private static object CreateContent(string message)
    {
        return new System.Windows.Controls.TextBlock
        {
            Text         = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize     = 13,
            MinWidth     = 400,   // room for three footer buttons
            MaxWidth     = 460
        };
    }
}
