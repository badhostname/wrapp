function Test-CMAppCollisions {
    <#
    .SYNOPSIS
        Detects name-based conflicts between packages and existing SCCM applications.

    .DESCRIPTION
        Queries SCCM for each package's AppName to check if an application
        with that name already exists. Returns structured results with
        collision details and the list of safe packages.

    .PARAMETER PackageList
        Array of package objects with an AppName property.

    .PARAMETER TerminateOnCollision
        If specified, stops processing upon the first detected collision.

    .OUTPUTS
        [hashtable] with:
            Collisions [array] - Matching app summaries from SCCM
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
        Write-Log "Checking for existing SCCM application matching '$pkgName'..."

        try {
            $Match = Get-CMApplication -Name $pkgName -ErrorAction SilentlyContinue
        }
        catch {
            Write-Log "Failed to query SCCM for '$pkgName': $_" -Type 2
            continue
        }

        if ($Match) {
            Write-Log "Collision: '$pkgName' already exists in SCCM" -Type 2

            $Collisions.Add([pscustomobject]@{
                PackageName     = $pkgName
                ExistingAppName = $Match.LocalizedDisplayName
                Publisher       = $Match.Manufacturer
                Version         = $Match.SoftwareVersion
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
