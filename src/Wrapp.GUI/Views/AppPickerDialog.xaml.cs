using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Views;

/// <summary>
/// Reusable app picker for selecting dependencies/supersedence targets.
/// Shows a searchable, multi-select list of apps from the inventory cache.
/// </summary>
public partial class AppPickerDialog : UserControl, INotifyPropertyChanged
{
    private readonly List<PickerItem> _allItems = new();

    public ObservableCollection<PickerItem> FilteredItems { get; } = new();

    public List<PickerItem> SelectedApps => _allItems.Where(i => i.IsChecked).ToList();

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set { _searchText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchText))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppPickerDialog(string platform, string targetKey, AppInventoryService inventoryService)
    {
        InitializeComponent();

        // Load from cache (no API call -- data was already loaded in Inventory view)
        if (platform == "Intune")
        {
            var cached = inventoryService.GetCachedIntuneApps(targetKey);
            if (cached is not null)
            {
                foreach (var app in cached.OrderBy(a => a.DisplayName))
                {
                    _allItems.Add(new PickerItem
                    {
                        Id = app.Id,
                        DisplayName = app.DisplayName,
                        Publisher = app.Publisher,
                        Version = app.AppVersion,
                    });
                }
            }
        }
        else
        {
            var cached = inventoryService.GetCachedSccmApps(targetKey);
            if (cached is not null)
            {
                foreach (var app in cached.OrderBy(a => a.Name))
                {
                    _allItems.Add(new PickerItem
                    {
                        Id = app.CI_ID,
                        DisplayName = app.Name,
                        Publisher = app.Manufacturer,
                        Version = app.SoftwareVersion,
                    });
                }
            }
        }

        ApplyFilter();
        AppListBox.ItemsSource = FilteredItems;
        UpdateStatus();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void CheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateStatus();
    }

    private void ApplyFilter()
    {
        FilteredItems.Clear();
        var query = SearchText.Trim().ToLowerInvariant();

        foreach (var item in _allItems)
        {
            if (string.IsNullOrEmpty(query) || item.SearchText.Contains(query))
                FilteredItems.Add(item);
        }

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var selected = _allItems.Count(i => i.IsChecked);
        StatusText.Text = selected > 0
            ? $"{selected} app(s) selected | {FilteredItems.Count} shown"
            : $"{FilteredItems.Count} app(s)";
    }
}

/// <summary>Lightweight item for the picker list with checkbox state.</summary>
public class PickerItem : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set { _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
    }

    public string SearchText => $"{DisplayName} {Publisher} {Version}".ToLowerInvariant();

    public event PropertyChangedEventHandler? PropertyChanged;
}
