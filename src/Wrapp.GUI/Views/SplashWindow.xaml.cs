using System.Threading.Tasks;
using System.Windows.Input;
using Wrapp.Models;
using Wrapp.Services;
using Wrapp.ViewModels;

namespace Wrapp.Views;

public partial class SplashWindow
{
    public SplashViewModel Vm { get; }

    private readonly TaskCompletionSource<bool> _done = new();
    private bool _locked;

    /// <summary>
    /// Awaitable result that completes when the user picks a config path or
    /// closes the window. True = config chosen, False = cancelled.
    /// </summary>
    public Task<bool> ResultTask => _done.Task;

    public SplashWindow()
    {
        InitializeComponent();
        Vm = new SplashViewModel();
        DataContext = Vm;

        // First window shown = the one the Win11 taskbar snapshots the icon
        // from; set the class icon before that snapshot (see TaskbarIconGuard).
        Helpers.TaskbarIconGuard.Attach(this);

        Vm.CloseRequested += () => _done.TrySetResult(Vm.DialogResult);

        // Block close while a workspace is being created or a bundle is loading
        WindowHelper.PreventCloseWhile(this, () => Vm.IsBusy);

        // If user closes the window via X button or Alt+F4
        Closed += (_, _) =>
        {
            Vm.StopMessages();
            _done.TrySetResult(false);
            // Closing the window mid-update-flow means "stop updating and
            // exit" — in the handoff path nobody awaits ResultTask, so drive
            // the shutdown explicitly. (During the final Restarting step
            // Shutdown is already in progress; a second call is harmless.)
            if (Vm.UpdateFlowEngaged)
            {
                Vm.UpdateCancelRequested = true;
                System.Windows.Application.Current.Shutdown();
            }
        };
    }

    // -------------------------------------------------------------------
    // Update mode (Phase D) — see UpdateFlowController
    // -------------------------------------------------------------------

    /// <summary>
    /// Update-mode content varies MID-FLOW: the waiting-for-windows card
    /// appears only when sibling windows are detected, the failure card and
    /// offer buttons come and go, and the hash block is three lines. A fixed
    /// height clips whichever combination is tallest (the 1.0.1 splash cut
    /// off the cancel link under the waiting card) — auto-size tracks the
    /// content instead; MinHeight keeps the frame from collapsing.
    /// </summary>
    private void EnterUpdateSizing() => SizeToContent = System.Windows.SizeToContent.Height;

    /// <summary>
    /// Startup path: flips to the update offer if the user hasn't committed
    /// to a bundle yet. Returns false when they were already mid-pick (the
    /// session proceeds; the action-needed indicator takes over).
    /// </summary>
    public bool TryEnterUpdateOffer(string targetVersion, AppSettings settings)
    {
        if (_locked || Vm.IsBusy || Vm.DialogResult || Vm.ShowFrameworkPicker || Vm.IsUpdateMode)
            return false;
        EnterUpdateSizing();
        Vm.EnterUpdateMode(targetVersion, settings, fromSession: false);
        return true;
    }

    /// <summary>Handoff path: this splash exists only to run the update.</summary>
    public void EnterUpdateHandoff(string targetVersion, AppSettings settings)
    {
        EnterUpdateSizing();
        Vm.EnterUpdateMode(targetVersion, settings, fromSession: true);
    }

    /// <summary>
    /// Failure policy: the startup path fails OPEN (back to the bundle cards
    /// — a broken feed must never lock a technician out); the handoff path
    /// has no cards left, so it shows the error with a close button.
    /// </summary>
    public void HandleUpdateFailure(string message)
    {
        AppLogger.Warn($"UpdateFlow: failed -- {message}");
        if (Vm.UpdateFromSession)
        {
            Vm.FailUpdate(message);
        }
        else
        {
            Vm.ExitUpdateMode("Update failed: " + message + " Continuing on the current version.");
            SizeToContent = System.Windows.SizeToContent.Manual;   // back to the fixed splash frame
            Height = MinHeight;
        }
    }

    private void UpdateNow_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var settings = Vm.UpdateSettings;
        if (settings is null) return;
        SafeFireAndForget.Run(
            () => Services.UpdateFlowController.RunSplashFlowAsync(this, settings),
            "splash-update-flow");
    }

    private void UpdateCloseApp_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        // Startup offer: resolves ResultTask false → App shuts down.
        // Handoff/failure: Closed handler drives the explicit shutdown.
        Close();
    }

    private void UpdateCancel_Click(object sender, MouseButtonEventArgs e)
    {
        Vm.UpdateCancelRequested = true;
        Vm.StatusText = "Cancelling — Wrapp will close without updating...";
    }

    /// <summary>
    /// Disables all card interaction and dims them. Called synchronously
    /// before any async work so no second click can sneak through.
    /// </summary>
    private void LockCards()
    {
        _locked = true;
        CardsArea.IsHitTestVisible = false;
        CardsArea.Opacity = 0.5;
    }

    /// <summary>
    /// Re-enables card interaction (e.g. after a cancelled file dialog or error).
    /// </summary>
    private void UnlockCards()
    {
        _locked = false;
        CardsArea.IsHitTestVisible = true;
        CardsArea.Opacity = 1.0;
    }

    private void BackButton_Click(object sender, MouseButtonEventArgs e)
    {
        if (_locked) return;
        Vm.ShowFrameworkPicker = false;
        Vm.SelectedActionKey = string.Empty;
        Vm.SelectedFrameworkKey = string.Empty;
        Vm.StatusText = string.Empty;
        Vm.StatusIsError = false;
    }

    private void NewBundleCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (_locked) return;
        Vm.SelectedActionKey = "new";
        if (Vm.NewPackageCommand.CanExecute(null))
            Vm.NewPackageCommand.Execute(null);
    }

    private async void OpenExistingCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (_locked) return;
        Vm.SelectedActionKey = "open";

        // Browse for folder before locking (the dialog is modal and blocks the UI)
        var folder = Services.FileDialogService.BrowseFolder("Select bundle folder containing Config.json");
        if (string.IsNullOrEmpty(folder))
        {
            Vm.SelectedActionKey = string.Empty;
            Vm.StatusIsError = false;
            Vm.StatusText = string.Empty;
            return;
        }

        var configPath = Services.BundleService.FindConfigJson(folder);
        if (configPath is null)
        {
            Vm.SelectedActionKey = string.Empty;
            Vm.StatusIsError = true;
            Vm.StatusText = "No Config.json found in the selected folder.";
            return;
        }

        // Valid path found -- lock cards and start loading
        LockCards();
        await Vm.OpenExistingCommand.ExecuteAsync(configPath);

        // If we're still here (error path), unlock
        if (!Vm.DialogResult) UnlockCards();
    }

    private async void AppeaseCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (_locked) return;
        LockCards();
        await Vm.CreateBundleWithFrameworkAsync(ScriptFramework.Appease);
        if (!Vm.DialogResult) UnlockCards();
    }

    private async void PsadtCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (_locked) return;
        LockCards();
        await Vm.CreateBundleWithFrameworkAsync(ScriptFramework.PSADT);
        if (!Vm.DialogResult) UnlockCards();
    }
}
