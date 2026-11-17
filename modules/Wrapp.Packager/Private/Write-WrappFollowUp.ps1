function Write-WrappFollowUp {
    <#
    .SYNOPSIS
        Reports something a failed step left behind that a technician has to
        clean up, with what to do about it.

    .DESCRIPTION
        One notice per package and step. It is written three ways so it is
        seen whatever runs the module:
          - the run log, as "ACTION NEEDED" lines (Write-Log, Type 2);
          - a tagged object on the output stream (_Type = 'WrappFollowUp'),
            which the Wrapp GUI turns into a Run log entry and an item in the
            status bar's "action needed" list;
          - the run result's FollowUps list, for CLI callers.

    .PARAMETER Result
        The Invoke-WrappIntune / Invoke-WrappSccm result object; the notice is
        added to its FollowUps list.

    .PARAMETER Target
        Intune or SCCM.

    .PARAMETER TargetId
        Tenant ID or site code.

    .PARAMETER Package
        The package (app) name.

    .PARAMETER Step
        The step that failed, as the GUI names it: AppCreation, Dependencies,
        Supersedence, Assignment.

    .PARAMETER Summary
        One sentence: what failed and what state it left.

    .PARAMETER Actions
        What to do, one sentence each.

    .PARAMETER Reason
        The underlying error, when there is one.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][ValidateSet('Intune', 'SCCM')][string]$Target,
        [string]$TargetId = '',
        [Parameter(Mandatory = $true)][string]$Package,
        [Parameter(Mandatory = $true)][string]$Step,
        [Parameter(Mandatory = $true)][string]$Summary,
        [Parameter(Mandatory = $true)][string[]]$Actions,
        [string]$Reason = ''
    )

    $FollowUp = [pscustomobject]@{
        _Type    = 'WrappFollowUp'
        Target   = $Target
        TargetId = $TargetId
        Package  = $Package
        Step     = $Step
        Summary  = $Summary
        Reason   = $Reason
        Actions  = @($Actions)
    }

    Write-Log "ACTION NEEDED - $Package ($Target, step $Step): $Summary" -Type 2
    if ($Reason) { Write-Log "  Reason: $Reason" -Type 2 }
    foreach ($a in $Actions) { Write-Log "  - $a" -Type 2 }

    if ($Result.PSObject.Properties['FollowUps']) { $Result.FollowUps.Add($FollowUp) }
    $FollowUp
}
