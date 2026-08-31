using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wrapp.Models;
using Wrapp.Services;
using Wrapp.Views;

namespace Wrapp.ViewModels;

public partial class GitHistoryViewModel : StatusViewModelBase
{
    private readonly GeneralViewModel _generalVm;
    private readonly DispatcherTimer _pollTimer;
    private string _lastKnownHash = string.Empty;

    [ObservableProperty] private ObservableCollection<CommitInfo> _commits = new();

    /// <summary>Forwards inherited <see cref="StatusViewModelBase.StatusText"/> under the legacy name bound by GitHistoryView.xaml.</summary>
    public string StatusMessage => StatusText;
    /// <summary>Forwards inherited <see cref="StatusViewModelBase.IsBusy"/> under the legacy name.</summary>
    public bool IsLoading => IsBusy;

    protected override void OnIsBusyChangedInternal(bool oldValue, bool newValue)
        => OnPropertyChanged(nameof(IsLoading));
    protected override void OnStatusTextChangedInternal(string oldValue, string newValue)
        => OnPropertyChanged(nameof(StatusMessage));

    public GitHistoryViewModel(GeneralViewModel generalVm)
    {
        _generalVm = generalVm;
        StatusText = "No package loaded.";

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pollTimer.Tick += async (_, _) => await PollForChangesAsync();
        // Timer starts on first ConfigLoaded, not here -- avoids wasted git
        // process spawns before any bundle is open.

        _generalVm.ConfigLoaded += (_, _) =>
        {
            // Config is loaded; polling itself starts only when the view is
            // visible (SetPollingActive) - hidden history spawns no git
            // processes. Track readiness so a later visibility flip can start.
            _configLoaded = true;
            if (_viewVisible && !_pollTimer.IsEnabled)
                _pollTimer.Start();
            // ConfigLoaded is a domain event that may be raised off the UI
            // thread, so an async-void handler's fault would escape to
            // AppDomain.UnhandledException (terminating). Route the refresh
            // through SafeFireAndForget, which try/catches + logs.
            SafeFireAndForget.Run(RefreshAsync, "githistory-config-loaded-refresh");
        };
    }

    private bool _pollRunning;
    private bool _configLoaded;
    private bool _viewVisible;

    /// <summary>
    /// Called by GitHistoryView on visibility changes: the 5s git poll runs
    /// only while the view is on screen. Becoming visible triggers an
    /// immediate catch-up poll so the list is never stale when looked at.
    /// </summary>
    public void SetPollingActive(bool visible)
    {
        _viewVisible = visible;
        if (visible && _configLoaded)
        {
            _pollTimer.Start();
            SafeFireAndForget.Run(PollForChangesAsync, "githistory-visible-poll");
        }
        else
        {
            _pollTimer.Stop();
        }
    }

    private async Task PollForChangesAsync()
    {
        if (_pollRunning) return;
        _pollRunning = true;
        try
        {
            var dir = _generalVm.GitRepoDir;
            if (string.IsNullOrEmpty(dir) || !GitService.IsGitRepo(dir)) return;

            // Auto-commit any external working-directory changes (files added/modified/deleted
            // outside the GUI) so they appear in the history immediately.
            // skipIfBusy: a missed poll tick is fine - next tick picks it up. Blocking
            // here would dog-pile on a user save already in flight.
            if (await GitService.HasChangesAsync(dir))
            {
                await GitService.CommitAllAsync(dir, "External changes detected", skipIfBusy: true);
                AppLogger.Info("Git: auto-committed external changes");
            }

            var latestHash = await GitService.GetLatestCommitHashAsync(dir);
            if (latestHash == _lastKnownHash) return;

            await RefreshAsync();
        }
        finally
        {
            _pollRunning = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var dir = _generalVm.GitRepoDir;
        if (string.IsNullOrEmpty(dir) || !GitService.IsGitRepo(dir))
        {
            Commits.Clear();
            _lastKnownHash = string.Empty;
            StatusText = "No git history available.";
            return;
        }

        await RunBusyAsync(async () =>
        {
            var log = await GitService.GetCommitLogAsync(dir);
            Commits = new ObservableCollection<CommitInfo>(log);
            _lastKnownHash = log.Count > 0 ? log[0].FullHash : string.Empty;
            StatusText = $"{log.Count} commit(s)";
        }, "Git history refresh");
    }

    [RelayCommand]
    private async Task ViewCommitAsync(CommitInfo? commit)
    {
        if (commit is null) return;
        var dir = _generalVm.GitRepoDir;
        if (string.IsNullOrEmpty(dir)) return;

        var files = commit.FileChanges.Count > 0
            ? commit.FileChanges
            : await GitService.GetCommitFilesAsync(dir, commit.FullHash);
        var window = new DiffWindow(commit, files, dir);
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
    }
}
