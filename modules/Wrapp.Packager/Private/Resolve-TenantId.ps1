function Resolve-TenantId {
    <#
    .SYNOPSIS
        Detects the Entra ID tenant ID from the Azure AD Join registry key.
        Falls back to an explicit tenant ID parameter if registry detection fails.

    .PARAMETER FallbackTenantId
        Optional explicit tenant ID if registry detection fails (non-AAD-joined machines).

    .OUTPUTS
        [string] Tenant ID GUID, or $null if not resolvable.
    #>
    [CmdletBinding()]
    param(
        [string]$FallbackTenantId
    )

    $TenantId = $null

    try {
        $JoinInfo = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey(
            'SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo'
        )
        if ($JoinInfo) {
            $TenantId = $JoinInfo.GetSubKeyNames() | ForEach-Object {
                $JoinInfo.OpenSubKey($_).GetValue('TenantId')
            } | Select-Object -First 1
        }
    }
    catch {
        Write-Log "Registry tenant detection failed: $_" -Type 2
    }

    if ($TenantId) {
        Write-Log "Tenant ID resolved from registry: $TenantId"
    }
    elseif ($FallbackTenantId) {
        Write-Log "Registry detection failed. Using fallback TenantId: $FallbackTenantId"
        $TenantId = $FallbackTenantId
    }
    else {
        Write-Log "Could not resolve TenantId from registry or fallback parameter." -Type 3
    }

    return $TenantId
}
