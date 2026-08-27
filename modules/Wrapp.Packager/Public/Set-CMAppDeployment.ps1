function Set-CMAppDeployment {
    <#
    .SYNOPSIS
        Creates SCCM application deployments from Config.json.

    .DESCRIPTION
        For each deployment entry in the SCCMSite configuration, creates
        an SCCM application deployment targeting the specified collection
        with the specified notification and purpose settings.

    .PARAMETER Deployments
        Array of deployment objects from SCCMSite.Deployments.

    .PARAMETER Creator
        The creator/operator display name.

    .PARAMETER Validate
        If specified, logs actions without making changes.

    .OUTPUTS
        [PSCustomObject] with Applied, Skipped, and Errors arrays.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [array]$Deployments,

        [string]$Creator = $env:USERNAME,

        [switch]$Validate
    )

    $Applied = [System.Collections.Generic.List[string]]::new()
    $Skipped = [System.Collections.Generic.List[string]]::new()
    $DeployErrors = [System.Collections.Generic.List[string]]::new()
    $TodayDate = Get-Date -UFormat '%Y-%m-%d'

    foreach ($Deployment in $Deployments) {
        $depLabel = "$($Deployment.AppName) -> $($Deployment.Collection)"

        Write-Log "Processing deployment: $depLabel"

        $DeploymentArgs = @{
            Name              = $Deployment.AppName
            CollectionName    = $Deployment.Collection
            Comment           = "$($Deployment.Comment). Created $TodayDate by $Creator"
            UserNotification  = $Deployment.UserNotification
            DeployAction      = $Deployment.DeployAction
            DeployPurpose     = $Deployment.DeployPurpose
            TimeBaseOn        = Get-ConfigValue $Deployment 'TimeBaseOn' $script:ModuleDefaults.DefaultDeploymentProperties.TimeBaseOn
        }

        # Scheduling: AvailableDateTime + DeadlineDateTime.
        # DateTime.MinValue (year 1) is Intune's "ASAP" sentinel and has no
        # SCCM equivalent -- New-CMApplicationDeployment runs the value
        # through .ToUniversalTime(), which subtracts the local timezone
        # offset and underflows year 1 in any negative TZ ("un-representable
        # DateTime"). Get-SafeDateTime -RejectMinValue treats any year-<=-1
        # input as if the field were empty (returns $null), so we can route
        # the unset/MinValue cases through a single branch.
        $availParsed = Get-SafeDateTime -InputValue $Deployment.AvailableDateTime -DefaultIfUnset $null -RejectMinValue
        if ($Deployment.AvailableDateTime -and $null -eq $availParsed) {
            Write-Log "Deployment '$depLabel': AvailableDateTime '$($Deployment.AvailableDateTime)' is the Intune MinValue sentinel or unparseable; treating as unset for SCCM." -Type 2
        }
        # Default to "now" when AvailableDateTime is unset/MinValue. The
        # previous 2099-12-25 fallback collided with user bundles that set
        # DeadlineDateTime to the same date and DeployPurpose=Required --
        # SCCM rejects Required with Available == Deadline as Invalid
        # operation (CMPSNoMask = True).
        $DeploymentArgs['AvailableDateTime'] = if ($null -ne $availParsed) { $availParsed } else { Get-Date }

        if ($Deployment.DeadlineDateTime) {
            $deadlineParsed = Get-SafeDateTime -InputValue $Deployment.DeadlineDateTime -DefaultIfUnset $null -RejectMinValue
            if ($null -eq $deadlineParsed) {
                Write-Log "Deployment '$depLabel': DeadlineDateTime '$($Deployment.DeadlineDateTime)' is the MinValue sentinel or unparseable; skipping for SCCM." -Type 2
            }
            else {
                $DeploymentArgs['DeadlineDateTime'] = $deadlineParsed
            }
        }

        # Optional boolean flags
        if ($Deployment.ApprovalRequired) { $DeploymentArgs['ApprovalRequired'] = $true }
        if ($Deployment.OverrideServiceWindow) { $DeploymentArgs['OverrideServiceWindow'] = $true }
        if ($Deployment.RebootOutsideServiceWindow) { $DeploymentArgs['RebootOutsideServiceWindow'] = $true }
        if ($Deployment.SendWakeupPacket) { $DeploymentArgs['SendWakeupPacket'] = $true }

        if ($Validate) {
            Write-Log "[VALIDATE] Would create deployment:"
            foreach ($key in $DeploymentArgs.Keys) {
                Write-Log "[VALIDATE]   $key = $($DeploymentArgs[$key])"
            }
            $Applied.Add($depLabel)
            continue
        }

        try {
            $null = New-CMApplicationDeployment @DeploymentArgs
            Write-Log "Deployment created: $depLabel"
            $Applied.Add($depLabel)
        }
        catch {
            Write-Log "Failed to create deployment '$depLabel': $_" -Type 3
            $DeployErrors.Add("Deployment '$depLabel': $_")
            $Skipped.Add($depLabel)
        }
    }

    return [PSCustomObject]@{
        Applied = $Applied.ToArray()
        Skipped = $Skipped.ToArray()
        Errors  = $DeployErrors.ToArray()
    }
}
