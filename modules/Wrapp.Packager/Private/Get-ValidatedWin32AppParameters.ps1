function Get-ValidatedWin32AppParameters {
    <#
    .SYNOPSIS
        Merges and validates parameters for Add-IntuneWin32App.

    .DESCRIPTION
        Applies the Win32AppFieldSchema from Defaults.psd1 to a merged set of
        default and package-specific override values. Returns a validated hashtable
        ready for splatting into Add-IntuneWin32App.

        The schema is the single source of truth for:
          - Which keys are accepted by Add-IntuneWin32App (schema membership)
          - Allowed values for enum fields (AllowedValues)
          - Valid ranges for integer fields (Type=Integer + RangeMin/RangeMax)
          - URL format for URL fields (Type=Url + Pattern)

        Fields not present in the schema are treated as unknown and discarded
        with an educational log message explaining what went wrong.

        Fields that fail validation are discarded with a log message that includes
        the constraint violated and guidance on the correct value to use.

        Output contract: returns [hashtable] -- same shape as before this refactor,
        safe to splat directly into Add-IntuneWin32App.

    .PARAMETER Defaults
        Default app parameter values (from Defaults.psd1 DefaultAppProperties
        and resolved tenant/config defaults).

    .PARAMETER Overrides
        Package-specific overrides from Config.json.

    .OUTPUTS
        [hashtable] Validated parameters ready for Add-IntuneWin32App splatting.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Defaults,

        [Parameter(Mandatory = $true)]
        [hashtable]$Overrides
    )

    $FieldSchema = $script:ModuleDefaults.Win32AppFieldSchema

    $Final = @{}

    # Apply defaults: accept only known schema keys
    foreach ($key in $Defaults.Keys) {
        if ($FieldSchema.ContainsKey($key)) {
            $Final[$key] = $Defaults[$key]
        }
        else {
            Write-Log "Default key '$key' is not in the Win32App field schema and will be ignored." -Type 2
        }
    }

    # Apply overrides: validate each key via Invoke-FieldSchema
    foreach ($key in $Overrides.Keys) {
        $val  = $Overrides[$key]
        $path = "Win32AppParameters.$key"

        $issues = Invoke-FieldSchema -Schema $FieldSchema -FieldName $key -Value $val -FieldPath $path

        if ($issues.Count -eq 0) {
            $Final[$key] = $val
        }
        else {
            foreach ($issue in $issues) {
                $logLine = $issue.Message
                if ($issue.Guidance) {
                    $logLine += " $($issue.Guidance)"
                }
                $logType = if ($issue.Severity -eq 'Warning') { 2 } else { 2 }
                Write-Log $logLine -Type $logType
            }
        }
    }

    # Strip empty/blank string values -- prevents failures from target module
    # parameter validators (e.g. IntuneWin32App [ValidatePattern] on URL fields)
    $emptyKeys = @($Final.Keys | Where-Object {
        $Final[$_] -is [string] -and [string]::IsNullOrWhiteSpace($Final[$_])
    })
    foreach ($key in $emptyKeys) {
        $Final.Remove($key)
    }

    return $Final
}
