using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Wrapp.Models;
using Wrapp.Services;
using static Wrapp.Services.AppLogger;

namespace Wrapp.ViewModels;

/// <summary>
/// Shared logic for IntuneViewModel and SCCMViewModel. Contains implementations
/// for package name validation, dependency/supersedence CRUD, icon management,
/// error count refresh, and package removal.
/// Subclasses provide platform-specific access via abstract members.
/// </summary>
public abstract class PackageViewModelBase
{
    /// <summary>Platform label for log messages ("Intune" or "SCCM").</summary>
    protected abstract string PlatformLabel { get; }

    /// <summary>The packages collection as IPackageEntry.</summary>
    protected abstract IList<IPackageEntry> GetPackageEntries();

    /// <summary>The underlying ObservableCollection for collection-changed subscription.</summary>
    protected abstract INotifyCollectionChanged GetPackageCollection();

    /// <summary>The currently selected package.</summary>
    protected abstract IPackageEntry? GetSelectedEntry();

    /// <summary>Called after name validation to refresh error counts.</summary>
    protected abstract void OnValidationChanged();

    /// <summary>Reference to the GeneralViewModel for bundle root, app data, etc.</summary>
    protected abstract GeneralViewModel GeneralVm { get; }

    /// <summary>Bindable property: icon source for the selected package.</summary>
    protected abstract ImageSource? SelectedPackageIconSourceProp { get; set; }

    /// <summary>Bindable property: whether the icon path is missing.</summary>
    protected abstract bool IconPathMissingProp { get; set; }

    /// <summary>Bindable property: tooltip for missing icon path.</summary>
    protected abstract string IconPathMissingTooltipProp { get; set; }

    // -----------------------------------------------------------------------
    // Package name validation
    // -----------------------------------------------------------------------

    private INotifyCollectionChanged? _wiredCollection;
    private NotifyCollectionChangedEventHandler? _collectionHandler;
    private readonly List<IPackageEntry> _subscribedEntries = new();

    public void WirePackageNameEvents()
    {
        if (_wiredCollection is not null && _collectionHandler is not null)
            _wiredCollection.CollectionChanged -= _collectionHandler;
        foreach (var pkg in _subscribedEntries)
            pkg.PropertyChanged -= OnAnyPackageNameChanged;
        _subscribedEntries.Clear();

        var entries = GetPackageEntries();
        foreach (var pkg in entries)
        {
            pkg.PropertyChanged += OnAnyPackageNameChanged;
            _subscribedEntries.Add(pkg);
        }

        var ncc = GetPackageCollection();
        _wiredCollection = ncc;
        _collectionHandler = (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (IPackageEntry pkg in e.NewItems)
                {
                    pkg.PropertyChanged += OnAnyPackageNameChanged;
                    _subscribedEntries.Add(pkg);
                }
            if (e.OldItems is not null)
                foreach (IPackageEntry pkg in e.OldItems)
                {
                    pkg.PropertyChanged -= OnAnyPackageNameChanged;
                    _subscribedEntries.Remove(pkg);
                }
            ValidatePackageNames();
        };
        _wiredCollection.CollectionChanged += _collectionHandler;
    }

    private void OnAnyPackageNameChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Every state flip is logged with the package identity — any code
        // path that silently re-enables/disables a package (like the stale
        // radio-binding write of 2026-08) shows up in app.log with a source
        // to correlate against [TRACE] clicks.
        if (e.PropertyName == nameof(IPackageEntry.IsEnabled) && sender is IPackageEntry entry)
            Info($"{PlatformLabel}: package '{entry.AppName}' " +
                 $"{(entry.IsEnabled ? "enabled" : "disabled")} (id {entry.PackageId})");

        // IsEnabled participates in duplicate detection (disabled packages
        // don't collide — they're excluded from runs), so a toggle re-validates.
        if (e.PropertyName is nameof(IPackageEntry.AppName) or nameof(IPackageEntry.IsEnabled))
            ValidatePackageNames();
        else if (e.PropertyName is nameof(IPackageEntry.ErrorCount)
                               or nameof(IPackageEntry.WarningCount))
            OnValidationChanged();
        else if (e.PropertyName == IPackageEntry.ChildTargetsProperty)
            ValidateChildTargets();
    }

    public void ValidatePackageNames()
    {
        // Only ENABLED packages can collide: a disabled package never reaches
        // the endpoint, so it neither claims a name nor gets flagged itself.
        var entries = GetPackageEntries();
        var duplicates = new HashSet<string>(
            entries
                .Where(p => p.IsEnabled && !string.IsNullOrEmpty(p.AppName))
                .GroupBy(p => p.AppName, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key),
            StringComparer.OrdinalIgnoreCase);

        foreach (var pkg in entries)
            pkg.HasDuplicateName = pkg.IsEnabled && duplicates.Contains(pkg.AppName);

        // Ends with OnValidationChanged, so one call covers both scans.
        ValidateChildTargets();
    }

    // -----------------------------------------------------------------------
    // Duplicate-target scan (assignment groups / deployment collections)
    // -----------------------------------------------------------------------

    /// <summary>The platform's targeted children of one package (Intune
    /// assignments / SCCM deployments).</summary>
    protected abstract IEnumerable<ITargetedChild> GetChildTargets(IPackageEntry package);

    /// <summary>
    /// Flags every enabled child whose target key (group ID / collection name)
    /// is claimed by another enabled child — same package or a sibling — as a
    /// WARNING: deploying twice is legal, but rarely intended. Disabled
    /// packages neither claim targets nor get flagged, mirroring
    /// <see cref="ValidatePackageNames"/>.
    /// </summary>
    public void ValidateChildTargets()
    {
        var entries = GetPackageEntries();

        var duplicates = new HashSet<string>(
            entries
                .Where(p => p.IsEnabled)
                .SelectMany(GetChildTargets)
                .Where(c => !string.IsNullOrEmpty(c.DuplicateTargetKey))
                .GroupBy(c => c.DuplicateTargetKey!, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key),
            StringComparer.OrdinalIgnoreCase);

        foreach (var pkg in entries)
            foreach (var child in GetChildTargets(pkg))
                child.HasDuplicateTarget =
                    pkg.IsEnabled
                    && child.DuplicateTargetKey is { Length: > 0 } key
                    && duplicates.Contains(key);

        OnValidationChanged();
    }

    // -----------------------------------------------------------------------
    // Dependency / Supersedence CRUD
    // -----------------------------------------------------------------------

    public void DoAddDependency()
    {
        var pkg = GetSelectedEntry();
        if (pkg is null) return;
        pkg.Dependencies.Add(new DependencyEntry());
        Info($"{PlatformLabel}: added dependency to '{pkg.AppName}'");
    }

    public void DoRemoveSelectedDependencies()
    {
        var pkg = GetSelectedEntry();
        if (pkg is null) return;
        var toRemove = pkg.Dependencies.Where(d => d.IsSelected).ToList();
        foreach (var d in toRemove) pkg.Dependencies.Remove(d);
        if (toRemove.Count > 0)
            Info($"{PlatformLabel}: removed {toRemove.Count} dependency(ies) from '{pkg.AppName}'");
    }

    public void DoAddSupersedence()
    {
        var pkg = GetSelectedEntry();
        if (pkg is null) return;
        pkg.Supersedence.Add(new SupersedenceEntry());
        Info($"{PlatformLabel}: added supersedence to '{pkg.AppName}'");
    }

    public void DoRemoveSelectedSupersedence()
    {
        var pkg = GetSelectedEntry();
        if (pkg is null) return;
        var toRemove = pkg.Supersedence.Where(s => s.IsSelected).ToList();
        foreach (var s in toRemove) pkg.Supersedence.Remove(s);
        if (toRemove.Count > 0)
            Info($"{PlatformLabel}: removed {toRemove.Count} supersedence(s) from '{pkg.AppName}'");
    }

    // -----------------------------------------------------------------------
    // Icon management (shared via IPackageEntry.IconPath)
    // -----------------------------------------------------------------------

    public void DoRefreshPackageIcon()
    {
        var pkg = GetSelectedEntry();
        if (pkg is null || string.IsNullOrEmpty(pkg.IconPath))
        {
            SelectedPackageIconSourceProp = null;
            IconPathMissingProp = false;
            IconPathMissingTooltipProp = string.Empty;
            return;
        }
        var bundleRoot = GeneralVm.BundleRootDir;
        if (string.IsNullOrEmpty(bundleRoot))
        {
            IconPathMissingProp = false;
            return;
        }
        var icon = IconService.LoadIcon(bundleRoot, pkg.IconPath);
        SelectedPackageIconSourceProp = icon;
        pkg.ListIconSource = icon;

        if (icon is null && !GeneralVm.HasPendingInstallerChanges)
        {
            var fullPath = Path.Combine(bundleRoot, pkg.IconPath);
            IconPathMissingProp = true;
            IconPathMissingTooltipProp = $"Icon file not found: {fullPath}";
            Warn($"{PlatformLabel}: icon file not found for '{pkg.AppName}': {fullPath}");
        }
        else
        {
            IconPathMissingProp = false;
            IconPathMissingTooltipProp = string.Empty;
        }
    }

    public void DoRefreshAllPackageIcons()
    {
        var bundleRoot = GeneralVm.BundleRootDir;
        if (string.IsNullOrEmpty(bundleRoot)) return;
        foreach (var pkg in GetPackageEntries())
        {
            pkg.ListIconSource = string.IsNullOrEmpty(pkg.IconPath)
                ? null
                : IconService.LoadIcon(bundleRoot, pkg.IconPath);
        }
    }

    public void DoBrowseIcon()
    {
        var pkg = GetSelectedEntry();
        if (pkg is null) return;
        var path = FileDialogService.BrowseFile(
            "Image Files|*.png;*.jpg;*.jpeg;*.ico|All Files|*.*",
            "Select App Icon");
        if (path is null) return;

        var bundleRoot = GeneralVm.BundleRootDir;
        if (string.IsNullOrEmpty(bundleRoot)) return;

        var relativePath = IconService.CopyToIconFolder(path, bundleRoot);
        pkg.IconPath = relativePath;
        Info($"{PlatformLabel}: icon set for '{pkg.AppName}' -> {relativePath}");
    }

    public void DoApplyDroppedIcon(string path)
    {
        var pkg = GetSelectedEntry();
        if (pkg is null) return;

        var bundleRoot = GeneralVm.BundleRootDir;
        if (string.IsNullOrEmpty(bundleRoot)) return;

        var relativePath = IconService.CopyToIconFolder(path, bundleRoot);
        pkg.IconPath = relativePath;
        Info($"{PlatformLabel}: icon dropped for '{pkg.AppName}' -> {relativePath}");
    }

    public void DoUseAppIcon()
    {
        var pkg = GetSelectedEntry();
        if (pkg is null) return;
        var appIconFile = GeneralVm.App.IconFile;
        if (string.IsNullOrEmpty(appIconFile)) return;
        pkg.IconPath = appIconFile;
        Info($"{PlatformLabel}: set '{pkg.AppName}' icon to app icon: {appIconFile}");
    }

    // -----------------------------------------------------------------------
    // Package removal
    // -----------------------------------------------------------------------

    public void DoRemovePackage<T>(ObservableCollection<T> packages, T? selected) where T : class, IPackageEntry
    {
        if (selected is null) return;
        packages.Remove(selected);
        Info($"{PlatformLabel}: removed package '{selected.AppName}'");
    }

    // -----------------------------------------------------------------------
    // Generic child-collection CRUD
    //
    // IntuneViewModel and SCCMViewModel have many parallel methods that just
    // add-a-new-item / remove-items-where-IsSelected on package-scoped
    // collections (Categories, ScopeTags, CustomReturnCodes, InstallBehaviors…).
    // These helpers let callers collapse each pair into one-liners without
    // duplicating the log-message boilerplate.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Adds a new <typeparamref name="T"/> (via default ctor) to the given
    /// collection and logs. No-op if <paramref name="collection"/> is null.
    /// </summary>
    public static void AddChild<T>(
        ObservableCollection<T>? collection,
        string platform,
        string childLabel,
        string? packageName) where T : new()
    {
        if (collection is null) return;
        collection.Add(new T());
        Info($"{platform}: added {childLabel} to '{packageName ?? "(no package)"}'");
    }

    /// <summary>
    /// Removes every item in <paramref name="collection"/> where
    /// <paramref name="isSelected"/> returns true. Logs the count on exit.
    /// Returns the number removed. No-op + returns 0 when collection is null.
    /// </summary>
    public static int RemoveSelectedChildren<T>(
        ObservableCollection<T>? collection,
        Func<T, bool> isSelected,
        string platform,
        string childLabel,
        string? packageName)
    {
        if (collection is null) return 0;
        var toRemove = collection.Where(isSelected).ToList();
        foreach (var x in toRemove) collection.Remove(x);
        if (toRemove.Count > 0)
            Info($"{platform}: removed {toRemove.Count} {childLabel}(s) from '{packageName ?? "(no package)"}'");
        return toRemove.Count;
    }

    // -----------------------------------------------------------------------
    // Error-count compute
    //
    // Package entries aggregate their own children (Assignments/Deployments)
    // into ErrorCount/WarningCount, so Total is a plain sum over packages —
    // adding children here again would double-count. The child selectors
    // remain for the SELECTED package's dialog-button badge, which shows
    // child-only counts. The collections differ (Assignments vs Deployments),
    // so we take selectors instead of a common interface.
    // -----------------------------------------------------------------------

    public static (int Total, int Selected) ComputeErrorCounts<TPkg, TChild>(
        IEnumerable<TPkg> packages,
        TPkg? selected,
        Func<TPkg, int> packageErrorCount,
        Func<TPkg, IEnumerable<TChild>> getChildren,
        Func<TChild, int> childErrorCount) where TPkg : class
    {
        int total = 0;
        foreach (var p in packages)
            total += packageErrorCount(p);

        int selectedChildren = 0;
        if (selected is not null)
        {
            foreach (var c in getChildren(selected))
                selectedChildren += childErrorCount(c);
        }

        return (total, selectedChildren);
    }

    // -----------------------------------------------------------------------
    // Selection rewire
    // -----------------------------------------------------------------------

    /// <summary>
    /// Detach <paramref name="handler"/> from <paramref name="oldValue"/>
    /// and attach it to <paramref name="newValue"/>. Safe for nulls on
    /// either side. Use from <c>OnSelectedPackageChanged</c> partial methods.
    /// </summary>
    public static void RewirePackageListener<T>(
        T? oldValue, T? newValue, PropertyChangedEventHandler handler)
        where T : class, INotifyPropertyChanged
    {
        if (oldValue is not null) oldValue.PropertyChanged -= handler;
        if (newValue is not null) newValue.PropertyChanged += handler;
    }

    // -----------------------------------------------------------------------
    // Target-picker (tenant / site) sync
    // -----------------------------------------------------------------------

    /// <summary>
    /// Non-destructively syncs a target-picker collection (Intune tenants /
    /// SCCM sites) to <paramref name="desired"/>, keyed by <c>Key</c>.
    ///
    /// <para><b>Must not <c>Clear()</c>.</b> The target ComboBox two-way-binds
    /// its <c>SelectedValue</c> to the selected package's <c>TenantId</c>/
    /// <c>SiteCode</c>. Clearing the ItemsSource makes the ComboBox set
    /// <c>SelectedValue</c> to null and write that null back into the bound
    /// field. Because these lists are rebuilt from <c>OnSelectedPackageChanged</c>
    /// (while the ComboBox is still bound to the package being navigated away
    /// from), a <c>Clear()</c> silently wipes the target of every package you
    /// leave. Diffing in place preserves the selected item, so navigation no
    /// longer clears targets.</para>
    ///
    /// Removes entries no longer desired, adds new ones, and refreshes changed
    /// display names in place (<see cref="TargetCheckItem"/> is observable).
    /// </summary>
    public static void SyncTargetItems(
        ObservableCollection<TargetCheckItem> collection,
        IEnumerable<(string Key, string Display)> desired)
    {
        var want = desired.ToList();

        for (int i = collection.Count - 1; i >= 0; i--)
        {
            if (!want.Any(d => string.Equals(d.Key, collection[i].Key, StringComparison.OrdinalIgnoreCase)))
                collection.RemoveAt(i);
        }

        foreach (var (key, display) in want)
        {
            var existing = collection.FirstOrDefault(
                a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                collection.Add(new TargetCheckItem { Key = key, DisplayName = display });
            else if (existing.DisplayName != display)
                existing.DisplayName = display;
        }
    }
}
