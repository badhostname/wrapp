function Update-ConfigSchema {
    <#
    .SYNOPSIS
        Ensures Config.json has all required fields, adding missing ones with defaults.
        Handles GUID auto-generation and legacy AppeaseGUID migration.

    .PARAMETER Config
        The loaded Config object from ConvertFrom-Json.

    .PARAMETER ConfigPath
        Path to Config.json for writing updates back.

    .OUTPUTS
        [PSCustomObject] Updated config object (reloaded from disk if modified).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        $Config,

        [Parameter(Mandatory = $true)]
        [string]$ConfigPath
    )

    $Modified = $false

    # GUID auto-generation (with legacy AppeaseGUID migration)
    $appProps = $Config.App.PSObject.Properties
    $hasGUID = $appProps.Name -contains 'GUID'
    $hasLegacy = $appProps.Name -contains 'AppeaseGUID'

    if (-not $hasGUID -and $hasLegacy) {
        $legacyValue = $Config.App.AppeaseGUID
        Write-Log "Migrating legacy AppeaseGUID to GUID."
        $orderedApp = [ordered]@{}
        foreach ($prop in $appProps) {
            if ($prop.Name -eq 'AppeaseGUID') {
                $orderedApp['GUID'] = $legacyValue
            } else {
                $orderedApp[$prop.Name] = $prop.Value
            }
        }
        $Config.App = [pscustomobject]$orderedApp
        $Modified = $true
        $hasGUID = $true
    }

    $guidValue = if ($hasGUID) { $Config.App.GUID } else { $null }

    if (-not $hasGUID -or [string]::IsNullOrWhiteSpace($guidValue)) {
        $NewGUID = [guid]::NewGuid().ToString()
        Write-Log "No GUID found in config. Generating: $NewGUID"

        # Insert GUID after MSIFile to preserve field order
        $orderedApp = [ordered]@{}
        $inserted = $false
        foreach ($prop in $Config.App.PSObject.Properties) {
            $orderedApp[$prop.Name] = $prop.Value
            if ($prop.Name -eq 'MSIFile' -and -not $inserted) {
                $orderedApp['GUID'] = $NewGUID
                $inserted = $true
            }
        }
        if (-not $inserted) {
            $orderedApp['GUID'] = $NewGUID
        }

        $Config.App = [pscustomobject]$orderedApp
        $Modified = $true
    }

    if ($Modified) {
        $JsonOut = $Config | ConvertTo-Json -Depth 10
        Set-Content -Path $ConfigPath -Value $JsonOut -Encoding UTF8
        Write-Log "Config.json updated with schema migrations."
        # Reload from disk to ensure consistency
        $Config = Get-Content -Raw -Path $ConfigPath | ConvertFrom-Json
    }

    return $Config
}
