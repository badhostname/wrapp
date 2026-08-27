function Test-WrappIntunePreflight {
    <#
    .SYNOPSIS
        Post-auth validation of Graph entities referenced in config.

    .DESCRIPTION
        Runs after Connect-WrappIntune to verify that categories, scope tags,
        groups, assignment filters, and dependency/supersedence targets actually
        exist in the tenant. Also checks for circular dependencies and mutual
        supersedence. Fails fast with clear errors before any app creation.

        Returns three parallel outputs for maximum compatibility:
          Errors   [string[]] - Fatal issues (existing callers use this)
          Warnings [string[]] - Non-fatal advisories (existing callers use this)
          Issues   [object[]] - Structured issue objects for the WPF GUI.
                                Each object has: FieldPath, Severity, Code,
                                Message, AttemptedValue, AllowedValues, Guidance.

    .PARAMETER Config
        The loaded Config object from ConvertFrom-Json.

    .PARAMETER TenantConfig
        The resolved IntuneTenant config for this tenant.

    .PARAMETER Packages
        The package list to validate.

    .PARAMETER DeployedApps
        Hashtable of apps already created in this run (AppName -> AppObject).

    .PARAMETER Validate
        Validation mode - logs what would happen without making API calls.

    .OUTPUTS
        [PSCustomObject] with:
            IsValid    [bool]     - True if no fatal errors
            Errors     [string[]] - Fatal issues preventing execution
            Warnings   [string[]] - Non-fatal advisories
            Issues     [object[]] - Structured issue objects (GUI-ready)
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        $Config,

        [Parameter(Mandatory = $true)]
        $TenantConfig,

        [Parameter(Mandatory = $true)]
        [array]$Packages,

        [hashtable]$DeployedApps = @{},

        [switch]$Validate
    )

    $Errors   = [System.Collections.Generic.List[string]]::new()
    $Warnings = [System.Collections.Generic.List[string]]::new()
    $Issues   = [System.Collections.Generic.List[object]]::new()

    # Helper: add an Error to both flat list and Issues
    function Add-Error {
        param([string]$FieldPath, [string]$Code, [string]$Msg, $AttemptedValue = $null, [string]$Guidance = '')
        $Errors.Add($Msg)
        $Issues.Add((New-ValidationIssue -FieldPath $FieldPath -Severity 'Error' -Code $Code -Message $Msg -AttemptedValue $AttemptedValue -Guidance $Guidance))
    }

    # Helper: add a Warning to both flat list and Issues
    function Add-Warning {
        param([string]$FieldPath, [string]$Code, [string]$Msg, $AttemptedValue = $null, [string]$Guidance = '')
        $Warnings.Add($Msg)
        $Issues.Add((New-ValidationIssue -FieldPath $FieldPath -Severity 'Warning' -Code $Code -Message $Msg -AttemptedValue $AttemptedValue -Guidance $Guidance))
    }

    # ==================================================================
    # 1. Graph connectivity test
    # ==================================================================
    try {
        # Suppress verbose for bulk app listing -- each app generates multiple
        # verbose messages that slow Graph lookups significantly when streamed to the GUI.
        $savedVerbose = $VerbosePreference
        $VerbosePreference = 'SilentlyContinue'
        $testApps = Get-IntuneWin32App -ErrorAction Stop
        $VerbosePreference = $savedVerbose
        Write-Log "Graph connectivity confirmed. Found $(@($testApps).Count) existing Win32 apps."
    }
    catch {
        Add-Error 'Graph.Connectivity' 'ENTITY_NOT_FOUND' "Graph connectivity test failed: $_" -Guidance 'Ensure the authentication token is valid and the account has the DeviceManagementApps.ReadWrite.All permission. Re-run Connect-WrappIntune if the token has expired.'
        return [PSCustomObject]@{
            IsValid  = $false
            Errors   = $Errors.ToArray()
            Warnings = $Warnings.ToArray()
            Issues   = $Issues.ToArray()
        }
    }

    # ==================================================================
    # 2. Category name resolution
    # ==================================================================
    $allCategories = @()
    foreach ($pkg in $Packages) {
        if ($pkg.Categories) {
            $allCategories += @($pkg.Categories)
        }
    }
    $scriptSection = $Config.Script.IntunePackager
    if ($scriptSection.Categories) {
        $allCategories += @($scriptSection.Categories)
    }
    $uniqueCategories = @($allCategories | Select-Object -Unique)

    if ($uniqueCategories.Count -gt 0) {
        Write-Log "Validating $($uniqueCategories.Count) category name(s)..."
        try {
            $existingCategories    = @(Get-IntuneWin32AppCategory -ErrorAction Stop)
            $existingCategoryNames = @($existingCategories | ForEach-Object { $_.displayName })

            foreach ($cat in $uniqueCategories) {
                if ($cat -notin $existingCategoryNames) {
                    $p = @{
                        FieldPath      = "Config.Script.IntunePackager.Categories[$cat]"
                        Code           = 'ENTITY_NOT_FOUND'
                        Msg            = "Category '$cat' does not exist in tenant. Available: $($existingCategoryNames -join ', ')"
                        AttemptedValue = $cat
                        Guidance       = 'Create the category in the Intune portal under Apps > App categories, or correct the category name in Config.json to match an existing category exactly (case-sensitive).'
                    }
                    Add-Error @p
                }
            }
        }
        catch {
            Add-Warning 'Config.Script.IntunePackager.Categories' 'ENTITY_NOT_FOUND' "Could not validate categories: $_" -Guidance 'Category validation requires the IntuneWin32App module to be authenticated. If authentication succeeded, this may be a transient Graph API error -- retry the operation.'
        }
    }

    # ==================================================================
    # 3. Scope tag name resolution
    # ==================================================================
    $allScopeTags = @()
    foreach ($pkg in $Packages) {
        if ($pkg.ScopeTags) {
            $allScopeTags += @($pkg.ScopeTags)
        }
    }
    if ($TenantConfig.ScopeTags) {
        $allScopeTags += @($TenantConfig.ScopeTags)
    }
    $uniqueScopeTags = @($allScopeTags | Select-Object -Unique)

    if ($uniqueScopeTags.Count -gt 0) {
        Write-Log "Validating $($uniqueScopeTags.Count) scope tag name(s)..."
        Add-Warning 'Config.IntuneTenant.ScopeTags' 'ENTITY_NOT_FOUND' "Scope tag validation deferred to app creation (resolved by IntuneWin32App module)" -Guidance 'Scope tags are validated when the app is created in Intune. If a scope tag name does not exist in the tenant, app creation will fail with a Graph API error. Verify scope tag names in the Intune portal under Tenant administration > Roles > Scope tags.'
    }

    # ==================================================================
    # 4. GroupID resolution (assignment groups)
    # ==================================================================
    if ($TenantConfig.Assignments) {
        $groupIds = @($TenantConfig.Assignments |
            Where-Object { $_.Type -eq 'Group' -and $_.GroupID } |
            ForEach-Object { $_.GroupID } |
            Select-Object -Unique)

        if ($groupIds.Count -gt 0) {
            Write-Log "Validating $($groupIds.Count) assignment group ID(s)..."
            $guidPattern = '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
            $headers = $Global:AuthenticationHeader

            foreach ($gid in $groupIds) {
                $isGuid = $gid -match $guidPattern
                try {
                    if ($isGuid) {
                        # Direct lookup by GUID via Graph API
                        if ($headers) {
                            $uri = "https://graph.microsoft.com/v1.0/groups/$gid`?`$select=id,displayName"
                            $response = Invoke-RestMethod -Uri $uri -Headers $headers -Method Get -ErrorAction Stop
                            if (-not $response -or -not $response.id) {
                                $p = @{
                                    FieldPath      = "Config.IntuneTenant.Assignments.GroupID[$gid]"
                                    Code           = 'ENTITY_NOT_FOUND'
                                    Msg            = "Group ID '$gid' not found in Entra ID"
                                    AttemptedValue = $gid
                                    Guidance       = 'Copy the Object ID from the Entra ID portal under Groups. The group must exist in the same tenant as the app deployment target.'
                                }
                                Add-Error @p
                            }
                            else {
                                Write-Log "  Group GUID '$gid' verified: $($response.displayName)"
                            }
                        }
                        else {
                            $p = @{
                                FieldPath      = "Config.IntuneTenant.Assignments.GroupID[$gid]"
                                Code           = 'ENTITY_NOT_FOUND'
                                Msg            = "Could not validate group GUID '$gid' - auth header unavailable"
                                AttemptedValue = $gid
                                Guidance       = 'Re-run Connect-WrappIntune to refresh the authentication token, then retry validation.'
                            }
                            Add-Warning @p
                        }
                    }
                    else {
                        # Display name -- use $filter query
                        if ($headers) {
                            $encodedName = [System.Uri]::EscapeDataString($gid)
                            $uri = "https://graph.microsoft.com/v1.0/groups?`$filter=displayName eq '$encodedName'&`$select=id,displayName&`$top=1"
                            $result = Invoke-RestMethod -Uri $uri -Headers $headers -Method Get -ErrorAction Stop
                            if (-not $result.value -or @($result.value).Count -eq 0) {
                                $p = @{
                                    FieldPath      = "Config.IntuneTenant.Assignments.GroupID[$gid]"
                                    Code           = 'ENTITY_NOT_FOUND'
                                    Msg            = "Group name '$gid' not found in Entra ID"
                                    AttemptedValue = $gid
                                    Guidance       = 'Verify the group display name matches exactly (case-sensitive) in the Entra ID portal under Groups, or use the group Object ID (GUID) instead.'
                                }
                                Add-Error @p
                            }
                            else {
                                Write-Log "  Group name '$gid' resolved to ID: $($result.value[0].id)"
                            }
                        }
                        else {
                            $p = @{
                                FieldPath      = "Config.IntuneTenant.Assignments.GroupID[$gid]"
                                Code           = 'ENTITY_NOT_FOUND'
                                Msg            = "Could not validate group name '$gid' - auth header unavailable"
                                AttemptedValue = $gid
                                Guidance       = 'Re-run Connect-WrappIntune to refresh the authentication token, then retry validation.'
                            }
                            Add-Warning @p
                        }
                    }
                }
                catch {
                    if ($_.Exception.Response.StatusCode -eq 404) {
                        $p = @{
                            FieldPath      = "Config.IntuneTenant.Assignments.GroupID[$gid]"
                            Code           = 'ENTITY_NOT_FOUND'
                            Msg            = "Group '$gid' not found in Entra ID"
                            AttemptedValue = $gid
                            Guidance       = 'Verify the group exists in the Entra ID portal under Groups. Use the Object ID (GUID) for most reliable lookups.'
                        }
                        Add-Error @p
                    }
                    else {
                        $p = @{
                            FieldPath      = "Config.IntuneTenant.Assignments.GroupID[$gid]"
                            Code           = 'ENTITY_NOT_FOUND'
                            Msg            = "Could not validate group '$gid': $_"
                            AttemptedValue = $gid
                            Guidance       = 'This may be a transient Graph API error or a permissions issue. Ensure the account has Directory.Read.All or Group.Read.All permission to query groups.'
                        }
                        Add-Warning @p
                    }
                }
            }
        }
    }

    # ==================================================================
    # 5. FilterName resolution (assignment filters)
    # ==================================================================
    if ($TenantConfig.Assignments) {
        $filterNames = @($TenantConfig.Assignments |
            Where-Object { $_.FilterName } |
            ForEach-Object { $_.FilterName } |
            Select-Object -Unique)

        if ($filterNames.Count -gt 0) {
            Write-Log "Validating $($filterNames.Count) assignment filter name(s)..."
            try {
                $headers = $Global:AuthenticationHeader
                if ($headers) {
                    $filtersUri    = "https://graph.microsoft.com/beta/deviceManagement/assignmentFilters"
                    $filtersResult = Invoke-RestMethod -Uri $filtersUri -Headers $headers -Method Get -ErrorAction Stop
                    $existingFilterNames = @($filtersResult.value | ForEach-Object { $_.displayName })

                    foreach ($fn in $filterNames) {
                        if ($fn -notin $existingFilterNames) {
                            $p = @{
                                FieldPath      = "Config.IntuneTenant.Assignments.FilterName[$fn]"
                                Code           = 'ENTITY_NOT_FOUND'
                                Msg            = "Assignment filter '$fn' does not exist in tenant"
                                AttemptedValue = $fn
                                Guidance       = 'Create the assignment filter in the Intune portal under Devices > Filters, or correct the filter name in Config.json to match an existing filter exactly (case-sensitive).'
                            }
                            Add-Error @p
                        }
                    }
                }
                else {
                    Add-Warning 'Config.IntuneTenant.Assignments.FilterName' 'ENTITY_NOT_FOUND' "Could not validate assignment filters - auth header unavailable" -Guidance 'Re-run Connect-WrappIntune to refresh the authentication token, then retry validation.'
                }
            }
            catch {
                Add-Warning 'Config.IntuneTenant.Assignments.FilterName' 'ENTITY_NOT_FOUND' "Could not validate assignment filters: $_" -Guidance 'This may be a transient Graph API error. Retry the operation, or validate filter names manually in the Intune portal under Devices > Filters.'
            }
        }
    }

    # ==================================================================
    # 6. Dependency/supersedence target existence
    # ==================================================================
    $targetAppIds = @{}

    foreach ($pkg in $Packages) {
        $pLabel = if ($pkg.AppName) { $pkg.AppName } else { '(unnamed)' }
        $pBase  = "Config.Script.IntunePackager.Packages[$pLabel]"

        if ($pkg.Dependencies) {
            foreach ($dep in $pkg.Dependencies) {
                $targetId   = $dep.TargetAppID
                $targetName = $dep.TargetAppName

                if ($targetId) {
                    if (-not $targetAppIds.ContainsKey($targetId)) {
                        try {
                            $existingApp = Get-IntuneWin32App -ID $targetId -ErrorAction Stop
                            $targetAppIds[$targetId] = ($null -ne $existingApp)
                        }
                        catch {
                            $targetAppIds[$targetId] = $false
                        }
                    }
                    if (-not $targetAppIds[$targetId]) {
                        $foundInRun = $false
                        foreach ($dKey in $DeployedApps.Keys) {
                            if ($DeployedApps[$dKey].Id -eq $targetId) { $foundInRun = $true; break }
                        }
                        if (-not $foundInRun) {
                            $p = @{
                                FieldPath      = "$pBase.Dependencies[$targetId].TargetAppID"
                                Code           = 'ENTITY_NOT_FOUND'
                                Msg            = "Package '${pLabel}': Dependency target app ID '$targetId' not found in Intune"
                                AttemptedValue = $targetId
                                Guidance       = 'Verify the TargetAppID is correct in the Intune portal. If the dependency app is in the current Config.json package list, use TargetAppName instead of TargetAppID so it is resolved at runtime.'
                            }
                            Add-Error @p
                        }
                    }
                }
                elseif ($targetName) {
                    $inPackages = $Packages | Where-Object { $_.AppName -eq $targetName }
                    $inDeployed = $DeployedApps.ContainsKey($targetName)
                    if (-not $inPackages -and -not $inDeployed) {
                        try {
                            $found = Get-IntuneWin32App -DisplayName $targetName -ErrorAction Stop
                            if (-not $found) {
                                $p = @{
                                    FieldPath      = "$pBase.Dependencies[$targetName].TargetAppName"
                                    Code           = 'ENTITY_NOT_FOUND'
                                    Msg            = "Package '${pLabel}': Dependency target '$targetName' not found in Intune or current package list"
                                    AttemptedValue = $targetName
                                    Guidance       = 'The dependency app must exist in Intune before this app is created, or must be in the current Config.json package list so it is created in the same run. Check the spelling of TargetAppName -- it must match the Intune display name exactly.'
                                }
                                Add-Error @p
                            }
                        }
                        catch {
                            $p = @{
                                FieldPath      = "$pBase.Dependencies[$targetName].TargetAppName"
                                Code           = 'ENTITY_NOT_FOUND'
                                Msg            = "Package '${pLabel}': Dependency target '$targetName' not found in Intune or current package list"
                                AttemptedValue = $targetName
                                Guidance       = 'The dependency app must exist in Intune before this app is created, or must be in the current Config.json package list so it is created in the same run. Check the spelling of TargetAppName -- it must match the Intune display name exactly.'
                            }
                            Add-Error @p
                        }
                    }
                }
            }
        }

        if ($pkg.Supersedence) {
            foreach ($sup in $pkg.Supersedence) {
                $targetId   = $sup.TargetAppID
                $targetName = $sup.TargetAppName

                if ($targetId) {
                    if (-not $targetAppIds.ContainsKey($targetId)) {
                        try {
                            $existingApp = Get-IntuneWin32App -ID $targetId -ErrorAction Stop
                            $targetAppIds[$targetId] = ($null -ne $existingApp)
                        }
                        catch {
                            $targetAppIds[$targetId] = $false
                        }
                    }
                    if (-not $targetAppIds[$targetId]) {
                        $foundInRun = $false
                        foreach ($dKey in $DeployedApps.Keys) {
                            if ($DeployedApps[$dKey].Id -eq $targetId) { $foundInRun = $true; break }
                        }
                        if (-not $foundInRun) {
                            $p = @{
                                FieldPath      = "$pBase.Supersedence[$targetId].TargetAppID"
                                Code           = 'ENTITY_NOT_FOUND'
                                Msg            = "Package '${pLabel}': Supersedence target app ID '$targetId' not found in Intune"
                                AttemptedValue = $targetId
                                Guidance       = 'The superseded app must already exist in Intune. Verify the TargetAppID in the Intune portal. If you are creating the superseded app in the same run, use TargetAppName instead.'
                            }
                            Add-Error @p
                        }
                    }
                }
                elseif ($targetName) {
                    $inPackages = $Packages | Where-Object { $_.AppName -eq $targetName }
                    $inDeployed = $DeployedApps.ContainsKey($targetName)
                    if (-not $inPackages -and -not $inDeployed) {
                        try {
                            $found = Get-IntuneWin32App -DisplayName $targetName -ErrorAction Stop
                            if (-not $found) {
                                $p = @{
                                    FieldPath      = "$pBase.Supersedence[$targetName].TargetAppName"
                                    Code           = 'ENTITY_NOT_FOUND'
                                    Msg            = "Package '${pLabel}': Supersedence target '$targetName' not found in Intune or current package list"
                                    AttemptedValue = $targetName
                                    Guidance       = 'The superseded app must exist in Intune. Check the spelling -- TargetAppName must match the Intune display name exactly. Alternatively, use TargetAppID with the GUID from the Intune portal.'
                                }
                                Add-Error @p
                            }
                        }
                        catch {
                            $p = @{
                                FieldPath      = "$pBase.Supersedence[$targetName].TargetAppName"
                                Code           = 'ENTITY_NOT_FOUND'
                                Msg            = "Package '${pLabel}': Supersedence target '$targetName' not found in Intune or current package list"
                                AttemptedValue = $targetName
                                Guidance       = 'The superseded app must exist in Intune. Check the spelling -- TargetAppName must match the Intune display name exactly. Alternatively, use TargetAppID with the GUID from the Intune portal.'
                            }
                            Add-Error @p
                        }
                    }
                }
            }
        }
    }

    # ==================================================================
    # 7. Circular dependency detection (DFS)
    # ==================================================================
    $adjList = @{}
    foreach ($pkg in $Packages) {
        if ($pkg.AppName -and $pkg.Dependencies) {
            $adjList[$pkg.AppName] = @($pkg.Dependencies |
                ForEach-Object { if ($_.TargetAppName) { $_.TargetAppName } } |
                Where-Object { $_ })
        }
    }

    if ($adjList.Keys.Count -gt 0) {
        $visited = @{}
        $inStack = @{}

        function Test-CycleDFS {
            param([string]$Node)
            $visited[$Node] = $true
            $inStack[$Node] = $true

            if ($adjList.ContainsKey($Node)) {
                foreach ($neighbor in $adjList[$Node]) {
                    if (-not $visited.ContainsKey($neighbor)) {
                        $cycle = Test-CycleDFS -Node $neighbor
                        if ($cycle) { return $true }
                    }
                    elseif ($inStack.ContainsKey($neighbor) -and $inStack[$neighbor]) {
                        return $true
                    }
                }
            }
            $inStack[$Node] = $false
            return $false
        }

        foreach ($node in $adjList.Keys) {
            if (-not $visited.ContainsKey($node)) {
                $hasCycle = Test-CycleDFS -Node $node
                if ($hasCycle) {
                    $p = @{
                        FieldPath      = "Config.Script.IntunePackager.Packages[$node].Dependencies"
                        Code           = 'CIRCULAR_DEP'
                        Msg            = "Circular dependency detected involving package '$node'"
                        AttemptedValue = $node
                        Guidance       = 'A circular dependency means A depends on B which depends on A (directly or via a chain). Intune rejects circular dependencies. Review the Dependencies entries for all packages in the cycle and remove the circular reference.'
                    }
                    Add-Error @p
                }
            }
        }
    }

    # ==================================================================
    # 8. Mutual supersedence check (A supersedes B and B supersedes A)
    # ==================================================================
    $supMap = @{}
    foreach ($pkg in $Packages) {
        if ($pkg.AppName -and $pkg.Supersedence) {
            $supMap[$pkg.AppName] = @($pkg.Supersedence |
                ForEach-Object { if ($_.TargetAppName) { $_.TargetAppName } } |
                Where-Object { $_ })
        }
    }

    foreach ($appA in $supMap.Keys) {
        foreach ($targetB in $supMap[$appA]) {
            if ($supMap.ContainsKey($targetB) -and $appA -in $supMap[$targetB]) {
                Add-Error "Config.Script.IntunePackager.Packages[$appA].Supersedence" 'MUTUAL_SUPERSEDENCE' "Mutual supersedence detected: '$appA' and '$targetB' each supersede the other" -Guidance 'Mutual supersedence is logically invalid -- if A replaces B and B replaces A, Intune cannot determine the correct install order. Keep supersedence unidirectional: the newer version supersedes the older version only.'
            }
        }
    }

    return [PSCustomObject]@{
        IsValid  = ($Errors.Count -eq 0)
        Errors   = $Errors.ToArray()
        Warnings = $Warnings.ToArray()
        Issues   = $Issues.ToArray()
    }
}
