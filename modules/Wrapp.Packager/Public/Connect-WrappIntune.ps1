function Connect-WrappIntune {
    <#
    .SYNOPSIS
        Authenticates to MS Graph for Intune operations using hybrid auth.

    .DESCRIPTION
        Wraps Connect-MSIntuneGraph with hybrid auth support:
        - Interactive/DeviceCode: Uses well-known Microsoft Graph PowerShell
          client ID if no ClientID provided (no app registration needed).
        - ClientSecret/ClientCert: Requires explicit ClientID (app registration).

    .PARAMETER TenantID
        Entra ID tenant ID.

    .PARAMETER ClientID
        Optional for Interactive/DeviceCode (falls back to well-known ID).
        Mandatory for ClientSecret/ClientCert.

    .PARAMETER AuthFlow
        One of: Interactive, DeviceCode, ClientSecret, ClientCert. Default: Interactive.

    .PARAMETER ClientSecret
        Required when AuthFlow is ClientSecret. Accepts a [SecureString];
        callers with a plaintext value should wrap via
        `ConvertTo-SecureString $value -AsPlainText -Force`. The plaintext is
        materialized only inside a narrow Marshal::SecureStringToBSTR scope
        immediately before the vendored Connect-MSIntuneGraph call, then the
        BSTR is zero-freed.

    .PARAMETER CertThumbprint
        Required when AuthFlow is ClientCert. Certificate looked up in
        Cert:\CurrentUser\My and Cert:\LocalMachine\My.

    .OUTPUTS
        The authentication header returned by Connect-MSIntuneGraph.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$TenantID,

        [string]$ClientID,

        [ValidateSet('Interactive', 'DeviceCode', 'ClientSecret', 'ClientCert')]
        [string]$AuthFlow = 'Interactive',

        [SecureString]$ClientSecret,

        [string]$CertThumbprint
    )

    # If a valid token is already injected (e.g. by the GUI via MSAL),
    # skip authentication entirely to avoid a redundant browser prompt.
    #
    # The short-circuit requires the injected token to belong to
    # THE REQUESTED TENANT. Without the tenant match, a module-owned
    # multi-tenant loop (Invoke-WrappPackaging) would silently reuse tenant A's
    # still-valid token for tenant B. A mismatched leftover token
    # falls through to a fresh auth for the correct tenant.
    if ($Global:AuthenticationHeader -and $Global:AccessToken -and
        $Global:AccessTokenTenantID -and
        ($Global:AccessTokenTenantID -eq $TenantID) -and (Test-AccessToken)) {
        Write-Log "Valid authentication token already present for tenant '$TenantID' - skipping auth flow."
        return $Global:AuthenticationHeader
    }
    elseif ($Global:AccessTokenTenantID -and $Global:AccessTokenTenantID -ne $TenantID) {
        Write-Log "Injected token belongs to tenant '$($Global:AccessTokenTenantID)' but '$TenantID' was requested - authenticating fresh." -Type 2
    }

    # Resolve ClientID: use well-known fallback for delegated flows
    $WellKnownClientID = $script:ModuleDefaults.WellKnownClientID

    $EffectiveClientID = if ($ClientID) {
        $ClientID
    }
    elseif ($AuthFlow -in @('Interactive', 'DeviceCode')) {
        Write-Log "No ClientID configured. Using well-known Microsoft Graph PowerShell client ID (no app registration required)."
        $WellKnownClientID
    }
    else {
        throw "ClientID is required for AuthFlow '$AuthFlow'. Only Interactive and DeviceCode support the well-known client ID fallback."
    }

    Write-Log "Auth flow: $AuthFlow | ClientID: $EffectiveClientID"

    $ConnectParams = @{
        TenantID = $TenantID
        ClientID = $EffectiveClientID
    }

    switch ($AuthFlow.ToLowerInvariant()) {
        'interactive' {
            # Default PKCE flow - opens browser for sign-in
        }
        'devicecode' {
            $ConnectParams['DeviceCode'] = $true
        }
        'clientsecret' {
            if ($null -eq $ClientSecret -or $ClientSecret.Length -eq 0) {
                throw "AuthFlow 'ClientSecret' requires a ClientSecret value."
            }
            # Unwrap just-in-time: the plaintext .NET string exists only for
            # the vendored Connect-MSIntuneGraph call below, then the BSTR
            # backing it is zero-freed. The vendored module's own copy remains
            # plaintext (we can't zero that without patching upstream), but
            # our side of the boundary holds no long-lived plaintext variable.
            $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ClientSecret)
            try {
                $ConnectParams['ClientSecret'] = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
                $AuthHeader = Connect-MSIntuneGraph @ConnectParams
                Write-Log "Successfully authenticated to MS Graph."
                return $AuthHeader
            }
            catch {
                Write-Log "Authentication failed (flow: $AuthFlow): $_" -Type 3
                throw
            }
            finally {
                [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
                $ConnectParams['ClientSecret'] = $null
            }
        }
        'clientcert' {
            if (-not $CertThumbprint) {
                throw "AuthFlow 'ClientCert' requires a CertThumbprint value."
            }
            $Cert = Get-ChildItem -Path @('Cert:\CurrentUser\My', 'Cert:\LocalMachine\My') -ErrorAction SilentlyContinue |
                Where-Object { $_.Thumbprint -eq $CertThumbprint } |
                Select-Object -First 1
            if (-not $Cert) {
                throw "Certificate with thumbprint '$CertThumbprint' not found in CurrentUser or LocalMachine stores."
            }
            Write-Log "Found certificate: Subject='$($Cert.Subject)', Store='$($Cert.PSParentPath)'"
            $ConnectParams['ClientCert'] = $Cert
        }
    }

    try {
        $AuthHeader = Connect-MSIntuneGraph @ConnectParams
        Write-Log "Successfully authenticated to MS Graph."
        return $AuthHeader
    }
    catch {
        Write-Log "Authentication failed (flow: $AuthFlow): $_" -Type 3
        throw
    }
}
