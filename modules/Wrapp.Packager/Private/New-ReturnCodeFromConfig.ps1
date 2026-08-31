function New-ReturnCodeFromConfig {
    <#
    .SYNOPSIS
        Builds return code objects from config arrays.

    .DESCRIPTION
        Maps config objects to New-IntuneWin32AppReturnCode calls.
        Each entry needs ReturnCode (int) and Type (string).

    .PARAMETER ReturnCodes
        Array of return code config objects.

    .OUTPUTS
        Array of hashtable return code objects.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [array]$ReturnCodes
    )

    $codes = @()

    foreach ($rc in $ReturnCodes) {
        if ($null -eq $rc.ReturnCode -or -not $rc.Type) {
            Write-Log "Return code entry missing ReturnCode or Type, skipping" -Type 2
            continue
        }

        try {
            $p = @{ ReturnCode = [int]$rc.ReturnCode; Type = $rc.Type }
            $code = New-IntuneWin32AppReturnCode @p
            $codes += $code
            Write-Log "Created return code: $($rc.ReturnCode) -> $($rc.Type)"
        }
        catch {
            Write-Log "Failed to create return code ($($rc.ReturnCode)): $_" -Type 3
        }
    }

    return $codes
}
