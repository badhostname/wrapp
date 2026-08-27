using System.IO;
using System.Management.Automation;
using System.Windows.Media.Imaging;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Bridges C# and PowerShell for inventory queries. Caches results per tenant/site.
/// Uses IntuneWin32App module for Intune and ConfigurationManager for SCCM.
///
/// <para>Declared <c>partial</c> so the 2k+ LoC implementation can be split
/// across multiple files by responsibility without breaking any call site.
/// Sibling files:</para>
/// <list type="bullet">
/// <item><c>AppInventoryService.PsObjectMapping.cs</c> &#x2014; pure static
/// PSObject &#x2192; model mappers and primitive-field readers.</item>
/// </list>
/// </summary>
public partial class AppInventoryService
{
    private readonly PowerShellService _ps;
    private readonly MsalAuthService _auth;

    // Per-tenant cache: tenantId -> (timestamp, apps)
    private readonly Dictionary<string, (DateTime Loaded, List<IntuneAppSummary> Apps)> _intuneCache = new();

    // Per-app detail cache: appId -> (timestamp, detail)
    private readonly Dictionary<string, (DateTime Loaded, AppInventoryDetail Detail)> _detailCache = new();

    // SCCM cache: siteCode -> (timestamp, apps)
    private readonly Dictionary<string, (DateTime Loaded, List<SCCMAppSummary> Apps)> _sccmCache = new();

    // Per-tenant group name cache: tenantId -> (groupId -> displayName)
    private readonly Dictionary<string, Dictionary<string, string>> _groupNameCache = new();

    // Per-tenant nested group cache: groupId -> NestedGroupData
    private readonly Dictionary<string, Dictionary<string, NestedGroupData>> _nestedGroupCache = new();

    // Member count cache: groupId -> count (session-permanent, cleared on refresh)
    private readonly Dictionary<string, int> _memberCountCache = new(StringComparer.OrdinalIgnoreCase);

    public AppInventoryService(PowerShellService ps, MsalAuthService auth)
    {
        _ps = ps;
        _auth = auth;
    }

    // -----------------------------------------------------------------------
    // Intune - list with assignments pre-fetched for group search
    // -----------------------------------------------------------------------

    /// <summary>Returns cached Intune apps if available (no TTL -- sticks until Refresh).</summary>
    public List<IntuneAppSummary>? GetCachedIntuneApps(string tenantId)
    {
        return _intuneCache.TryGetValue(tenantId, out var cached) ? cached.Apps : null;
    }

    /// <summary>Returns cached detail for an app if available.</summary>
    public AppInventoryDetail? GetCachedDetail(string appId)
    {
        return _detailCache.TryGetValue(appId, out var cached) ? cached.Detail : null;
    }

    /// <summary>Returns cached SCCM apps if available (no TTL -- sticks until Refresh).</summary>
    public List<SCCMAppSummary>? GetCachedSccmApps(string siteCode)
    {
        return _sccmCache.TryGetValue(siteCode, out var cached) ? cached.Apps : null;
    }

    public async Task<List<IntuneAppSummary>> GetIntuneAppsAsync(string tenantId, bool forceRefresh = false)
    {
        // Cache sticks until explicit refresh -- no TTL expiry
        if (!forceRefresh && _intuneCache.TryGetValue(tenantId, out var cached))
        {
            return cached.Apps;
        }

        using var op = OperationScope.Begin($"Inventory.IntuneRefresh");

        // Clear stale caches for THIS tenant only (preserve other tenants' data)
        if (_intuneCache.TryGetValue(tenantId, out var oldApps))
        {
            foreach (var app in oldApps.Apps)
                _detailCache.Remove(app.Id);
        }
        _nestedGroupCache.Remove(tenantId);
        _groupNameCache.Remove(tenantId);
        _memberCountCache.Clear(); // member counts may have changed

        var token = await GetTokenAsync(tenantId);
        if (token is null)
        {
            op.Complete($"no access token for tenant {tenantId}");
            return new List<IntuneAppSummary>();
        }

        // No $select -- subtype properties (displayVersion, size, etc.) don't exist on
        // the base mobileApp type that OData evaluates $select against before $filter.
        // $filter already limits to win32LobApp; Graph returns all properties.
        var script = @"
$header = $Global:AuthenticationHeader
$baseUrl = 'https://graph.microsoft.com/beta'
$url = ""$baseUrl/deviceAppManagement/mobileApps?`$filter=isof('microsoft.graph.win32LobApp')&`$top=999""

$allApps = [System.Collections.Generic.List[object]]::new()
try {
    do {
        $response = Invoke-RestMethod -Uri $url -Headers $header -Method Get -MaximumRetryCount 5 -RetryIntervalSec 5 -ErrorAction Stop
        if ($response.value) {
            foreach ($v in $response.value) { $allApps.Add($v) }
        }
        $url = $response.'@odata.nextLink'
    } while ($url)
} catch {
    Write-Warning ""Graph API list call failed: $_""
    throw
}

foreach ($app in $allApps) {
    $sizeBytes = 0
    if ($app.size) { $sizeBytes = $app.size }
    $assignCount = 0
    if ($app.isAssigned -eq $true) { $assignCount = 1 }
    $depCount = 0
    if ($app.dependentAppCount) { $depCount = $app.dependentAppCount }
    # Supersedence in either direction counts: this app supersedes others,
    # or something newer supersedes it.
    $supCount = 0
    if ($app.supersedingAppCount) { $supCount += $app.supersedingAppCount }
    if ($app.supersededAppCount) { $supCount += $app.supersededAppCount }

    [PSCustomObject]@{
        Id                = $app.id
        DisplayName       = $app.displayName
        Publisher         = $app.publisher
        AppVersion        = $app.displayVersion
        AssignmentCount   = $assignCount
        DependencyCount   = $depCount
        SupersedenceCount = $supCount
        LastModified      = $app.lastModifiedDateTime
        SizeInBytes       = $sizeBytes
        Architecture      = $app.applicableArchitectures
        MinOSVersion      = $app.minimumSupportedWindowsRelease
    }
}
";

        var results = await _ps.RunScriptWithTokenAsync(script, token);
        var apps = new List<IntuneAppSummary>();

        foreach (var obj in results)
        {
            if (obj?.BaseObject is null) continue;
            apps.Add(new IntuneAppSummary
            {
                Id                = GetStr(obj, "Id"),
                DisplayName       = GetStr(obj, "DisplayName"),
                Publisher         = GetStr(obj, "Publisher"),
                AppVersion        = GetStr(obj, "AppVersion"),
                TenantId          = tenantId,
                AssignmentCount   = GetInt(obj, "AssignmentCount"),
                DependencyCount   = GetInt(obj, "DependencyCount"),
                SupersedenceCount = GetInt(obj, "SupersedenceCount"),
                LastModified      = GetDate(obj, "LastModified"),
                SizeInBytes       = GetLong(obj, "SizeInBytes"),
                Architecture      = GetStr(obj, "Architecture"),
                MinOSVersion      = GetStr(obj, "MinOSVersion"),
            });
        }

        _intuneCache[tenantId] = (SystemClock.UtcNow, apps);
        op.Complete($"loaded {apps.Count} Intune app(s) for tenant {tenantId}");
        return apps;
    }

    // -----------------------------------------------------------------------
    // Intune - batch preload details (background, after list loads)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Preloads full details for all apps via Graph $batch API (20 per batch).
    /// Call after list loads to make clicking any app instant.
    /// </summary>
    public async Task PreloadIntuneDetailsAsync(string tenantId, List<IntuneAppSummary> apps)
    {
        // Skip apps already cached
        var uncached = apps.Where(a => !_detailCache.ContainsKey(a.Id)).ToList();
        if (uncached.Count == 0) return;

        using var op = OperationScope.Begin("Inventory.PreloadIntuneDetails");

        var token = await GetTokenAsync(tenantId);
        if (token is null) { op.Complete("no token"); return; }

        var idList = string.Join(",", uncached.Select(a => $"'{Escape(a.Id)}'"));
        var script = $@"
$header = $Global:AuthenticationHeader
$baseUrl = 'https://graph.microsoft.com/beta'
$appIds = @({idList})

# Process in batches of 20 (Graph $batch limit)
for ($i = 0; $i -lt $appIds.Count; $i += 20) {{
    $chunk = $appIds[$i..[Math]::Min($i + 19, $appIds.Count - 1)]
    $requests = @()
    $idx = 1
    foreach ($aid in $chunk) {{
        $requests += @{{
            id      = ""$idx""
            method  = 'GET'
            url     = ""/deviceAppManagement/mobileApps/$aid`?`$expand=assignments,categories""
            headers = @{{ 'Accept' = 'application/json;odata.metadata=minimal' }}
        }}
        $idx++
    }}

    $batchBody = @{{ requests = $requests }} | ConvertTo-Json -Depth 10 -Compress
    $batchResult = Invoke-RestMethod -Uri ""$baseUrl/`$batch"" -Headers $header -Method Post -Body $batchBody -ContentType 'application/json' -MaximumRetryCount 5 -RetryIntervalSec 5 -ErrorAction Stop

    foreach ($resp in $batchResult.responses) {{
        if ($resp.status -ne 200) {{ continue }}
        $app = $resp.body

        # Detection rules
        $detType = 'Unknown'; $detSummary = ''; $detScript = ''
        if ($app.detectionRules) {{
            $rule = $app.detectionRules | Select-Object -First 1
            switch ($rule.'@odata.type') {{
                '#microsoft.graph.win32LobAppPowerShellScriptDetection' {{
                    $detType = 'PowerShell Script'
                    $detScript = [System.Text.Encoding]::UTF8.GetString(
                        [System.Convert]::FromBase64String($rule.scriptContent))
                    $detSummary = ""PowerShell script ($($detScript.Length) chars)""
                }}
                '#microsoft.graph.win32LobAppRegistryDetection' {{
                    $detType = 'Registry'
                    $detSummary = ""$($rule.keyPath)\$($rule.valueName) $($rule.detectionType)""
                }}
                '#microsoft.graph.win32LobAppFileSystemDetection' {{
                    $detType = 'File'
                    $detSummary = ""$($rule.path)\$($rule.fileOrFolderName) $($rule.detectionType)""
                }}
                '#microsoft.graph.win32LobAppProductCodeDetection' {{
                    $detType = 'MSI Product Code'
                    $detSummary = ""Product: $($rule.productCode)""
                }}
            }}
        }}

        # Requirement rules
        $reqs = @()
        if ($app.requirementRules) {{
            foreach ($req in $app.requirementRules) {{
                switch ($req.'@odata.type') {{
                    '#microsoft.graph.win32LobAppPowerShellScriptRequirement' {{
                        $scriptBody = ''
                        if ($req.scriptContent) {{
                            $scriptBody = [System.Text.Encoding]::UTF8.GetString(
                                [System.Convert]::FromBase64String($req.scriptContent))
                        }}
                        $reqs += [PSCustomObject]@{{
                            RuleType = 'Script'
                            Summary = ""PowerShell: $($req.displayName) ($($req.detectionType) $($req.operator) $($req.detectionValue))""
                            ScriptContent = $scriptBody
                        }}
                    }}
                    '#microsoft.graph.win32LobAppRegistryRequirement' {{
                        $reqs += [PSCustomObject]@{{
                            RuleType = 'Registry'
                            Summary = ""Registry: $($req.keyPath)\$($req.valueName) $($req.detectionType)""
                            ScriptContent = ''
                        }}
                    }}
                    '#microsoft.graph.win32LobAppFileSystemRequirement' {{
                        $reqs += [PSCustomObject]@{{
                            RuleType = 'File'
                            Summary = ""File: $($req.path)\$($req.fileOrFolderName) $($req.detectionType)""
                            ScriptContent = ''
                        }}
                    }}
                }}
            }}
        }}

        $minOS = ''; $arch = ''
        if ($app.minimumSupportedWindowsRelease) {{ $minOS = $app.minimumSupportedWindowsRelease }}
        if ($app.applicableArchitectures) {{ $arch = $app.applicableArchitectures }}

        $iconBase64 = ''
        if ($app.largeIcon -and $app.largeIcon.value) {{ $iconBase64 = $app.largeIcon.value }}

        # Map assignments from raw Graph $expand
        $assignDetail = @($app.assignments | ForEach-Object {{
            $ttype = $_.target.'@odata.type'
            $label = ''; $gid = ''; $groupMode = 'Include'
            if ($ttype -match 'allDevices') {{ $label = 'All Devices' }}
            elseif ($ttype -match 'allLicensedUsers') {{ $label = 'All Users' }}
            elseif ($ttype -match 'exclusionGroup') {{ $gid = $_.target.groupId; $label = $gid; $groupMode = 'Exclude' }}
            elseif ($ttype -match 'group') {{ $gid = $_.target.groupId; $label = $gid }}

            $filterId = ''; $filterMode = ''
            if ($_.target.deviceAndAppManagementAssignmentFilterId) {{
                $filterId = $_.target.deviceAndAppManagementAssignmentFilterId
            }}
            if ($_.target.deviceAndAppManagementAssignmentFilterType) {{
                $filterMode = $_.target.deviceAndAppManagementAssignmentFilterType
            }}

            $avail = ''; $deadline = ''; $notif = ''; $delOpt = ''; $grace = ''
            if ($_.settings) {{
                if ($_.settings.notifications) {{ $notif = $_.settings.notifications }}
                if ($_.settings.deliveryOptimizationPriority) {{ $delOpt = $_.settings.deliveryOptimizationPriority }}
                if ($_.settings.installTimeSettings -ne $null) {{
                    $its = $_.settings.installTimeSettings
                    if ($its.startDateTime) {{ $avail = $its.startDateTime }}
                    if ($its.deadlineDateTime) {{ $deadline = $its.deadlineDateTime }}
                    if (-not $avail -and -not $deadline) {{ $avail = 'As soon as possible' }}
                }} else {{
                    $avail = 'As soon as possible'
                }}
                if ($_.settings.restartSettings -ne $null) {{
                    $rs = $_.settings.restartSettings
                    if ($rs.gracePeriodInMinutes) {{
                        $grace = ""$($rs.gracePeriodInMinutes) min""
                        if ($rs.countdownDisplayBeforeRestartInMinutes) {{
                            $grace += "" (countdown: $($rs.countdownDisplayBeforeRestartInMinutes) min)""
                        }}
                    }}
                }}
            }}

            [PSCustomObject]@{{
                Intent = $_.intent; TargetType = $ttype; TargetLabel = $label; GroupId = $gid
                GroupMode = $groupMode; Notification = $notif; AvailableTime = $avail
                DeadlineTime = $deadline; DeliveryOptimization = $delOpt
                RestartGracePeriod = $grace; FilterId = $filterId; FilterMode = $filterMode
                Source = if ($_.source) {{ $_.source }} else {{ 'direct' }}
            }}
        }})

        [PSCustomObject]@{{
            Platform = 'Intune'; Id = $app.id; DisplayName = $app.displayName
            Publisher = $app.publisher; Version = $app.displayVersion
            Description = $app.description
            CreatedDateTime = if ($app.createdDateTime) {{ $app.createdDateTime }} else {{ '' }}
            LastModifiedDateTime = if ($app.lastModifiedDateTime) {{ $app.lastModifiedDateTime }} else {{ '' }}
            InstallCommand = $app.installCommandLine; UninstallCommand = $app.uninstallCommandLine
            InstallExperience = $app.installExperience.runAsAccount
            RestartBehavior = $app.installExperience.deviceRestartBehavior
            MaxInstallTime = $app.maximumInstallationTimeInMinutes
            Developer = $app.developer; Owner = $app.owner; Notes = $app.notes
            InformationUrl = $app.informationUrl; PrivacyUrl = $app.privacyInformationUrl
            IsFeatured = if ($app.isFeatured) {{ $true }} else {{ $false }}
            DetectionType = $detType; DetectionSummary = $detSummary; DetectionScript = $detScript
            MinimumOSVersion = $minOS; Architecture = $arch; IconBase64 = $iconBase64
            MinimumFreeDiskSpaceMB = if ($app.minimumFreeDiskSpaceInMB) {{ $app.minimumFreeDiskSpaceInMB }} else {{ 0 }}
            MinimumMemoryMB = if ($app.minimumMemoryInMB) {{ $app.minimumMemoryInMB }} else {{ 0 }}
            MinimumProcessors = if ($app.minimumNumberOfProcessors) {{ $app.minimumNumberOfProcessors }} else {{ 0 }}
            MinimumCpuSpeedMHz = if ($app.minimumCpuSpeedInMHz) {{ $app.minimumCpuSpeedInMHz }} else {{ 0 }}
            SizeInBytes = if ($app.size) {{ $app.size }} else {{ 0 }}
            FileName = $app.fileName
            Requirements = $reqs
            Categories = @($app.categories | ForEach-Object {{ $_.displayName }})
            ReturnCodes = @($app.returnCodes | ForEach-Object {{
                [PSCustomObject]@{{ Code = $_.returnCode; Type = $_.type }}
            }})
            Assignments = $assignDetail; Dependencies = @(); Supersedence = @()
        }}
    }}
}}
";

        var results = await _ps.RunScriptWithTokenAsync(script, token);
        int preloaded = 0;
        foreach (var obj in results)
        {
            if (obj?.BaseObject is null) continue;
            var detail = MapDetail(obj);
            if (!string.IsNullOrEmpty(detail.Id))
            {
                _detailCache[detail.Id] = (SystemClock.UtcNow, detail);
                preloaded++;
            }
        }

        op.Complete($"preloaded {preloaded} app detail(s) via $batch for tenant {tenantId}");

        // Group name + nested resolution are called separately by the ViewModel
        // so it can update the UI between phases.
    }


    // -----------------------------------------------------------------------
    // Intune - detail (single app, fallback for cache miss)
    // -----------------------------------------------------------------------

    public async Task<AppInventoryDetail?> GetIntuneAppDetailAsync(string tenantId, string appId)
    {
        // Check detail cache (populated by preload or previous fetch)
        if (_detailCache.TryGetValue(appId, out var cached))
        {
            // Preload caches details without relationships for speed.
            // Lazy-load them on first detail view so they're available in the pane.
            if (!cached.Detail.RelationshipsLoaded)
            {
                await LoadRelationshipsAsync(tenantId, cached.Detail);
            }
            return cached.Detail;
        }

        using var op = OperationScope.Begin("Inventory.LoadIntuneDetail");

        var token = await GetTokenAsync(tenantId);
        if (token is null) { op.Complete($"no token for tenant {tenantId}"); return null; }

        var script = $@"
$header = $Global:AuthenticationHeader
$baseUrl = 'https://graph.microsoft.com/beta'
$appId = '{Escape(appId)}'

# Single call: app + assignments + categories via $expand
$app = Invoke-RestMethod -Uri ""$baseUrl/deviceAppManagement/mobileApps/$appId`?`$expand=assignments,categories"" -Headers $header -Method Get -MaximumRetryCount 5 -RetryIntervalSec 5 -ErrorAction Stop

# Dual-axis relationship classification: @odata.type (Dependency vs
# Supersedence) x targetType (child = downstream, parent = upstream).
# See LoadRelationshipsAsync for the same logic on the lazy-load path.
$depsRaw         = @()
$dependedOnByRaw = @()
$supsRaw         = @()
$supersededByRaw = @()
try {{
    $depsResp = Invoke-RestMethod -Uri ""$baseUrl/deviceAppManagement/mobileApps/$appId/relationships"" -Headers $header -Method Get -MaximumRetryCount 5 -RetryIntervalSec 5 -ErrorAction SilentlyContinue
    if ($depsResp.value) {{
        foreach ($r in $depsResp.value) {{
            $odata = $r.'@odata.type'
            $isDep = $odata -eq '#microsoft.graph.mobileAppDependency'
            $isSup = $odata -eq '#microsoft.graph.mobileAppSupersedence'
            if (-not ($isDep -or $isSup)) {{ continue }}

            $row = [PSCustomObject]@{{
                AppId       = $r.targetId
                AppName     = $r.targetDisplayName
                Type        = if ($isDep) {{ 'Dependency' }}
                              else        {{ if ($r.supersedenceType) {{ $r.supersedenceType }} else {{ 'replace' }} }}
                AutoInstall = $isDep -and [string]::Equals($r.dependencyType, 'autoInstall', [StringComparison]::OrdinalIgnoreCase)
            }}

            if ($isDep) {{
                if ($r.targetType -eq 'child') {{ $depsRaw         += $row }}
                else                            {{ $dependedOnByRaw += $row }}
            }} else {{
                if ($r.targetType -eq 'child') {{ $supsRaw          += $row }}
                else                            {{ $supersededByRaw += $row }}
            }}
        }}
    }}
}} catch {{}}

# Detection rules
$detType = 'Unknown'; $detSummary = ''; $detScript = ''
if ($app.detectionRules) {{
    $rule = $app.detectionRules | Select-Object -First 1
    switch ($rule.'@odata.type') {{
        '#microsoft.graph.win32LobAppPowerShellScriptDetection' {{
            $detType = 'PowerShell Script'
            $detScript = [System.Text.Encoding]::UTF8.GetString(
                [System.Convert]::FromBase64String($rule.scriptContent))
            $detSummary = ""PowerShell script ($($detScript.Length) chars)""
        }}
        '#microsoft.graph.win32LobAppRegistryDetection' {{
            $detType = 'Registry'
            $detSummary = ""$($rule.keyPath)\$($rule.valueName) $($rule.detectionType)""
        }}
        '#microsoft.graph.win32LobAppFileSystemDetection' {{
            $detType = 'File'
            $detSummary = ""$($rule.path)\$($rule.fileOrFolderName) $($rule.detectionType)""
        }}
        '#microsoft.graph.win32LobAppProductCodeDetection' {{
            $detType = 'MSI Product Code'
            $detSummary = ""Product: $($rule.productCode)""
        }}
    }}
}}

# Requirement rules
$reqs = @()
if ($app.requirementRules) {{
    foreach ($req in $app.requirementRules) {{
        switch ($req.'@odata.type') {{
            '#microsoft.graph.win32LobAppPowerShellScriptRequirement' {{
                $scriptBody = ''
                if ($req.scriptContent) {{
                    $scriptBody = [System.Text.Encoding]::UTF8.GetString(
                        [System.Convert]::FromBase64String($req.scriptContent))
                }}
                $reqs += [PSCustomObject]@{{
                    RuleType = 'Script'
                    Summary = ""PowerShell: $($req.displayName) ($($req.detectionType) $($req.operator) $($req.detectionValue))""
                    ScriptContent = $scriptBody
                }}
            }}
            '#microsoft.graph.win32LobAppRegistryRequirement' {{
                $reqs += [PSCustomObject]@{{
                    RuleType = 'Registry'
                    Summary = ""Registry: $($req.keyPath)\$($req.valueName) $($req.detectionType)""
                    ScriptContent = ''
                }}
            }}
            '#microsoft.graph.win32LobAppFileSystemRequirement' {{
                $reqs += [PSCustomObject]@{{
                    RuleType = 'File'
                    Summary = ""File: $($req.path)\$($req.fileOrFolderName) $($req.detectionType)""
                    ScriptContent = ''
                }}
            }}
        }}
    }}
}}

$minOS = ''; $arch = ''
if ($app.minimumSupportedWindowsRelease) {{ $minOS = $app.minimumSupportedWindowsRelease }}
if ($app.applicableArchitectures) {{ $arch = $app.applicableArchitectures }}

$iconBase64 = ''
if ($app.largeIcon -and $app.largeIcon.value) {{ $iconBase64 = $app.largeIcon.value }}

# Map assignments from raw Graph API $expand result (not IntuneWin32App module objects)
$assignDetail = @($app.assignments | ForEach-Object {{
    $ttype = $_.target.'@odata.type'
    $label = ''; $gid = ''; $groupMode = 'Include'
    if ($ttype -match 'allDevices') {{ $label = 'All Devices' }}
    elseif ($ttype -match 'allLicensedUsers') {{ $label = 'All Users' }}
    elseif ($ttype -match 'exclusionGroup') {{ $gid = $_.target.groupId; $label = $gid; $groupMode = 'Exclude' }}
    elseif ($ttype -match 'group') {{ $gid = $_.target.groupId; $label = $gid }}

    $filterId = ''; $filterMode = ''
    if ($_.target.deviceAndAppManagementAssignmentFilterId) {{
        $filterId = $_.target.deviceAndAppManagementAssignmentFilterId
    }}
    if ($_.target.deviceAndAppManagementAssignmentFilterType) {{
        $filterMode = $_.target.deviceAndAppManagementAssignmentFilterType
    }}

    $avail = ''; $deadline = ''; $notif = ''; $delOpt = ''; $grace = ''
    if ($_.settings) {{
        if ($_.settings.notifications) {{ $notif = $_.settings.notifications }}
        if ($_.settings.deliveryOptimizationPriority) {{ $delOpt = $_.settings.deliveryOptimizationPriority }}
        if ($_.settings.installTimeSettings -ne $null) {{
            $its = $_.settings.installTimeSettings
            if ($its.startDateTime) {{ $avail = $its.startDateTime }}
            if ($its.deadlineDateTime) {{ $deadline = $its.deadlineDateTime }}
            if (-not $avail -and -not $deadline) {{ $avail = 'As soon as possible' }}
        }} else {{
            $avail = 'As soon as possible'
        }}
        if ($_.settings.restartSettings -ne $null) {{
            $rs = $_.settings.restartSettings
            if ($rs.gracePeriodInMinutes) {{
                $grace = ""$($rs.gracePeriodInMinutes) min""
                if ($rs.countdownDisplayBeforeRestartInMinutes) {{
                    $grace += "" (countdown: $($rs.countdownDisplayBeforeRestartInMinutes) min)""
                }}
            }}
        }}
    }}

    [PSCustomObject]@{{
        Intent = $_.intent; TargetType = $ttype; TargetLabel = $label; GroupId = $gid
        GroupMode = $groupMode; Notification = $notif; AvailableTime = $avail
        DeadlineTime = $deadline; DeliveryOptimization = $delOpt
        RestartGracePeriod = $grace; FilterId = $filterId; FilterMode = $filterMode
        Source = if ($_.source) {{ $_.source }} else {{ 'direct' }}
    }}
}})

[PSCustomObject]@{{
    Platform         = 'Intune'
    Id               = $app.id
    DisplayName      = $app.displayName
    Publisher        = $app.publisher
    Version          = $app.displayVersion
    Description      = $app.description
    CreatedDateTime  = if ($app.createdDateTime) {{ $app.createdDateTime }} else {{ '' }}
    LastModifiedDateTime = if ($app.lastModifiedDateTime) {{ $app.lastModifiedDateTime }} else {{ '' }}
    InstallCommand   = $app.installCommandLine
    UninstallCommand = $app.uninstallCommandLine
    InstallExperience = $app.installExperience.runAsAccount
    RestartBehavior  = $app.installExperience.deviceRestartBehavior
    MaxInstallTime   = $app.maximumInstallationTimeInMinutes
    Developer        = $app.developer
    Owner            = $app.owner
    Notes            = $app.notes
    InformationUrl   = $app.informationUrl
    PrivacyUrl       = $app.privacyInformationUrl
    IsFeatured       = if ($app.isFeatured) {{ $true }} else {{ $false }}
    DetectionType    = $detType
    DetectionSummary = $detSummary
    DetectionScript  = $detScript
    MinimumOSVersion = $minOS
    Architecture     = $arch
    IconBase64       = $iconBase64
    MinimumFreeDiskSpaceMB = if ($app.minimumFreeDiskSpaceInMB) {{ $app.minimumFreeDiskSpaceInMB }} else {{ 0 }}
    MinimumMemoryMB  = if ($app.minimumMemoryInMB) {{ $app.minimumMemoryInMB }} else {{ 0 }}
    MinimumProcessors = if ($app.minimumNumberOfProcessors) {{ $app.minimumNumberOfProcessors }} else {{ 0 }}
    MinimumCpuSpeedMHz = if ($app.minimumCpuSpeedInMHz) {{ $app.minimumCpuSpeedInMHz }} else {{ 0 }}
    SizeInBytes      = if ($app.size) {{ $app.size }} else {{ 0 }}
    FileName         = $app.fileName
    Requirements     = $reqs
    Categories       = @($app.categories | ForEach-Object {{ $_.displayName }})
    ReturnCodes      = @($app.returnCodes | ForEach-Object {{
        [PSCustomObject]@{{ Code = $_.returnCode; Type = $_.type }}
    }})
    Assignments      = $assignDetail
    Dependencies     = $depsRaw
    DependedOnBy     = $dependedOnByRaw
    Supersedence     = $supsRaw
    SupersededBy     = $supersededByRaw
}}
";

        var results = await _ps.RunScriptWithTokenAsync(script, token);
        if (results.Count == 0) return null;

        var detail = MapDetail(results[0]);
        detail.RelationshipsLoaded = true; // full fetch includes relationships

        _detailCache[appId] = (SystemClock.UtcNow, detail);
        op.Complete($"loaded Intune detail for '{detail.DisplayName}'");
        return detail;
    }

    /// <summary>
    /// Fetches the largeIcon base64 value for an app. This is a heavy property
    /// not included in standard $expand queries, so it's fetched on demand.
    /// </summary>
    public async Task<string?> FetchIconBase64Async(string tenantId, string appId)
    {
        var token = await GetTokenAsync(tenantId);
        if (token is null) return null;

        // SEC-14 (2026-07 audit): bind appId in a SINGLE-quoted PS literal (where
        // Escape's doubled-single-quote is the correct escaping) and expand the
        // variable into the URI, instead of interpolating single-quote-escaped
        // text directly into a DOUBLE-quoted string (wrong metacharacter set).
        var script = $@"
$header = $Global:AuthenticationHeader
$appId = '{Escape(appId)}'
$app = Invoke-RestMethod -Uri ""https://graph.microsoft.com/beta/deviceAppManagement/mobileApps/$appId`?`$select=largeIcon"" -Headers $header -Method Get -MaximumRetryCount 5 -RetryIntervalSec 5 -ErrorAction Stop
if ($app.largeIcon -and $app.largeIcon.value) {{ $app.largeIcon.value }} else {{ '' }}
";

        try
        {
            var results = await _ps.RunScriptWithTokenAsync(script, token);
            var value = results.Count > 0 ? results[0]?.BaseObject?.ToString() : null;
            if (!string.IsNullOrEmpty(value))
            {
                AppLogger.Info($"Inventory: fetched icon for app {appId} ({value.Length} chars)");
                return value;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Inventory: failed to fetch icon for app {appId}: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Fetches only the relationships (dependencies + supersedence) for a cached app
    /// and merges them into the existing detail object. Called lazily on first detail
    /// view after the preload (which skips relationships for speed).
    /// </summary>
    private async Task LoadRelationshipsAsync(string tenantId, AppInventoryDetail detail)
    {
        var token = await GetTokenAsync(tenantId);
        if (token is null) return;

        // Classification: the /mobileApps/{id}/relationships endpoint returns a
        // heterogeneous list of mobileAppDependency + mobileAppSupersedence.
        // We need BOTH axes to bucket correctly:
        //   - `@odata.type`  -> dependency vs supersedence
        //   - `targetType`   -> direction ('child' = downstream of this app,
        //                                   'parent' = upstream of this app)
        // Four buckets:
        //   Dependency  + child   -> Dependencies      (this app depends on X)
        //   Dependency  + parent  -> DependedOnBy      (X depends on this app)
        //   Supersedence + child  -> Supersedence      (this app supersedes X)
        //   Supersedence + parent -> SupersededBy      (X supersedes this app)
        var script = $@"
$header = $Global:AuthenticationHeader
$baseUrl = 'https://graph.microsoft.com/beta'
$appId = '{Escape(detail.Id)}'

$depsRaw         = @()
$dependedOnByRaw = @()
$supsRaw         = @()
$supersededByRaw = @()
try {{
    $resp = Invoke-RestMethod -Uri ""$baseUrl/deviceAppManagement/mobileApps/$appId/relationships"" -Headers $header -Method Get -MaximumRetryCount 5 -RetryIntervalSec 5 -ErrorAction SilentlyContinue
    if ($resp.value) {{
        foreach ($r in $resp.value) {{
            $odata = $r.'@odata.type'
            $isDep = $odata -eq '#microsoft.graph.mobileAppDependency'
            $isSup = $odata -eq '#microsoft.graph.mobileAppSupersedence'
            if (-not ($isDep -or $isSup)) {{ continue }}

            $row = [PSCustomObject]@{{
                AppId       = $r.targetId
                AppName     = $r.targetDisplayName
                Type        = if ($isDep) {{ 'Dependency' }}
                              else        {{ if ($r.supersedenceType) {{ $r.supersedenceType }} else {{ 'replace' }} }}
                AutoInstall = $isDep -and [string]::Equals($r.dependencyType, 'autoInstall', [StringComparison]::OrdinalIgnoreCase)
            }}

            if ($isDep) {{
                if ($r.targetType -eq 'child') {{ $depsRaw         += $row }}
                else                            {{ $dependedOnByRaw += $row }}
            }} else {{
                if ($r.targetType -eq 'child') {{ $supsRaw          += $row }}
                else                            {{ $supersededByRaw += $row }}
            }}
        }}
    }}
}} catch {{}}

[PSCustomObject]@{{
    Dependencies = $depsRaw
    DependedOnBy = $dependedOnByRaw
    Supersedence = $supsRaw
    SupersededBy = $supersededByRaw
}}
";

        try
        {
            var results = await _ps.RunScriptWithTokenAsync(script, token);
            if (results.Count > 0 && results[0]?.BaseObject is not null)
            {
                var obj = results[0];
                detail.Dependencies = MapRelationships(obj, "Dependencies");
                detail.DependedOnBy = MapRelationships(obj, "DependedOnBy");
                detail.Supersedence = MapRelationships(obj, "Supersedence");
                detail.SupersededBy = MapRelationships(obj, "SupersededBy");
                detail.RelationshipsLoaded = true;
                AppLogger.Info($"Inventory: loaded relationships for '{detail.DisplayName}' -- " +
                    $"{detail.Dependencies.Count} dep(s), {detail.DependedOnBy.Count} depended-on-by, " +
                    $"{detail.Supersedence.Count} sup(s), {detail.SupersededBy.Count} superseded-by");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Inventory: failed to load relationships for '{detail.DisplayName}': {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Token injection
    // -----------------------------------------------------------------------

    /// <summary>
    /// Gets a valid access token for the tenant. Returns null if unavailable.
    /// The token string is embedded directly in PS scripts instead of relying
    /// on global variables (which may not persist across RunspacePool runspaces).
    /// </summary>
    private async Task<MsalTokenResult?> GetTokenAsync(string tenantId)
    {
        return await _auth.TryAcquireTokenSilentForTenantAsync(tenantId);
    }

}
