function Set-CMAppDependencyFromConfig {
    <#
    .SYNOPSIS
        Adds SCCM application dependencies from Config.json App.Dependencies.

    .DESCRIPTION
        For each dependency entry in App.Dependencies, resolves the target
        SCCM application, retrieves its first deployment type, creates a
        dependency group, and links the dependency. Follows the same pattern
        as the original SCCMPackager.ps1 dependency handling.

    .PARAMETER DeploymentType
        The SCCM deployment type object to add dependencies to.

    .PARAMETER Dependencies
        Array of dependency objects from Config.App.Dependencies.
        Each must have: AppName, SCCMAppName, SCCMAutoInstall.

    .PARAMETER Validate
        If specified, logs actions without making changes.

    .OUTPUTS
        [PSCustomObject] with Applied, Skipped, and Errors arrays.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        $DeploymentType,

        [Parameter(Mandatory = $true)]
        [array]$Dependencies,

        [switch]$Validate
    )

    $Applied = [System.Collections.Generic.List[string]]::new()
    $Skipped = [System.Collections.Generic.List[string]]::new()
    $DepErrors = [System.Collections.Generic.List[string]]::new()

    foreach ($Dep in $Dependencies) {
        $depLabel = if ($Dep.AppName) { $Dep.AppName } else { $Dep.SCCMAppName }
        $sccmAppName = $Dep.SCCMAppName
        $autoInstall = if ($null -ne $Dep.SCCMAutoInstall) { $Dep.SCCMAutoInstall } else { $true }

        if (-not $sccmAppName) {
            $DepErrors.Add("Dependency '$depLabel': SCCMAppName is required")
            $Skipped.Add($depLabel)
            continue
        }

        Write-Log "Processing SCCM dependency: $depLabel (SCCMAppName: $sccmAppName)"

        if ($Validate) {
            Write-Log "[VALIDATE] Would add dependency on '$sccmAppName' (AutoInstall: $autoInstall)"
            $Applied.Add($depLabel)
            continue
        }

        try {
            # Resolve the target SCCM application
            $DepApp = Get-CMApplication -Name $sccmAppName
            if (-not $DepApp) {
                $DepErrors.Add("Dependency '$depLabel': SCCM application '$sccmAppName' not found")
                $Skipped.Add($depLabel)
                continue
            }

            # Get the first deployment type from the target app
            $DepDT = Get-CMDeploymentType -InputObject $DepApp | Select-Object -First 1
            if (-not $DepDT) {
                $DepErrors.Add("Dependency '$depLabel': SCCM application '$sccmAppName' has no deployment type")
                $Skipped.Add($depLabel)
                continue
            }

            # Create a dependency group and add the dependency
            $DepGroup = New-CMDeploymentTypeDependencyGroup -InputObject $DeploymentType -GroupName $depLabel
            $null = Add-CMDeploymentTypeDependency -DeploymentTypeDependency $DepDT -InputObject $DepGroup -IsAutoInstall $autoInstall

            Write-Log "Added SCCM dependency: $depLabel -> $sccmAppName (AutoInstall: $autoInstall)"
            $Applied.Add($depLabel)
        }
        catch {
            Write-Log "Failed to add SCCM dependency '$depLabel': $_" -Type 3
            $DepErrors.Add("Dependency '$depLabel': $_")
            $Skipped.Add($depLabel)
        }
    }

    return [PSCustomObject]@{
        Applied = $Applied.ToArray()
        Skipped = $Skipped.ToArray()
        Errors  = $DepErrors.ToArray()
    }
}
