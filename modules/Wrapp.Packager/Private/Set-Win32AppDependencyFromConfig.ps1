function Set-Win32AppDependencyFromConfig {
    <#
    .SYNOPSIS
        Resolves and applies dependency relationships for a Win32 app.

    .DESCRIPTION
        Takes dependency config entries, resolves target app IDs (by ID or
        name lookup), builds dependency objects, and calls
        Add-IntuneWin32AppDependency.

    .PARAMETER AppId
        The Intune app ID of the parent (dependent) app.

    .PARAMETER Dependencies
        Array of dependency config objects with TargetAppName/TargetAppID
        and DependencyType fields.

    .PARAMETER DeployedApps
        Hashtable of apps created in the current run (AppName -> AppObject).

    .PARAMETER Validate
        Validation mode - logs what would happen without making API calls.

    .OUTPUTS
        [PSCustomObject] with Applied, Skipped, Errors arrays.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$AppId,

        [Parameter(Mandatory = $true)]
        [array]$Dependencies,

        [hashtable]$DeployedApps = @{},

        [switch]$Validate
    )

    $Applied = [System.Collections.Generic.List[string]]::new()
    $Skipped = [System.Collections.Generic.List[string]]::new()
    $Errors = [System.Collections.Generic.List[string]]::new()

    $depObjects = @()

    foreach ($dep in $Dependencies) {
        $targetId = $dep.TargetAppID
        $targetName = $dep.TargetAppName
        $depType = if ($dep.DependencyType) { $dep.DependencyType } else { 'AutoInstall' }
        $label = if ($targetName) { $targetName } else { $targetId }

        if (-not $targetId -and $targetName) {
            # Check deployed apps from current run first
            if ($DeployedApps.ContainsKey($targetName) -and $DeployedApps[$targetName].Id) {
                $targetId = $DeployedApps[$targetName].Id
                Write-Log "Dependency '$label': resolved from current run (ID: $targetId)"
            }
            else {
                try {
                    $found = Get-IntuneWin32App -DisplayName $targetName -ErrorAction Stop
                    if ($found -and @($found).Count -eq 1) {
                        $targetId = $found.Id
                        Write-Log "Dependency '$label': resolved from Intune (ID: $targetId)"
                    }
                    elseif ($found -and @($found).Count -gt 1) {
                        $Errors.Add("Dependency '$label': multiple apps match name '$targetName'. Use TargetAppID instead.")
                        continue
                    }
                    else {
                        $Errors.Add("Dependency '$label': app '$targetName' not found in Intune")
                        continue
                    }
                }
                catch {
                    $Errors.Add("Dependency '$label': lookup failed - $_")
                    continue
                }
            }
        }

        if (-not $targetId) {
            $Errors.Add("Dependency '$label': could not resolve target app ID")
            continue
        }

        try {
            $depObj = New-IntuneWin32AppDependency -ID $targetId -DependencyType $depType
            $depObjects += $depObj
            Write-Log "Built dependency object: $label ($depType)"
        }
        catch {
            $Errors.Add("Dependency '$label': failed to create object - $_")
        }
    }

    if ($depObjects.Count -eq 0) {
        $Skipped.Add("No valid dependency objects to apply")
        return [PSCustomObject]@{
            Applied = $Applied.ToArray()
            Skipped = $Skipped.ToArray()
            Errors  = $Errors.ToArray()
        }
    }

    if ($Validate) {
        Write-Log "[VALIDATE] Would add $($depObjects.Count) dependencies to app $AppId"
        foreach ($d in $depObjects) {
            $Applied.Add("Dependency validated: $($d.ID)")
        }
    }
    else {
        try {
            Add-IntuneWin32AppDependency -ID $AppId -Dependency $depObjects
            Write-Log "Applied $($depObjects.Count) dependencies to app $AppId"
            $Applied.Add("$($depObjects.Count) dependencies applied")
        }
        catch {
            $Errors.Add("Failed to apply dependencies to app $AppId - $_")
        }
    }

    return [PSCustomObject]@{
        Applied = $Applied.ToArray()
        Skipped = $Skipped.ToArray()
        Errors  = $Errors.ToArray()
    }
}
