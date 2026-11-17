using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wrapp.Helpers;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.ViewModels;

public partial class GeneralViewModel
{
    // -----------------------------------------------------------------------
    // Bundle notices: what the real folder and the config disagree on,
    // computed on open and after every save (Helpers/BundleNotices). A count
    // on the General nav item, a list at the top of the view, one-click
    // fixes. Nothing is stored; a bundle moved in Explorer is looked at again.
    // -----------------------------------------------------------------------

    public ObservableCollection<BundleNotice> BundleNotices { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBundleNotices))]
    private int _bundleNoticeCount;

    [ObservableProperty]
    private int _bundleNoticeWarningCount;

    public bool HasBundleNotices => BundleNoticeCount > 0;

    /// <summary>A notice's fix wants another view (the Intune Output card); MainViewModel navigates.</summary>
    public event Action<NavigationSection>? NavigateRequested;

    private int _noticeRun;

    /// <summary>Recomputes the notices off the UI thread; the newest request wins.</summary>
    public async Task RefreshBundleNoticesAsync()
    {
        var run = ++_noticeRun;
        var root = HasConfig && !IsTempWorkspace() ? BundleRootDir : null;
        var configPath = _configPath;
        var config = _config;
        var settings = _settings;

        IReadOnlyList<BundleNotice> notices;
        try
        {
            notices = await Task.Run(() => Helpers.BundleNotices.Evaluate(root, configPath, config, settings));
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Bundle notices: not evaluated - {ex.Message}");
            notices = Array.Empty<BundleNotice>();
        }
        if (run != _noticeRun) return;

        BundleNotices.Clear();
        foreach (var n in notices) BundleNotices.Add(n);
        BundleNoticeCount = notices.Count;
        BundleNoticeWarningCount = notices.Count(n => n.IsWarning);
        if (notices.Count > 0)
            AppLogger.Info($"Bundle notices: {string.Join(" | ", notices.Select(n => n.Id))}");
    }

    private void ScheduleBundleNoticesRefresh()
        => SafeFireAndForget.Run(RefreshBundleNoticesAsync, "bundle-notices");

    [RelayCommand]
    private async Task ApplyNoticeFixAsync(BundleNotice? notice)
    {
        if (notice is null) return;
        switch (notice.Fix)
        {
            case BundleNoticeFix.SetVersionFromFolder when !string.IsNullOrEmpty(notice.Data):
            {
                var oldDot = App.DotVersion;
                var oldUnderscore = App.Version;
                App.DotVersion = notice.Data;
                App.Version = notice.Data.Replace('.', '_');
                ReplicateVersionToPackages(oldDot, oldUnderscore);
                StatusText = $"Version set to {notice.Data} from the folder name; save to keep it.";
                AppLogger.Info($"Bundle notices: version {oldDot} -> {notice.Data} from folder");
                await RefreshBundleNoticesAsync();
                break;
            }
            case BundleNoticeFix.CopyToConventionalFolder:
            {
                var dir = await ShowSaveSheetAsync("Copy to the conventional folder", App, SaveTargetMode.Convention);
                if (dir is null) return;
                if (!await ConfirmOverwriteAsync(dir)) return;
                await SaveBundleToDirectoryAsync(dir, BundleRootDir, "Copy");
                break;
            }
            case BundleNoticeFix.OpenIntuneOutput:
                NavigateRequested?.Invoke(NavigationSection.Intune);
                break;
        }
    }
}
