function Use-WrappTenantToken {
    <#
    .SYNOPSIS
        Promotes one tenant's entry from the GUI-injected token map
        ($Global:WrappTokenMap) into the per-tenant token globals that
        IntuneWin32App / Connect-WrappIntune consume (Workstream P).

    .DESCRIPTION
        The Wrapp GUI acquires one MSAL token per enabled tenant up front and
        injects them as a single map: $Global:WrappTokenMap[tenantId] ->
        @{ AccessToken; ExpiresOnUtc; Scopes; ClientId; RefreshHandle }.
        The module-owned multi-tenant loop (Invoke-WrappPackaging) calls this
        per tenant iteration to promote that tenant's entry into the globals --
        exactly what the .NET per-tenant injection used to do per PackageAsync
        call, just module-side.

        The globals' field names are a CONTRACT shared with
        PowerShellTokenBridge.TokenGlobalsBody (C#) and
        Invoke-TokenRefreshIfNeeded's tier-1 rebuild -- renaming any of them
        silently breaks auth inside a run.

    .PARAMETER TenantId
        The tenant whose map entry to promote.

    .OUTPUTS
        [bool] $true when an entry was promoted; $false when no map or no
        entry exists (CLI flow -- the caller lets Connect-WrappIntune
        self-authenticate from the config's AuthFlow).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$TenantId
    )

    if (-not $Global:WrappTokenMap) { return $false }
    $Entry = $Global:WrappTokenMap[$TenantId]
    if (-not $Entry) { return $false }

    $Global:AccessToken = [PSCustomObject]@{
        access_token = $Entry.AccessToken
        AccessToken  = $Entry.AccessToken
        ExpiresOn    = $Entry.ExpiresOnUtc
        Scopes       = $Entry.Scopes
        token_type   = 'Bearer'
        client_id    = $Entry.ClientId
    }
    $Global:AuthenticationHeader = @{
        'Content-Type'  = 'application/json'
        'Authorization' = 'Bearer ' + $Entry.AccessToken
        'ExpiresOn'     = $Entry.ExpiresOnUtc
    }
    $Global:AccessTokenTenantID = $TenantId
    if ($Entry.RefreshHandle) {
        $Global:WrappMsalRefreshHandle = $Entry.RefreshHandle
    }

    return $true
}
