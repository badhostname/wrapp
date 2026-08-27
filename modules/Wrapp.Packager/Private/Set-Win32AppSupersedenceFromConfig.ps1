function Set-Win32AppSupersedenceFromConfig {
    <#
    .SYNOPSIS
        Resolves and applies supersedence relationships for a Win32 app.

    .DESCRIPTION
        Takes supersedence config entries, resolves target app IDs (by ID or
        name lookup), builds supersedence objects, and calls
        Add-IntuneWin32AppSupersedence.

    .PARAMETER AppId
        The Intune app ID of the superseding app.

    .PARAMETER Supersedence
        Array of supersedence config objects with TargetAppName/TargetAppID
        and SupersedenceType fields.

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
        [array]$Supersedence,

        [hashtable]$DeployedApps = @{},

        [switch]$Validate
    )

    $Applied = [System.Collections.Generic.List[string]]::new()
    $Skipped = [System.Collections.Generic.List[string]]::new()
    $Errors = [System.Collections.Generic.List[string]]::new()

    $supObjects = @()

    foreach ($sup in $Supersedence) {
        $targetId = $sup.TargetAppID
        $targetName = $sup.TargetAppName
        $supType = if ($sup.SupersedenceType) { $sup.SupersedenceType } else { 'Replace' }
        $label = if ($targetName) { $targetName } else { $targetId }

        # Resolve target ID
        if (-not $targetId -and $targetName) {
            if ($DeployedApps.ContainsKey($targetName) -and $DeployedApps[$targetName].Id) {
                $targetId = $DeployedApps[$targetName].Id
                Write-Log "Supersedence '$label': resolved from current run (ID: $targetId)"
            }
            else {
                try {
                    $found = Get-IntuneWin32App -DisplayName $targetName -ErrorAction Stop
                    if ($found -and @($found).Count -eq 1) {
                        $targetId = $found.Id
                        Write-Log "Supersedence '$label': resolved from Intune (ID: $targetId)"
                    }
                    elseif ($found -and @($found).Count -gt 1) {
                        $Errors.Add("Supersedence '$label': multiple apps match name '$targetName'. Use TargetAppID instead.")
                        continue
                    }
                    else {
                        $Errors.Add("Supersedence '$label': app '$targetName' not found in Intune")
                        continue
                    }
                }
                catch {
                    $Errors.Add("Supersedence '$label': lookup failed - $_")
                    continue
                }
            }
        }

        if (-not $targetId) {
            $Errors.Add("Supersedence '$label': could not resolve target app ID")
            continue
        }

        try {
            $supObj = New-IntuneWin32AppSupersedence -ID $targetId -SupersedenceType $supType
            $supObjects += $supObj
            Write-Log "Built supersedence object: $label ($supType)"
        }
        catch {
            $Errors.Add("Supersedence '$label': failed to create object - $_")
        }
    }

    if ($supObjects.Count -eq 0) {
        $Skipped.Add("No valid supersedence objects to apply")
        return [PSCustomObject]@{
            Applied = $Applied.ToArray()
            Skipped = $Skipped.ToArray()
            Errors  = $Errors.ToArray()
        }
    }

    if ($Validate) {
        Write-Log "[VALIDATE] Would add $($supObjects.Count) supersedence entries to app $AppId"
        foreach ($s in $supObjects) {
            $Applied.Add("Supersedence validated: $($s.ID)")
        }
    }
    else {
        try {
            Add-IntuneWin32AppSupersedence -ID $AppId -Supersedence $supObjects
            Write-Log "Applied $($supObjects.Count) supersedence entries to app $AppId"
            $Applied.Add("$($supObjects.Count) supersedence entries applied")
        }
        catch {
            $Errors.Add("Failed to apply supersedence to app $AppId - $_")
        }
    }

    return [PSCustomObject]@{
        Applied = $Applied.ToArray()
        Skipped = $Skipped.ToArray()
        Errors  = $Errors.ToArray()
    }
}
