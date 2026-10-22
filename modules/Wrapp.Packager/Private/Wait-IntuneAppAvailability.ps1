function Wait-IntuneAppAvailability {
    <#
    .SYNOPSIS
        Polls Microsoft Graph to wait for a Win32 app to become available.

    .DESCRIPTION
        After creating a new app, there is a delay before it becomes queryable
        via the Graph API. This function polls until the app is indexed or
        the timeout is reached.

    .PARAMETER AppId
        The GUID of the newly created Intune Win32 app.

    .PARAMETER TimeoutSeconds
        Maximum number of seconds to wait. Default: 90.

    .PARAMETER IntervalSeconds
        Time between poll attempts. Default: 5.

    .OUTPUTS
        The app object if available within the timeout, or $null.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$AppId,

        [int]$TimeoutSeconds = 90,

        [int]$IntervalSeconds = 5
    )

    $Elapsed = 0
    do {
        try {
            $app = Get-IntuneWin32App -Id $AppId
            if ($app) {
                Write-Log "App '$AppId' available after $Elapsed seconds."
                return $app
            }
        }
        catch {
            Write-Log "Graph API not ready for app '$AppId'. Elapsed: ${Elapsed}s" -Type 2
        }

        Start-Sleep -Seconds $IntervalSeconds
        $Elapsed += $IntervalSeconds
    } while ($Elapsed -lt $TimeoutSeconds)

    Write-Log "App '$AppId' did not become available within ${TimeoutSeconds}s." -Type 3
    return $null
}
