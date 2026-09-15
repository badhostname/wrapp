using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Wrapp.Services;
using Wrapp.ViewModels;

namespace Wrapp.Views;

public partial class GeneralView : UserControl
{
    private bool _isDragActive;

    // Mirrors GeneralViewModel.IsDropBusy: while dropped-file discovery runs
    // off-thread, the drag overlay stays up in "busy" mode (blur + status)
    // and every drag/drop event is refused, so the user can't queue a second
    // drop or interact with the half-updated form underneath.
    private bool _dropBusy;

    public GeneralView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is GeneralViewModel oldVm) oldVm.PropertyChanged -= OnVmPropertyChanged;
            if (e.NewValue is GeneralViewModel newVm) newVm.PropertyChanged += OnVmPropertyChanged;
        };
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not GeneralViewModel vm) return;
        if (e.PropertyName == nameof(GeneralViewModel.IsDropBusy))
        {
            if (vm.IsDropBusy) ShowBusyOverlay(vm.DropBusyText);
            else if (_dropBusy) { _dropBusy = false; HideDragOverlay(); }
        }
        else if (e.PropertyName == nameof(GeneralViewModel.DropBusyText) && _dropBusy)
        {
            DragOverlayHint.Text = vm.DropBusyText;
        }
    }

    private void UserControl_DragEnter(object sender, DragEventArgs e)
    {
        if (_dropBusy)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        _isDragActive = true;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        bool valid = paths != null && GeneralViewModel.ValidateDroppedPaths(paths);

        ShowDragOverlay(valid, paths);

        e.Effects = valid ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void UserControl_DragOver(object sender, DragEventArgs e)
    {
        if (_dropBusy)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        _isDragActive = true;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        bool valid = paths != null && GeneralViewModel.ValidateDroppedPaths(paths);
        e.Effects = valid ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void UserControl_DragLeave(object sender, DragEventArgs e)
    {
        // Busy overlay is owned by the discovery state, not the drag state.
        if (_dropBusy) { e.Handled = true; return; }

        _isDragActive = false;

        var pos = e.GetPosition(this);
        bool outsideBounds = pos.X < 0 || pos.Y < 0 ||
                             pos.X > ActualWidth || pos.Y > ActualHeight;

        if (outsideBounds)
        {
            HideDragOverlay();
        }
        else
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)(() =>
            {
                if (!_isDragActive)
                    HideDragOverlay();
            }));
        }

        e.Handled = true;
    }

    private async void UserControl_Drop(object sender, DragEventArgs e)
    {
        if (_dropBusy) { e.Handled = true; return; }

        _isDragActive = false;
        HideDragOverlay();

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        if (DataContext is GeneralViewModel vm)
            await vm.HandleDropAsync(paths);
    }

    private void ShowDragOverlay(bool valid, string[]? paths)
    {
        var first = paths is { Length: > 0 } ? paths[0] : string.Empty;

        // Check for active bundle + special file combos -> show warning overlay
        bool isWarning = false;
        bool isBlocked = false;
        if (valid && !string.IsNullOrEmpty(first) && !Directory.Exists(first))
        {
            var ext = Path.GetExtension(first).ToLowerInvariant();
            if (ext is ".exe" or ".msi" or ".msp"
                && DataContext is GeneralViewModel vm1 && vm1.IsActiveBundle)
            {
                isWarning = true;
            }
            else if (ext == ".intunewin"
                && DataContext is GeneralViewModel vm2 && vm2.IsActiveBundle)
            {
                isBlocked = true;
            }
        }

        DragOverlayService.ApplyBlur(MainScroll);
        DragBusyIcon.Visibility = Visibility.Collapsed;

        if (isBlocked)
        {
            DragValidIcon.Visibility   = Visibility.Collapsed;
            DragInvalidIcon.Visibility = Visibility.Visible;
            DragWarningIcon.Visibility = Visibility.Collapsed;
            DragOverlayHint.Text       = "Cannot import .intunewin on an active bundle";
            DragOverlaySubHint.Text    = "Create a new bundle first, then drop the .intunewin file";
        }
        else if (isWarning)
        {
            DragValidIcon.Visibility   = Visibility.Collapsed;
            DragInvalidIcon.Visibility = Visibility.Collapsed;
            DragWarningIcon.Visibility = Visibility.Visible;
            DragOverlayHint.Text       = "Active bundle -- choose how to apply";
            DragOverlaySubHint.Text    = "Drop to select: Apply Full, Upgrade, or Extract Icon";
        }
        else
        {
            DragValidIcon.Visibility   = valid ? Visibility.Visible   : Visibility.Collapsed;
            DragInvalidIcon.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
            DragWarningIcon.Visibility = Visibility.Collapsed;
            DragOverlayHint.Text = valid ? "Drop to load" : "Unsupported file type";
            DragOverlaySubHint.Text = valid
                ? DragOverlayService.DescribeDropTarget(first)
                : "Accepted: EXE, MSI, MSP, INTUNEWIN, PNG, JPG, ICO, or a folder with Config.json";
        }

        DragOverlay.Visibility = Visibility.Visible;
    }

    private void HideDragOverlay()
    {
        DragOverlayService.HideOverlay(DragOverlay, MainScroll);
    }

    /// <summary>Clicking the icon preview box opens the Select Icon flow (same as the button).</summary>
    private void IconBox_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is GeneralViewModel vm && vm.SelectAppIconCommand.CanExecute(null))
            vm.SelectAppIconCommand.Execute(null);
    }

    /// <summary>
    /// Repurposes the drag overlay as a busy lock while the view-model runs
    /// dropped-file discovery off-thread: same blur + dim, sync glyph, live
    /// status text. The overlay grid's background intercepts clicks, so the
    /// form beneath can't be edited mid-discovery.
    /// </summary>
    private void ShowBusyOverlay(string status)
    {
        _dropBusy = true;
        _isDragActive = false;
        DragOverlayService.ApplyBlur(MainScroll);
        DragValidIcon.Visibility   = Visibility.Collapsed;
        DragInvalidIcon.Visibility = Visibility.Collapsed;
        DragWarningIcon.Visibility = Visibility.Collapsed;
        DragBusyIcon.Visibility    = Visibility.Visible;
        DragOverlayHint.Text       = status;
        DragOverlaySubHint.Text    = "Drops are paused while this file is read";
        DragOverlay.Visibility     = Visibility.Visible;
    }
}
