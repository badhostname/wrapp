using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wrapp.Models;
using Wrapp.Services;
using Microsoft.Identity.Client;

namespace Wrapp.ViewModels;

/// <summary>
/// Represents a cached MSAL account shown in the account flyout.
/// </summary>
public sealed class CachedAccountItem
{
    public string Username { get; init; } = string.Empty;
    public string TenantDisplay { get; init; } = string.Empty;
    public string Initials { get; init; } = string.Empty;
    public string HomeAccountId { get; init; } = string.Empty;
    /// <summary>Auth status line: "WAM | Microsoft Graph PowerShell | 1h 23m remaining" or "Expired".</summary>
    public string TokenStatus { get; init; } = string.Empty;

    /// <summary>The underlying MSAL account for silent token acquisition.</summary>
    public IAccount? MsalAccount { get; init; }
}

/// <summary>
/// Manages the sign-in button state, account flyout, and account switching.
/// Lives in the title bar of the main window.
/// </summary>
public partial class AccountViewModel : StatusViewModelBase
{
    private readonly MsalAuthService  _authService;
    private readonly GeneralViewModel _generalVm;
    private readonly PowerShellService _ps;
    private readonly AppSettings _appSettings;

    /// <summary>Set by App.xaml.cs so we can suggest adding unconfigured tenants.</summary>
    public TenantsViewModel? TenantsVm { get; set; }

    /// <summary>
    /// Raised when the operator accepts adding a signed-in tenant to the
    /// configuration. SettingsViewModel subscribes (it owns the preferences
    /// list AND the save path), so the discovered tenant also reaches the
    /// technician's saved tenants instead of living only in this bundle.
    /// An event rather than a direct reference keeps the two view-models
    /// independent — wired in CompositionRoot like the other Wire* hookups.
    /// </summary>
    public event Action<IntuneTenantEntry>? TenantDiscovered;

    private Services.DevOpsAuthService? _devOpsAuth;

    /// <summary>Wire DevOps auth for the account popup.</summary>
    public void WireDevOpsAuth(Services.DevOpsAuthService devOpsAuth)
    {
        _devOpsAuth = devOpsAuth;
        // Check if already signed in from MSAL cache. Logged via
        // SafeFireAndForget so a DevOps endpoint outage during startup
        // surfaces in app.log instead of vanishing into a discard.
        SafeFireAndForget.Run(RefreshDevOpsStatusAsync, "AccountVM.RefreshDevOpsStatus");
    }

    [ObservableProperty]
    private string _devOpsAuthStatus = "Not connected";

    [ObservableProperty]
    private string _devOpsUsername = "";

    [ObservableProperty]
    private string _devOpsInitials = "";

    [ObservableProperty]
    private string _devOpsDetail = "";

    [ObservableProperty]
    private bool _isDevOpsSignedIn;

    private Models.MsalTokenResult? _lastDevOpsToken;

    /// <summary>Known OAuth client IDs mapped to their display names.</summary>
    private static readonly Dictionary<string, string> KnownClientApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["14d82eec-204b-4c2f-b7e8-296a70dab67e"] = "Microsoft Graph PowerShell",
        ["872cd9fa-d31f-45e0-9eab-6e460a02d1f1"] = "Visual Studio",
        ["04b07795-8ddb-461a-bbee-02f9e1bf7b46"] = "Azure CLI",
        ["1950a258-227b-4e31-a9cf-717495945fc2"] = "Microsoft Azure PowerShell",
    };

    /// <summary>Resolves a client ID to a friendly display name.</summary>
    public static string ResolveClientAppName(string clientId) =>
        KnownClientApps.TryGetValue(clientId, out var name) ? name : clientId;

    /// <summary>Formatted title from the Key Vault repo URL: "Org - Project - Repo".</summary>
    public string DevOpsRepoTitle
    {
        get
        {
            var url = _appSettings.KeyVaultRepoUrl?.Trim() ?? "";
            if (string.IsNullOrEmpty(url)) return "Azure DevOps Key Vault";
            try
            {
                var segments = new Uri(url).AbsolutePath.Trim('/').Split('/');
                if (segments.Length >= 4 && segments[2] == "_git")
                    return $"{segments[0]} - {segments[1]} - {segments[3]}";
            }
            // must-stay-silent: malformed URL means user hasn't configured the
            // DevOps vault yet. Falls back to the generic title; logging here
            // would fire on every UI refresh before configuration.
            catch { }
            return "Azure DevOps Key Vault";
        }
    }

    private System.Collections.ObjectModel.ObservableCollection<IntuneTenantEntry>? _trackedTenants;

    public AccountViewModel(MsalAuthService authService, GeneralViewModel generalVm, PowerShellService ps, AppSettings appSettings)
    {
        _authService = authService;
        _generalVm   = generalVm;
        _ps          = ps;
        _appSettings = appSettings;

        // Re-evaluate tenant mapping when the config or tenant list changes
        generalVm.ConfigLoaded += (_, _) => WireTenantCollection();
        WireTenantCollection();
    }

    /// <summary>
    /// Subscribes to the current config's IntuneTenants CollectionChanged so
    /// we can re-evaluate the config mapping when tenants are added or removed.
    /// </summary>
    private void WireTenantCollection()
    {
        if (_trackedTenants is not null)
            _trackedTenants.CollectionChanged -= OnTenantsCollectionChanged;

        _trackedTenants = _generalVm.FullConfig.IntuneTenants;
        _trackedTenants.CollectionChanged += OnTenantsCollectionChanged;
        ReevaluateConfigMapping();
    }

    private void OnTenantsCollectionChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        ReevaluateConfigMapping();
    }

    /// <summary>
    /// Checks whether the currently authenticated tenant ID still matches
    /// a configured tenant entry and updates ConfigTenantKey accordingly.
    /// </summary>
    private void ReevaluateConfigMapping()
    {
        if (_lastTokenResult is null) return;

        var matchKey = string.Empty;
        foreach (var t in _generalVm.FullConfig.IntuneTenants)
        {
            if (string.Equals(t.Key, _lastTokenResult.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                matchKey = !string.IsNullOrWhiteSpace(t.Name) ? t.Name : t.Key;
                break;
            }
        }
        ConfigTenantKey = matchKey;
    }

    // -------------------------------------------------------------------
    // Signed-in state
    // -------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonLabel))]
    [NotifyPropertyChangedFor(nameof(ShowInitials))]
    [NotifyPropertyChangedFor(nameof(ShowPersonIcon))]
    private bool _isSignedIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonLabel))]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _userPrincipalName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInitials))]
    [NotifyPropertyChangedFor(nameof(ShowPersonIcon))]
    private string _initials = string.Empty;

    /// <summary>Actual tenant display name resolved from Graph API.</summary>
    [ObservableProperty]
    private string _tenantDisplayName = string.Empty;

    [ObservableProperty]
    private string _authFlowDisplay = string.Empty;

    /// <summary>
    /// Forwards the inherited <see cref="StatusViewModelBase.IsBusy"/> under
    /// the domain-specific name. Internal call sites assign <c>IsBusy</c>
    /// (or use <c>RunBusyAsync</c>); this alias keeps any future XAML binding
    /// or external read working without a rename.
    /// </summary>
    public bool IsAuthenticating => IsBusy;

    /// <summary>
    /// Bridge the inherited <see cref="StatusViewModelBase.IsBusy"/> change
    /// notification to the legacy <see cref="IsAuthenticating"/> property name
    /// so any binding or observer wired against the old name still fires.
    /// </summary>
    protected override void OnIsBusyChangedInternal(bool oldValue, bool newValue)
        => OnPropertyChanged(nameof(IsAuthenticating));

    [ObservableProperty]
    private bool _isPopupOpen;

    /// <summary>Label on the title bar button: display name when signed in, "Sign in" otherwise.</summary>
    public string ButtonLabel => IsSignedIn ? DisplayName : "Sign in";

    /// <summary>Show initials circle when signed in and initials are available.</summary>
    public bool ShowInitials => IsSignedIn && !string.IsNullOrEmpty(Initials);

    /// <summary>Show person icon when not signed in or no initials.</summary>
    public bool ShowPersonIcon => !ShowInitials;

    // -------------------------------------------------------------------
    // Token detail properties (extracted from JWT claims)
    // -------------------------------------------------------------------

    [ObservableProperty]
    private string _appDisplayName = string.Empty;

    [ObservableProperty]
    private string _appId = string.Empty;

    [ObservableProperty]
    private string _tokenExpiry = string.Empty;

    /// <summary>The config Key of the matching tenant, or empty if not found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTenantInConfig))]
    private string _configTenantKey = string.Empty;

    /// <summary>True when the connected tenant ID matches a configured tenant.</summary>
    public bool IsTenantInConfig => !string.IsNullOrEmpty(ConfigTenantKey);

    /// <summary>Last acquired token result, kept for detail display and refresh.</summary>
    private MsalTokenResult? _lastTokenResult;

    /// <summary>
    /// Timer that checks token expiry every 60 seconds and silently refreshes
    /// when the token is within 15 minutes of expiring.
    /// </summary>
    private DispatcherTimer? _autoRefreshTimer;

    /// <summary>Decoded access token claims (kept in memory for dialog display).</summary>
    private List<KeyValuePair<string, string>> _accessTokenClaims = new();

    /// <summary>Decoded id token claims (kept in memory for dialog display).</summary>
    private List<KeyValuePair<string, string>> _idTokenClaims = new();

    // -------------------------------------------------------------------
    // Cached accounts and configured tenants for the flyout
    // -------------------------------------------------------------------

    public ObservableCollection<CachedAccountItem> CachedAccounts { get; } = new();

    // -------------------------------------------------------------------
    // Commands
    // -------------------------------------------------------------------

    [RelayCommand]
    private async Task TogglePopupAsync()
    {
        if (IsPopupOpen)
        {
            IsPopupOpen = false;
            return;
        }

        // Refresh cached accounts before showing the flyout
        await RefreshCachedAccountsAsync();

        IsPopupOpen = true;
    }

    [RelayCommand]
    private Task SignInAsync()
    {
        IsPopupOpen = false;
        // If a tenant is configured, use its settings; otherwise connect
        // with /organizations authority (tenant discovered from auth result)
        return RunAuthFlowAsync(
            tenant: GetFirstIntuneTenant(),
            acquire: () => _authService.AcquireTokenAsync(),
            opContext: "Account sign-in");
    }

    /// <summary>
    /// Standard shape for an Account-VM auth command:
    /// <list type="number">
    /// <item><description>Initialize MSAL for the supplied tenant + the main-window HWND.</description></item>
    /// <item><description>Acquire a token via the supplied <paramref name="acquire"/> delegate.</description></item>
    /// <item><description>Apply the result via <see cref="ApplyTokenResultAsync"/> (which also seeds the PowerShell runspace).</description></item>
    /// <item><description>Optionally set <see cref="StatusViewModelBase.StatusText"/> on success.</description></item>
    /// </list>
    /// All wrapped in <see cref="StatusViewModelBase.RunBusyAsync"/> so the
    /// busy / status / error surface follows the standard contract.
    /// Replaces a recurring 6-line inner-lambda pattern across SignIn /
    /// SwitchAccount / RefreshToken.
    /// </summary>
    private Task RunAuthFlowAsync(
        IntuneTenantEntry? tenant,
        Func<Task<MsalTokenResult>> acquire,
        string opContext,
        string? successStatus = null,
        AuthFlow? authFlowOverride = null)
        => RunBusyAsync(async () =>
        {
            await _authService.InitializeForTenantAsync(tenant, WindowHelper.GetMainWindowHwnd(), authFlowOverride);
            var result = await acquire();
            await ApplyTokenResultAsync(result);
            if (successStatus is not null) StatusText = successStatus;
        }, opContext);

    [RelayCommand]
    private async Task SignOutAsync()
    {
        IsPopupOpen = false;
        try
        {
            await _authService.SignOutAsync();
            // Scrub any token left in the ambient runspace pool on sign-out
            // (previously nothing cleared it -- a stale, though inert, token
            // could linger). Cheap clear; no pool dispose.
            _ps.ClearAmbientPoolToken();
            StopAutoRefreshTimer();
            IsSignedIn = false;
            DisplayName = string.Empty;
            UserPrincipalName = string.Empty;
            Initials = string.Empty;
            TenantDisplayName = string.Empty;
            AuthFlowDisplay = string.Empty;
            StatusText = string.Empty;
            ConfigTenantKey = string.Empty;
            AppDisplayName = string.Empty;
            AppId = string.Empty;
            TokenExpiry = string.Empty;
            _lastTokenResult = null;
            _accessTokenClaims.Clear();
            _idTokenClaims.Clear();
            CachedAccounts.Clear();
        }
        catch (Exception ex)
        {
            StatusText = $"Sign-out failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private Task SwitchAccountAsync(CachedAccountItem? item)
    {
        if (item?.MsalAccount is null) return Task.CompletedTask;

        IsPopupOpen = false;
        // WAM needs a valid parent window handle for the interactive fallback
        // dialog if the silent path requires re-consent.
        return RunAuthFlowAsync(
            tenant: GetFirstIntuneTenant(),
            acquire: () => _authService.AcquireTokenForAccountAsync(item.MsalAccount),
            opContext: "Account switch");
    }

    [RelayCommand]
    private Task SignInDifferentAccountAsync()
    {
        IsPopupOpen = false;
        // Force AuthFlow.Interactive regardless of the tenant's saved
        // preference so the WAM account picker always appears.
        return RunAuthFlowAsync(
            tenant: GetFirstIntuneTenant(),
            acquire: () => _authService.AcquireInteractiveForceAsync(),
            opContext: "Account sign-in (different account)",
            authFlowOverride: AuthFlow.Interactive);
    }

    [RelayCommand]
    private async Task ForgetAccountAsync(CachedAccountItem? item)
    {
        if (item?.MsalAccount is null) return;
        await _authService.ForgetAccountAsync(item.MsalAccount);
        CachedAccounts.Remove(item);
    }

    // -----------------------------------------------------------------------
    // Azure DevOps auth (Key Vault)
    // -----------------------------------------------------------------------

    [RelayCommand]
    private async Task SignInDevOpsAsync()
    {
        if (_devOpsAuth is null) return;
        try
        {
            DevOpsAuthStatus = "Signing in via browser...";
            var result = await _devOpsAuth.AcquireTokenAsync();
            IsDevOpsSignedIn = result is not null;
            if (result is not null)
                ApplyDevOpsToken(result);
            else
                DevOpsAuthStatus = "Sign-in failed";
        }
        catch (OperationCanceledException)
        {
            DevOpsAuthStatus = "Cancelled";
        }
        catch (Exception ex)
        {
            DevOpsAuthStatus = $"Error: {ex.Message}";
            AppLogger.Warn($"DevOps sign-in failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SignOutDevOpsAsync()
    {
        if (_devOpsAuth is null) return;
        await _devOpsAuth.SignOutAsync();
        IsDevOpsSignedIn = false;
        _lastDevOpsToken = null;
        DevOpsAuthStatus = "Not connected";
        DevOpsUsername = "";
        DevOpsInitials = "";
        DevOpsDetail = "";
    }

    [RelayCommand]
    private async Task ShowDevOpsTokenDetailsAsync()
    {
        IsPopupOpen = false;
        if (_lastDevOpsToken is null) return;
        var claims = Services.JwtDecoder.DecodePayloadForDisplay(_lastDevOpsToken.AccessToken);
        var content = BuildClaimsGrid(claims);
        await FluentDialog.ShowContentAsync("DevOps Access Token Claims", content);
    }

    [RelayCommand]
    private async Task RefreshDevOpsTokenAsync()
    {
        if (_devOpsAuth is null) return;
        try
        {
            var token = await _devOpsAuth.AcquireTokenAsync();
            if (token is not null)
            {
                IsDevOpsSignedIn = true;
                ApplyDevOpsToken(token);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"DevOps token refresh failed: {ex.Message}");
        }
    }

    private async Task RefreshDevOpsStatusAsync()
    {
        if (_devOpsAuth is null) return;
        try
        {
            var token = await _devOpsAuth.TryAcquireTokenSilentAsync();
            IsDevOpsSignedIn = token is not null;
            if (token is not null)
                ApplyDevOpsToken(token);
            else
                DevOpsAuthStatus = "Not connected";
        }
        catch
        {
            DevOpsAuthStatus = "Not connected";
        }
    }

    private void ApplyDevOpsToken(Models.MsalTokenResult token)
    {
        _lastDevOpsToken = token;
        var upn = token.UserPrincipalName ?? "";
        DevOpsUsername = upn;
        DevOpsInitials = ExtractInitials(upn);

        var remaining = token.ExpiresOnUtc - SystemClock.UtcNow;
        var expiry = remaining.TotalMinutes > 0
            ? remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m remaining"
                : $"{remaining.Minutes}m remaining"
            : "Expired";
        var appName = ResolveClientAppName(token.ClientId);
        DevOpsDetail = $"Browser | {appName} | {expiry}";
        DevOpsAuthStatus = upn;
    }

    [RelayCommand]
    private Task RefreshTokenAsync()
        => RunAuthFlowAsync(
            tenant: GetCurrentTenant(),
            acquire: () => _authService.AcquireTokenAsync(forceRefresh: true),
            opContext: "Token refresh",
            successStatus: "Token refreshed.");

    [RelayCommand]
    private async Task ShowAccessTokenDetailsAsync()
    {
        IsPopupOpen = false;
        if (_accessTokenClaims.Count == 0) return;
        var content = BuildClaimsGrid(_accessTokenClaims);
        await FluentDialog.ShowContentAsync("Access Token Claims", content);
    }

    [RelayCommand]
    private async Task ShowIdTokenDetailsAsync()
    {
        IsPopupOpen = false;
        if (_idTokenClaims.Count == 0) return;
        var content = BuildClaimsGrid(_idTokenClaims);
        await FluentDialog.ShowContentAsync("Id Token Claims", content);
    }

    [RelayCommand]
    private async Task SuggestTenantToConfigAsync()
    {
        if (_lastTokenResult is null || TenantsVm is null) return;
        IsPopupOpen = false;

        var tenantId = _lastTokenResult.TenantId;
        var tenantName = TenantDisplayName;
        if (string.IsNullOrEmpty(tenantName)) tenantName = tenantId;

        var confirmed = await FluentDialog.ConfirmAsync(
            "Add Tenant",
            $"Add \"{tenantName}\" ({tenantId})?\n\n"
            + "It is added to this bundle's tenant list and to your saved tenants "
            + "(Settings → Intune), so the two stay aligned.",
            "Add Tenant", "Cancel");

        if (!confirmed) return;

        var entry = new IntuneTenantEntry
        {
            Key      = tenantId,
            Name     = tenantName,
            AuthFlow = AuthFlow.Interactive
        };
        TenantsVm.IntuneTenants.Add(entry);
        TenantsVm.SelectedIntuneTenant = entry;

        // Also reaches the technician's saved tenants -- previously the entry
        // landed only in the bundle while the status text claimed otherwise.
        TenantDiscovered?.Invoke(entry);

        // Re-evaluate config match
        ConfigTenantKey = tenantName;
        StatusText = $"Tenant \"{tenantName}\" added to this bundle and your saved tenants.";
    }

    // -------------------------------------------------------------------
    // Automatic token refresh
    // -------------------------------------------------------------------

    private void StartAutoRefreshTimer()
    {
        _autoRefreshTimer?.Stop();
        _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
        _autoRefreshTimer.Start();
    }

    private void StopAutoRefreshTimer()
    {
        _autoRefreshTimer?.Stop();
        _autoRefreshTimer = null;
    }

    /// <summary>Fresh countdown strings the moment the popup opens.</summary>
    partial void OnIsPopupOpenChanged(bool value)
    {
        if (value) RefreshCountdownDisplays();
    }

    private void RefreshCountdownDisplays()
    {
        if (_lastTokenResult is not null)
        {
            var remaining = _lastTokenResult.ExpiresOnUtc - SystemClock.UtcNow;
            TokenExpiry = remaining.TotalMinutes > 0
                ? remaining.TotalHours >= 1
                    ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m remaining"
                    : $"{remaining.Minutes}m remaining"
                : "Expired";
        }

        if (_lastDevOpsToken is not null)
        {
            var devRemaining = _lastDevOpsToken.ExpiresOnUtc - SystemClock.UtcNow;
            var devExpiry = devRemaining.TotalMinutes > 0
                ? devRemaining.TotalHours >= 1
                    ? $"{(int)devRemaining.TotalHours}h {devRemaining.Minutes}m remaining"
                    : $"{devRemaining.Minutes}m remaining"
                : "Expired";
            var appName = ResolveClientAppName(_lastDevOpsToken.ClientId);
            DevOpsDetail = $"Browser | {appName} | {devExpiry}";
        }
    }

    // Re-entrancy guard: the tick is async void, so without this a slow
    // silent refresh (network stall) lets the next 60s tick start another
    // one on top of it — the same pile-up pattern that parked 76 calls
    // in-flight in the RunViewModel poller (0.6.322 field logs).
    private bool _autoRefreshInFlight;

    private async void AutoRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_autoRefreshInFlight) return;
        if (_lastTokenResult is null || IsAuthenticating) return;

        // Countdown strings render only inside the account popup — recompute
        // them on ticks only while it's open (OnIsPopupOpenChanged covers the
        // moment it opens). The silent-refresh check below always runs.
        if (IsPopupOpen) RefreshCountdownDisplays();

        var remaining = _lastTokenResult.ExpiresOnUtc - SystemClock.UtcNow;
        if (remaining > TimeSpan.FromMinutes(15)) return;

        // Token expires within 15 minutes -- refresh silently (never trigger interactive)
        _autoRefreshInFlight = true;
        try
        {
            var result = await _authService.TryAcquireTokenSilentAsync();
            if (result is null) return;

            _lastTokenResult = result;
            var newRemaining = result.ExpiresOnUtc - SystemClock.UtcNow;
            TokenExpiry = newRemaining.TotalHours >= 1
                ? $"{(int)newRemaining.TotalHours}h {newRemaining.Minutes}m remaining"
                : $"{newRemaining.Minutes}m remaining";
            // No pool injection here: the refreshed token lands in the MSAL cache,
            // and consumers (inventory/packaging) acquire + inject it at point of use.
            StatusText = "Token auto-refreshed.";
            AppLogger.Info($"Token auto-refreshed, new expiry: {result.ExpiresOnUtc:HH:mm:ss} UTC");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Token auto-refresh failed: {ex.Message}");
        }
        finally
        {
            _autoRefreshInFlight = false;
        }
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private async Task ApplyTokenResultAsync(MsalTokenResult result)
    {
        // Detect identity/tenant switch BEFORE overwriting _lastTokenResult so
        // we can recycle the PowerShell RunspacePool. Any $Global: state left
        // over from the prior identity (tokens, MSAL client, tenant ID) would
        // otherwise persist in reused runspaces and leak across tenants.
        var identityChanged = _lastTokenResult is null
            || !string.Equals(_lastTokenResult.TenantId, result.TenantId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_lastTokenResult.UserPrincipalName, result.UserPrincipalName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_lastTokenResult.ClientId, result.ClientId, StringComparison.OrdinalIgnoreCase);

        // Note: no longer blocks identity switches during a packaging run -- a
        // run has its own dedicated runspace pool (B″), so switching identity
        // here only touches the ambient pool and can't affect the run.

        if (!string.IsNullOrEmpty(result.UserPrincipalName))
        {
            DisplayName = ExtractDisplayName(result.UserPrincipalName);
            UserPrincipalName = result.UserPrincipalName;
            Initials = ExtractInitials(result.UserPrincipalName);
            AuthFlowDisplay = $"WAM | {ResolveClientAppName(result.ClientId)}";
        }
        else
        {
            DisplayName = "App";
            UserPrincipalName = "Application credentials";
            Initials = "AP";
            AuthFlowDisplay = $"{_authService.CurrentAuthFlow} | {ResolveClientAppName(result.ClientId)}";
        }

        IsSignedIn = true;
        StatusText = string.Empty;

        if (identityChanged)
        {
            // Scrub the prior identity's token from the ambient pool (hygiene).
            // Cheap clear -- no pool dispose/recreate (see ClearAmbientPoolToken).
            _ps.ClearAmbientPoolToken();
        }

        // Store for detail display
        _lastTokenResult = result;

        // Extract app info from JWT claims, fall back to known app name
        AppDisplayName = JwtDecoder.GetClaimForDisplay(result.AccessToken, "app_displayname")
            ?? ResolveClientAppName(result.ClientId);
        AppId = JwtDecoder.GetClaimForDisplay(result.AccessToken, "appid") ?? result.ClientId;
        var remaining = result.ExpiresOnUtc - SystemClock.UtcNow;
        TokenExpiry = remaining.TotalMinutes > 0
            ? remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m remaining"
                : $"{remaining.Minutes}m remaining"
            : "Expired";

        // Decode JWT claims for dialog display
        _accessTokenClaims = JwtDecoder.DecodePayloadForDisplay(result.AccessToken);
        _idTokenClaims = JwtDecoder.DecodePayloadForDisplay(result.IdToken);

        // Resolve actual tenant display name from Graph API (non-blocking)
        TenantDisplayName = result.TenantId; // temporary until resolved
        var orgName = await MsalAuthService.ResolveOrganizationNameAsync(result.AccessToken);
        if (!string.IsNullOrEmpty(orgName))
        {
            TenantDisplayName = orgName;

            // Persist tenant name so cached accounts can display it later
            // (mirrors IntuneManagement's Save-Setting {TenantId}_Name pattern)
            _appSettings.TenantNameCache[result.TenantId] = orgName;
            SettingsService.Save(_appSettings);
        }

        // Match against configured tenants by Key (tenant ID / GUID)
        ConfigTenantKey = string.Empty;
        foreach (var t in _generalVm.FullConfig.IntuneTenants)
        {
            if (string.Equals(t.Key, result.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                ConfigTenantKey = !string.IsNullOrWhiteSpace(t.Name) ? t.Name : t.Key;
                break;
            }
        }

        // (Re)start the auto-refresh timer so the token is renewed
        // silently ~15 minutes before it expires.
        StartAutoRefreshTimer();

        // Sign-in no longer seeds the runspace pool. The token is in the MSAL
        // cache; each consumer (inventory per call, packaging per pipeline on
        // its own run pool) acquires + injects it at point of use. Decoupling
        // the account UI from the pool is what lets a run keep its own pool and
        // makes mid-run identity switches safe (B″).
    }

    /// <summary>
    /// Builds a ScrollViewer containing a Grid of claim name/value pairs
    /// for display in a ContentDialog.
    /// </summary>
    private static object BuildClaimsGrid(List<KeyValuePair<string, string>> claims)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < claims.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var nameBlock = new System.Windows.Controls.TextBlock
            {
                Text = claims[i].Key,
                FontSize = 11,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextMutedBrush"),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 8, 2)
            };
            Grid.SetRow(nameBlock, i);
            Grid.SetColumn(nameBlock, 0);
            grid.Children.Add(nameBlock);

            var valueBlock = new System.Windows.Controls.TextBlock
            {
                Text = claims[i].Value,
                FontSize = 11,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 0, 2)
            };
            Grid.SetRow(valueBlock, i);
            Grid.SetColumn(valueBlock, 1);
            grid.Children.Add(valueBlock);
        }

        return new ScrollViewer
        {
            Content = grid,
            MaxHeight = 400,
            MaxWidth = 520,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    private async Task RefreshCachedAccountsAsync()
    {
        try
        {
            var accounts = await _authService.GetCachedAccountsAsync();
            CachedAccounts.Clear();

            foreach (var account in accounts)
            {
                // Skip the currently signed-in user
                if (string.Equals(account.Username, UserPrincipalName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Resolve tenant name from persistent cache (saved during previous logins),
                // fall back to raw tenant ID GUID. No config dependency.
                var tenantId = account.HomeAccountId?.TenantId ?? string.Empty;
                _appSettings.TenantNameCache.TryGetValue(tenantId, out var tenantDisplay);
                if (string.IsNullOrEmpty(tenantDisplay))
                    tenantDisplay = tenantId;

                // Read token status from the MSAL cache without calling the broker.
                // The cache has ID tokens with exp claims we can decode.
                var tokenStatus = "Cached";
                try
                {
                    var cacheData = await ReadMsalCacheAsync();
                    if (cacheData is not null)
                    {
                        var homeId = account.HomeAccountId?.Identifier ?? "";
                        var idTokenStatus = ExtractIdTokenExpiry(cacheData, homeId);
                        if (!string.IsNullOrEmpty(idTokenStatus))
                            tokenStatus = idTokenStatus;
                    }
                }
                catch { /* cache read failed, show generic status */ }

                CachedAccounts.Add(new CachedAccountItem
                {
                    Username = account.Username,
                    TenantDisplay = tenantDisplay,
                    Initials = ExtractInitials(account.Username),
                    HomeAccountId = account.HomeAccountId?.Identifier ?? string.Empty,
                    TokenStatus = tokenStatus,
                    MsalAccount = account
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to load cached accounts: {ex.Message}");
        }
    }

    private IntuneTenantEntry? GetFirstIntuneTenant()
    {
        var tenants = _generalVm.FullConfig.IntuneTenants;
        if (tenants.Count == 0) return null;
        return tenants[0];
    }

    /// <summary>
    /// Returns the tenant entry matching the currently authenticated session.
    /// Falls back to the first tenant if no session is active or no match found.
    /// </summary>
    private IntuneTenantEntry? GetCurrentTenant()
    {
        var tenants = _generalVm.FullConfig.IntuneTenants;
        if (tenants.Count == 0) return null;

        if (_lastTokenResult is not null && !string.IsNullOrEmpty(_lastTokenResult.TenantId))
        {
            var match = tenants.FirstOrDefault(t =>
                string.Equals(t.Key, _lastTokenResult.TenantId, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return tenants[0];
    }

    private static string ExtractDisplayName(string upn)
    {
        // "user@domain.com" -> "user" with first letter capitalized
        var atIndex = upn.IndexOf('@');
        var name = atIndex > 0 ? upn[..atIndex] : upn;

        // Use last two dot-separated parts: "azure.john.doe" -> "John Doe"
        var parts = name.Split('.');
        if (parts.Length >= 2)
        {
            var relevant = parts.Length > 2 ? parts[^2..] : parts;
            return string.Join(" ", relevant.Select(p =>
                p.Length > 0 ? char.ToUpper(p[0]) + p[1..] : p));
        }

        return name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : name;
    }

    private static string ExtractInitials(string upn)
    {
        var atIndex = upn.IndexOf('@');
        var name = atIndex > 0 ? upn[..atIndex] : upn;

        // Use the last two dot-separated parts so that
        // "azure.firstname.lastname" -> "FL" (not "AF").
        var parts = name.Split('.');
        if (parts.Length >= 2)
        {
            var first = parts[^2]; // second-to-last
            var last  = parts[^1]; // last
            return $"{char.ToUpper(first[0])}{char.ToUpper(last[0])}";
        }

        return name.Length >= 2
            ? $"{char.ToUpper(name[0])}{char.ToUpper(name[1])}"
            : name.ToUpper();
    }

    // -------------------------------------------------------------------
    // MSAL cache reading (no broker calls)
    // -------------------------------------------------------------------

    // BUG-1: must be the SAME path MsalAuthService writes — PlatformConfig
    // owns the resolution (env override + appsettings.json included). A
    // hand-derived copy here read a file nobody writes under WRAPP_MSAL_CACHE_PATH.
    private static string MsalCachePath => Services.PlatformConfig.MsalCachePath;

    /// <summary>Reads and decrypts the MSAL cache file. Returns null on failure.</summary>
    private static Task<System.Text.Json.JsonDocument?> ReadMsalCacheAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                if (!System.IO.File.Exists(MsalCachePath)) return null;
                // Use the shared envelope helper so this stays in lockstep with
                // BeforeAccessCallback's format handling. Recognises v2 magic +
                // falls back to v1 for caches written by older builds.
                var stored    = System.IO.File.ReadAllBytes(MsalCachePath);
                var decrypted = Wrapp.Models.SecretProtection.UnprotectBytes(stored);
                var json      = System.Text.Encoding.UTF8.GetString(decrypted);
                return System.Text.Json.JsonDocument.Parse(json);
            }
            catch { return null; }
        });
    }

    /// <summary>
    /// Finds the ID token for a given home account ID in the MSAL cache,
    /// decodes its exp claim, and returns a status string like "WAM | Microsoft Graph PowerShell | 45m".
    /// </summary>
    private static string? ExtractIdTokenExpiry(System.Text.Json.JsonDocument cache, string homeAccountId)
    {
        if (string.IsNullOrEmpty(homeAccountId)) return null;
        if (!cache.RootElement.TryGetProperty("IdToken", out var idTokens)) return null;

        foreach (var entry in idTokens.EnumerateObject())
        {
            if (entry.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            if (!entry.Value.TryGetProperty("home_account_id", out var hid)) continue;
            if (!string.Equals(hid.GetString(), homeAccountId, StringComparison.OrdinalIgnoreCase)) continue;

            // Found matching ID token -- decode the JWT exp claim
            if (!entry.Value.TryGetProperty("secret", out var secret)) continue;
            var jwt = secret.GetString();
            if (string.IsNullOrEmpty(jwt)) continue;

            var expStr = Services.JwtDecoder.GetClaimForDisplay(jwt, "exp");
            if (expStr is null || !long.TryParse(expStr, out var expUnix)) continue;

            var expiry = DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime;
            var elapsed = SystemClock.UtcNow - expiry;

            // Get client ID from the cache entry
            var clientId = "";
            if (entry.Value.TryGetProperty("client_id", out var cid))
                clientId = cid.GetString() ?? "";
            var appName = ResolveClientAppName(clientId);

            // ID tokens expire ~1h after issuance but WAM can still refresh
            // access tokens independently. Show when the last session was, not
            // whether sign-in will work (we can't know that from the cache).
            if (elapsed.TotalMinutes > 0)
            {
                // ID token expired -- show how long ago the last session was
                var ago = elapsed.TotalDays >= 1
                    ? $"{(int)elapsed.TotalDays}d ago"
                    : elapsed.TotalHours >= 1
                        ? $"{(int)elapsed.TotalHours}h ago"
                        : $"{(int)elapsed.TotalMinutes}m ago";
                return $"WAM | {appName} | Last session: {ago}";
            }
            // ID token still valid -- session is very recent
            var remaining = expiry - SystemClock.UtcNow;
            return $"WAM | {appName} | Active ({(int)remaining.TotalMinutes}m)";
        }

        return null;
    }
}
