using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wrapp.Helpers;

/// <summary>
/// Observable "any item selected" flag for a collection of checkbox-style
/// rows. Bind the <see cref="HasSelected"/> property to the
/// <c>IsEnabled</c> attribute of a "Remove Selected" button so the button
/// disables itself when nothing is ticked - matching the convention used in
/// <c>DetectionViewModel.RefreshHasAnySelected</c>.
///
/// <para>The tracker subscribes to the collection's <c>CollectionChanged</c>
/// event plus each item's <c>PropertyChanged</c>. Call <see cref="Bind"/>
/// (with null to detach) when the source collection itself swaps out - for
/// example when <c>SelectedPackage</c> changes in the Intune / SCCM views and
/// the per-package Categories / ScopeTags / Dependencies lists change
/// identity.</para>
/// </summary>
public sealed partial class SelectionTracker<T> : ObservableObject, IDisposable
    where T : class, INotifyPropertyChanged
{
    [ObservableProperty] private bool _hasSelected;

    private readonly Func<T, bool> _isSelected;
    private readonly string _selectedPropertyName;
    private ObservableCollection<T>? _current;

    /// <summary>
    /// <paramref name="isSelected"/> reads the "selected" flag off an item
    /// (typically <c>t => t.IsSelected</c>). <paramref name="selectedPropertyName"/>
    /// is the property name to listen for on each item's PropertyChanged
    /// event; defaults to <c>"IsSelected"</c>.
    /// </summary>
    public SelectionTracker(Func<T, bool> isSelected, string selectedPropertyName = "IsSelected")
    {
        _isSelected = isSelected;
        _selectedPropertyName = selectedPropertyName;
    }

    /// <summary>
    /// Points the tracker at a new source collection. Pass <c>null</c> to
    /// clear (sets <see cref="HasSelected"/> to false and unsubscribes). Safe
    /// to call with the same collection repeatedly.
    /// </summary>
    public void Bind(ObservableCollection<T>? collection)
    {
        if (ReferenceEquals(_current, collection))
        {
            Refresh();
            return;
        }

        if (_current is not null)
        {
            _current.CollectionChanged -= OnCollectionChanged;
            foreach (var item in _current) item.PropertyChanged -= OnItemPropertyChanged;
        }

        _current = collection;

        if (_current is not null)
        {
            _current.CollectionChanged += OnCollectionChanged;
            foreach (var item in _current) item.PropertyChanged += OnItemPropertyChanged;
        }

        Refresh();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (T item in e.OldItems) item.PropertyChanged -= OnItemPropertyChanged;
        if (e.NewItems is not null)
            foreach (T item in e.NewItems) item.PropertyChanged += OnItemPropertyChanged;
        Refresh();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == _selectedPropertyName) Refresh();
    }

    private void Refresh()
        => HasSelected = _current?.Any(_isSelected) ?? false;

    public void Dispose() => Bind(null);
}
