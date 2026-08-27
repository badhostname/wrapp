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

public partial class SCCMViewModel : ObservableObject
{
    private readonly GeneralViewModel _generalVm;
    private readonly MainViewModel _mainVm;
    private readonly TenantsViewModel _tenantsVm;
    private readonly AppSettings _appSettings;
    private readonly SCCMShared _shared;

    /// <summary>Composition helper for shared package logic.</summary>
    private sealed class SCCMShared : PackageViewModelBase
    {
        private readonly SCCMViewModel _owner;
        public SCCMShared(SCCMViewModel owner) => _owner = owner;
        protected override string PlatformLabel => "SCCM";
        protected override GeneralViewModel GeneralVm => _owner._generalVm;
        protected override IList<IPackageEntry> GetPackageEntries()
            => _owner.SCCMPackager.Packages.Cast<IPackageEntry>().ToList();
        protected override INotifyCollectionChanged GetPackageCollection()
            => _owner.SCCMPackager.Packages;
        protected override IPackageEntry? GetSelectedEntry() => _owner.SelectedPackage;
        protected override void OnValidationChanged() => _owner.RefreshErrorCounts();
        protected override IEnumerable<ITargetedChild> GetChildTargets(IPackageEntry package)
            => package is SCCMPackageEntry p ? p.Deployments : Enumerable.Empty<ITargetedChild>();
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

    public SCCMViewModel(GeneralViewModel generalVm, MainViewModel mainVm, TenantsViewModel tenantsVm, AppSettings appSettings)
    {
        _generalVm   = generalVm;
        _mainVm      = mainVm;
        _tenantsVm   = tenantsVm;
        _appSettings = appSettings;
        _shared      = new SCCMShared(this);
        _generalVm.ConfigLoaded += OnConfigLoaded;
        // Forward GeneralViewModel.InstallerIconSource changes onto our own
        // property of the same name so the SCCM view updates when the user
        // changes the installer icon.
        PropertyRelay.Wire(_generalVm, OnPropertyChanged,
            PropertyRelay.When(
                nameof(GeneralViewModel.InstallerIconSource),
                nameof(InstallerIconSource)));
    }

    private void OnConfigLoaded(object? sender, (AppConfigModel Config, string Path) e)
    {
        OnPropertyChanged(nameof(SCCMPackager));
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
    public SCCMPackagerSection SCCMPackager => _generalVm.FullConfig.Script.SCCMPackager;
    public ModuleDefaults Defaults => _mainVm.ModuleDefaults;
    public TenantsViewModel Tenants => _tenantsVm;

    /// <summary>Icon extracted from the loaded installer.</summary>
    public ImageSource? InstallerIconSource => _generalVm.InstallerIconSource;

    /// <summary>Total validation error count across all SCCM packages and their deployments.</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private int _totalErrorCount;

    /// <summary>Deployment error count for the currently selected package.</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private int _selectedPackageDeploymentErrorCount;

    /// <summary>Deployment WARNING count for the currently selected package
    /// (amber badge on the Deployments button).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private int _selectedPackageDeploymentWarningCount;

    /// <summary>
    /// Total NON-BLOCKING issues across all SCCM packages (for the amber nav
    /// badge). Disjoint from <see cref="TotalErrorCount"/> — see
    /// <see cref="SCCMPackageEntry.WarningCount"/>.
    /// </summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private int _totalWarningCount;

    /// <summary>Recalculates aggregate error counts across all packages and deployments.</summary>
    public void RefreshErrorCounts()
    {
        // Package counts already include deployment issues (and go silent when
        // the package is disabled); the child selector only feeds the
        // Deployments-button badges for the selected package.
        var (total, selected) = PackageViewModelBase.ComputeErrorCounts(
            SCCMPackager.Packages, SelectedPackage,
            p => p.ErrorCount,
            p => p.IsEnabled ? p.Deployments : (IEnumerable<SCCMDeploymentEntry>)Array.Empty<SCCMDeploymentEntry>(),
            d => d.ErrorCount);
        TotalErrorCount                    = total;
        SelectedPackageDeploymentErrorCount = selected;

        var (warnings, selectedWarnings) = PackageViewModelBase.ComputeErrorCounts(
            SCCMPackager.Packages, SelectedPackage,
            p => p.WarningCount,
            p => p.IsEnabled ? p.Deployments : (IEnumerable<SCCMDeploymentEntry>)Array.Empty<SCCMDeploymentEntry>(),
            d => d.WarningCount);
        TotalWarningCount = warnings;
        SelectedPackageDeploymentWarningCount = selectedWarnings;
    }

    // -----------------------------------------------------------------------
    // Selection tracking
    // -----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedPackage))]
    private SCCMPackageEntry? _selectedPackage;

    public bool HasSelectedPackage => SelectedPackage is not null;

    // -----------------------------------------------------------------------
    // Selection trackers for per-package "Remove Selected" buttons. Each is
    // re-bound to the new SelectedPackage's collection in OnSelectedPackageChanged.
    // -----------------------------------------------------------------------

    public SelectionTracker<InstallBehaviorEntry> InstallBehaviorsSelection { get; } = new(e => e.IsSelected);
    public SelectionTracker<DependencyEntry>      DependenciesSelection     { get; } = new(d => d.IsSelected);
    public SelectionTracker<SupersedenceEntry>    SupersedenceSelection     { get; } = new(s => s.IsSelected);

    // -----------------------------------------------------------------------
    // Per-package site targeting (single dropdown)
    // -----------------------------------------------------------------------

    /// <summary>Available sites for the site dropdown.</summary>
    public ObservableCollection<TargetCheckItem> AvailableSites { get; } = new();

    private void RebuildAvailableSites()
    {
        // Non-destructive sync (see PackageViewModelBase.SyncTargetItems) -- a
        // Clear() here would null out the selected package's SiteCode on every
        // navigation via the ComboBox's TwoWay SelectedValue binding.
        var desired = _generalVm.FullConfig.SccmSites
            .Where(s => !string.IsNullOrWhiteSpace(s.Key))
            .Select(s => (Key: s.Key, Display: !string.IsNullOrWhiteSpace(s.Comment) ? $"{s.Key} - {s.Comment}" : s.Key));
        PackageViewModelBase.SyncTargetItems(AvailableSites, desired);
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

    partial void OnSelectedPackageChanged(SCCMPackageEntry? oldValue, SCCMPackageEntry? newValue)
    {
        PackageViewModelBase.RewirePackageListener(oldValue, newValue, OnPackagePropertyChanged);
        RefreshPackageIcon();
        RebuildAvailableSites();
        RefreshErrorCounts();

        InstallBehaviorsSelection.Bind(newValue?.InstallBehaviors);
        DependenciesSelection.Bind(newValue?.Dependencies);
        SupersedenceSelection.Bind(newValue?.Supersedence);
    }

    private void OnPackagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SCCMPackageEntry.Icon))
            RefreshPackageIcon();
        if (e.PropertyName is nameof(SCCMPackageEntry.ErrorCount)
                           or nameof(SCCMPackageEntry.WarningCount))
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
    // Commands
    // -----------------------------------------------------------------------

    [RelayCommand]
    private void AddPackage()
    {
        var app = _generalVm.App;
        var defaultIcon = !string.IsNullOrEmpty(app.IconFile)
            ? app.IconFile
            : SCCMPackager.Packages.FirstOrDefault()?.Icon ?? string.Empty;
        var appName = !string.IsNullOrEmpty(app.Name) ? app.Name : "New Package";
        var fw = Services.ScriptFrameworkProvider.Parse(app.ScriptFramework);

        // Preferences-backed defaults.
        var pkgDefaults  = _appSettings.SccmPackageDefaults;
        var metaDefaults = _appSettings.SccmMetadataDefaults;

        var pkg = new SCCMPackageEntry
        {
            // Identity
            AppName          = appName,
            Icon             = defaultIcon,
            InstallCommand   = Services.ScriptFrameworkProvider.GetSccmInstallCommand(fw),
            UninstallCommand = Services.ScriptFrameworkProvider.GetSccmUninstallCommand(fw),

            // Package defaults (Preferences → SCCM → Package defaults)
            InstallationBehaviorType  = pkgDefaults.InstallationBehaviorType,
            LogonRequirementType      = pkgDefaults.LogonRequirementType,
            UserInteractionMode       = pkgDefaults.UserInteractionMode,
            RebootBehavior            = pkgDefaults.RebootBehavior,
            SlowNetworkDeploymentMode = pkgDefaults.SlowNetworkDeploymentMode,
            EstimatedRuntimeMins      = pkgDefaults.EstimatedRuntimeMins,
            MaximumAllowedRuntimeMins = pkgDefaults.MaximumAllowedRuntimeMins,
            InstallBehavior           = pkgDefaults.InstallBehavior,
            ContentFallback           = pkgDefaults.ContentFallback,

            // Metadata defaults — string templates expanded against current App
            Publisher            = TemplateService.ApplyTokens(metaDefaults.PublisherTemplate,            app),
            Owner                = TemplateService.ApplyTokens(metaDefaults.OwnerTemplate,                app),
            SupportContact       = TemplateService.ApplyTokens(metaDefaults.SupportContactTemplate,       app),
            Description          = TemplateService.ApplyTokens(metaDefaults.DescriptionTemplate,          app),
            Comment              = TemplateService.ApplyTokens(metaDefaults.CommentTemplate,              app),
            Name                 = TemplateService.ApplyTokens(metaDefaults.NameTemplate,                 app),
            LocalizedName        = TemplateService.ApplyTokens(metaDefaults.LocalizedNameTemplate,        app),
            LocalizedDescription = TemplateService.ApplyTokens(metaDefaults.LocalizedDescriptionTemplate, app),
            Keywords             = TemplateService.ApplyTokens(metaDefaults.KeywordsTemplate,             app),
            PrivacyUrl           = TemplateService.ApplyTokens(metaDefaults.PrivacyUrl,                   app),
            UserDocumentation    = TemplateService.ApplyTokens(metaDefaults.UserDocumentation,            app),
            LinkText             = TemplateService.ApplyTokens(metaDefaults.LinkText,                     app),
            ReleaseDate          = TemplateService.ApplyTokens(metaDefaults.ReleaseDateTemplate,          app),
            IsFeatured           = metaDefaults.IsFeatured,
            AutoInstall          = metaDefaults.AutoInstall,
        };

        // Fallbacks to legacy behaviour when a template is left empty, so the
        // grid never opens with blank identity fields.
        if (string.IsNullOrWhiteSpace(pkg.Publisher))      pkg.Publisher      = app.Company;
        if (string.IsNullOrWhiteSpace(pkg.Owner))          pkg.Owner          = Environment.UserName;
        if (string.IsNullOrWhiteSpace(pkg.SupportContact)) pkg.SupportContact = Environment.UserName;
        if (string.IsNullOrWhiteSpace(pkg.Description))
            pkg.Description = $"Created {SystemClock.Now.ToIsoDateOnly()} by {Environment.UserName}";
        if (string.IsNullOrWhiteSpace(pkg.ReleaseDate))
            pkg.ReleaseDate = SystemClock.Now.ToIsoDateOnly();
        if (string.IsNullOrWhiteSpace(pkg.LocalizedName)) pkg.LocalizedName = appName;
        if (string.IsNullOrWhiteSpace(pkg.Name))          pkg.Name          = $"{appName} - Script";
        if (string.IsNullOrWhiteSpace(pkg.Comment))
            pkg.Comment = $"{app.Company} {appName}".Trim();

        SCCMPackager.Packages.Add(pkg);
        SelectedPackage = pkg;
        Info($"SCCM: added new package '{pkg.AppName}'");
    }

    [RelayCommand]
    private void ApplyAppInfo()
    {
        if (SelectedPackage is null) return;
        var pkg = SelectedPackage;
        var app = _generalVm.App;

        // Identity fields: always sync from app info
        if (!string.IsNullOrEmpty(app.Name))
        {
            pkg.AppName       = app.Name;
            pkg.LocalizedName = app.Name;
        }
        pkg.Publisher       = app.Company;

        // Icon: sync if app has one
        if (!string.IsNullOrEmpty(app.IconFile))
            pkg.Icon = app.IconFile;

        // Deployment type name
        if (string.IsNullOrEmpty(pkg.Name))
            pkg.Name = $"{app.Name} - Script";
        if (string.IsNullOrEmpty(pkg.Comment))
            pkg.Comment = $"{app.Company} {app.Name}".Trim();

        // Ownership: fill only if empty (don't overwrite user customizations)
        if (string.IsNullOrEmpty(pkg.Owner))
            pkg.Owner = Environment.UserName;
        if (string.IsNullOrEmpty(pkg.SupportContact))
            pkg.SupportContact = Environment.UserName;
        if (string.IsNullOrEmpty(pkg.Description))
            pkg.Description = $"Created {SystemClock.Now:yyyy-MM-dd} by {Environment.UserName}";

        // Commands: fill only if empty
        var fw = Services.ScriptFrameworkProvider.Parse(_generalVm.App.ScriptFramework);
        if (string.IsNullOrEmpty(pkg.InstallCommand))
            pkg.InstallCommand = Services.ScriptFrameworkProvider.GetSccmInstallCommand(fw);
        if (string.IsNullOrEmpty(pkg.UninstallCommand))
            pkg.UninstallCommand = Services.ScriptFrameworkProvider.GetSccmUninstallCommand(fw);

        Info($"SCCM: applied app info to package '{pkg.AppName}'");
    }

    [RelayCommand]
    private void DuplicatePackage()
    {
        if (SelectedPackage is null) return;
        var src = SelectedPackage;
        var pkg = new SCCMPackageEntry
        {
            AppName                   = src.AppName + " (Copy)",
            AppComment                = src.AppComment,
            Icon                      = src.Icon,
            Publisher                 = src.Publisher,
            SoftwareVersion           = src.SoftwareVersion,
            Owner                     = src.Owner,
            SupportContact            = src.SupportContact,
            Description               = src.Description,
            ReleaseDate               = src.ReleaseDate,
            LocalizedName             = src.LocalizedName,
            LocalizedDescription      = src.LocalizedDescription,
            Keywords                  = src.Keywords,
            IsFeatured                = src.IsFeatured,
            AutoInstall               = src.AutoInstall,
            PrivacyUrl                = src.PrivacyUrl,
            UserDocumentation         = src.UserDocumentation,
            LinkText                  = src.LinkText,
            Name                      = src.Name,
            Comment                   = src.Comment,
            PackageOption             = src.PackageOption,
            InstallCommand            = src.InstallCommand,
            UninstallCommand          = src.UninstallCommand,
            RepairCommand             = src.RepairCommand,
            InstallationBehaviorType  = src.InstallationBehaviorType,
            LogonRequirementType      = src.LogonRequirementType,
            InstallBehavior           = src.InstallBehavior,
            UserInteractionMode       = src.UserInteractionMode,
            RebootBehavior            = src.RebootBehavior,
            EstimatedRuntimeMins      = src.EstimatedRuntimeMins,
            MaximumAllowedRuntimeMins = src.MaximumAllowedRuntimeMins,
            SlowNetworkDeploymentMode = src.SlowNetworkDeploymentMode,
            ContentFallback           = src.ContentFallback
        };
        foreach (var ib in src.InstallBehaviors)
            pkg.InstallBehaviors.Add(new InstallBehaviorEntry { ExeFileName = ib.ExeFileName, DisplayName = ib.DisplayName });
        foreach (var d in src.Dependencies)
            pkg.Dependencies.Add(new DependencyEntry { AppName = d.AppName, AutoInstall = d.AutoInstall });
        foreach (var s in src.Supersedence)
            pkg.Supersedence.Add(new SupersedenceEntry { AppName = s.AppName, SupersedenceType = s.SupersedenceType });

        SCCMPackager.Packages.Add(pkg);
        SelectedPackage = pkg;
        Info($"SCCM: duplicated package '{src.AppName}' as '{pkg.AppName}'");
    }

    [RelayCommand]
    private void RemovePackage()
    {
        _shared.DoRemovePackage(SCCMPackager.Packages, SelectedPackage);
        SelectedPackage = SCCMPackager.Packages.LastOrDefault();
    }

    /// <summary>
    /// Workstream P (P3): expands {{Name}} placeholders across the selected
    /// package's string fields and its deployments' string fields — summary
    /// confirm dialog first, mutation only on confirm.
    /// </summary>
    [RelayCommand]
    private async Task ReplacePlaceholdersAsync()
    {
        if (SelectedPackage is null) return;
        await PlaceholderApplyService.ApplyAsync(
            $"SCCM package \"{SelectedPackage.AppName}\"",
            PlaceholderApplyService.SccmPackageFields(SelectedPackage),
            _generalVm.App);
    }

    [RelayCommand]
    private void BrowseIcon() => _shared.DoBrowseIcon();

    /// <summary>
    /// Applies a dropped image file as the selected package icon.
    /// Called from code-behind drop handler.
    /// </summary>
    public void ApplyDroppedIcon(string path) => _shared.DoApplyDroppedIcon(path);

    [RelayCommand]
    private void UseAppIcon() => _shared.DoUseAppIcon();

    [RelayCommand] private void AddDependency() => _shared.DoAddDependency();
    [RelayCommand] private void RemoveSelectedDependencies() => _shared.DoRemoveSelectedDependencies();
    [RelayCommand] private void AddSupersedence() => _shared.DoAddSupersedence();
    [RelayCommand] private void RemoveSelectedSupersedence() => _shared.DoRemoveSelectedSupersedence();

    [RelayCommand]
    private void AddInstallBehavior() =>
        PackageViewModelBase.AddChild(SelectedPackage?.InstallBehaviors, "SCCM", "install behavior", SelectedPackage?.AppName);

    [RelayCommand]
    private void RemoveSelectedInstallBehaviors() =>
        PackageViewModelBase.RemoveSelectedChildren(SelectedPackage?.InstallBehaviors,
            ib => ib.IsSelected, "SCCM", "install behavior", SelectedPackage?.AppName);

    // -----------------------------------------------------------------------
    // Deployments
    // -----------------------------------------------------------------------

    [RelayCommand]
    private async Task OpenDeployments()
    {
        if (SelectedPackage is null) return;

        var appName = SelectedPackage.AppName;
        var packageId = SelectedPackage.PackageId;

        // Deployments now live directly on the package
        var dialog = new SCCMDeploymentDialog(SelectedPackage.Deployments, appName, Defaults, packageId, App);
        await FluentDialog.ShowContentAsync($"Deployments - {appName}", dialog);

        Info($"SCCM: updated deployments for '{appName}'");
        RefreshErrorCounts();
    }
}
