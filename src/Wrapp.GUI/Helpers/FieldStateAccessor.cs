using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wrapp.Helpers;

/// <summary>
/// Per-field observable state - one instance per (entry, field-name) pair, owned
/// by <see cref="FieldStateProvider"/> and surfaced through
/// <see cref="FieldStateAccessor"/> so XAML can bind via
/// <c>{Binding FieldStates[FieldName].IsEnabled}</c>.
/// </summary>
public sealed partial class FieldState : ObservableObject
{
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isRequired;
    [ObservableProperty] private string? _disabledReason;
    [ObservableProperty] private string? _validationError;

    public Visibility Visibility => IsVisible ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsVisibleChanged(bool value) => OnPropertyChanged(nameof(Visibility));
}

/// <summary>
/// XAML-friendly indexer wrapper. The accessor lazily creates and caches a
/// <see cref="FieldState"/> per field name so WPF's binding subsystem keeps
/// listening to the same observable instance for the lifetime of the entry.
/// </summary>
public sealed class FieldStateAccessor
{
    private readonly Dictionary<string, FieldState> _states =
        new(StringComparer.OrdinalIgnoreCase);

    public FieldState this[string fieldName]
    {
        get
        {
            if (!_states.TryGetValue(fieldName, out var state))
            {
                state = new FieldState();
                _states[fieldName] = state;
            }
            return state;
        }
    }

    /// <summary>Enumerates currently-tracked states (used by the provider for resets).</summary>
    public IEnumerable<KeyValuePair<string, FieldState>> Tracked => _states;
}
