using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Wrapp.Helpers;

/// <summary>
/// Two-level subscription for a child collection: raises one callback for
/// membership changes (propertyName null) and for any item's PropertyChanged.
/// Package entries use it to keep their aggregate Error/WarningCount live
/// while an assignment/deployment is edited - the piece that was missing
/// when badges only refreshed on dialog close.
/// <para>Re-<see cref="Attach"/>ing (the [ObservableProperty] collection was
/// replaced, e.g. on bundle load) unhooks the previous collection and every
/// previously tracked item.</para>
/// </summary>
public sealed class ChildCollectionWatcher
{
    private readonly Action<string?> _onChildChanged;
    private INotifyCollectionChanged? _collection;
    private readonly List<INotifyPropertyChanged> _items = new();

    public ChildCollectionWatcher(Action<string?> onChildChanged)
        => _onChildChanged = onChildChanged;

    public void Attach(IEnumerable? items)
    {
        if (_collection is not null)
            _collection.CollectionChanged -= OnCollectionChanged;
        foreach (var item in _items)
            item.PropertyChanged -= OnItemPropertyChanged;
        _items.Clear();
        _collection = null;

        if (items is null) return;
        if (items is INotifyCollectionChanged ncc)
        {
            _collection = ncc;
            ncc.CollectionChanged += OnCollectionChanged;
        }
        SubscribeAll(items);
    }

    private void SubscribeAll(IEnumerable items)
    {
        foreach (var item in items)
            if (item is INotifyPropertyChanged inpc)
            {
                inpc.PropertyChanged += OnItemPropertyChanged;
                _items.Add(inpc);
            }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // Reset gives no item deltas - rebuild subscriptions from scratch.
            foreach (var item in _items)
                item.PropertyChanged -= OnItemPropertyChanged;
            _items.Clear();
            if (sender is IEnumerable all) SubscribeAll(all);
        }
        else
        {
            if (e.OldItems is not null)
                foreach (var item in e.OldItems)
                    if (item is INotifyPropertyChanged inpc)
                    {
                        inpc.PropertyChanged -= OnItemPropertyChanged;
                        _items.Remove(inpc);
                    }
            if (e.NewItems is not null)
                foreach (var item in e.NewItems)
                    if (item is INotifyPropertyChanged inpc)
                    {
                        inpc.PropertyChanged += OnItemPropertyChanged;
                        _items.Add(inpc);
                    }
        }
        _onChildChanged(null);
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => _onChildChanged(e.PropertyName);
}
