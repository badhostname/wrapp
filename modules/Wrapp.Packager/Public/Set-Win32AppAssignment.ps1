function Set-Win32AppAssignment {
    <#
    .SYNOPSIS
        Applies validated assignment rules to a Win32 App in Intune.

    .DESCRIPTION
        Supports AllUsers, AllDevices, and Group (Include/Exclude) assignment types.
        Enforces intent-specific validation, merges defaults, and routes to the
        appropriate Add-IntuneWin32AppAssignment* function.

    .PARAMETER AppId
        The ID of the target Win32 App in Intune.

    .PARAMETER Assignments
        One or more hashtables representing assignment configuration objects.

    .PARAMETER AssignmentDefaults
        Optional default values applied to each assignment.

    .PARAMETER Validate
        Validation mode - logs what would happen without making API calls.

    .OUTPUTS
        [PSCustomObject] with: Applied [array], Skipped [array], Errors [array]
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$AppId,

        [Parameter(Mandatory = $true)]
        [array]$Assignments,

        [hashtable]$AssignmentDefaults,

        [switch]$Validate
    )

    # Intent-specific disallowed keys
    $IntentDisallowedKeys = @{
        required                   = @()
        available                  = @('deadlinetime', 'uselocaltime')
        availablewithoutenrollment = @('deadlinetime', 'uselocaltime')
        uninstall                  = @('availabletime', 'deadlinetime', 'uselocaltime')
    }

    # Map Config.json field names to IntuneWin32App parameter names (lowercase)
    $FieldNameMap = @{
        'restartgraceperiodinminutes'           = 'restartgraceperiod'
        'restartcountdowndisplayinminutes'      = 'restartcountdowndisplay'
        'restartnotificationsnoozeinminutes'    = 'restartnotificationsnooze'
    }

    # Valid keys for Add-IntuneWin32AppAssignment* functions (must match parameter names)
    $ValidAssignmentKeys = @(
        'intent', 'groupid', 'notification', 'availabletime', 'deadlinetime',
        'uselocaltime', 'deliveryoptimizationpriority', 'enablerestartgraceperiod',
        'restartgraceperiod', 'restartcountdowndisplay',
        'restartnotificationsnooze', 'filtermode', 'filtername',
        'autoupdatesupersededapps'
    )

    # Valid value sets
    $ValidValues = @{
        intent                       = @('available', 'required', 'uninstall', 'availablewithoutenrollment')
        notification                 = @('showAll', 'showReboot', 'hideAll')
        uselocaltime                 = @('true', 'false')
        deliveryoptimizationpriority = @('notConfigured', 'foreground')
        enablerestartgraceperiod     = @('true', 'false')
        filtermode                   = @('include', 'exclude')
        groupmode                    = @('include', 'exclude')
        autoupdatesupersededapps     = @('notConfigured', 'enabled', 'unknownFutureValue')
    }

    # Keys used for routing/control, not passed to the API
    $ControlKeys = @('type', 'groupmode', 'appname', 'label')

    # Keys that are optional -- silently skip when empty/null (uses IntuneWin32App names)
    $OptionalKeys = @(
        'filtermode', 'filtername',
        'availabletime', 'deadlinetime', 'uselocaltime',
        'autoupdatesupersededapps',
        'enablerestartgraceperiod',
        'restartgraceperiod', 'restartcountdowndisplay',
        'restartnotificationsnooze'
    )

    $ResultApplied = [System.Collections.Generic.List[string]]::new()
    $ResultSkipped = [System.Collections.Generic.List[string]]::new()
    $ResultErrors = [System.Collections.Generic.List[string]]::new()

    foreach ($assignment in $Assignments) {
        $assignLabel = if ($assignment.AppName) { $assignment.AppName }
                       elseif ($assignment.Label) { $assignment.Label }
                       else { 'unnamed' }
        Write-Log "Processing assignment: $assignLabel"

        # Merge defaults with current assignment (case-insensitive)
        $merged = @{}
        if ($AssignmentDefaults) {
            foreach ($k in $AssignmentDefaults.Keys) {
                $merged[$k.ToLowerInvariant()] = $AssignmentDefaults[$k]
            }
        }
        foreach ($k in $assignment.Keys) {
            $merged[$k.ToLowerInvariant()] = $assignment[$k]
        }

        # Strip restart-grace-period defaults when the feature is disabled on
        # this assignment. These keys are inert without EnableRestartGracePeriod
        # and passing them just clutters the cmdlet call.
        $graceEnabled = $false
        if ($merged.ContainsKey('enablerestartgraceperiod')) {
            try { $graceEnabled = [bool]::Parse($merged['enablerestartgraceperiod'].ToString()) }
            catch { $graceEnabled = $false }
        }
        if (-not $graceEnabled) {
            foreach ($rk in @('restartgraceperiodinminutes','restartcountdowndisplayinminutes','restartnotificationsnoozeinminutes')) {
                $merged.Remove($rk) | Out-Null
            }
        }

        # Rename Config.json fields to IntuneWin32App parameter names
        foreach ($configName in @($FieldNameMap.Keys)) {
            if ($merged.ContainsKey($configName)) {
                $merged[$FieldNameMap[$configName]] = $merged[$configName]
                $merged.Remove($configName)
            }
        }

        $intent    = $merged['intent']
        $type      = $merged['type']
        $groupMode = $merged['groupmode']

        if (-not $intent -or -not $type) {
            $msg = "Skipping assignment '$assignLabel' - missing 'intent' or 'type'."
            Write-Log $msg -Type 2
            $ResultSkipped.Add($msg)
            continue
        }

        $intentLower = $intent.ToLowerInvariant()
        $disallowedKeys = $IntentDisallowedKeys[$intentLower]
        $params = @{ id = $AppId }
        $ignored = @()
        $errors = @()

        # Filter allowed keys and validate values
        foreach ($key in $merged.Keys) {
            if ($ControlKeys -contains $key) { continue }

            $value = $merged[$key]

            # Skip null/empty values: optional keys silently skipped, required keys error
            # GroupID is only required for 'Group' type, not AllDevices/AllUsers
            if ($null -eq $value -or ($value -is [string] -and [string]::IsNullOrWhiteSpace($value))) {
                $isGroupOnlyKey = $key -in @('groupid', 'groupmode')
                $isNonGroupType = $type -and $type.ToLowerInvariant() -in @('alldevices', 'allusers')
                if ($isGroupOnlyKey -and $isNonGroupType) {
                    # Silently skip -- not applicable for this assignment type
                }
                elseif ($OptionalKeys -notcontains $key -and $ValidAssignmentKeys -contains $key) {
                    $errors += "'$key' is null or empty."
                }
                continue
            }

            # Skip keys disallowed by intent
            if ($disallowedKeys -contains $key) {
                $ignored += $key
                continue
            }

            # Validate allowed values
            if ($ValidValues.ContainsKey($key)) {
                if ($value -notin $ValidValues[$key]) {
                    $errors += "'$key' value '$value' is invalid. Allowed: $($ValidValues[$key] -join ', ')"
                    continue
                }
            }

            # FilterMode requires FilterName
            if ($key -eq 'filtermode' -and -not $merged.ContainsKey('filtername')) {
                $errors += "'FilterMode' requires a corresponding 'FilterName'."
                continue
            }

            # AutoUpdateSupersededApps only valid with intent=available
            if ($key -eq 'autoupdatesupersededapps' -and $intentLower -ne 'available') {
                $errors += "'AutoUpdateSupersededApps' is only valid with intent 'available'."
                continue
            }

            if ($ValidAssignmentKeys -contains $key) {
                # Convert boolean string fields to actual booleans for IntuneWin32App cmdlets
                if ($key -in @('uselocaltime', 'enablerestartgraceperiod')) {
                    try { $value = [bool]::Parse($value.ToString()) }
                    catch { $value = $false }
                }
                $params[$key] = $value
            }
            else {
                $ignored += $key
            }
        }

        # IntuneWin32App validation: AvailableTime without DeadlineTime must be in the past.
        # For available intent (no deadline), strip AvailableTime to avoid rejection.
        if ($params.ContainsKey('availabletime') -and -not $params.ContainsKey('deadlinetime')) {
            $params.Remove('availabletime')
            Write-Log "  Stripped 'availabletime' (no deadline set, module requires past date without deadline)"
        }

        if ($ignored.Count -gt 0) {
            Write-Log "  Ignored keys (not applicable for intent '$intent'): $($ignored -join ', ')"
        }

        if ($errors.Count -gt 0) {
            Write-Log "  Validation errors for assignment '$assignLabel':" -Type 2
            foreach ($e in $errors) { Write-Log "    $e" -Type 2 }
            $ResultErrors.Add("Assignment '$assignLabel': $($errors -join '; ')")
            continue
        }

        if ($Validate) {
            Write-Log "[VALIDATE] Assignment '$assignLabel': type=$type, intent=$intent"
            if ($type -eq 'group') { Write-Log "[VALIDATE]   groupMode=$groupMode" }
            $ResultApplied.Add("$assignLabel (validated)")
            continue
        }

        # Execute the assignment
        Write-Log "Executing assignment for AppId: $AppId (type: $type)"
        try {
            switch ($type.ToLowerInvariant()) {
                'alldevices' {
                    $null = Add-IntuneWin32AppAssignmentAllDevices @params
                    $ResultApplied.Add("$assignLabel (AllDevices)")
                }
                'allusers' {
                    $null = Add-IntuneWin32AppAssignmentAllUsers @params
                    $ResultApplied.Add("$assignLabel (AllUsers)")
                }
                'group' {
                    if (-not $groupMode) {
                        Write-Log "Missing 'groupmode' for group assignment '$assignLabel'." -Type 2
                        $ResultSkipped.Add("${assignLabel}: missing groupmode")
                        continue
                    }
                    if (-not $params.groupid) {
                        Write-Log "Missing 'groupid' for group assignment '$assignLabel'." -Type 2
                        $ResultSkipped.Add("${assignLabel}: missing groupid")
                        continue
                    }
                    # Resolve group name to GUID if needed
                    try {
                        $params.groupid = Resolve-EntraGroupId -GroupIdentifier $params.groupid
                    }
                    catch {
                        Write-Log "Failed to resolve GroupID for '$assignLabel': $_" -Type 3
                        $ResultErrors.Add("${assignLabel}: $_")
                        continue
                    }
                    switch ($groupMode.ToLowerInvariant()) {
                        'include' {
                            $null = Add-IntuneWin32AppAssignmentGroup @params -Include
                            $ResultApplied.Add("$assignLabel (Group-Include)")
                        }
                        'exclude' {
                            $null = Add-IntuneWin32AppAssignmentGroup @params -Exclude
                            $ResultApplied.Add("$assignLabel (Group-Exclude)")
                        }
                        default {
                            Write-Log "Invalid groupmode '$groupMode' for '$assignLabel'." -Type 2
                            $ResultSkipped.Add("${assignLabel}: invalid groupmode '$groupMode'")
                        }
                    }
                }
                default {
                    Write-Log "Unknown assignment type '$type' for '$assignLabel'." -Type 2
                    $ResultSkipped.Add("${assignLabel}: unknown type '$type'")
                }
            }
        }
        catch {
            Write-Log "Assignment failed for '$assignLabel': $_" -Type 3
            $ResultErrors.Add("${assignLabel}: $_")
        }
    }

    return [PSCustomObject]@{
        Applied = $ResultApplied.ToArray()
        Skipped = $ResultSkipped.ToArray()
        Errors  = $ResultErrors.ToArray()
    }
}
