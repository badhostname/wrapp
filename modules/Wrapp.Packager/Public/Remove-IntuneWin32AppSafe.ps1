function Remove-IntuneWin32AppSafe {
    <#
    .SYNOPSIS
        Safely removes a Win32 app from Intune with confirmation.

    .DESCRIPTION
        Standalone wrapper around Remove-IntuneWin32App with ShouldProcess
        support. NOT part of the automated orchestrator flow (deletion is
        too dangerous for automation). For manual use only.

    .PARAMETER AppId
        The Intune app ID to remove.

    .PARAMETER DisplayName
        Optional display name for log/confirmation messages.

    .OUTPUTS
        [bool] True if removal succeeded, False otherwise.
    #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory = $true)]
        [string]$AppId,

        [string]$DisplayName
    )

    $label = if ($DisplayName) { "$DisplayName ($AppId)" } else { $AppId }

    # Verify the app exists before attempting removal
    try {
        $existingApp = Get-IntuneWin32App -ID $AppId -ErrorAction Stop
        if (-not $existingApp) {
            Write-Log "App not found: $label" -Type 2
            return $false
        }
        if (-not $DisplayName) {
            $label = "$($existingApp.displayName) ($AppId)"
        }
    }
    catch {
        Write-Log "Could not verify app existence for $label - $_" -Type 3
        return $false
    }

    if ($PSCmdlet.ShouldProcess($label, "Remove Win32 app from Intune")) {
        try {
            Remove-IntuneWin32App -ID $AppId
            Write-Log "Removed app: $label"
            return $true
        }
        catch {
            Write-Log "Failed to remove app $label - $_" -Type 3
            return $false
        }
    }
    else {
        Write-Log "Removal cancelled for: $label"
        return $false
    }
}
