using CommunityToolkit.Mvvm.ComponentModel;

namespace Wrapp.ViewModels;

/// <summary>
/// Checkbox item for per-package tenant/site targeting.
/// Shared by <c>IntuneViewModel</c> (tenants) and <c>SCCMViewModel</c> (sites).
/// </summary>
public partial class TargetCheckItem : ObservableObject
{
    [ObservableProperty] private string _key = string.Empty;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private bool _isChecked;
}
