function Clear-WrappTenantToken {
    <#
    .SYNOPSIS
        Removes the promoted per-tenant token globals between tenant passes
        (Workstream P). Counterpart of Use-WrappTenantToken.

    .DESCRIPTION
        Invoke-WrappPackaging calls this in a finally after every tenant pass
        so the next tenant can never inherit the previous tenant's token:
        Connect-WrappIntune's short-circuit and IntuneWin32App's
        Test-AccessToken both read these globals, and a leftover valid token
        would satisfy them. The token MAP ($Global:WrappTokenMap) survives --
        it holds the remaining tenants' entries and is cleared by the .NET
        side's post-run cleanup (ClearInjectedGlobalsScript).
    #>
    [CmdletBinding()]
    param()

    Remove-Variable -Scope Global -Name `
        AccessToken, AuthenticationHeader, AccessTokenTenantID, WrappMsalRefreshHandle `
        -ErrorAction SilentlyContinue
}
