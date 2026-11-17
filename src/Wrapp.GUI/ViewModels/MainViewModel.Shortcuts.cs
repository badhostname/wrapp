using CommunityToolkit.Mvvm.Input;
using Wrapp.Services;

namespace Wrapp.ViewModels;

public partial class MainViewModel
{
    // -----------------------------------------------------------------------
    // Keyboard shortcuts (MainWindow.InputBindings). Each one runs the very
    // command its button runs and checks the very gate its button is bound
    // to, so a shortcut never saves, opens or creates anything the buttons
    // would not. The flows themselves are untouched: the save sheet, its
    // field warnings, the discard prompt all behave as they do from a click.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Ctrl+S. On the Settings page saves the settings when they have
    /// changes, then the bundle when it has changes (the save strip's two
    /// buttons and their gates); everywhere else, the bundle.
    /// </summary>
    [RelayCommand]
    private async Task SaveShortcutAsync()
    {
        var saved = false;

        if (IsSettingsSection && SettingsVm?.IsDirty == true && SaveSettingsWithFeedbackCommand.CanExecute(null))
        {
            AppLogger.Info("Shortcut Ctrl+S: saving settings");
            SaveSettingsWithFeedback();
            saved = true;
        }

        if (CanSaveBundle && SaveBundleCommand?.CanExecute(null) == true && SaveBundleWithFeedbackCommand.CanExecute(null))
        {
            AppLogger.Info("Shortcut Ctrl+S: saving bundle");
            await SaveBundleWithFeedback();
            saved = true;
        }

        if (!saved)
            AppLogger.Info("Shortcut Ctrl+S: nothing to save");
    }

    /// <summary>Ctrl+Alt+S. Save the bundle as, exactly as the toolbar button; never touches Settings.</summary>
    [RelayCommand]
    private async Task SaveAsShortcutAsync()
    {
        if (SaveBundleAsCommand?.CanExecute(null) != true)
        {
            AppLogger.Info("Shortcut Ctrl+Alt+S: Save As not available");
            return;
        }
        AppLogger.Info("Shortcut Ctrl+Alt+S: Save As");
        await SaveBundleAsCommand.ExecuteAsync(null);
    }

    /// <summary>Ctrl+O. Open a bundle, as the toolbar button (the unsaved-changes prompt included).</summary>
    [RelayCommand]
    private async Task OpenShortcutAsync()
    {
        if (!OpenCommand.CanExecute(null)) return;
        AppLogger.Info("Shortcut Ctrl+O: Open");
        await OpenCommand.ExecuteAsync(null);
    }

    /// <summary>Ctrl+N. New bundle, as the toolbar button (the unsaved-changes prompt included).</summary>
    [RelayCommand]
    private async Task NewShortcutAsync()
    {
        if (!NewCommand.CanExecute(null)) return;
        AppLogger.Info("Shortcut Ctrl+N: New");
        await NewCommand.ExecuteAsync(null);
    }
}
