using CommunityToolkit.Mvvm.ComponentModel;

namespace Wrapp.Models;

/// <summary>
/// Represents the real-time connectivity state for one deployment target
/// (Intune or SCCM). Bound to the connection status panel in RunView.
/// </summary>
public partial class ConnectionStatus : ObservableObject
{
    [ObservableProperty] private string _targetName = string.Empty;
    [ObservableProperty] private string _tenantId = string.Empty;
    [ObservableProperty] private ConnectionState _state = ConnectionState.Unknown;
    [ObservableProperty] private string _statusText = "Not checked";
    [ObservableProperty] private string _detailLine1 = string.Empty;
    [ObservableProperty] private string _detailLine2 = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private DateTime? _tokenExpiresUtc;
    [ObservableProperty] private string _tokenCountdown = string.Empty;

    /// <summary>
    /// Run-time toggle: when false the tenant/site is skipped during execution.
    /// Defaults to true. Not persisted -- resets each session.
    /// </summary>
    [ObservableProperty] private bool _isEnabled = true;
}

public enum ConnectionState
{
    Unknown,
    Checking,
    Connected,
    Disconnected,
    Skipped,
    Error
}
