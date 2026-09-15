using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Services = Wrapp.Services;

namespace Wrapp.ViewModels;

public partial class LogsViewModel : ObservableObject
{
    private readonly ObservableCollection<Services.LogEntry> _entries = new();

    public ICollectionView FilteredEntries { get; }

    public string LogFilePath => Services.AppLogger.LogPath;

    [ObservableProperty] private string _filterText = string.Empty;

    partial void OnFilterTextChanged(string value) => FilteredEntries.Refresh();

    public LogsViewModel()
    {
        FilteredEntries = CollectionViewSource.GetDefaultView(_entries);
        FilteredEntries.Filter = FilterPredicate;

        // Use BeginInvoke (fire-and-forget) not Invoke: AppLogger raises
        // EntryLogged inline from the producer thread, so a blocking Invoke
        // from a hot log path can stall whichever thread is producing.
        Services.AppLogger.EntryLogged += (_, e) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _entries.Add(e);
                while (_entries.Count > 2000)
                    _entries.RemoveAt(0);
            });
        };
    }

    private bool FilterPredicate(object obj)
    {
        if (string.IsNullOrWhiteSpace(FilterText)) return true;
        if (obj is not Services.LogEntry entry) return false;
        return entry.Message.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
            || entry.Level.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        var count = _entries.Count;
        if (count == 0) return;
        var confirmed = await Services.FluentDialog.ConfirmAsync(
            "Clear log view",
            $"Clear {count} entr{(count == 1 ? "y" : "ies")} from the view?\n\n" +
            "Only this view is cleared - the log files on disk are not affected.",
            "Clear", "Cancel");
        if (confirmed) _entries.Clear();
    }

    [RelayCommand]
    private void ClearFilter() => FilterText = string.Empty;

    [RelayCommand]
    private void OpenLogFolder()
    {
        var dir = Path.GetDirectoryName(LogFilePath);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }
}
