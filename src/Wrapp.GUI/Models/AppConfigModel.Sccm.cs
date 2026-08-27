using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Wrapp.Helpers;

namespace Wrapp.Models;

// ============================================================
// SCCM packager / site / deployment models. Mirrors
// Config.Script.SCCMPackager and Config.SCCMSite in Config.json.
// Split out of AppConfigModel.cs for navigability.
// ============================================================

public partial class SCCMPackagerSection : ObservableObject
{
    [ObservableProperty] private string _tag = string.Empty;
    [ObservableProperty] private ObservableCollection<SCCMPackageEntry> _packages = new();
}

public partial class SCCMPackageEntry : ObservableObject, IPackageEntry
{
    /// <summary>IPackageEntry.IconPath maps to Icon for SCCM packages.</summary>
    [JsonIgnore]
    public string IconPath
    {
        get => Icon;
        set => Icon = value;
    }
    partial void OnIconChanged(string value) => OnPropertyChanged(nameof(IconPath));

    /// <summary>Stable GUID for linking deployments to this package. Auto-generated if missing.</summary>
    [ObservableProperty] private string _packageId = Guid.NewGuid().ToString();

    /// <summary>UI-only icon for the package list (not serialized).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private ImageSource? _listIconSource;

    /// <summary>UI-only: true when another package shares the same AppName.</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _hasDuplicateName;

    // -- Identity --
    [ObservableProperty] private string _appName = string.Empty;
    [ObservableProperty] private string _appComment = string.Empty;
    [ObservableProperty] private string _icon = string.Empty;

    // -- New-CMApplication metadata --
    [ObservableProperty] private string _publisher = string.Empty;
    [ObservableProperty] private string _softwareVersion = string.Empty;
    [ObservableProperty] private string _owner = string.Empty;
    [ObservableProperty] private string _supportContact = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _releaseDate = string.Empty;
    [ObservableProperty] private string _localizedName = string.Empty;
    [ObservableProperty] private string _localizedDescription = string.Empty;
    [ObservableProperty] private string _keywords = string.Empty;
    [ObservableProperty] private bool _isFeatured;
    [ObservableProperty] private bool _autoInstall;
    [ObservableProperty] private string _privacyUrl = string.Empty;
    [ObservableProperty] private string _userDocumentation = string.Empty;
    [ObservableProperty] private string _linkText = string.Empty;

    // -- Add-CMScriptDeploymentType --

    public static readonly FieldRule[] Rules = FieldDependencyRules.SCCMPackageRules;

    private readonly FieldStateProvider _fieldStateProvider = new();

    /// <summary>XAML-binding accessor: <c>{Binding FieldStates[FieldName].IsEnabled}</c>.</summary>
    [JsonIgnore]
    public FieldStateAccessor FieldStates => _fieldStateProvider.FieldStates;

    public SCCMPackageEntry()
    {
        _fieldStateProvider.Bind(this, Rules);
        _deploymentWatcher = new ChildCollectionWatcher(OnDeploymentChildChanged);
        _deploymentWatcher.Attach(Deployments);
    }

    // -- Live child aggregation — see IntunePackageEntry for the rationale. --

    private readonly ChildCollectionWatcher _deploymentWatcher;

    partial void OnDeploymentsChanged(ObservableCollection<SCCMDeploymentEntry> value)
    {
        _deploymentWatcher.Attach(value);
        RaiseCounts();
        OnPropertyChanged(IPackageEntry.ChildTargetsProperty);
    }

    private void OnDeploymentChildChanged(string? propertyName)
    {
        if (propertyName is null
            or nameof(SCCMDeploymentEntry.ErrorCount)
            or nameof(SCCMDeploymentEntry.WarningCount))
            RaiseCounts();
        if (propertyName is null
            or nameof(SCCMDeploymentEntry.Collection))
            OnPropertyChanged(IPackageEntry.ChildTargetsProperty);
    }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _comment = string.Empty;
    [ObservableProperty] private string _packageOption = string.Empty;
    [ObservableProperty] private string _installCommand = string.Empty;
    [ObservableProperty] private string _uninstallCommand = string.Empty;
    [ObservableProperty] private string _repairCommand = string.Empty;
    [ObservableProperty] private string _installationBehaviorType = "InstallForSystem";
    [ObservableProperty] private string _logonRequirementType = "WhetherOrNotUserLoggedOn";
    [ObservableProperty] private bool _installBehavior;
    [ObservableProperty] private string _userInteractionMode = "Hidden";
    [ObservableProperty] private string _rebootBehavior = "BasedOnExitCode";
    [ObservableProperty] private int _estimatedRuntimeMins = 15;
    [ObservableProperty] private int _maximumAllowedRuntimeMins = 120;
    [ObservableProperty] private string _slowNetworkDeploymentMode = "DoNothing";
    [ObservableProperty] private bool _contentFallback;

    // -- Install behaviors (processes to close) --
    [ObservableProperty] private ObservableCollection<InstallBehaviorEntry> _installBehaviors = new();

    // -- Relationships --
    [ObservableProperty] private ObservableCollection<DependencyEntry> _dependencies = new();
    [ObservableProperty] private ObservableCollection<SupersedenceEntry> _supersedence = new();

    /// <summary>
    /// The single SCCM site code this package targets.
    /// Empty = no site selected (package skipped during run).
    /// </summary>
    [ObservableProperty] private string _siteCode = string.Empty;

    partial void OnSiteCodeChanged(string value)
    {
        OnPropertyChanged(nameof(HasNoSite));
        OnPropertyChanged(nameof(WarningCount));
    }

    /// <summary>True when no site is selected (package will be skipped during run).</summary>
    [JsonIgnore]
    public bool HasNoSite => string.IsNullOrWhiteSpace(SiteCode);

    /// <summary>
    /// Persistent operator intent — see <see cref="IntunePackageEntry.IsEnabled"/>.
    /// False excludes the package from runs while keeping it in the bundle.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WarningCount))]
    [NotifyPropertyChangedFor(nameof(ErrorCount))]
    private bool _isEnabled = true;

    /// <summary>Deployments for this package (single-site, inherited from SiteCode).</summary>
    [ObservableProperty] private ObservableCollection<SCCMDeploymentEntry> _deployments = new();

    /// <summary>UI-only: true when AppName is empty (required field indicator).</summary>
    [JsonIgnore]
    public bool IsAppNameMissing => string.IsNullOrWhiteSpace(AppName);

    /// <summary>UI-only: true when Name (deployment type name) is empty (required field indicator).</summary>
    [JsonIgnore]
    public bool IsNameMissing => string.IsNullOrWhiteSpace(Name);

    /// <summary>UI-only: true when InstallCommand is empty (required field indicator).</summary>
    [JsonIgnore]
    public bool IsInstallCommandMissing => string.IsNullOrWhiteSpace(InstallCommand);

    /// <summary>
    /// UI-only: count of validation errors on this package INCLUDING its
    /// deployments' errors (the list-row badge points at packages whose only
    /// problem lives inside the deployments dialog). Silent for a DISABLED
    /// package — see <see cref="IntunePackageEntry.ErrorCount"/>.
    /// </summary>
    [JsonIgnore]
    public int ErrorCount =>
        !IsEnabled ? 0 :
        (IsAppNameMissing ? 1 : 0)
        + (IsNameMissing ? 1 : 0)
        + (IsInstallCommandMissing ? 1 : 0)
        + (HasDuplicateName ? 1 : 0)
        + Deployments.Sum(d => d.ErrorCount);

    /// <summary>
    /// UI-only non-blocking issues INCLUDING the deployments' warnings —
    /// see <see cref="IntunePackageEntry.WarningCount"/> for the rules
    /// (silent when disabled). Strictly disjoint from ErrorCount.
    /// </summary>
    [JsonIgnore]
    public int WarningCount
    {
        get
        {
            if (!IsEnabled) return 0;
            // Independent of ErrorCount: both badges report simultaneously
            // (see the Intune WarningCount remarks).
            return (HasNoSite ? 1 : 0) + Deployments.Sum(d => d.WarningCount);
        }
    }

    partial void OnAppNameChanged(string value)
    {
        OnPropertyChanged(nameof(IsAppNameMissing));
        RaiseCounts();
    }
    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(IsNameMissing));
        RaiseCounts();
    }
    partial void OnInstallCommandChanged(string value)
    {
        OnPropertyChanged(nameof(IsInstallCommandMissing));
        RaiseCounts();
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(WarningCount));
    }
    partial void OnHasDuplicateNameChanged(bool value) => RaiseCounts();
}

public partial class InstallBehaviorEntry : ObservableObject
{
    [ObservableProperty] private string _exeFileName = string.Empty;
    [ObservableProperty] private string _displayName = string.Empty;
    /// <summary>UI-only selection state (not serialized to Config.json).</summary>
    [ObservableProperty] [property: JsonIgnore] private bool _isSelected;
}

// ============================================================
// SCCM site entries
// ============================================================

public partial class SCCMSiteEntry : ObservableObject
{
    /// <summary>The dictionary key in Config.json (e.g. "CB1", "LCB", "PCB").</summary>
    [ObservableProperty] private string _key = string.Empty;

    /// <summary>UI-only: provisioned by organization policy (see IntuneTenantEntry).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isPolicyManaged;
    [ObservableProperty] private string _comment = string.Empty;
    [ObservableProperty] private string _appFolder = string.Empty;
    [ObservableProperty] private string _iconFolder = string.Empty;
    [ObservableProperty] private ObservableCollection<string> _deploymentGroups = new();

    /// <summary>UI-only checkbox selection state (not serialized).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isSelected;
}

public partial class SCCMDeploymentEntry : ObservableObject, ITargetedChild
{
    /// <summary>UI-only selection state (not serialized to Config.json).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isSelected;

    /// <summary>User-editable label for this deployment (persisted in Config.json).</summary>
    [ObservableProperty] private string _label = string.Empty;

    /// <summary>Display name shown in the expander header.</summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Label)) return Label;
            return string.IsNullOrWhiteSpace(Collection) ? "Deployment" : Collection;
        }
    }

    /// <summary>Auto-generated subtitle shown below the display name.</summary>
    [JsonIgnore]
    public string DisplaySubtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Collection)) parts.Add(Collection);
            if (!string.IsNullOrWhiteSpace(DeployPurpose)) parts.Add(DeployPurpose);
            if (!string.IsNullOrWhiteSpace(DeployAction)) parts.Add(DeployAction);
            return parts.Count > 0 ? string.Join(" / ", parts) : "No collection specified";
        }
    }

    // -- Informational tooltips (no hard disables for SCCM deployments) --

    /// <summary>Advisory tooltip for DeadlineDateTime when DeployPurpose is Available.</summary>
    [JsonIgnore]
    public string? DeadlineDateTimeHint =>
        string.Equals(DeployPurpose, "Available", StringComparison.OrdinalIgnoreCase)
            ? FieldDependencyRules.DeadlineAvailableHint
            : null;

    /// <summary>Advisory tooltip for ApprovalRequired when DeployAction is Uninstall.</summary>
    [JsonIgnore]
    public string? ApprovalRequiredHint =>
        string.Equals(DeployAction, "Uninstall", StringComparison.OrdinalIgnoreCase)
            ? FieldDependencyRules.ApprovalUninstallHint
            : null;

    partial void OnLabelChanged(string value) => OnPropertyChanged(nameof(DisplayName));
    partial void OnCollectionChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DisplaySubtitle));
        OnPropertyChanged(nameof(IsCollectionMissing));
        RaiseIssueCounts();
    }

    private void RaiseIssueCounts()
    {
        OnPropertyChanged(nameof(HasValidationWarning));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(WarningCount));
    }
    partial void OnDeployPurposeChanged(string value)
    {
        OnPropertyChanged(nameof(DisplaySubtitle));
        OnPropertyChanged(nameof(DeadlineDateTimeHint));
    }
    partial void OnDeployActionChanged(string value)
    {
        OnPropertyChanged(nameof(DisplaySubtitle));
        OnPropertyChanged(nameof(ApprovalRequiredHint));
    }

    [ObservableProperty] private string _comment = string.Empty;
    [ObservableProperty] private string _appName = string.Empty;
    /// <summary>Links this deployment to a specific package by its PackageId GUID.</summary>
    [ObservableProperty] private string _packageId = string.Empty;
    [ObservableProperty] private string _collection = string.Empty;
    [ObservableProperty] private string _userNotification = "DisplaySoftwareCenterOnly";
    [ObservableProperty] private string _deployAction = "Install";
    [ObservableProperty] private string _deployPurpose = "Required";
    // -- New-CMApplicationDeployment scheduling --
    [ObservableProperty] private string _availableDateTime = string.Empty;
    [ObservableProperty] private string _deadlineDateTime = string.Empty;
    [ObservableProperty] private string _timeBaseOn = "LocalTime";
    [ObservableProperty] private bool _approvalRequired;
    [ObservableProperty] private bool _overrideServiceWindow;
    [ObservableProperty] private bool _rebootOutsideServiceWindow;
    [ObservableProperty] private bool _sendWakeupPacket;

    /// <summary>UI-only: true when Collection is empty (required field indicator).</summary>
    [JsonIgnore]
    public bool IsCollectionMissing => string.IsNullOrWhiteSpace(Collection);

    // -- Issue classification — see AssignmentEntry for the warning/error split. --

    /// <summary>
    /// UI-only: true when the deployment card needs attention (incomplete
    /// mandatory field or duplicate target). Drives the amber card border.
    /// </summary>
    [JsonIgnore]
    public bool HasValidationWarning => WarningCount > 0;

    /// <summary>Set by the bundle-wide duplicate-target scan: another enabled
    /// deployment (same package or a sibling package) targets the same collection.</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _hasDuplicateTarget;

    partial void OnHasDuplicateTargetChanged(bool value) => RaiseIssueCounts();

    /// <summary>Key the duplicate-target scan groups by (the collection name).</summary>
    [JsonIgnore]
    public string? DuplicateTargetKey =>
        string.IsNullOrWhiteSpace(Collection) ? null : Collection.Trim();

    /// <summary>UI-only: blocking validation errors on this deployment (none
    /// classified today — the slot keeps the red badge pipeline uniform).</summary>
    [JsonIgnore]
    public int ErrorCount => 0;

    /// <summary>UI-only: non-blocking issues on this deployment (amber badge).</summary>
    [JsonIgnore]
    public int WarningCount =>
        (IsCollectionMissing ? 1 : 0)
        + (HasDuplicateTarget ? 1 : 0);
}
