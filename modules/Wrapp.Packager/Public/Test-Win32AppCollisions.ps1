function Test-Win32AppCollisions {
    <#
    .SYNOPSIS
        Detects name-based conflicts between packages and existing Intune Win32 apps.

    .DESCRIPTION
        Queries Intune for each package's AppName to check if an app with that
        display name already exists. Returns structured results with collision
        details and the list of safe packages.

    .PARAMETER PackageList
        Array of package objects with an AppName property.

    .PARAMETER TerminateOnCollision
        If specified, stops processing upon the first detected collision.

    .OUTPUTS
        [hashtable] with:
            Collisions [array] - Matching app summaries from Intune
            Valid      [array] - Packages with no name conflict
            AllBlocked [bool]  - True if no packages can safely proceed
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [array]$PackageList,

        [switch]$TerminateOnCollision
    )

    $Collisions = [System.Collections.Generic.List[object]]::new()
    $ValidPackages = [System.Collections.Generic.List[object]]::new()

    foreach ($Package in $PackageList) {
        $pkgName = $Package.AppName
        $updateMode = Get-ConfigValue $Package 'UpdateMode' 'Create'

        # Update / UpdateContent target an existing app by ID -- a display-name
        # match is the expected state (it IS the app we're updating). Skip the
        # collision check entirely. Downstream Update-IntuneWin32AppFromConfig
        # validates ExistingAppID and fails the package fast if it's missing
        # or doesn't resolve to an existing app.
        if ($updateMode -in @('Update', 'UpdateContent')) {
            Write-Log "Skipping collision check for '$pkgName' (UpdateMode=$updateMode -- name overlap with existing app is expected)."
            $ValidPackages.Add($Package)
            continue
        }

        Write-Log "Checking for existing Intune app matching '$pkgName'..."

        try {
            # Get-IntuneWin32App uses -like "*name*" (substring match).
            # Filter to exact display name matches only to avoid false positives.
            # Suppress verbose to avoid slow per-app messages during Graph lookup.
            $savedVerbose = $VerbosePreference
            $VerbosePreference = 'SilentlyContinue'
            $Candidates = @(Get-IntuneWin32App -DisplayName $pkgName)
            $VerbosePreference = $savedVerbose
            $MatchedApps = @($Candidates | Where-Object { $_.DisplayName -eq $pkgName })
        }
        catch {
            Write-Log "Failed to query Intune for '$pkgName': $_" -Type 2
            continue
        }

        if ($MatchedApps.Count -gt 0) {
            foreach ($Match in $MatchedApps) {
                Write-Log "Collision: '$pkgName' already exists in Intune (ID: $($Match.ID), v$($Match.DisplayVersion))" -Type 2

                $Collisions.Add([pscustomobject]@{
                    PackageName     = $pkgName
                    ExistingAppName = $Match.DisplayName
                    Version         = $Match.DisplayVersion
                    Publisher       = $Match.Publisher
                    ID              = $Match.ID
                    Notes           = $Match.Notes
                })

                if ($TerminateOnCollision) {
                    Write-Log "Aborting due to TerminateOnCollision flag. Package: $pkgName" -Type 3
                    return @{
                        Collisions = $Collisions.ToArray()
                        Valid      = @()
                        AllBlocked = $true
                    }
                }
            }
        }
        else {
            $ValidPackages.Add($Package)
        }
    }

    return @{
        Collisions = $Collisions.ToArray()
        Valid      = $ValidPackages.ToArray()
        AllBlocked = ($ValidPackages.Count -eq 0)
    }
}
