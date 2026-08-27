function Invoke-TokenRefreshIfNeeded {
    <#
    .SYNOPSIS
        Checks whether the current access token is nearing expiration and
        refreshes it using the best available method.

    .DESCRIPTION
        Three-tier refresh strategy:
          1. Wrapp GUI injected MSAL app object (AcquireTokenSilent -- fastest, in-memory)
          2. IntuneWin32App module's own refresh (Connect-MSIntuneGraph -Refresh)
          3. Full interactive re-authentication (last resort)

        Call this before any Graph API-dependent operation to avoid
        mid-operation auth failures.

    .PARAMETER TenantID
        The tenant ID for token refresh.

    .PARAMETER ClientID
        The client ID used for the original authentication.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$TenantID,

        [Parameter(Mandatory = $true)]
        [string]$ClientID
    )

    # Token still valid -- nothing to do
    if ((Test-AccessToken) -eq $true) { return }

    Write-Log "Access token expiring or expired. Attempting refresh..."

    # Tier 1: Use injected MSAL refresh handle (Wrapp GUI flow).
    #
    # The live IPCA is no longer exposed to
    # PowerShell; instead, $Global:WrappMsalRefreshHandle holds an opaque
    # GUID and the C# Invoke-WrappTokenRefresh cmdlet performs the silent
    # acquisition. The cmdlet returns a PSObject with .AccessToken,
    # .ExpiresOn, and .Scopes -- or emits a warning and no object when the
    # handle is missing / refresh requires UI, signalling tier-2 fallback.
    if ($Global:WrappMsalRefreshHandle) {
        try {
            Write-Log "  Tier 1: Attempting silent refresh via injected MSAL handle..."
            $authResult = Invoke-WrappTokenRefresh -Handle $Global:WrappMsalRefreshHandle

            if ($authResult -and $authResult.AccessToken) {
                $Global:AccessToken = [PSCustomObject]@{
                    access_token = $authResult.AccessToken
                    AccessToken  = $authResult.AccessToken
                    ExpiresOn    = $authResult.ExpiresOn
                    Scopes       = $authResult.Scopes
                    token_type   = 'Bearer'
                    client_id    = $Global:AccessToken.client_id
                }
                $Global:AuthenticationHeader = @{
                    'Content-Type'  = 'application/json'
                    'Authorization' = 'Bearer ' + $authResult.AccessToken
                    'ExpiresOn'     = $authResult.ExpiresOn
                }
                Write-Log "  Token refreshed via MSAL handle (expires $($authResult.ExpiresOn))"
                return
            }
        }
        catch {
            Write-Log "  MSAL silent refresh failed: $_" -Type 2
        }
    }

    # Tier 2: Use IntuneWin32App module's own refresh (CLI flow / fallback)
    try {
        Write-Log "  Tier 2: Attempting refresh via Connect-MSIntuneGraph..."
        Connect-MSIntuneGraph -TenantID $TenantID -ClientID $ClientID -Refresh
        Write-Log "  Token refreshed via Connect-MSIntuneGraph"
        return
    }
    catch {
        Write-Log "  Connect-MSIntuneGraph -Refresh failed: $_" -Type 2
    }

    # Tier 3: Full interactive re-auth (last resort)
    Write-Log "  Tier 3: All silent methods failed, attempting interactive auth..." -Type 2
    Connect-MSIntuneGraph -TenantID $TenantID -ClientID $ClientID
}
