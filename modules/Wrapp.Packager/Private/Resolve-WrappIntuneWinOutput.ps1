function Resolve-WrappIntuneWinOutput {
    <#
    .SYNOPSIS
        Decides where a run writes the .intunewin.

    .DESCRIPTION
        One file per run, shared by every package in the pass, so the folder
        is a property of the bundle. In order:

          1. Config.Script.IntunePackager.Output.Path - written by Wrapp at
             save from Settings or the bundle's own choice. A relative value
             (Wrapp writes '..\Output' for "beside the bundle") is joined to
             the bundle root, so it travels with the bundle.
          2. TenantConfig.IntuneWinPath + Company\Name\Version - bundles
             saved before the Output block existed. Kept one release.
          3. Output beside the bundle: <parent of the bundle>\Output.

        When the drive or share root of the chosen folder is not reachable
        from this computer, the result is Output beside the bundle and
        Source is 'Fallback', with Requested and Reason filled in; callers
        log it, the preflight warns about it, nothing prompts.

    .PARAMETER Config
        The loaded Config object.

    .PARAMETER PackagePath
        The bundle root (Resolve-WrappBundle's Root).

    .PARAMETER TenantConfig
        The tenant's Config.IntuneTenant entry, for the legacy IntuneWinPath.

    .OUTPUTS
        [PSCustomObject] Path, Source ('Config' | 'Tenant' | 'Default' | 'Fallback'), Requested, Reason
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        $Config,

        [Parameter(Mandatory = $true)]
        [string]$PackagePath,

        $TenantConfig
    )

    $bundleRoot = $PackagePath.TrimEnd('\', '/')
    $parent = [System.IO.Path]::GetDirectoryName($bundleRoot)
    if (-not $parent) { $parent = $bundleRoot }
    # Path.Combine rather than Join-Path throughout: Join-Path validates the
    # drive and throws for an unreachable one, which is exactly the case the
    # fallback below is for.
    $default = [System.IO.Path]::Combine($parent, 'Output')

    $requested = $null
    $source = 'Default'

    $output = $null
    try { $output = $Config.Script.IntunePackager.Output } catch { $output = $null }
    $configured = if ($output) { "$($output.Path)".Trim() } else { '' }

    if ($configured) {
        if ([System.IO.Path]::IsPathRooted($configured)) {
            $requested = $configured
        }
        else {
            $requested = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($bundleRoot, $configured))
        }
        $source = 'Config'
    }
    elseif ($TenantConfig -and "$($TenantConfig.IntuneWinPath)".Trim()) {
        $app = $Config.App
        $requested = [System.IO.Path]::Combine("$($TenantConfig.IntuneWinPath)".Trim(), "$($app.Company)\$($app.Name)\$($app.Version)")
        $source = 'Tenant'
    }

    if (-not $requested) {
        return [pscustomobject]@{ Path = $default; Source = 'Default'; Requested = $null; Reason = $null }
    }

    $root = [System.IO.Path]::GetPathRoot($requested)
    if ($root -and (Test-Path -LiteralPath $root -ErrorAction SilentlyContinue)) {
        return [pscustomobject]@{ Path = $requested; Source = $source; Requested = $requested; Reason = $null }
    }

    return [pscustomobject]@{
        Path      = $default
        Source    = 'Fallback'
        Requested = $requested
        Reason    = "the drive or share root '$root' is not reachable from this computer"
    }
}
