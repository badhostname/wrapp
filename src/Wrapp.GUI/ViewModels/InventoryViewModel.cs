using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wrapp.Models;
using Wrapp.Services;
using Wrapp.Services.Targets;

namespace Wrapp.ViewModels;

public partial class InventoryViewModel : StatusViewModelBase
{
    private readonly AppInventoryService _inventoryService;
    private readonly TenantsViewModel _tenantsVm;
    private readonly MsalAuthService _authService;
    private readonly PowerShellService _psService;
    private readonly PublishTargetRegistry _targets;
    private GeneralViewModel? _generalVm;
    private EncryptionKeyStoreService? _keyStore;
    private AppSettings? _settings;
    private BackgroundJobTracker? _jobTracker;

    // Platform toggle
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlatformIntune))]
    [NotifyPropertyChangedFor(nameof(IsPlatformSCCM))]
    private AppPlatform _platform = AppPlatform.Intune;

    public bool IsPlatformIntune
    {
        get => Platform == AppPlatform.Intune;
        set { if (value) Platform = AppPlatform.Intune; }
    }

    public bool IsPlatformSCCM
    {
        get => Platform == AppPlatform.SCCM;
        set { if (value) Platform = AppPlatform.SCCM; }
    }

    // Tenant/Site selector
    [ObservableProperty]
    private TargetOption? _selectedTarget;

    public ObservableCollection<TargetOption> AvailableTargets { get; } = new();

    // App list
    private List<object> _allApps = new();
    public ObservableCollection<object> AppList { get; } = new();

    [ObservableProperty]
    private object? _selectedApp;

    [ObservableProperty]
    private string _searchText = "";

    /// <summary>Forwards inherited <see cref="StatusViewModelBase.IsBusy"/> under the legacy domain-specific name bound by InventoryView.xaml.</summary>
    public bool IsLoading => IsBusy;

    /// <summary>Separate flag for background work (initial preload, group resolution) that should not block the foreground UI like <see cref="IsBusy"/> does.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCatalogCommand))]
    private bool _isBackgroundWorking;

    protected override void OnIsBusyChangedInternal(bool oldValue, bool newValue)
        => OnPropertyChanged(nameof(IsLoading));

    // Filter state
    public InventoryFilterState Filters { get; } = new();

    [ObservableProperty]
    private bool _isFilterOpen;

    /// <summary>
    /// Filtered match count for the nav badge. Non-zero only when a search or filter is active,
    /// showing how many apps match the current criteria.
    /// </summary>
    [ObservableProperty]
    private int _matchCount;

    // Detail panel
    [ObservableProperty]
    private AppInventoryDetail? _appDetail;

    [ObservableProperty]
    private bool _isDetailLoading;

    [ObservableProperty]
    private bool _hasDetail;

    // -----------------------------------------------------------------------
    // Empty-state machine + Query/Refresh semantics (Inventory revamp).
    // The left pane guides instead of erroring: an unauthenticated Intune
    // target shows a sign-in card (same affordance as the Run view), an
    // unreachable SCCM site says so, and a ready target invites a Query.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Button label: "Query" until data exists for the current selection —
    /// the first fetch is a query; only re-fetching existing data is a
    /// "Refresh".
    /// </summary>
    public string QueryButtonLabel => HasLoadedData ? "Refresh" : "Query";

    /// <summary>True when apps are loaded (live or cached) for the selection.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueryButtonLabel))]
    [NotifyCanExecuteChangedFor(nameof(ExportCatalogCommand))]
    private bool _hasLoadedData;

    /// <summary>Intune target without a silent token — shows the sign-in card.</summary>
    [ObservableProperty]
    private bool _needsIntuneSignIn;

    /// <summary>SCCM site failed the connectivity probe.</summary>
    [ObservableProperty]
    private bool _sccmUnreachable;

    [ObservableProperty]
    private string _sccmUnreachableReason = string.Empty;

    /// <summary>Target is reachable/authenticated but nothing loaded yet.</summary>
    [ObservableProperty]
    private bool _showQueryHint;

    /// <summary>Text on the busy overlay — reflects the actual phase
    /// ("Signing in to …" vs "Loading apps...") instead of one generic label.</summary>
    [ObservableProperty]
    private string _busyOverlayText = "Loading apps...";

    public string SignInTenantName => SelectedTarget?.Display ?? string.Empty;

    /// <summary>
    /// Decides which left-pane empty state applies. One bounded probe per
    /// selection change (silent token check / SCCM connectivity), never a
    /// poll — this is user-action-driven only.
    /// </summary>
    private async Task EvaluateEmptyStateAsync()
    {
        NeedsIntuneSignIn = false;
        SccmUnreachable = false;
        ShowQueryHint = false;
        OnPropertyChanged(nameof(SignInTenantName));

        if (SelectedTarget is null || HasLoadedData) return;
        var probedTarget = SelectedTarget;

        if (Platform == AppPlatform.Intune)
        {
            var token = await _authService.TryAcquireTokenSilentForTenantAsync(probedTarget.Key);
            if (!ReferenceEquals(SelectedTarget, probedTarget)) return;   // user moved on mid-probe
            if (token is null) NeedsIntuneSignIn = true;
            else ShowQueryHint = true;
        }
        else
        {
            var sccm = await _psService.TestSccmConnectivityAsync();
            if (!ReferenceEquals(SelectedTarget, probedTarget)) return;
            if (!sccm.Available)
            {
                SccmUnreachable = true;
                SccmUnreachableReason = sccm.Reason ?? "The ConfigMgr site did not respond.";
            }
            else
            {
                ShowQueryHint = true;
            }
        }
    }

    /// <summary>
    /// Sign-in card action: interactive auth with the credentials mapped to
    /// this tenant (same flow as the Run view's target card), then query
    /// automatically so the list fills without a second click.
    /// </summary>
    [RelayCommand]
    private async Task SignInTenantAsync()
    {
        if (SelectedTarget is null) return;
        var entry = _tenantsVm.IntuneTenants.FirstOrDefault(t =>
            string.Equals(t.Key, SelectedTarget.Key, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            StatusText = "No tenant configuration found for this target.";
            StatusIsError = true;
            return;
        }

        try
        {
            // Hide the card immediately (no bleed-through under the overlay)
            // and label the overlay with the ACTUAL phase.
            NeedsIntuneSignIn = false;
            BusyOverlayText = $"Signing in to {SelectedTarget.Display}...";
            IsBusy = true;
            StatusIsError = false;
            StatusText = $"Signing in to {SelectedTarget.Display}...";
            await _authService.InitializeForTenantAsync(entry, WindowHelper.GetMainWindowHwnd());
            await _authService.AcquireTokenAsync();
            IsBusy = false;
            await QueryAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Sign-in cancelled.";
            NeedsIntuneSignIn = true;   // still unauthenticated — card returns
        }
        catch (Exception ex)
        {
            StatusText = $"Sign-in failed: {ex.Message}";
            StatusIsError = true;
            NeedsIntuneSignIn = true;
            AppLogger.Warn($"Inventory: sign-in failed for {SelectedTarget.Display}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            BusyOverlayText = "Loading apps...";
        }
    }

    public InventoryViewModel(AppInventoryService inventoryService, TenantsViewModel tenantsVm, MsalAuthService authService, PowerShellService psService, PublishTargetRegistry targets)
    {
        _inventoryService = inventoryService;
        _tenantsVm = tenantsVm;
        _psService = psService;
        _authService = authService;
        _targets = targets;

        StatusText = "Select a tenant or site, then click Query.";

        // Re-apply filters whenever any filter property changes
        Filters.PropertyChanged += (_, _) => ApplyFilter();
    }

    /// <summary>Wire GeneralViewModel after construction (avoids circular dependency).</summary>
    public void WireGeneralVm(GeneralViewModel generalVm) => _generalVm = generalVm;

    /// <summary>Wire EncryptionKeyStoreService for Full Clone key lookup.</summary>
    public void WireKeyStore(EncryptionKeyStoreService keyStore) => _keyStore = keyStore;
    public void WireSettings(AppSettings settings) => _settings = settings;
    public void WireJobTracker(BackgroundJobTracker tracker) => _jobTracker = tracker;

    public void RefreshTargets()
    {
        if (_suppressRefreshOnConfigLoad) return;
        RefreshTargetList();
    }

    // -----------------------------------------------------------------------
    // React to property changes
    // -----------------------------------------------------------------------

    partial void OnPlatformChanged(AppPlatform value)
    {
        RefreshTargetList();
        // Don't clear -- RestoreFromCache will show cached data or empty
    }

    partial void OnSelectedTargetChanged(TargetOption? value)
    {
        RestoreFromCache();
        _ = EvaluateEmptyStateAsync();
    }

    partial void OnSearchTextChanged(string value) { ApplyFilter(); MarkMatchingAssignments(); }

    partial void OnSelectedAppChanged(object? value) => _ = LoadDetailAsync();

    private bool _suppressDetailClear;
    private bool _suppressRefreshOnConfigLoad;

    partial void OnAppDetailChanged(AppInventoryDetail? value)
    {
        if (!_suppressDetailClear)
            HasDetail = value is not null;
        MarkMatchingAssignments();
    }

    /// <summary>
    /// Forces WPF to re-render the detail pane by swapping AppDetail null->current.
    /// Suppresses HasDetail toggle so the pane doesn't flash.
    /// </summary>
    private void RefreshDetailPane()
    {
        if (AppDetail is null) return;
        var current = AppDetail;
        _suppressDetailClear = true;
        AppDetail = null;
        _suppressDetailClear = false;
        AppDetail = current;
    }

    /// <summary>Marks which assignments match the current search query for badge display.</summary>
    private void MarkMatchingAssignments()
    {
        if (AppDetail is null) return;
        var query = SearchText?.Trim().ToLowerInvariant() ?? "";
        var hasQuery = !string.IsNullOrEmpty(query);

        foreach (var a in AppDetail.Assignments)
        {
            if (!hasQuery) { a.IsSearchMatch = false; continue; }

            bool match = false;
            if (!string.IsNullOrEmpty(a.TargetLabel) && a.TargetLabel.ToLowerInvariant().Contains(query))
                match = true;
            else if (!string.IsNullOrEmpty(a.GroupId) && a.GroupId.ToLowerInvariant().Contains(query))
                match = true;
            else if (a.NestedGroups is not null)
            {
                foreach (var name in a.NestedGroups.AllNestedGroupNames)
                {
                    if (name.ToLowerInvariant().Contains(query))
                    { match = true; break; }
                }
            }
            a.IsSearchMatch = match;
        }
    }

    // -----------------------------------------------------------------------
    // Target list management
    // -----------------------------------------------------------------------

    private void RefreshTargetList()
    {
        AvailableTargets.Clear();
        SelectedTarget = null;

        if (Platform == AppPlatform.Intune)
        {
            foreach (var t in _tenantsVm.IntuneTenants)
            {
                if (string.IsNullOrEmpty(t.Key)) continue;
                AvailableTargets.Add(new TargetOption
                {
                    Key = t.Key,
                    Display = string.IsNullOrEmpty(t.Name) ? t.Key : t.Name,
                });
            }
        }
        else
        {
            foreach (var s in _tenantsVm.SCCMSites)
            {
                if (string.IsNullOrEmpty(s.Key)) continue;
                AvailableTargets.Add(new TargetOption
                {
                    Key = s.Key,
                    Display = string.IsNullOrEmpty(s.Comment) ? s.Key : $"{s.Key} ({s.Comment})",
                });
            }
        }

        if (AvailableTargets.Count > 0)
            SelectedTarget = AvailableTargets[0];
    }

    // -----------------------------------------------------------------------
    // Load app list
    // -----------------------------------------------------------------------

    [RelayCommand]
    private async Task QueryAsync()
    {
        StatusIsError = false;

        if (SelectedTarget is null)
        {
            StatusText = "No target selected.";
            return;
        }

        // Pre-checks: verify connectivity before attempting API calls
        if (Platform == AppPlatform.Intune)
        {
            var token = await _authService.TryAcquireTokenSilentForTenantAsync(SelectedTarget.Key);
            if (token is null)
            {
                NeedsIntuneSignIn = true;   // surface the sign-in card, not just an error line
                StatusText = "This tenant needs a sign-in — use the sign-in card in the app list.";
                StatusIsError = true;
                AppLogger.Warn($"Inventory: no token available for tenant {SelectedTarget.Key}");
                return;
            }
        }
        else // SCCM
        {
            StatusText = "Checking ConfigMgr connectivity...";
            var sccmResult = await _psService.TestSccmConnectivityAsync();
            if (!sccmResult.Available)
            {
                SccmUnreachable = true;
                SccmUnreachableReason = sccmResult.Reason ?? "The ConfigMgr site did not respond.";
                StatusText = $"SCCM not available: {sccmResult.Reason}";
                StatusIsError = true;
                AppLogger.Warn($"Inventory: SCCM precheck failed for site {SelectedTarget.Key}: {sccmResult.Reason}");
                return;
            }
        }

        NeedsIntuneSignIn = false;
        SccmUnreachable = false;
        ShowQueryHint = false;

        IsBusy = true;
        StatusText = "Loading...";
        ClearAppList();
        var job = _jobTracker?.BeginJob($"Inventory query: {SelectedTarget.Display}") ?? default;
        job.SetDetail("Target", SelectedTarget.Display);
        job.SetDetail("Platform", Platform.ToString());

        try
        {
            if (Platform == AppPlatform.Intune)
            {
                job.SetStatus("Loading app list from Intune...");
                job.SetProgress(0);
                var apps = await _inventoryService.GetIntuneAppsAsync(SelectedTarget.Key, forceRefresh: true);
                _allApps = apps.Cast<object>().ToList();
                ApplyFilter();
                HasLoadedData = _allApps.Count > 0;

                // Query statistics onto the job's detail card.
                var assigned = apps.Count(a => a.AssignmentCount > 0);
                job.SetDetail("Apps returned", apps.Count.ToString());
                job.SetDetail("Assigned", assigned.ToString());
                job.SetDetail("Not assigned", (apps.Count - assigned).ToString());
                job.SetDetail("With dependencies", apps.Count(a => a.DependencyCount > 0).ToString());
                job.SetDetail("With supersedence", apps.Count(a => a.SupersedenceCount > 0).ToString());

                // App list is loaded -- hide the list overlay so apps are selectable
                IsBusy = false;
                // Start background enrichment -- bottom progress bar tracks this
                IsBackgroundWorking = true;

                // Background: preload details -> resolve group names -> resolve nested groups
                // Status bar updates between phases. NO ApplyFilter calls -- the list stays
                // stable so the user can click apps without deselection.
                var tenantKey = SelectedTarget.Key;
                var count = apps.Count;
                var dispatcher = System.Windows.Application.Current.Dispatcher;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Phase 1: Fetch all app details via $batch
                        await dispatcher.InvokeAsync(() =>
                        {
                            StatusText = $"{count} app(s) loaded. Fetching details...";
                            job.SetStatus($"Step 1/3: Fetching details for {count} apps...");
                            job.SetProgress(10);
                        });
                        await _inventoryService.PreloadIntuneDetailsAsync(tenantKey, apps);

                        // Phase 2: Resolve group names
                        await dispatcher.InvokeAsync(() =>
                        {
                            StatusText = $"{count} app(s) loaded. Resolving group names...";
                            job.SetStatus("Step 2/3: Resolving group names...");
                            job.SetProgress(40);
                        });
                        await _inventoryService.ResolveGroupNamesForTenantAsync(tenantKey);

                        // Phase 3: Resolve nested group membership
                        await dispatcher.InvokeAsync(() =>
                        {
                            StatusText = $"{count} app(s) loaded. Resolving nested groups...";
                            job.SetStatus("Step 3/3: Resolving nested groups...");
                            job.SetProgress(70);
                        });
                        await _inventoryService.ResolveNestedGroupsForTenantAsync(tenantKey);

                        // All done -- gentle refresh without touching the list
                        await dispatcher.InvokeAsync(() =>
                        {
                            // Mark apps with nested data (badge shows on next user-driven filter)
                            foreach (var app in _allApps.OfType<IntuneAppSummary>())
                            {
                                var detail = _inventoryService.GetCachedDetail(app.Id);
                                if (detail?.Assignments.Any(a => a.NestedGroups is not null) == true)
                                    app.HasNestedGroupData = true;
                            }

                            // Re-load the selected app's detail from cache (now enriched
                            // with group names + nested data). LoadDetailAsync reads from
                            // _detailCache which was mutated in-place, but returns the same
                            // reference. So we clear AppDetail first to force a re-render.
                            if (SelectedApp is not null)
                            {
                                _suppressDetailClear = true;
                                AppDetail = null;
                                _suppressDetailClear = false;
                                // LoadDetailAsync sets AppDetail to the cached object,
                                // which is now != null (the current value), so WPF re-renders.
                                _ = LoadDetailAsync();
                            }

                            // Force list items to re-render (picks up HasNestedGroupData badges)
                            System.Windows.Data.CollectionViewSource
                                .GetDefaultView(AppList)?.Refresh();

                            StatusText = $"{count} app(s) loaded";
                            IsBackgroundWorking = false;
                            job.SetProgress(100);
                            job.Complete($"{count} app(s) loaded");
                        });
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"Inventory: background preload failed: {ex.Message}");
                        await dispatcher.InvokeAsync(() =>
                        {
                            StatusText = $"Background error: {ex.Message}";
                            StatusIsError = true;
                            IsBackgroundWorking = false;
                            job.SetError(ex.GetType().Name, ex.ToString());
                            job.Fail(ex.Message);
                        });
                    }
                });
            }
            else
            {
                var apps = await _inventoryService.GetSccmAppsAsync(SelectedTarget.Key, forceRefresh: true);
                _allApps = apps.Cast<object>().ToList();
                HasLoadedData = _allApps.Count > 0;
                ApplyFilter();
                IsBusy = false;
                job.SetDetail("Apps returned", apps.Count.ToString());
                // The SCCM path used to leave "Loading..." on screen forever --
                // success never wrote a final status.
                StatusText = apps.Count == 0
                    ? "0 apps found on this site."
                    : $"{apps.Count} app(s) loaded";
                job.Complete($"{apps.Count} SCCM app(s) loaded");
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Load failed: {ex.Message}";
            StatusIsError = true;
            AppLogger.Warn($"Inventory refresh failed: {ex.Message}");
            IsBusy = false;
            // Structured error onto the detail card: type as the code, the
            // full exception text (Graph error bodies ride in the message /
            // inner exception) as the raw payload.
            job.SetError(ex.GetType().Name, ex.ToString());
            job.Fail(ex.Message);
        }
    }

    // -----------------------------------------------------------------------
    // Filter commands
    // -----------------------------------------------------------------------

    [RelayCommand]
    private void ToggleFilter()
    {
        IsFilterOpen = !IsFilterOpen;
    }

    [RelayCommand]
    private void ClearFilters()
    {
        Filters.Reset();
    }

    // -----------------------------------------------------------------------
    // Multi-criteria filter
    // -----------------------------------------------------------------------

    private void ApplyFilter()
    {
        AppList.Clear();
        var query = SearchText.Trim().ToLowerInvariant();

        foreach (var app in _allApps)
        {
            if (!string.IsNullOrEmpty(query) && !MatchesSearch(app, query))
                continue;

            if (!MatchesFilters(app))
                continue;

            AppList.Add(app);
        }

        // Badge count: non-zero only when search/filter is active and narrowing results
        bool isFiltering = !string.IsNullOrEmpty(SearchText.Trim()) || Filters.IsActive;
        MatchCount = isFiltering && _allApps.Count > 0 ? AppList.Count : 0;

        if (_allApps.Count > 0)
        {
            if (isFiltering)
                StatusText = $"{AppList.Count} of {_allApps.Count} app(s) shown";
            else
                StatusText = $"{_allApps.Count} app(s) loaded";
        }
    }

    private bool MatchesSearch(object app, string query)
    {
        // Basic name/publisher/version match
        var baseText = app switch
        {
            IntuneAppSummary intune => intune.SearchText,
            SCCMAppSummary sccm => sccm.SearchText,
            _ => ""
        };
        if (baseText.Contains(query)) return true;

        // Search assignment group names/IDs from the detail cache
        var appId = app switch
        {
            IntuneAppSummary intune => intune.Id,
            SCCMAppSummary sccm => sccm.CI_ID,
            _ => null
        };
        if (appId is null) return false;

        var detail = _inventoryService.GetCachedDetail(appId);
        if (detail is null) return false;

        foreach (var a in detail.Assignments)
        {
            if (!string.IsNullOrEmpty(a.TargetLabel) && a.TargetLabel.ToLowerInvariant().Contains(query))
                return true;
            if (!string.IsNullOrEmpty(a.GroupId) && a.GroupId.ToLowerInvariant().Contains(query))
                return true;
            // Search nested group names when the toggle is enabled
            if (a.NestedGroups is not null)
            {
                foreach (var name in a.NestedGroups.AllNestedGroupNames)
                {
                    if (name.ToLowerInvariant().Contains(query))
                        return true;
                }
            }
        }

        return false;
    }

    private bool MatchesFilters(object app)
    {
        var f = Filters;

        // If no filters active, pass everything
        if (!f.IsActive) return true;

        if (app is IntuneAppSummary intune)
            return MatchesIntuneFilters(intune, f);

        if (app is SCCMAppSummary sccm)
        {
            if (f.HasAssignments && sccm.DeploymentCount == 0) return false;
            if (f.NoAssignments && sccm.DeploymentCount > 0) return false;
            if (f.HasDependencies && sccm.DependencyCount == 0) return false;
            return true;
        }

        return true;
    }

    private bool MatchesIntuneFilters(IntuneAppSummary app, InventoryFilterState f)
    {
        // Intent and assignment filters use the detail cache (populated by batch preload).
        // If the detail isn't cached yet, these filters pass (don't exclude).
        var detail = _inventoryService.GetCachedDetail(app.Id);

        // Intent filters (OR logic)
        bool anyIntentSelected = f.IntentRequired || f.IntentAvailable || f.IntentUninstall;
        if (anyIntentSelected && detail is not null)
        {
            bool matched = false;
            foreach (var a in detail.Assignments)
            {
                if (f.IntentRequired && a.Intent.Equals("required", StringComparison.OrdinalIgnoreCase))
                    matched = true;
                if (f.IntentAvailable && a.Intent.Equals("available", StringComparison.OrdinalIgnoreCase))
                    matched = true;
                if (f.IntentUninstall && a.Intent.Equals("uninstall", StringComparison.OrdinalIgnoreCase))
                    matched = true;
            }
            if (!matched) return false;
        }

        // Assignment presence (use detail if available, fall back to isAssigned flag)
        if (f.HasAssignments)
        {
            if (detail is not null && detail.Assignments.Count == 0) return false;
            if (detail is null && app.AssignmentCount == 0) return false;
        }
        if (f.NoAssignments)
        {
            if (detail is not null && detail.Assignments.Count > 0) return false;
            if (detail is null && app.AssignmentCount > 0) return false;
        }

        // Architecture (OR logic)
        bool anyArchSelected = f.ArchX64 || f.ArchX86 || f.ArchArm;
        if (anyArchSelected)
        {
            var arch = (app.Architecture ?? "").ToLowerInvariant();
            bool matched = false;
            if (f.ArchX64) matched |= arch.Contains("x64") || arch.Contains("all");
            if (f.ArchX86) matched |= arch.Contains("x86") || arch.Contains("all");
            if (f.ArchArm) matched |= arch.Contains("arm") || arch.Contains("all");
            if (!matched) return false;
        }

        // Min OS filter
        if (!string.IsNullOrEmpty(f.MinOSFilter))
        {
            var minOS = app.MinOSVersion ?? "";
            if (string.IsNullOrEmpty(minOS)) return false;
            if (!minOS.Contains(f.MinOSFilter, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Size filters (from list data)
        if (f.MinSizeMB > 0)
        {
            var sizeMB = app.SizeInBytes / (1024.0 * 1024.0);
            if (sizeMB < f.MinSizeMB) return false;
        }
        if (f.MaxSizeMB > 0)
        {
            var sizeMB = app.SizeInBytes / (1024.0 * 1024.0);
            if (sizeMB > f.MaxSizeMB) return false;
        }

        // Relationship filters (use detail if available, fall back to summary counts)
        if (f.HasDependencies)
        {
            if (detail is not null && detail.Dependencies.Count == 0) return false;
            if (detail is null && app.DependencyCount == 0) return false;
        }
        if (f.HasSupersedence)
        {
            if (detail is not null && detail.Supersedence.Count == 0) return false;
            if (detail is null && app.SupersedenceCount == 0) return false;
        }

        return true;
    }

    // -----------------------------------------------------------------------
    // Load detail for selected app
    // -----------------------------------------------------------------------

    private async Task LoadDetailAsync()
    {
        if (SelectedApp is null || SelectedTarget is null)
        {
            AppDetail = null;
            return;
        }

        IsDetailLoading = true;

        try
        {
            // Detail dispatch unified through the publish-target framework:
            // the identifier differs per summary type (Graph id vs SCCM name),
            // but which service to call is the target's job now, not a branch here.
            var identifier = SelectedApp switch
            {
                IntuneAppSummary intune => intune.Id,
                SCCMAppSummary sccm     => sccm.Name,
                _                       => null,
            };
            if (identifier is not null)
                AppDetail = await _targets.Get(Platform).GetAppDetailAsync(SelectedTarget.Key, identifier);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Inventory detail load failed: {ex.Message}");
            AppDetail = null;
        }
        finally
        {
            IsDetailLoading = false;
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private void RestoreFromCache()
    {
        SelectedApp = null;
        AppDetail = null;
        _allApps.Clear();
        AppList.Clear();
        HasLoadedData = false;

        if (SelectedTarget is null) return;

        if (Platform == AppPlatform.Intune)
        {
            var cached = _inventoryService.GetCachedIntuneApps(SelectedTarget.Key);
            if (cached is not null)
            {
                _allApps = cached.Cast<object>().ToList();
                ApplyFilter();
                HasLoadedData = _allApps.Count > 0;   // cached data ⇒ button reads "Refresh"
                return;
            }
        }
        else
        {
            var cached = _inventoryService.GetCachedSccmApps(SelectedTarget.Key);
            if (cached is not null)
            {
                _allApps = cached.Cast<object>().ToList();
                ApplyFilter();
                HasLoadedData = _allApps.Count > 0;
                return;
            }
        }

        StatusText = "Click Query to load apps.";
    }

    private void ClearAppList()
    {
        _allApps.Clear();
        AppList.Clear();
        SelectedApp = null;
        AppDetail = null;
        HasLoadedData = false;
    }

}

public class TargetOption
{
    public string Key { get; init; } = "";
    public string Display { get; init; } = "";

    public override string ToString() => Display;
}
