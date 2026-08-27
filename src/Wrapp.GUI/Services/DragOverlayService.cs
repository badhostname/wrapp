using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Wrapp.Services;

/// <summary>
/// Reusable helpers for drag-and-drop overlay effects (blur + overlay panel).
/// Used by GeneralView, IntuneView, and SCCMView.
/// </summary>
public static class DragOverlayService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".ico"
    };

    private static readonly HashSet<string> InstallerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msp"
    };

    /// <summary>Applies a blur effect to the target element.</summary>
    public static void ApplyBlur(UIElement target, double radius = 10)
    {
        target.Effect = new BlurEffect { Radius = radius };
    }

    /// <summary>Removes the blur effect from the target element.</summary>
    public static void RemoveBlur(UIElement target)
    {
        target.Effect = null;
    }

    /// <summary>
    /// Shows a drag overlay grid with valid/invalid icons and hint text.
    /// Blurs the specified content element underneath.
    /// </summary>
    public static void ShowOverlay(
        Grid overlay,
        UIElement blurTarget,
        TextBlock validIcon,
        TextBlock invalidIcon,
        TextBlock hint,
        TextBlock subHint,
        bool valid,
        string hintText,
        string subHintText)
    {
        ApplyBlur(blurTarget);

        validIcon.Visibility   = valid ? Visibility.Visible   : Visibility.Collapsed;
        invalidIcon.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;

        hint.Text    = hintText;
        subHint.Text = subHintText;

        overlay.Visibility = Visibility.Visible;
    }

    /// <summary>Hides the overlay and removes blur.</summary>
    public static void HideOverlay(Grid overlay, UIElement blurTarget)
    {
        overlay.Visibility = Visibility.Collapsed;
        RemoveBlur(blurTarget);
    }

    /// <summary>Returns true if the path points to a supported image file.</summary>
    public static bool IsImageFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        return ImageExtensions.Contains(Path.GetExtension(path));
    }

    /// <summary>Returns true if the path points to a supported installer file.</summary>
    public static bool IsInstallerFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        return InstallerExtensions.Contains(Path.GetExtension(path));
    }

    /// <summary>Gets a user-friendly description of what will happen when the file is dropped.</summary>
    public static string DescribeDropTarget(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;

        if (Directory.Exists(path))
            return "Folder -- will load Config.json inside";

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".exe" => "EXE installer -- will populate installer fields",
            ".msi" => "MSI package -- will populate installer fields",
            ".msp" => "MSP patch -- will populate installer fields",
            ".intunewin" => "IntuneWin package -- will decrypt and import",
            ".png" or ".jpg" or ".jpeg" or ".ico" => "Image -- will set as app icon",
            _      => string.Empty
        };
    }

    /// <summary>Gets a description for image-only drop zones (package screens).</summary>
    public static string DescribeImageDrop(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".ico" => "Image -- will set as package icon",
            _ => string.Empty
        };
    }

    // -----------------------------------------------------------------------
    // Shared package-view drag-drop handlers (Intune/SCCM icon drop)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Per-view drag state. A class (not a ref bool) so the deferred
    /// DragLeave check can observe whether a later DragEnter/DragOver
    /// re-armed the drag.
    /// </summary>
    public sealed class DragOverlayState
    {
        public bool IsDragActive;
    }

    /// <summary>
    /// Shared DragEnter handler for package views (Intune/SCCM).
    /// Validates the drop is an image file and a package is selected.
    /// </summary>
    public static void HandlePackageDragEnter(
        DragEventArgs e,
        Func<bool> hasSelectedPackage,
        Grid overlay, UIElement mainContent,
        TextBlock validIcon, TextBlock invalidIcon,
        TextBlock hint, TextBlock subHint,
        DragOverlayState state)
    {
        state.IsDragActive = true;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        var first = paths is { Length: > 0 } ? paths[0] : string.Empty;
        bool valid = IsImageFile(first) && hasSelectedPackage();

        string hintText, subHintText;
        if (!hasSelectedPackage())
        {
            valid = false;
            hintText = "No package selected";
            subHintText = "Select a package first, then drop an image to set its icon";
        }
        else if (valid)
        {
            hintText = "Drop to set package icon";
            subHintText = DescribeImageDrop(first);
        }
        else
        {
            hintText = "Unsupported file type";
            subHintText = "Accepted: PNG, JPG, or ICO image files";
        }

        ShowOverlay(overlay, mainContent, validIcon, invalidIcon, hint, subHint, valid, hintText, subHintText);
        e.Effects = valid ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Shared DragOver handler for package views.</summary>
    public static void HandlePackageDragOver(DragEventArgs e, Func<bool> hasSelectedPackage, DragOverlayState state)
    {
        state.IsDragActive = true;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        var first = paths is { Length: > 0 } ? paths[0] : string.Empty;
        bool valid = IsImageFile(first) && hasSelectedPackage();
        e.Effects = valid ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Shared DragLeave handler for package views.</summary>
    public static void HandlePackageDragLeave(
        DragEventArgs e,
        UserControl view,
        Grid overlay, UIElement mainContent,
        DragOverlayState state)
    {
        state.IsDragActive = false;

        // DragLeave fires for every nested child control as the mouse crosses
        // between them, and its position is often the LAST hover point — still
        // inside bounds even when the drag genuinely ended (releasing an
        // unsupported file over the view fires no Drop because Effects=None,
        // only a DragLeave at the release point; the overlay used to stick
        // forever). So: hide immediately when the position is clearly outside,
        // otherwise defer one input cycle — a real drag still in progress
        // re-arms IsDragActive via DragOver before the check runs; a finished
        // drag doesn't, and the overlay comes down. Same pattern as
        // GeneralView, which never had the stuck overlay.
        var pos = e.GetPosition(view);
        bool outsideBounds = pos.X <= 0 || pos.Y <= 0 ||
                             pos.X >= view.ActualWidth || pos.Y >= view.ActualHeight;

        if (outsideBounds)
        {
            HideOverlay(overlay, mainContent);
        }
        else
        {
            view.Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)(() =>
            {
                if (!state.IsDragActive)
                    HideOverlay(overlay, mainContent);
            }));
        }

        e.Handled = true;
    }

    /// <summary>Shared Drop handler for package views.</summary>
    public static void HandlePackageDrop(
        DragEventArgs e,
        Grid overlay, UIElement mainContent,
        Action<string> applyDroppedIcon,
        DragOverlayState state)
    {
        state.IsDragActive = false;
        HideOverlay(overlay, mainContent);

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        if (paths.Length == 0) return;

        var first = paths[0];
        if (IsImageFile(first))
            applyDroppedIcon(first);
    }
}
