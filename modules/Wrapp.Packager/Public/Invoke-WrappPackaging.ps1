function Invoke-WrappPackaging {
    <#
    .SYNOPSIS
        Module-owned multi-tenant / multi-site packaging orchestrator.
        One call packages every routed target.

    .DESCRIPTION
        Owns the loop that used to live in the Wrapp GUI (RunViewModel): reads
        the config's routing (each Intune package's TenantId, each SCCM
        package's SiteCode), groups packages by target, and invokes the
        existing per-target orchestrators (Invoke-WrappIntune /
        Invoke-WrappSccm) once per target with the packages scoped to it.

        Because the GUI calls THIS function too, UI and CLI behavior are the
        same code path by construction -- a broken UI can be replaced by:

            Import-Module Wrapp.Packager
            Invoke-WrappPackaging -ConfigPath C:\pkg\Script\Config.json

        Authentication is hybrid, per tenant:
          - GUI flow: the .NET side injects $Global:WrappTokenMap (one MSAL
            token per enabled tenant); each tenant pass promotes its entry via
            Use-WrappTenantToken and clears it after (Clear-WrappTenantToken),
            so no tenant can inherit another's token.
          - CLI flow: no map entry -> Connect-WrappIntune self-authenticates
            from the tenant's configured AuthFlow (Interactive / DeviceCode /
            ClientSecret / ClientCert).

        Emits Write-WrappStep 'TenantPass' Start/Success/Fail boundary events
        (Package = '*', Detail = the pass's package names joined with '|') so
        the GUI scopes per-pass progress from the stream instead of from its
        own loop iteration.

    .PARAMETER ConfigPath
        Path to Config.json.

    .PARAMETER Target
        Intune (default), SCCM, or Both.

    .PARAMETER TenantIds
        Optional tenant filter. The GUI passes its enabled/connected subset;
        CLI omits it to process every tenant the config routes packages to.

    .PARAMETER SiteCodes
        Optional SCCM site filter (same semantics as TenantIds).

    .PARAMETER PackageNames
        Optional package filter applied before routing.

    .PARAMETER SkipCollisionCheck
        Passed through to Invoke-WrappIntune. Only for callers that have just
        run Test-Win32AppCollisions themselves (the Wrapp GUI pre-flight);
        CLI callers should omit this. SCCM passes are unaffected.

    .PARAMETER Validate
        Validation mode - passed through to the per-target orchestrators.

    .PARAMETER LogPath
        Optional log file override - passed through to the per-target
        orchestrators.

    .OUTPUTS
        [PSCustomObject] with:
            Success       [bool]      True when every executed pass succeeded
            TenantResults [hashtable] TenantId -> Invoke-WrappIntune result
            SiteResults   [hashtable] SiteCode -> Invoke-WrappSccm result
            Errors        [array]     Flattened per-pass errors
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ConfigPath,

        [ValidateSet('Intune', 'SCCM', 'Both')]
        [string]$Target = 'Intune',

        [string[]]$TenantIds,

        [string[]]$SiteCodes,

        [string[]]$PackageNames,

        [switch]$SkipCollisionCheck,

        [switch]$Validate,

        [string]$LogPath
    )

    $ErrorActionPreference = 'Stop'

    $Aggregate = [PSCustomObject]@{
        Success       = $true
        TenantResults = @{}
        SiteResults   = @{}
        Errors        = [System.Collections.Generic.List[string]]::new()
    }

    if (-not (Test-Path -Path $ConfigPath)) {
        throw "Config.json not found at: $ConfigPath"
    }
    # Routing-only read; the per-target orchestrators re-load and validate the
    # config themselves (schema migration, preflight, etc.).
    $Config = Get-Content -Raw -Path $ConfigPath | ConvertFrom-Json

    # Shared: group packages by a per-package routing key, preserving the
    # config's package order and the order targets first appear.
    function Group-PackagesByTarget {
        param($Packages, [string]$RouteKey, [string[]]$TargetFilter)

        $Groups   = [ordered]@{}
        $Unrouted = [System.Collections.Generic.List[string]]::new()
        $Disabled = [System.Collections.Generic.List[string]]::new()

        foreach ($p in @($Packages)) {
            # Persistent operator intent (GUI enable checkbox); absent = enabled.
            # Also enforced by the inner orchestrators for direct CLI calls;
            # filtering here keeps the routing logs and pass counts honest.
            if ((Get-ConfigValue $p 'IsEnabled' $true) -eq $false) {
                $Disabled.Add($p.AppName)
                continue
            }
            if ($PackageNames -and $PackageNames.Count -gt 0 -and $p.AppName -notin $PackageNames) {
                continue
            }
            $route = Get-ConfigValue $p $RouteKey ''
            if (-not $route) {
                $Unrouted.Add($p.AppName)
                continue
            }
            if ($TargetFilter -and $TargetFilter.Count -gt 0 -and $route -notin $TargetFilter) {
                continue
            }
            if (-not $Groups.Contains($route)) {
                $Groups[$route] = [System.Collections.Generic.List[string]]::new()
            }
            $Groups[$route].Add($p.AppName)
        }

        [PSCustomObject]@{ Groups = $Groups; Unrouted = $Unrouted; Disabled = $Disabled }
    }

    # Intune: one pass per tenant
    if ($Target -in @('Intune', 'Both')) {
        $Routing = Group-PackagesByTarget -Packages $Config.Script.IntunePackager.Packages `
            -RouteKey 'TenantId' -TargetFilter $TenantIds

        if ($Routing.Disabled.Count -gt 0) {
            Write-Log "Skipping disabled package(s): $($Routing.Disabled -join ', ')"
        }
        if ($Routing.Unrouted.Count -gt 0) {
            Write-Log "Skipping package(s) with no TenantId routing: $($Routing.Unrouted -join ', ')" -Type 2
        }
        if ($Routing.Groups.Count -eq 0) {
            Write-Log "No Intune packages routed to any tenant$(if ($TenantIds) { " (filter: $($TenantIds -join ', '))" })." -Type 2
        }

        foreach ($TenantId in $Routing.Groups.Keys) {
            $Names = @($Routing.Groups[$TenantId])
            Write-Log "=== Tenant pass: $TenantId ($($Names.Count) package(s): $($Names -join ', ')) ==="

            # Boundary marker: the GUI scopes per-pass progress from this
            # (Detail carries the pass's package names).
            $PassStart = @{
                Package  = '*'
                Step     = 'TenantPass'
                Kind     = 'Start'
                TenantId = $TenantId
                Detail   = $Names -join '|'
            }
            Write-WrappStep @PassStart

            $TenantResult = $null
            try {
                if (Use-WrappTenantToken -TenantId $TenantId) {
                    Write-Log "Using injected token for tenant '$TenantId'."
                }
                else {
                    Write-Log "No injected token for tenant '$TenantId' - the module will authenticate (config AuthFlow)."
                }

                $InnerParams = @{
                    ConfigPath   = $ConfigPath
                    TenantId     = $TenantId
                    PackageNames = $Names
                }
                if ($SkipCollisionCheck) { $InnerParams['SkipCollisionCheck'] = $true }
                if ($Validate) { $InnerParams['Validate'] = $true }
                if ($LogPath)  { $InnerParams['LogPath']  = $LogPath }

                # The inner orchestrator interleaves WrappStep / EncryptionKeys
                # events with its final result object; re-emit the events
                # upward (live) and capture the result (the only object
                # carrying a .Success property).
                Invoke-WrappIntune @InnerParams | ForEach-Object {
                    if ($_ -and $_.PSObject.Properties['Success']) { $TenantResult = $_ }
                    else { $_ }
                }
            }
            catch {
                Write-Log "Tenant pass failed for '$TenantId': $_" -Type 3
                $Aggregate.Errors.Add("Tenant '$TenantId': $_")
            }
            finally {
                # Next tenant must never inherit this tenant's token
                # (Connect-WrappIntune's short-circuit + Test-AccessToken
                # both read the promoted globals).
                Clear-WrappTenantToken
            }

            $PassOk = [bool]($TenantResult -and $TenantResult.Success)
            $Aggregate.TenantResults[$TenantId] = $TenantResult
            if (-not $PassOk) {
                $Aggregate.Success = $false
                if ($TenantResult) {
                    foreach ($e in $TenantResult.Errors) {
                        $Aggregate.Errors.Add("Tenant '$TenantId': $e")
                    }
                }
            }

            $PassEnd = @{
                Package  = '*'
                Step     = 'TenantPass'
                Kind     = if ($PassOk) { 'Success' } else { 'Fail' }
                TenantId = $TenantId
            }
            Write-WrappStep @PassEnd
        }
    }

    # SCCM: one pass per site
    if ($Target -in @('SCCM', 'Both')) {
        $Routing = Group-PackagesByTarget -Packages $Config.Script.SCCMPackager.Packages `
            -RouteKey 'SiteCode' -TargetFilter $SiteCodes

        if ($Routing.Disabled.Count -gt 0) {
            Write-Log "Skipping disabled SCCM package(s): $($Routing.Disabled -join ', ')"
        }
        if ($Routing.Unrouted.Count -gt 0) {
            Write-Log "Skipping SCCM package(s) with no SiteCode routing: $($Routing.Unrouted -join ', ')" -Type 2
        }
        if ($Routing.Groups.Count -eq 0) {
            Write-Log "No SCCM packages routed to any site$(if ($SiteCodes) { " (filter: $($SiteCodes -join ', '))" })." -Type 2
        }

        foreach ($SiteCode in $Routing.Groups.Keys) {
            $Names = @($Routing.Groups[$SiteCode])
            Write-Log "=== Site pass: $SiteCode ($($Names.Count) package(s): $($Names -join ', ')) ==="

            $PassStart = @{
                Package  = '*'
                Step     = 'TenantPass'
                Kind     = 'Start'
                TenantId = $SiteCode
                Detail   = $Names -join '|'
            }
            Write-WrappStep @PassStart

            $SiteResult = $null
            try {
                $InnerParams = @{
                    ConfigPath   = $ConfigPath
                    SiteCode     = $SiteCode
                    PackageNames = $Names
                }
                if ($Validate) { $InnerParams['Validate'] = $true }
                if ($LogPath)  { $InnerParams['LogPath']  = $LogPath }

                Invoke-WrappSccm @InnerParams | ForEach-Object {
                    if ($_ -and $_.PSObject.Properties['Success']) { $SiteResult = $_ }
                    else { $_ }
                }
            }
            catch {
                Write-Log "Site pass failed for '$SiteCode': $_" -Type 3
                $Aggregate.Errors.Add("Site '$SiteCode': $_")
            }

            $PassOk = [bool]($SiteResult -and $SiteResult.Success)
            $Aggregate.SiteResults[$SiteCode] = $SiteResult
            if (-not $PassOk) {
                $Aggregate.Success = $false
                if ($SiteResult) {
                    foreach ($e in $SiteResult.Errors) {
                        $Aggregate.Errors.Add("Site '$SiteCode': $e")
                    }
                }
            }

            $PassEnd = @{
                Package  = '*'
                Step     = 'TenantPass'
                Kind     = if ($PassOk) { 'Success' } else { 'Fail' }
                TenantId = $SiteCode
            }
            Write-WrappStep @PassEnd
        }
    }

    if ($Aggregate.Success) {
        Write-Log "Invoke-WrappPackaging completed successfully."
    }
    else {
        Write-Log "Invoke-WrappPackaging completed with $($Aggregate.Errors.Count) error(s)." -Type 2
    }

    return $Aggregate
}
