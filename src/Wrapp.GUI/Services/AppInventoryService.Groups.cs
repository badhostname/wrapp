using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Microsoft Graph group-name + nested-membership resolution for
/// <see cref="AppInventoryService"/>.
///
/// <para>Two phases: (1) batch-resolve display names for every group ID
/// referenced by cached app assignments, then (2) walk transitive group
/// memberOf graphs so the inventory tree can show nested-group context.
/// Both write into the per-tenant caches (<c>_groupNameCache</c>,
/// <c>_nestedGroupCache</c>, <c>_memberCountCache</c>) and apply results
/// to assignments stored in the shared <c>_detailCache</c>.</para>
/// </summary>
public partial class AppInventoryService
{
    /// <summary>Resolves group names for all cached details. Call after PreloadIntuneDetailsAsync.</summary>
    public async Task ResolveGroupNamesForTenantAsync(string tenantId)
    {
        var token = await GetTokenAsync(tenantId);
        if (token is null) return;
        await ResolveGroupNamesAsync(tenantId, token);
    }

    /// <summary>Resolves nested group membership for all cached details. Call after group names are resolved.</summary>
    public async Task ResolveNestedGroupsForTenantAsync(string tenantId)
    {
        await ResolveNestedGroupsAsync(tenantId, null, CancellationToken.None);
    }

    // -----------------------------------------------------------------------
    // Group name resolution (per-tenant, batched)
    // -----------------------------------------------------------------------

    private async Task ResolveGroupNamesAsync(string tenantId, MsalTokenResult token)
    {
        // Collect all unique group GUIDs from cached details for this tenant
        var groupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _detailCache)
        {
            foreach (var a in kv.Value.Detail.Assignments)
            {
                if (!string.IsNullOrEmpty(a.GroupId) && Guid.TryParse(a.GroupId, out _))
                    groupIds.Add(a.GroupId);
            }
        }

        if (!_groupNameCache.TryGetValue(tenantId, out var cache))
        {
            cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _groupNameCache[tenantId] = cache;
        }

        var unresolved = groupIds.Where(g => !cache.ContainsKey(g)).ToList();
        if (unresolved.Count == 0) return;

        using var op = OperationScope.Begin("Inventory.ResolveGroupNames");

        // Use $batch to resolve group names (20 per batch)
        var idList = string.Join(",", unresolved.Select(g => $"'{Escape(g)}'"));
        var script = $@"
$header = $Global:AuthenticationHeader
$baseUrl = 'https://graph.microsoft.com/v1.0'
$groupIds = @({idList})

for ($i = 0; $i -lt $groupIds.Count; $i += 20) {{
    $chunk = $groupIds[$i..[Math]::Min($i + 19, $groupIds.Count - 1)]
    $requests = @()
    $idx = 1
    foreach ($gid in $chunk) {{
        $requests += @{{
            id     = ""$idx""
            method = 'GET'
            url    = ""/groups/$gid`?`$select=id,displayName""
        }}
        $idx++
    }}

    $batchBody = @{{ requests = $requests }} | ConvertTo-Json -Depth 5 -Compress
    try {{
        $batchResult = Invoke-RestMethod -Uri ""$baseUrl/`$batch"" -Headers $header -Method Post -Body $batchBody -ContentType 'application/json' -MaximumRetryCount 5 -RetryIntervalSec 5 -ErrorAction Stop
        foreach ($resp in $batchResult.responses) {{
            if ($resp.status -eq 200 -and $resp.body.id -and $resp.body.displayName) {{
                [PSCustomObject]@{{ GroupId = $resp.body.id; DisplayName = $resp.body.displayName }}
            }}
        }}
    }} catch {{
        Write-Warning ""Group batch resolve failed: $_""
    }}
}}
";

        try
        {
            var results = await _ps.RunScriptWithTokenAsync(script, token);
            foreach (var obj in results)
            {
                if (obj?.BaseObject is null) continue;
                var gid = GetStr(obj, "GroupId");
                var name = GetStr(obj, "DisplayName");
                if (!string.IsNullOrEmpty(gid) && !string.IsNullOrEmpty(name))
                    cache[gid] = name;
            }

            ApplyGroupNamesToCache(tenantId);

            op.Complete($"resolved {cache.Count} group name(s) for tenant {tenantId}");
        }
        catch (Exception ex)
        {
            op.Fail(ex, $"tenant={tenantId}");
        }
    }

    private void ApplyGroupNamesToCache(string tenantId)
    {
        if (!_groupNameCache.TryGetValue(tenantId, out var cache)) return;

        foreach (var kv in _detailCache)
        {
            foreach (var a in kv.Value.Detail.Assignments)
            {
                if (string.IsNullOrEmpty(a.GroupId)) continue;

                if (cache.TryGetValue(a.GroupId, out var name)
                    && (a.TargetLabel == a.GroupId || string.IsNullOrEmpty(a.TargetLabel)))
                {
                    a.TargetLabel = name;
                }
                else if (Guid.TryParse(a.GroupId, out _)
                    && (a.TargetLabel == a.GroupId || string.IsNullOrEmpty(a.TargetLabel)))
                {
                    // Group GUID was not resolved -- likely deleted
                    a.TargetLabel = $"{a.GroupId} (Not Found)";
                }
            }
        }
    }
    // -----------------------------------------------------------------------
    // Nested group membership resolution
    // -----------------------------------------------------------------------

    private const int MaxNestingDepth = 10;

    /// <summary>
    /// Resolves nested Entra ID group membership for all assignment groups.
    /// Uses BFS with $batch to minimize Graph API calls.
    /// </summary>
    public async Task ResolveNestedGroupsAsync(
        string tenantId,
        IProgress<(int Done, int Total)>? progress,
        CancellationToken ct)
    {
        var token = await GetTokenAsync(tenantId);
        if (token is null) return;

        // Collect unique assignment group IDs from cached details
        var rootGroupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _detailCache)
        {
            foreach (var a in kv.Value.Detail.Assignments)
            {
                if (!string.IsNullOrEmpty(a.GroupId) && Guid.TryParse(a.GroupId, out _))
                    rootGroupIds.Add(a.GroupId);
            }
        }

        if (rootGroupIds.Count == 0) return;

        if (!_nestedGroupCache.TryGetValue(tenantId, out var cache))
        {
            cache = new Dictionary<string, NestedGroupData>(StringComparer.OrdinalIgnoreCase);
            _nestedGroupCache[tenantId] = cache;
        }

        var toResolve = rootGroupIds.Where(g => !cache.ContainsKey(g)).ToList();
        if (toResolve.Count == 0)
        {
            // All groups already resolved -- just apply to cache
            ApplyNestedDataToCache(tenantId);
            return;
        }

        using var op = OperationScope.Begin("Inventory.ResolveNestedGroups");

        // BFS: level by level, batch 20 groups per $batch call
        // parentChildMap: parentId -> list of (childId, childName)
        var parentChildMap = new Dictionary<string, List<ChildGroupInfo>>(StringComparer.OrdinalIgnoreCase);
        var allGroupInfo = new Dictionary<string, ChildGroupInfo>(StringComparer.OrdinalIgnoreCase);
        var allGroupNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Seed with root groups (names already in _groupNameCache)
        if (_groupNameCache.TryGetValue(tenantId, out var nameCache))
        {
            foreach (var g in toResolve)
            {
                if (nameCache.TryGetValue(g, out var name))
                    allGroupNames[g] = name;
            }
        }

        var currentLevel = new List<string>(toResolve);
        foreach (var g in toResolve) visited.Add(g);
        int depth = 0;

        while (currentLevel.Count > 0 && depth < MaxNestingDepth)
        {
            ct.ThrowIfCancellationRequested();
            depth++;

            var children = await FetchDirectChildGroupsAsync(currentLevel, token, ct);

            var nextLevel = new List<string>();
            foreach (var child in children)
            {
                if (!parentChildMap.TryGetValue(child.ParentId, out var list))
                {
                    list = new List<ChildGroupInfo>();
                    parentChildMap[child.ParentId] = list;
                }
                list.Add(child);
                allGroupNames[child.ChildId] = child.ChildName;
                allGroupInfo[child.ChildId] = child;

                if (visited.Add(child.ChildId))
                    nextLevel.Add(child.ChildId);
            }

            currentLevel = nextLevel;
            progress?.Report((toResolve.Count - currentLevel.Count, toResolve.Count));
        }

        // Build tree and flat index only for root groups that have children in the map
        foreach (var rootId in toResolve)
        {
            if (!parentChildMap.ContainsKey(rootId)) continue;

            var treeRoot = BuildTree(rootId, allGroupNames.GetValueOrDefault(rootId, rootId),
                parentChildMap, allGroupInfo, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            var flatNames = new List<string>();
            var flatIds = new List<string>();
            bool hasCircular = false;
            int maxDepthFound = 0;
            CollectFlat(treeRoot, flatNames, flatIds, ref hasCircular, 0, ref maxDepthFound);

            if (flatNames.Count == 0) continue;

            cache[rootId] = new NestedGroupData
            {
                RootGroupId = rootId,
                RootDisplayName = allGroupNames.GetValueOrDefault(rootId, rootId),
                TreeRoot = treeRoot,
                AllNestedGroupNames = flatNames,
                AllNestedGroupIds = flatIds,
                MaxDepth = maxDepthFound,
                HasCircularReference = hasCircular,
            };
        }

        var allGroupIds = visited.ToList();
        if (allGroupIds.Count > 0)
        {
            var counts = await BatchFetchMemberCountsAsync(allGroupIds, token, ct);
            foreach (var kv in cache)
            {
                if (kv.Value.TreeRoot is not null)
                    ApplyMemberCounts(kv.Value.TreeRoot, counts);
            }
        }

        ApplyNestedDataToCache(tenantId);

        op.Complete($"{cache.Count} group(s), max depth {depth}, tenant {tenantId}");
        progress?.Report((toResolve.Count, toResolve.Count));
    }

    /// <summary>Batch-fetches direct child groups for a list of parent group IDs.</summary>
    private async Task<List<ChildGroupInfo>> FetchDirectChildGroupsAsync(
        List<string> groupIds, MsalTokenResult token, CancellationToken ct)
    {
        var idList = string.Join(",", groupIds.Select(g => $"'{Escape(g)}'"));
        var script = $@"
$header = $Global:AuthenticationHeader
$headerClone = @{{}}
foreach ($k in $header.Keys) {{ $headerClone[$k] = $header[$k] }}
$headerClone['ConsistencyLevel'] = 'eventual'
$baseUrl = 'https://graph.microsoft.com/v1.0'
$groupIds = @({idList})

for ($i = 0; $i -lt $groupIds.Count; $i += 20) {{
    $chunk = $groupIds[$i..[Math]::Min($i + 19, $groupIds.Count - 1)]
    $requests = @()
    $idx = 1
    foreach ($gid in $chunk) {{
        $requests += @{{
            id      = ""$idx""
            method  = 'GET'
            url     = ""/groups/$gid/members/microsoft.graph.group?`$select=id,displayName,description,mail,securityEnabled,groupTypes,createdDateTime,visibility&`$top=999""
            headers = @{{ 'ConsistencyLevel' = 'eventual' }}
        }}
        $idx++
    }}

    $batchBody = @{{ requests = $requests }} | ConvertTo-Json -Depth 5 -Compress
    try {{
        $batchResult = Invoke-RestMethod -Uri ""$baseUrl/`$batch"" -Headers $headerClone -Method Post -Body $batchBody -ContentType 'application/json' -MaximumRetryCount 5 -RetryIntervalSec 5 -ErrorAction Stop
        foreach ($resp in $batchResult.responses) {{
            $parentIdx = [int]$resp.id - 1
            $parentId = $chunk[$parentIdx]
            if ($resp.status -eq 200 -and $resp.body.value) {{
                foreach ($child in $resp.body.value) {{
                    $gType = 'Security'
                    if ($child.groupTypes -and $child.groupTypes -contains 'Unified') {{ $gType = 'Microsoft 365' }}
                    if ($child.groupTypes -and $child.groupTypes -contains 'DynamicMembership') {{ $gType = 'Dynamic' }}
                    [PSCustomObject]@{{
                        ParentId = $parentId; ChildId = $child.id; ChildName = $child.displayName
                        Description = if ($child.description) {{ $child.description }} else {{ '' }}
                        Mail = if ($child.mail) {{ $child.mail }} else {{ '' }}
                        SecurityEnabled = if ($child.securityEnabled) {{ $true }} else {{ $false }}
                        GroupType = $gType
                        CreatedDateTime = if ($child.createdDateTime) {{ $child.createdDateTime }} else {{ '' }}
                        Visibility = if ($child.visibility) {{ $child.visibility }} else {{ '' }}
                    }}
                }}
            }}
        }}
    }} catch {{
        Write-Warning ""Nested group batch failed: $_""
    }}
}}
";

        ct.ThrowIfCancellationRequested();
        var results = await _ps.RunScriptWithTokenAsync(script, token);

        var output = new List<ChildGroupInfo>();
        foreach (var obj in results)
        {
            if (obj?.BaseObject is null) continue;
            var parentId = GetStr(obj, "ParentId");
            var childId = GetStr(obj, "ChildId");
            if (string.IsNullOrEmpty(parentId) || string.IsNullOrEmpty(childId)) continue;
            output.Add(new ChildGroupInfo
            {
                ParentId = parentId,
                ChildId = childId,
                ChildName = GetStr(obj, "ChildName"),
                Description = GetStr(obj, "Description"),
                Mail = GetStr(obj, "Mail"),
                SecurityEnabled = GetBool(obj, "SecurityEnabled"),
                GroupType = GetStr(obj, "GroupType"),
                CreatedDateTime = GetStr(obj, "CreatedDateTime"),
                Visibility = GetStr(obj, "Visibility"),
            });
        }
        return output;
    }

    private record ChildGroupInfo
    {
        public string ParentId { get; init; } = "";
        public string ChildId { get; init; } = "";
        public string ChildName { get; init; } = "";
        public string Description { get; init; } = "";
        public string Mail { get; init; } = "";
        public bool SecurityEnabled { get; init; }
        public string GroupType { get; init; } = "";
        public string CreatedDateTime { get; init; } = "";
        public string Visibility { get; init; } = "";
    }

    private static NestedGroupNode BuildTree(
        string groupId, string displayName,
        Dictionary<string, List<ChildGroupInfo>> parentChildMap,
        Dictionary<string, ChildGroupInfo> allGroupInfo,
        HashSet<string> path)
    {
        if (!path.Add(groupId))
            return new NestedGroupNode { GroupId = groupId, DisplayName = displayName, IsCircular = true };

        var info = allGroupInfo.GetValueOrDefault(groupId);
        var node = new NestedGroupNode
        {
            GroupId = groupId,
            DisplayName = displayName,
            Description = info?.Description ?? "",
            Mail = info?.Mail ?? "",
            SecurityEnabled = info?.SecurityEnabled ?? false,
            GroupType = info?.GroupType ?? "",
            CreatedDateTime = info?.CreatedDateTime ?? "",
            Visibility = info?.Visibility ?? "",
        };

        if (parentChildMap.TryGetValue(groupId, out var children))
        {
            foreach (var child in children)
            {
                node.Children.Add(BuildTree(child.ChildId, child.ChildName, parentChildMap, allGroupInfo, path));
            }
        }

        path.Remove(groupId);
        return node;
    }

    private static void CollectFlat(
        NestedGroupNode node, List<string> names, List<string> ids,
        ref bool hasCircular, int depth, ref int maxDepth)
    {
        if (node.IsCircular) { hasCircular = true; return; }
        // Skip root (depth 0) -- only collect children
        if (depth > 0)
        {
            names.Add(node.DisplayName);
            ids.Add(node.GroupId);
        }
        if (depth > maxDepth) maxDepth = depth;

        foreach (var child in node.Children)
            CollectFlat(child, names, ids, ref hasCircular, depth + 1, ref maxDepth);
    }

    private void ApplyNestedDataToCache(string tenantId)
    {
        if (!_nestedGroupCache.TryGetValue(tenantId, out var cache)) return;

        foreach (var kv in _detailCache)
        {
            foreach (var a in kv.Value.Detail.Assignments)
            {
                if (!string.IsNullOrEmpty(a.GroupId) && cache.TryGetValue(a.GroupId, out var data))
                    a.NestedGroups = data;
            }
        }
    }

    /// <summary>Batch-fetches member counts for all group IDs via $batch.</summary>
    private async Task<Dictionary<string, int>> BatchFetchMemberCountsAsync(
        List<string> groupIds, MsalTokenResult token, CancellationToken ct)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var idList = string.Join(",", groupIds.Select(g => $"'{Escape(g)}'"));

        var script = $@"
$header = $Global:AuthenticationHeader
$headerClone = @{{}}
foreach ($k in $header.Keys) {{ $headerClone[$k] = $header[$k] }}
$headerClone['ConsistencyLevel'] = 'eventual'
$baseUrl = 'https://graph.microsoft.com/v1.0'
$groupIds = @({idList})

for ($i = 0; $i -lt $groupIds.Count; $i += 20) {{
    $chunk = $groupIds[$i..[Math]::Min($i + 19, $groupIds.Count - 1)]
    $requests = @()
    $idx = 1
    foreach ($gid in $chunk) {{
        $requests += @{{
            id      = ""$idx""
            method  = 'GET'
            url     = ""/groups/$gid/members/`$count""
            headers = @{{ 'ConsistencyLevel' = 'eventual' }}
        }}
        $idx++
    }}

    $batchBody = @{{ requests = $requests }} | ConvertTo-Json -Depth 5 -Compress
    try {{
        $batchResult = Invoke-RestMethod -Uri ""$baseUrl/`$batch"" -Headers $headerClone -Method Post -Body $batchBody -ContentType 'application/json' -MaximumRetryCount 5 -RetryIntervalSec 5 -ErrorAction Stop
        foreach ($resp in $batchResult.responses) {{
            $parentIdx = [int]$resp.id - 1
            $gid = $chunk[$parentIdx]
            $count = -1
            if ($resp.status -eq 200) {{ $count = [int]$resp.body }}
            [PSCustomObject]@{{ GroupId = $gid; Count = $count }}
        }}
    }} catch {{}}
}}
";

        ct.ThrowIfCancellationRequested();
        var results = await _ps.RunScriptWithTokenAsync(script, token);
        foreach (var obj in results)
        {
            if (obj?.BaseObject is null) continue;
            var gid = GetStr(obj, "GroupId");
            var count = GetInt(obj, "Count");
            if (!string.IsNullOrEmpty(gid) && count >= 0)
            {
                counts[gid] = count;
                _memberCountCache[gid] = count; // also populate the on-demand cache
            }
        }

        AppLogger.Info($"Inventory: batch-resolved member counts for {counts.Count} group(s)");
        return counts;
    }

    private static void ApplyMemberCounts(NestedGroupNode node, Dictionary<string, int> counts)
    {
        if (counts.TryGetValue(node.GroupId, out var count))
            node.MemberCount = count;
        foreach (var child in node.Children)
            ApplyMemberCounts(child, counts);
    }

}
