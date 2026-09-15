using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Wrapp.Helpers;

namespace Wrapp.Models;

// ============================================================
// Intune packager section -- Win32 app metadata, detection /
// requirement rules, return codes, dependencies, supersedence.
// Mirrors Config.Script.IntunePackager in Config.json. Split out
// of AppConfigModel.cs for navigability.
// ============================================================

public partial class IntunePackagerSection : ObservableObject
{
    [ObservableProperty] private string _tag = string.Empty;
    [ObservableProperty] private string _backgroundColor = string.Empty;
    [ObservableProperty] private string _foregroundColor = string.Empty;
    [ObservableProperty] private bool _terminateOnCollision;
    [ObservableProperty] private ObservableCollection<string> _categories = new();
    [ObservableProperty] private ObservableCollection<IntunePackageEntry> _packages = new();
}

public partial class IntunePackageEntry : ObservableObject, IPackageEntry
{
    /// <summary>IPackageEntry.IconPath maps to IconFile for Intune packages.</summary>
    [JsonIgnore]
    public string IconPath
    {
        get => IconFile;
        set => IconFile = value;
    }
    partial void OnIconFileChanged(string value) => OnPropertyChanged(nameof(IconPath));

    /// <summary>Stable GUID for linking assignments to this package. Auto-generated if missing.</summary>
    [ObservableProperty] private string _packageId = Guid.NewGuid().ToString();

    /// <summary>
    /// Persistent operator intent: false excludes this package from runs
    /// (no collision check, no wrapping, no publish) while keeping it in the
    /// bundle. Distinct from the validation state - a package can be complete
    /// but deliberately disabled, or enabled but not yet targeted.
    /// <para>Defaults to true, and a config without the field parses as
    /// enabled, so existing bundles are unaffected.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WarningCount))]
    [NotifyPropertyChangedFor(nameof(ErrorCount))]
    private bool _isEnabled = true;

    /// <summary>UI-only icon for the package list (not serialized).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private ImageSource? _listIconSource;

    /// <summary>UI-only: true when another package shares the same AppName.</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _hasDuplicateName;

    /// <summary>UI-only: true when AppName is empty (required field indicator).</summary>
    [JsonIgnore]
    public bool IsAppNameMissing => string.IsNullOrWhiteSpace(AppName);

    /// <summary>UI-only: true when InstallCommand is empty (required field indicator).</summary>
    [JsonIgnore]
    public bool IsInstallCommandMissing => string.IsNullOrWhiteSpace(InstallCommand);

    /// <summary>UI-only: true when UninstallCommand is empty (required field indicator).</summary>
    [JsonIgnore]
    public bool IsUninstallCommandMissing => string.IsNullOrWhiteSpace(UninstallCommand);

    /// <summary>UI-only: true when MaximumInstallationTimeInMinutes is outside [1-1440].</summary>
    [JsonIgnore]
    public bool IsMaxInstallTimeOutOfRange =>
        MaximumInstallationTimeInMinutes < 1 || MaximumInstallationTimeInMinutes > 1440;

    /// <summary>UI-only: true when InformationURL is non-empty and not a valid http(s) URL.</summary>
    [JsonIgnore]
    public bool IsInformationURLInvalid => Wrapp.Helpers.FieldValidators.IsHttpUrlInvalid(InformationURL);

    /// <summary>UI-only: true when PrivacyURL is non-empty and not a valid http(s) URL.</summary>
    [JsonIgnore]
    public bool IsPrivacyURLInvalid => Wrapp.Helpers.FieldValidators.IsHttpUrlInvalid(PrivacyURL);

    /// <summary>
    /// UI-only: count of validation errors on this package INCLUDING its
    /// assignments' errors, so the list-row badge points at a package whose
    /// only problem lives inside the assignments dialog. Silent for a DISABLED
    /// package, same rule as <see cref="WarningCount"/> - a package excluded
    /// from runs can't fail a run, so its badge shows only the disabled state.
    /// </summary>
    [JsonIgnore]
    public int ErrorCount =>
        !IsEnabled ? 0 :
        (IsAppNameMissing ? 1 : 0)
        + (IsInstallCommandMissing ? 1 : 0)
        + (IsUninstallCommandMissing ? 1 : 0)
        + (HasDuplicateName ? 1 : 0)
        + (IsMaxInstallTimeOutOfRange ? 1 : 0)
        + Assignments.Sum(a => a.ErrorCount);

    /// <summary>
    /// UI-only: non-blocking issues the operator should know about, INCLUDING
    /// the assignments' warnings (missing targeting, duplicate targets).
    /// Strictly disjoint from <see cref="ErrorCount"/> so the two badges never
    /// double-report the same problem.
    /// <para>Nothing is reported for a DISABLED package - you've already said
    /// you don't want it to run. Otherwise warnings are INDEPENDENT of errors:
    /// both badges show their own count at the same time, so fixing a
    /// validation error never hides an outstanding targeting warning (it used
    /// to suppress "no tenant" while any error existed, which read as the
    /// amber badge being replaced by the red one).</para>
    /// </summary>
    [JsonIgnore]
    public int WarningCount
    {
        get
        {
            if (!IsEnabled) return 0;
            var warnings = 0;
            if (HasNoTenant) warnings++;
            if (IsInformationURLInvalid) warnings++;
            if (IsPrivacyURLInvalid) warnings++;
            return warnings + Assignments.Sum(a => a.WarningCount);
        }
    }

    // ErrorCount feeds the red nav badge and WarningCount the amber one; both
    // are computed, so every input change must re-raise them.
    partial void OnAppNameChanged(string value)
    {
        OnPropertyChanged(nameof(IsAppNameMissing));
        RaiseCounts();
    }
    partial void OnInstallCommandChanged(string value)
    {
        OnPropertyChanged(nameof(IsInstallCommandMissing));
        RaiseCounts();
    }
    partial void OnUninstallCommandChanged(string value)
    {
        OnPropertyChanged(nameof(IsUninstallCommandMissing));
        RaiseCounts();
    }
    partial void OnMaximumInstallationTimeInMinutesChanged(int value)
    {
        OnPropertyChanged(nameof(IsMaxInstallTimeOutOfRange));
        RaiseCounts();
    }
    partial void OnInformationURLChanged(string value)
    {
        OnPropertyChanged(nameof(IsInformationURLInvalid));
        OnPropertyChanged(nameof(WarningCount));
    }
    partial void OnPrivacyURLChanged(string value)
    {
        OnPropertyChanged(nameof(IsPrivacyURLInvalid));
        OnPropertyChanged(nameof(WarningCount));
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(WarningCount));
    }

    public static readonly FieldRule[] Rules = FieldDependencyRules.IntunePackageRules;

    private readonly FieldStateProvider _fieldStateProvider = new();

    /// <summary>XAML-binding accessor: <c>{Binding FieldStates[FieldName].IsEnabled}</c>.</summary>
    [JsonIgnore]
    public FieldStateAccessor FieldStates => _fieldStateProvider.FieldStates;

    public IntunePackageEntry()
    {
        _fieldStateProvider.Bind(this, Rules);
        _assignmentWatcher = new ChildCollectionWatcher(OnAssignmentChildChanged);
        _assignmentWatcher.Attach(Assignments);
    }
    partial void OnHasDuplicateNameChanged(bool value) => RaiseCounts();

    // -- Live child aggregation: keeps ErrorCount/WarningCount (which include
    //    assignment issues) honest while an assignment is edited, added or
    //    removed - badges used to refresh only when the dialog closed. --

    private readonly ChildCollectionWatcher _assignmentWatcher;

    partial void OnAssignmentsChanged(ObservableCollection<AssignmentEntry> value)
    {
        _assignmentWatcher.Attach(value);
        RaiseCounts();
        OnPropertyChanged(IPackageEntry.ChildTargetsProperty);
    }

    private void OnAssignmentChildChanged(string? propertyName)
    {
        if (propertyName is null
            or nameof(AssignmentEntry.ErrorCount)
            or nameof(AssignmentEntry.WarningCount))
            RaiseCounts();
        // Targeting changed → the VM layer re-runs the duplicate-target scan.
        if (propertyName is null
            or nameof(AssignmentEntry.GroupID)
            or nameof(AssignmentEntry.Type))
            OnPropertyChanged(IPackageEntry.ChildTargetsProperty);
    }

    [ObservableProperty] private string _appName = string.Empty;
    [ObservableProperty] private string _comment = string.Empty;

    /// <summary>
    /// Operator note merged into the Intune app's Notes JSON by the packager
    /// (admin-only in the Intune console; never shown to end users). The
    /// tracking keys (CreatedBy/Guid/Date) are always preserved.
    /// </summary>
    [ObservableProperty] private string _notes = string.Empty;

    [ObservableProperty] private string _appVersion = string.Empty;
    [ObservableProperty] private string _iconFile = string.Empty;
    [ObservableProperty] private string _packageOption = string.Empty;
    [ObservableProperty] private UpdateMode _updateMode = UpdateMode.Create;
    [ObservableProperty] private string _existingAppID = string.Empty;
    [ObservableProperty] private string _installCommand = string.Empty;
    [ObservableProperty] private string _uninstallCommand = string.Empty;
    [ObservableProperty] private string _installExperience = "system";
    [ObservableProperty] private string _restartBehavior = "basedOnReturnCode";
    [ObservableProperty] private int _maximumInstallationTimeInMinutes = 60;
    [ObservableProperty] private bool _allowAvailableUninstall;
    [ObservableProperty] private bool _companyPortalFeaturedApp;
    [ObservableProperty] private string _developer = string.Empty;
    [ObservableProperty] private string _owner = string.Empty;
    [ObservableProperty] private string _informationURL = string.Empty;
    [ObservableProperty] private string _privacyURL = string.Empty;
    [ObservableProperty] private bool _useAzCopy;
    [ObservableProperty] private string _azCopyWindowStyle = "Hidden";
    [ObservableProperty] private string _architecture = string.Empty;
    [ObservableProperty] private string _minimumSupportedWindowsRelease = string.Empty;
    [ObservableProperty] private ObservableCollection<TagEntry> _categories = new();
    [ObservableProperty] private ObservableCollection<TagEntry> _scopeTags = new();
    [ObservableProperty] private ObservableCollection<DetectionRuleEntry> _detectionRules = new();
    [ObservableProperty] private ObservableCollection<RequirementRuleEntry> _additionalRequirementRules = new();
    [ObservableProperty] private ObservableCollection<ReturnCodeEntry> _customReturnCodes = new();
    [ObservableProperty] private ObservableCollection<DependencyEntry> _dependencies = new();
    [ObservableProperty] private ObservableCollection<SupersedenceEntry> _supersedence = new();

    /// <summary>
    /// The single Intune tenant ID (GUID) this package targets.
    /// Empty = no tenant selected (package skipped during run).
    /// </summary>
    [ObservableProperty] private string _tenantId = string.Empty;

    partial void OnTenantIdChanged(string value)
    {
        OnPropertyChanged(nameof(HasNoTenant));
        OnPropertyChanged(nameof(WarningCount));
    }

    /// <summary>True when no tenant is selected (package will be skipped during run).</summary>
    [JsonIgnore]
    public bool HasNoTenant => string.IsNullOrWhiteSpace(TenantId);

    /// <summary>Assignments for this package (single-tenant, inherited from TenantId).</summary>
    [ObservableProperty] private ObservableCollection<AssignmentEntry> _assignments = new();
}

/// <summary>Detection rule - stores raw JSON object for round-trip fidelity
/// (complex nested structure better preserved as JsonElement).</summary>
public partial class DetectionRuleEntry : ObservableObject
{
    [ObservableProperty] private string _type = string.Empty;
    // Raw JSON preserved as string for editing in Monaco; parsed on save
    [ObservableProperty] private string _rawJson = "{}";
}

public partial class RequirementRuleEntry : ObservableObject
{
    [ObservableProperty] private string _type = string.Empty;
    [ObservableProperty] private string _rawJson = "{}";
}

public partial class TagEntry : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    /// <summary>UI-only selection state (not serialized to Config.json).</summary>
    [ObservableProperty] [property: JsonIgnore] private bool _isSelected;
}

public partial class ReturnCodeEntry : ObservableObject
{
    [ObservableProperty] private int _returnCode;
    [ObservableProperty] private string _type = "success";
    /// <summary>UI-only selection state (not serialized to Config.json).</summary>
    [ObservableProperty] [property: JsonIgnore] private bool _isSelected;
}

public partial class DependencyEntry : ObservableObject
{
    [ObservableProperty] private string _appName = string.Empty;
    [ObservableProperty] private bool _autoInstall = true;
    /// <summary>UI-only selection state (not serialized to Config.json).</summary>
    [ObservableProperty] [property: JsonIgnore] private bool _isSelected;
}

public partial class SupersedenceEntry : ObservableObject
{
    [ObservableProperty] private string _appName = string.Empty;
    [ObservableProperty] private string _supersedenceType = "Update";
    /// <summary>When true, the old app's deployment type is uninstalled before installing the new one (SCCM -IsUninstall parameter).</summary>
    [ObservableProperty] private bool _uninstallOldApp;
    /// <summary>UI-only selection state (not serialized to Config.json).</summary>
    [ObservableProperty] [property: JsonIgnore] private bool _isSelected;
}
