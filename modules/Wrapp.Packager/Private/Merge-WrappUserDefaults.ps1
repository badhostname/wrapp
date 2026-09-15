function Merge-WrappUserDefaults {
    <#
    .SYNOPSIS
        Layers the user's shared defaults (user-defaults.json) over the
        module's shipped Defaults.psd1 hashtable and returns the merged copy.

    .DESCRIPTION
        Called at orchestrator start (Invoke-WrappIntune / Invoke-WrappSccm)
        so every run picks up current user preferences without a module
        re-import. Only non-empty user values override; the property names on
        the JSON side are a CONTRACT with the GUI's UserDefaultsService
        export. Unknown/absent sections are simply skipped, so an old or
        hand-trimmed file degrades gracefully to module defaults.

        Note the metadata values (Owner/Developer templates) may contain
        {{Company}}-style tokens; consumers expand them per app via
        Expand-WrappTokens.

    .PARAMETER Base
        The module defaults hashtable (from Defaults.psd1). NOT mutated.

    .PARAMETER User
        The parsed user-defaults.json object (Get-WrappUserDefaults), or $null.

    .OUTPUTS
        [hashtable] Merged defaults (a shallow clone of Base with cloned
        sub-hashtables for every section this function touches).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][hashtable]$Base,
        $User
    )

    $Merged = $Base.Clone()
    if (-not $User) { return $Merged }

    # Helper: set $Target[$Key] = user value when the user value is "set"
    # (non-empty string / non-zero number / any boolean). Numbers arrive as
    # int32 (PS 5.1) or int64 (PS 7 ConvertFrom-Json) -- treat both.
    function Set-IfPresent {
        param([hashtable]$Target, [string]$Key, $Value)
        if ($null -eq $Value) { return }
        if ($Value -is [string] -and [string]::IsNullOrWhiteSpace($Value)) { return }
        if (($Value -is [int] -or $Value -is [long] -or $Value -is [double]) -and $Value -eq 0) { return }
        $Target[$Key] = $Value
    }

    # --- Endpoint script paths -------------------------------------------
    if ($User.PSObject.Properties['Endpoint'] -and $User.Endpoint) {
        Set-IfPresent $Merged 'EndpointTagFolder'      $User.Endpoint.TagFolder
        Set-IfPresent $Merged 'EndpointLocalAppFolder' $User.Endpoint.LocalAppFolder
    }

    # --- Intune package defaults -> DefaultIntunePackageProperties --------
    if ($User.PSObject.Properties['IntunePackageDefaults'] -and $User.IntunePackageDefaults) {
        $p = $User.IntunePackageDefaults
        $t = $Merged['DefaultIntunePackageProperties'].Clone()
        Set-IfPresent $t 'Architecture'                     $p.Architecture
        Set-IfPresent $t 'MinimumSupportedWindowsRelease'   $p.MinimumSupportedWindowsRelease
        Set-IfPresent $t 'InstallExperience'                $p.InstallExperience
        Set-IfPresent $t 'RestartBehavior'                  $p.RestartBehavior
        Set-IfPresent $t 'MaximumInstallationTimeInMinutes' $p.MaximumInstallationTimeInMinutes
        $Merged['DefaultIntunePackageProperties'] = $t
    }

    # --- Intune metadata defaults -> DefaultAppProperties ------------------
    # Values are token templates; consumers expand via Expand-WrappTokens.
    if ($User.PSObject.Properties['IntuneMetadataDefaults'] -and $User.IntuneMetadataDefaults) {
        $m = $User.IntuneMetadataDefaults
        $t = $Merged['DefaultAppProperties'].Clone()
        Set-IfPresent $t 'Owner'     $m.OwnerTemplate
        Set-IfPresent $t 'Developer' $m.DeveloperTemplate
        $Merged['DefaultAppProperties'] = $t
    }

    # --- Intune assignment defaults -> DefaultAssignmentProperties ---------
    if ($User.PSObject.Properties['IntuneAssignmentDefaults'] -and $User.IntuneAssignmentDefaults) {
        $a = $User.IntuneAssignmentDefaults
        $t = $Merged['DefaultAssignmentProperties'].Clone()
        Set-IfPresent $t 'Notification'                       $a.Notification
        Set-IfPresent $t 'DeliveryOptimizationPriority'       $a.DeliveryOptimizationPriority
        Set-IfPresent $t 'RestartGracePeriodInMinutes'        $a.RestartGracePeriodInMinutes
        Set-IfPresent $t 'RestartCountDownDisplayInMinutes'   $a.RestartCountDownDisplayInMinutes
        Set-IfPresent $t 'RestartNotificationSnoozeInMinutes' $a.RestartNotificationSnoozeInMinutes
        $Merged['DefaultAssignmentProperties'] = $t
    }

    # --- SCCM package defaults -> DefaultSCCMPackageProperties -------------
    if ($User.PSObject.Properties['SccmPackageDefaults'] -and $User.SccmPackageDefaults) {
        $s = $User.SccmPackageDefaults
        $t = $Merged['DefaultSCCMPackageProperties'].Clone()
        Set-IfPresent $t 'InstallationBehaviorType'  $s.InstallationBehaviorType
        Set-IfPresent $t 'LogonRequirementType'      $s.LogonRequirementType
        Set-IfPresent $t 'UserInteractionMode'       $s.UserInteractionMode
        Set-IfPresent $t 'RebootBehavior'            $s.RebootBehavior
        Set-IfPresent $t 'SlowNetworkDeploymentMode' $s.SlowNetworkDeploymentMode
        Set-IfPresent $t 'EstimatedRuntimeMins'      $s.EstimatedRuntimeMins
        Set-IfPresent $t 'MaximumAllowedRuntimeMins' $s.MaximumAllowedRuntimeMins
        $Merged['DefaultSCCMPackageProperties'] = $t
    }

    # --- SCCM deployment defaults -> DefaultDeploymentProperties -----------
    if ($User.PSObject.Properties['SccmDeploymentDefaults'] -and $User.SccmDeploymentDefaults) {
        $d = $User.SccmDeploymentDefaults
        $t = $Merged['DefaultDeploymentProperties'].Clone()
        Set-IfPresent $t 'DeployAction'     $d.DeployAction
        Set-IfPresent $t 'DeployPurpose'    $d.DeployPurpose
        Set-IfPresent $t 'UserNotification' $d.UserNotification
        Set-IfPresent $t 'TimeBaseOn'       $d.TimeBaseOn
        $Merged['DefaultDeploymentProperties'] = $t
    }

    return $Merged
}
