using System.Collections.ObjectModel;
using System.Security;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Wrapp.Helpers;

namespace Wrapp.Models;

// ============================================================
// Intune tenant + assignment + domain entries. Mirrors
// Config.IntuneTenant, the package-level Assignments list, and
// Config.Domain in Config.json. Split out of AppConfigModel.cs
// for navigability.
// ============================================================

public partial class IntuneTenantEntry : ObservableObject
{
    /// <summary>The dictionary key in Config.json -- the Azure AD tenant ID (GUID).</summary>
    [ObservableProperty] private string _key = string.Empty;

    /// <summary>UI-only: this entry's Key is provisioned by organization
    /// policy — the row is read-only and non-removable (padlock).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isPolicyManaged;
    /// <summary>Display name for the tenant (e.g. "Dev", "Prod"). Shown in lists and dropdowns.</summary>
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _comment = string.Empty;
    /// <summary>Domain FQDN. Must match a key in Config.Domain for path resolution.</summary>
    [ObservableProperty] private string _domain = string.Empty;
    [ObservableProperty] private string _clientID = string.Empty;
    [ObservableProperty] private AuthFlow _authFlow = AuthFlow.Interactive;

    /// <summary>
    /// Transient secret value -- only populated when the user has just typed
    /// a new ClientSecret into the PasswordBox. On load from settings.json this
    /// stays <c>null</c>; the stored DPAPI ciphertext lives in
    /// <see cref="ClientSecretCipher"/> instead and is never eagerly decrypted
    /// into this field.
    /// <para>
    /// Phase 15 (S-6, version 0.6.0.0235): the field type is
    /// <see cref="SecureString"/> rather than <see cref="string"/> so the
    /// plaintext never lives in the managed string pool. PasswordBox writes
    /// directly via <c>SecurePassword</c>; consumers route through
    /// <see cref="SecretProtection.WithPlaintext{T}"/> for the brief moment
    /// they need a managed <see cref="string"/>.
    /// </para>
    /// </summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private SecureString? _clientSecret;

    /// <summary>
    /// DPAPI ciphertext ("dpapi:BASE64") as read from settings.json. Carried on
    /// the entry so the save path can preserve it byte-for-byte when the user
    /// doesn't touch the PasswordBox, avoiding re-encryption churn.
    /// <c>[JsonIgnore]</c> -- the bundle's permanent Script/Config.json never
    /// persists the cipher (it uses a "ref:settings" sentinel instead).
    /// </summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private string _clientSecretCipher = string.Empty;

    /// <summary>
    /// True when the entry has a usable secret -- either a fresh
    /// <see cref="SecureString"/> just typed in, or a DPAPI ciphertext on
    /// file. UI uses this to render a "&#x2022;&#x2022;&#x2022;&#x2022;&#x2022;&#x2022;&#x2022;&#x2022; [Change]"
    /// placeholder vs an empty PasswordBox.
    /// </summary>
    [JsonIgnore]
    public bool HasStoredSecret =>
        ClientSecret is { Length: > 0 } || !string.IsNullOrEmpty(ClientSecretCipher);

    partial void OnClientSecretChanged(SecureString? value)   => OnPropertyChanged(nameof(HasStoredSecret));
    partial void OnClientSecretCipherChanged(string value)    => OnPropertyChanged(nameof(HasStoredSecret));

    public static readonly FieldRule[] Rules = FieldDependencyRules.IntuneTenantRules;

    private readonly FieldStateProvider _fieldStateProvider = new();

    /// <summary>XAML-binding accessor: <c>{Binding FieldStates[FieldName].IsEnabled}</c>.</summary>
    [JsonIgnore]
    public FieldStateAccessor FieldStates => _fieldStateProvider.FieldStates;

    public IntuneTenantEntry()
    {
        _fieldStateProvider.Bind(this, Rules);
    }

    [ObservableProperty] private string _certThumbprint = string.Empty;
    [ObservableProperty] private string _architecture = "x64";
    [ObservableProperty] private string _minimumSupportedWindowsRelease = string.Empty;
    [ObservableProperty] private string _intuneWinPath = string.Empty;
    [ObservableProperty] private string _iconFolder = string.Empty;
    [ObservableProperty] private ObservableCollection<string> _scopeTags = new();

    /// <summary>UI-only checkbox selection state (not serialized).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isSelected;
}

public partial class AssignmentEntry : ObservableObject, ITargetedChild
{
    /// <summary>UI-only selection state (not serialized to Config.json).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isSelected;

    /// <summary>User-editable label for this assignment (persisted in Config.json).</summary>
    [ObservableProperty] private string _label = string.Empty;

    /// <summary>Display name shown in the expander header.</summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Label)) return Label;
            var fallback = $"{Intent} - {Type}".Trim(' ', '-');
            return string.IsNullOrWhiteSpace(fallback) ? "Assignment" : fallback;
        }
    }

    /// <summary>Auto-generated subtitle shown below the display name.</summary>
    [JsonIgnore]
    public string DisplaySubtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Intent)) parts.Add(Intent);
            if (!string.IsNullOrWhiteSpace(Type)) parts.Add(Type);
            if (!string.IsNullOrWhiteSpace(GroupID)) parts.Add(GroupID);
            return parts.Count > 0 ? string.Join(" / ", parts) : "No group specified";
        }
    }

    private static readonly Regex GuidPattern = new(
        @"^[{(]?[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}[)}]?$",
        RegexOptions.Compiled);

    /// <summary>True when GroupID contains a GUID (single-tenant enforcement applies).</summary>
    [JsonIgnore]
    public bool IsGroupIdGuid => GuidPattern.IsMatch(GroupID?.Trim() ?? "");

    /// <summary>UI-only: true when Type=Group and GroupID is empty (required field indicator).</summary>
    [JsonIgnore]
    public bool IsGroupIdMissing =>
        string.Equals(Type, "Group", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrWhiteSpace(GroupID);

    /// <summary>UI-only: true when Type=Group and GroupMode is empty (required field indicator).</summary>
    [JsonIgnore]
    public bool IsGroupModeMissing =>
        string.Equals(Type, "Group", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrWhiteSpace(GroupMode);

    // -- Conditional-field framework wiring --

    /// <summary>
    /// Per-field enable/disable/visibility table evaluated by <see cref="FieldStateProvider"/>.
    /// Combines the rule sets in <see cref="FieldDependencyRules"/> with cascading rules
    /// for the EnableRestartGracePeriod toggle and the FilterName-&gt;FilterMode dependency.
    /// </summary>
    public static readonly FieldRule[] Rules =
        FieldDependencyRules.IntuneAssignmentByIntent
            .Concat(FieldDependencyRules.IntuneAssignmentByType)
            .Concat(FieldDependencyRules.IntuneAssignmentByGroupMode)
            .Concat(new[]
            {
                new FieldRule("RestartGracePeriodInMinutes", "EnableRestartGracePeriod",
                    ["False"], FieldDependencyRules.RestartFieldsRequireGracePeriod),
                new FieldRule("RestartCountDownDisplayInMinutes", "EnableRestartGracePeriod",
                    ["False"], FieldDependencyRules.RestartFieldsRequireGracePeriod),
                new FieldRule("RestartNotificationSnoozeInMinutes", "EnableRestartGracePeriod",
                    ["False"], FieldDependencyRules.RestartFieldsRequireGracePeriod),
                new FieldRule("FilterMode", "FilterName",
                    [""], FieldDependencyRules.FilterModeRequiresFilterName),
            })
            .ToArray();

    /// <summary>
    /// Per-field validation metadata. Min/Max bounds for the restart int fields
    /// are exercised by the framework's validator engine -- Phase 1 does not yet
    /// surface ValidationError into XAML chrome.
    /// </summary>
    public static readonly FieldDescriptor[] Descriptors =
    {
        new("RestartGracePeriodInMinutes",        FieldKind.Int, Min: 1, Max: 1440),
        new("RestartCountDownDisplayInMinutes",   FieldKind.Int, Min: 1, Max: 240),
        new("RestartNotificationSnoozeInMinutes", FieldKind.Int, Min: 1, Max: 1440),
    };

    private readonly FieldStateProvider _fieldStateProvider = new();

    /// <summary>XAML-binding accessor: <c>{Binding FieldStates[FieldName].IsEnabled}</c>.</summary>
    [JsonIgnore]
    public FieldStateAccessor FieldStates => _fieldStateProvider.FieldStates;

    public AssignmentEntry()
    {
        _fieldStateProvider.Bind(this, Rules, Descriptors);
    }

    // -- Side-effect change handlers (the framework auto-cascades enable/disable;
    //    these handlers only carry value-mutation and downstream-property notifications) --

    partial void OnLabelChanged(string value) => OnPropertyChanged(nameof(DisplayName));

    private const string AsapSentinel = "0001-01-01T00:00:00.000Z";

    partial void OnIntentChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DisplaySubtitle));

        // Apply intent-specific defaults so the right fields are pre-populated
        if (string.Equals(value, "required", StringComparison.OrdinalIgnoreCase))
        {
            // Switching to Required: populate DeadlineTime if empty so the user
            // sees the ASAP default (matching the Intune API default)
            if (string.IsNullOrEmpty(DeadlineTime))
                DeadlineTime = AsapSentinel;
            if (string.IsNullOrEmpty(AvailableTime))
                AvailableTime = AsapSentinel;
        }
        else
        {
            // Switching away from Required: clear deadline-only fields
            // so they don't cause validation issues on non-required intents
            DeadlineTime = string.Empty;
            UseLocalTime = string.Empty;
            EnableRestartGracePeriod = false;
            RestartGracePeriodInMinutes = string.Empty;
            RestartCountDownDisplayInMinutes = string.Empty;
            RestartNotificationSnoozeInMinutes = string.Empty;
        }
    }

    partial void OnTypeChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DisplaySubtitle));
        OnPropertyChanged(nameof(IsGroupIdMissing));
        OnPropertyChanged(nameof(IsGroupModeMissing));
        RaiseIssueCounts();
    }

    partial void OnGroupIDChanged(string value)
    {
        OnPropertyChanged(nameof(DisplaySubtitle));
        OnPropertyChanged(nameof(IsGroupIdGuid));
        OnPropertyChanged(nameof(IsGroupIdMissing));
        RaiseIssueCounts();
    }

    partial void OnGroupModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsGroupModeMissing));
        RaiseIssueCounts();
    }

    private void RaiseIssueCounts()
    {
        OnPropertyChanged(nameof(HasValidationWarning));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(WarningCount));
    }

    [ObservableProperty] private string _appName = string.Empty;
    /// <summary>Links this assignment to a specific package by its PackageId GUID.</summary>
    [ObservableProperty] private string _packageId = string.Empty;
    [ObservableProperty] private string _type = "Group";
    [ObservableProperty] private string _groupID = string.Empty;
    [ObservableProperty] private string _groupMode = "include";
    [ObservableProperty] private string _intent = "available";
    [ObservableProperty] private string _notification = "showAll";
    [ObservableProperty] private string _deliveryOptimizationPriority = "foreground";
    [ObservableProperty] private string _availableTime = string.Empty;
    [ObservableProperty] private string _deadlineTime = string.Empty;
    [ObservableProperty] private string _useLocalTime = string.Empty;
    [ObservableProperty] private string _autoUpdateSupersededApps = string.Empty;
    [ObservableProperty] private bool _enableRestartGracePeriod;
    [ObservableProperty] private string _restartGracePeriodInMinutes = string.Empty;
    [ObservableProperty] private string _restartCountDownDisplayInMinutes = string.Empty;
    [ObservableProperty] private string _restartNotificationSnoozeInMinutes = string.Empty;
    [ObservableProperty] private string _filterName = string.Empty;
    [ObservableProperty] private string _filterMode = string.Empty;

    // -- Issue classification (matches the package-level split) --
    // WARNINGS are non-blocking: incomplete targeting (the run skips or the
    // operator finishes it) and duplicate targeting (deliberate double-deploy
    // is legal). ERRORS are reserved for incompatible combinations — none are
    // classified yet, but the slot keeps the red badge pipeline uniform.

    /// <summary>
    /// UI-only: true when the assignment card needs attention (incomplete
    /// mandatory field or duplicate target). Drives the amber card border.
    /// </summary>
    [JsonIgnore]
    public bool HasValidationWarning => WarningCount > 0;

    /// <summary>Set by the bundle-wide duplicate-target scan: another enabled
    /// assignment (same package or a sibling package) targets the same group.</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _hasDuplicateTarget;

    partial void OnHasDuplicateTargetChanged(bool value) => RaiseIssueCounts();

    /// <summary>Key the duplicate-target scan groups by (the group ID).</summary>
    [JsonIgnore]
    public string? DuplicateTargetKey =>
        string.Equals(Type, "Group", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(GroupID)
            ? GroupID.Trim()
            : null;

    /// <summary>UI-only: blocking validation errors on this assignment (none
    /// classified today — see the classification note above).</summary>
    [JsonIgnore]
    public int ErrorCount => 0;

    /// <summary>UI-only: non-blocking issues on this assignment (amber badge).</summary>
    [JsonIgnore]
    public int WarningCount =>
        (IsGroupIdMissing ? 1 : 0)
        + (IsGroupModeMissing ? 1 : 0)
        + (HasDuplicateTarget ? 1 : 0);
}

// ============================================================
// Domain entries
// ============================================================

public partial class DomainEntry : ObservableObject
{
    /// <summary>The dictionary key in Config.json (domain name, e.g. "DEV.DOMAIN.COM").</summary>
    [ObservableProperty] private string _key = string.Empty;

    /// <summary>UI-only: provisioned by organization policy (see IntuneTenantEntry).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isPolicyManaged;
    [ObservableProperty] private string _isDistPath = string.Empty;
    [ObservableProperty] private string _appFolder = string.Empty;
    [ObservableProperty] private string _tagFolder = string.Empty;

    /// <summary>UI-only selection state for the Preferences Domains table
    /// "Remove selected" button. Not serialized.</summary>
    [ObservableProperty]
    [property: System.Text.Json.Serialization.JsonIgnore]
    private bool _isSelected;
}
