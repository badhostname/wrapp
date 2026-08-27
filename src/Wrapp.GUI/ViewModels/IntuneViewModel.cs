using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wrapp.Helpers;
using Wrapp.Models;
using Wrapp.Services;
using Wrapp.Views;
using static Wrapp.Services.AppLogger;

namespace Wrapp.ViewModels;

public partial class IntuneViewModel : ObservableObject
{
    private readonly GeneralViewModel _generalVm;
    private readonly MainViewModel _mainVm;
    private readonly TenantsViewModel _tenantsVm;
    private readonly AppSettings _appSettings;
    private readonly IntuneShared _shared;

    /// <summary>Composition helper for shared package logic.</summary>
    private sealed class IntuneShared : PackageViewModelBase
    {
        private readonly IntuneViewModel _owner;
        public IntuneShared(IntuneViewModel owner) => _owner = owner;
        protected override string PlatformLabel => "Intune";
        protected override GeneralViewModel GeneralVm => _owner._generalVm;
        protected override IList<IPackageEntry> GetPackageEntries()
            => _owner.IntunePackager.Packages.Cast<IPackageEntry>().ToList();
        protected override INotifyCollectionChanged GetPackageCollection()
            => _owner.IntunePackager.Packages;
        protected override IPackageEntry? GetSelectedEntry() => _owner.SelectedPackage;
        protected override void OnValidationChanged() => _owner.RefreshErrorCounts();
        protected override IEnumerable<ITargetedChild> GetChildTargets(IPackageEntry package)
            => package is IntunePackageEntry p ? p.Assignments : Enumerable.Empty<ITargetedChild>();
        protected override ImageSource? SelectedPackageIconSourceProp
        {
            get => _owner.SelectedPackageIconSource;
            set => _owner.SelectedPackageIconSource = value;
        }
        protected override bool IconPathMissingProp
        {
            get => _owner.IconPathMissing;
            set => _owner.IconPathMissing = value;
        }
        protected override string IconPathMissingTooltipProp
        {
            get => _owner.IconPathMissingTooltip;
            set => _owner.IconPathMissingTooltip = value;
        }
    }

    public IntuneViewModel(GeneralViewModel generalVm, MainViewModel mainVm, TenantsViewModel tenantsVm, AppSettings appSettings)
    {
        _generalVm   = generalVm;
        _mainVm      = mainVm;
        _tenantsVm   = tenantsVm;
        _appSettings = appSettings;
        _shared      = new IntuneShared(this);
        _generalVm.ConfigLoaded += OnConfigLoaded;
        // Forward GeneralViewModel.InstallerIconSource changes onto our own
        // property of the same name so the Intune view updates when the user
        // changes the installer icon.
        PropertyRelay.Wire(_generalVm, OnPropertyChanged,
            PropertyRelay.When(
                nameof(GeneralViewModel.InstallerIconSource),
                nameof(InstallerIconSource)));
    }

    private void OnConfigLoaded(object? sender, (AppConfigModel Config, string Path) e)
    {
        OnPropertyChanged(nameof(IntunePackager));
        SelectedPackage = null;
        RefreshAllPackageIcons();
        WirePackageNameEvents();
        ValidatePackageNames();
        RefreshErrorCounts();
    }

    // -----------------------------------------------------------------------
    // Exposed model sections
    // -----------------------------------------------------------------------

    public AppSection App => _generalVm.App;
    public IntunePackagerSection IntunePackager => _generalVm.FullConfig.Script.IntunePackager;
    public ModuleDefaults Defaults => _mainVm.ModuleDefaults;
    public TenantsViewModel Tenants => _tenantsVm;

    /// <summary>Icon extracted from the loaded installer.</summary>
    public ImageSource? InstallerIconSource => _generalVm.InstallerIconSource;

    /// <summary>Total validation error count across all Intune packages and their assignments.</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private int _totalErrorCount;

    /// <summary>Assignment error count for the currently selected package.</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private int _selectedPackageAssignmentErrorCount;

    /// <summary>Assignment WARNING count for the currently selected package
    /// (amber badge on the Assignments button).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private int _selectedPackageAssignmentWarningCount;

    /// <summary>
    /// Total NON-BLOCKING issues across all Intune packages (for the amber nav
    /// badge). Disjoint from <see cref="TotalErrorCount"/> — see
    /// <see cref="IntunePackageEntry.WarningCount"/>.
    /// </summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private int _totalWarningCount;

    /// <summary>Recalculates aggregate error counts across all packages and assignments.</summary>
    public void RefreshErrorCounts()
    {
        // Package counts already include assignment issues (and go silent when
        // the package is disabled); the child selector only feeds the
        // Assignments-button badges for the selected package.
        var (total, selected) = PackageViewModelBase.ComputeErrorCounts(
            IntunePackager.Packages, SelectedPackage,
            p => p.ErrorCount,
            p => p.IsEnabled ? p.Assignments : (IEnumerable<AssignmentEntry>)Array.Empty<AssignmentEntry>(),
            a => a.ErrorCount);
        TotalErrorCount                    = total;
        SelectedPackageAssignmentErrorCount = selected;

        var (warnings, selectedWarnings) = PackageViewModelBase.ComputeErrorCounts(
            IntunePackager.Packages, SelectedPackage,
            p => p.WarningCount,
            p => p.IsEnabled ? p.Assignments : (IEnumerable<AssignmentEntry>)Array.Empty<AssignmentEntry>(),
            a => a.WarningCount);
        TotalWarningCount = warnings;
        SelectedPackageAssignmentWarningCount = selectedWarnings;
    }

    // -----------------------------------------------------------------------
    // Selection tracking
    // -----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedPackage))]
    private IntunePackageEntry? _selectedPackage;

    public bool HasSelectedPackage => SelectedPackage is not null;

    // -----------------------------------------------------------------------
    // Selection trackers for per-package "Remove Selected" buttons. Each is
    // re-bound to the new SelectedPackage's collection in OnSelectedPackageChanged.
    // -----------------------------------------------------------------------

    public SelectionTracker<TagEntry>             CategoriesSelection     { get; } = new(t => t.IsSelected);
    public SelectionTracker<TagEntry>             ScopeTagsSelection      { get; } = new(t => t.IsSelected);
    public SelectionTracker<ReturnCodeEntry>      ReturnCodesSelection    { get; } = new(r => r.IsSelected);
    public SelectionTracker<DependencyEntry>      DependenciesSelection   { get; } = new(d => d.IsSelected);
    public SelectionTracker<SupersedenceEntry>    SupersedenceSelection   { get; } = new(s => s.IsSelected);

    // -----------------------------------------------------------------------
    // Per-package tenant targeting (single dropdown)
    // -----------------------------------------------------------------------

    /// <summary>Available tenants for the tenant dropdown.</summary>
    public ObservableCollection<TargetCheckItem> AvailableTenants { get; } = new();

    private void RebuildAvailableTenants()
    {
        // Non-destructive sync (see PackageViewModelBase.SyncTargetItems) -- a
        // Clear() here would null out the selected package's TenantId on every
        // navigation via the ComboBox's TwoWay SelectedValue binding.
        var desired = _generalVm.FullConfig.IntuneTenants
            .Where(t => !string.IsNullOrWhiteSpace(t.Key) && Guid.TryParse(t.Key, out _))
            .Select(t => (Key: t.Key, Display: !string.IsNullOrWhiteSpace(t.Name) ? t.Name : t.Key));
        PackageViewModelBase.SyncTargetItems(AvailableTenants, desired);
    }

    // -----------------------------------------------------------------------
    // Per-package icon preview
    // -----------------------------------------------------------------------

    [ObservableProperty]
    private ImageSource? _selectedPackageIconSource;

    [ObservableProperty]
    private bool _iconPathMissing;

    [ObservableProperty]
    private string _iconPathMissingTooltip = string.Empty;

    partial void OnSelectedPackageChanged(IntunePackageEntry? oldValue, IntunePackageEntry? newValue)
    {
        PackageViewModelBase.RewirePackageListener(oldValue, newValue, OnPackagePropertyChanged);
        RefreshPackageIcon();
        RebuildAvailableTenants();
        RefreshErrorCounts();

        CategoriesSelection.Bind(newValue?.Categories);
        ScopeTagsSelection.Bind(newValue?.ScopeTags);
        ReturnCodesSelection.Bind(newValue?.CustomReturnCodes);
        DependenciesSelection.Bind(newValue?.Dependencies);
        SupersedenceSelection.Bind(newValue?.Supersedence);
    }

    private void OnPackagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IntunePackageEntry.IconFile))
            RefreshPackageIcon();
        if (e.PropertyName is nameof(IntunePackageEntry.ErrorCount)
                           or nameof(IntunePackageEntry.WarningCount))
            RefreshErrorCounts();
    }

    private void RefreshPackageIcon() => _shared.DoRefreshPackageIcon();
    private void RefreshAllPackageIcons() => _shared.DoRefreshAllPackageIcons();

    // -----------------------------------------------------------------------
    // Duplicate package name validation
    // -----------------------------------------------------------------------

    private void WirePackageNameEvents() => _shared.WirePackageNameEvents();
    private void ValidatePackageNames() => _shared.ValidatePackageNames();

    // -----------------------------------------------------------------------
    // Package commands
    // -----------------------------------------------------------------------

    [RelayCommand]
    private void AddPackage()
    {
        var app = _generalVm.App;
        var defaultIcon = !string.IsNullOrEmpty(app.IconFile)
            ? app.IconFile
            : IntunePackager.Packages.FirstOrDefault()?.IconFile ?? string.Empty;
        var fw = Services.ScriptFrameworkProvider.Parse(app.ScriptFramework);

        // Preferences-backed defaults. Package + metadata sections are seeded
        // from ModuleDefaultsSeed on first-launch so these are never empty.
        var pkgDefaults  = _appSettings.IntunePackageDefaults;
        var metaDefaults = _appSettings.IntuneMetadataDefaults;

        // AppName: preferences-supplied token template wins when set; otherwise
        // fall back to the current App.Name, and finally to "New Package".
        var templatedName = TemplateService.ApplyTokens(metaDefaults.AppNameTemplate, app);
        var appName = !string.IsNullOrWhiteSpace(templatedName)
            ? templatedName
            : (!string.IsNullOrEmpty(app.Name) ? app.Name : "New Package");

        var pkg = new IntunePackageEntry
        {
            AppName                          = appName,
            IconFile                         = defaultIcon,
            InstallCommand                   = Services.ScriptFrameworkProvider.GetIntuneInstallCommand(fw),
            UninstallCommand                 = Services.ScriptFrameworkProvider.GetIntuneUninstallCommand(fw),

            // Package defaults (Preferences → Intune → Package defaults)
            Architecture                     = pkgDefaults.Architecture,
            MinimumSupportedWindowsRelease   = pkgDefaults.MinimumSupportedWindowsRelease,
            InstallExperience                = pkgDefaults.InstallExperience,
            RestartBehavior                  = pkgDefaults.RestartBehavior,
            MaximumInstallationTimeInMinutes = pkgDefaults.MaximumInstallationTimeInMinutes,
            AllowAvailableUninstall          = pkgDefaults.AllowAvailableUninstall,
            CompanyPortalFeaturedApp         = pkgDefaults.CompanyPortalFeaturedApp,
            UseAzCopy                        = pkgDefaults.UseAzCopy,
            AzCopyWindowStyle                = pkgDefaults.AzCopyWindowStyle,
            UpdateMode                       = pkgDefaults.UpdateMode,

            // Metadata defaults — string templates expanded against current App
            Comment        = TemplateService.ApplyTokens(metaDefaults.CommentTemplate,   app),
            Notes          = TemplateService.ApplyTokens(metaDefaults.NotesTemplate,     app),
            Developer      = TemplateService.ApplyTokens(metaDefaults.DeveloperTemplate, app),
            Owner          = TemplateService.ApplyTokens(metaDefaults.OwnerTemplate,     app),
            InformationURL = TemplateService.ApplyTokens(metaDefaults.InformationURL,    app),
            PrivacyURL     = TemplateService.ApplyTokens(metaDefaults.PrivacyURL,        app),
        };

        // Ownership fallback: if the OwnerTemplate is empty, keep the legacy
        // behaviour of defaulting to the current Windows user.
        if (string.IsNullOrWhiteSpace(pkg.Owner))
            pkg.Owner = Environment.UserName;
        // Comment fallback: if the CommentTemplate is empty, keep the legacy
        // "{Company} {Name}" autofill so new bundles aren't blank.
        if (string.IsNullOrWhiteSpace(pkg.Comment))
            pkg.Comment = $"{app.Company} {appName}".Trim();
        // Developer fallback: legacy behaviour was App.Company.
        if (string.IsNullOrWhiteSpace(pkg.Developer))
            pkg.Developer = app.Company;

        IntunePackager.Packages.Add(pkg);
        SelectedPackage = pkg;
        Info($"Intune: added new package '{pkg.AppName}'");
    }

    [RelayCommand]
    private void ApplyAppInfo()
    {
        if (SelectedPackage is null) return;
        var pkg = SelectedPackage;
        var app = _generalVm.App;

        // Identity fields: always sync from app info
        if (!string.IsNullOrEmpty(app.Name))
            pkg.AppName = app.Name;
        pkg.Comment   = $"{app.Company} {app.Name}".Trim();
        pkg.Developer = app.Company;

        // Icon: sync if app has one
        if (!string.IsNullOrEmpty(app.IconFile))
            pkg.IconFile = app.IconFile;

        // Ownership: fill only if empty (don't overwrite user customizations)
        if (string.IsNullOrEmpty(pkg.Owner))
            pkg.Owner = Environment.UserName;

        // Commands: fill only if empty
        var fw = Services.ScriptFrameworkProvider.Parse(_generalVm.App.ScriptFramework);
        if (string.IsNullOrEmpty(pkg.InstallCommand))
            pkg.InstallCommand = Services.ScriptFrameworkProvider.GetIntuneInstallCommand(fw);
        if (string.IsNullOrEmpty(pkg.UninstallCommand))
            pkg.UninstallCommand = Services.ScriptFrameworkProvider.GetIntuneUninstallCommand(fw);

        Info($"Intune: applied app info to package '{pkg.AppName}'");
    }

    [RelayCommand]
    private void RemovePackage()
    {
        _shared.DoRemovePackage(IntunePackager.Packages, SelectedPackage);
        SelectedPackage = IntunePackager.Packages.LastOrDefault();
    }

    /// <summary>
    /// Workstream P (P3): expands {{Name}} placeholders across the selected
    /// package's string fields and its assignments' string fields — summary
    /// confirm dialog first, mutation only on confirm.
    /// </summary>
    [RelayCommand]
    private async Task ReplacePlaceholdersAsync()
    {
        if (SelectedPackage is null) return;
        await PlaceholderApplyService.ApplyAsync(
            $"Intune package \"{SelectedPackage.AppName}\"",
            PlaceholderApplyService.IntunePackageFields(SelectedPackage),
            _generalVm.App);
    }

    [RelayCommand]
    private void DuplicatePackage()
    {
        if (SelectedPackage is null) return;
        var src = SelectedPackage;
        var pkg = new IntunePackageEntry
        {
            AppName                         = src.AppName + " (Copy)",
            Comment                         = src.Comment,
            IconFile                        = src.IconFile,
            PackageOption                   = src.PackageOption,
            UpdateMode                      = UpdateMode.Create,
            ExistingAppID                   = string.Empty,
            InstallCommand                  = src.InstallCommand,
            UninstallCommand                = src.UninstallCommand,
            InstallExperience               = src.InstallExperience,
            RestartBehavior                 = src.RestartBehavior,
            MaximumInstallationTimeInMinutes = src.MaximumInstallationTimeInMinutes,
            AllowAvailableUninstall         = src.AllowAvailableUninstall,
            CompanyPortalFeaturedApp        = src.CompanyPortalFeaturedApp,
            Developer                       = src.Developer,
            Owner                           = src.Owner,
            InformationURL                  = src.InformationURL,
            PrivacyURL                      = src.PrivacyURL,
            Architecture                    = src.Architecture,
            MinimumSupportedWindowsRelease  = src.MinimumSupportedWindowsRelease
        };
        foreach (var c in src.Categories)
            pkg.Categories.Add(new TagEntry { Name = c.Name });
        foreach (var t in src.ScopeTags)
            pkg.ScopeTags.Add(new TagEntry { Name = t.Name });
        foreach (var r in src.CustomReturnCodes)
            pkg.CustomReturnCodes.Add(new ReturnCodeEntry { ReturnCode = r.ReturnCode, Type = r.Type });
        foreach (var d in src.Dependencies)
            pkg.Dependencies.Add(new DependencyEntry { AppName = d.AppName, AutoInstall = d.AutoInstall });
        foreach (var s in src.Supersedence)
            pkg.Supersedence.Add(new SupersedenceEntry { AppName = s.AppName, SupersedenceType = s.SupersedenceType });
        foreach (var dr in src.DetectionRules)
            pkg.DetectionRules.Add(new DetectionRuleEntry { Type = dr.Type, RawJson = dr.RawJson });
        foreach (var ar in src.AdditionalRequirementRules)
            pkg.AdditionalRequirementRules.Add(new RequirementRuleEntry { Type = ar.Type, RawJson = ar.RawJson });

        IntunePackager.Packages.Add(pkg);
        SelectedPackage = pkg;
        Info($"Intune: duplicated package '{src.AppName}' as '{pkg.AppName}'");
    }

    // -----------------------------------------------------------------------
    // Icon
    // -----------------------------------------------------------------------

    [RelayCommand]
    private void BrowseIcon() => _shared.DoBrowseIcon();

    /// <summary>
    /// Applies a dropped image file as the selected package icon.
    /// Called from code-behind drop handler.
    /// </summary>
    public void ApplyDroppedIcon(string path) => _shared.DoApplyDroppedIcon(path);

    [RelayCommand]
    private void UseAppIcon() => _shared.DoUseAppIcon();

    // -----------------------------------------------------------------------
    // Dependencies
    // -----------------------------------------------------------------------

    [RelayCommand] private void AddDependency() => _shared.DoAddDependency();
    [RelayCommand] private void RemoveSelectedDependencies() => _shared.DoRemoveSelectedDependencies();

    // -----------------------------------------------------------------------
    // Supersedence
    // -----------------------------------------------------------------------

    [RelayCommand] private void AddSupersedence() => _shared.DoAddSupersedence();
    [RelayCommand] private void RemoveSelectedSupersedence() => _shared.DoRemoveSelectedSupersedence();

    // -----------------------------------------------------------------------
    // Per-package child-collection CRUD (return codes, categories, scope tags)
    // Each is a one-liner via PackageViewModelBase.AddChild / RemoveSelectedChildren.
    // -----------------------------------------------------------------------

    [RelayCommand]
    private void AddReturnCode() =>
        PackageViewModelBase.AddChild(SelectedPackage?.CustomReturnCodes, "Intune", "return code", SelectedPackage?.AppName);

    [RelayCommand]
    private void RemoveSelectedReturnCodes() =>
        PackageViewModelBase.RemoveSelectedChildren(SelectedPackage?.CustomReturnCodes,
            r => r.IsSelected, "Intune", "return code", SelectedPackage?.AppName);

    [RelayCommand]
    private void AddCategory() =>
        PackageViewModelBase.AddChild(SelectedPackage?.Categories, "Intune", "category", SelectedPackage?.AppName);

    [RelayCommand]
    private void RemoveSelectedCategories() =>
        PackageViewModelBase.RemoveSelectedChildren(SelectedPackage?.Categories,
            c => c.IsSelected, "Intune", "category", SelectedPackage?.AppName);

    [RelayCommand]
    private void AddScopeTag() =>
        PackageViewModelBase.AddChild(SelectedPackage?.ScopeTags, "Intune", "scope tag", SelectedPackage?.AppName);

    [RelayCommand]
    private void RemoveSelectedScopeTags() =>
        PackageViewModelBase.RemoveSelectedChildren(SelectedPackage?.ScopeTags,
            t => t.IsSelected, "Intune", "scope tag", SelectedPackage?.AppName);

    // -----------------------------------------------------------------------
    // Assignments
    // -----------------------------------------------------------------------

    [RelayCommand]
    private async Task OpenAssignments()
    {
        if (SelectedPackage is null) return;

        var appName = SelectedPackage.AppName;
        var packageId = SelectedPackage.PackageId;

        // Assignments now live directly on the package
        var dialog = new IntuneAssignmentDialog(SelectedPackage.Assignments, appName, Defaults, packageId, App);
        await FluentDialog.ShowContentAsync($"Assignments - {appName}", dialog);

        Info($"Intune: updated assignments for '{appName}'");
        RefreshErrorCounts();
    }
}

// TargetCheckItem moved to Models/TargetCheckItem.cs — it's shared by both
// IntuneViewModel and SCCMViewModel and shouldn't live inside one of them.
