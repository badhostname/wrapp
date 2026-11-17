function Invoke-FieldSchema {
    <#
    .SYNOPSIS
        Applies a field schema entry to a value and returns structured validation issues.

    .DESCRIPTION
        Generic schema-driven validator. Reads a field's constraints from the
        Win32AppFieldSchema in Defaults.psd1 (or any compatible hashtable) and
        applies them to the provided value.

        Supported constraint keys in a schema entry:
          AllowedValues  - string array; emits INVALID_ENUM_VALUE if not in list
          Type           - 'Integer' or 'Boolean' or 'Url'; emits WRONG_TYPE if mismatch
          RangeMin       - integer; used with Type='Integer'; emits OUT_OF_RANGE
          RangeMax       - integer; used with Type='Integer'; emits OUT_OF_RANGE
          Pattern        - regex string; emits INVALID_URL (for Url type) or INVALID_PATTERN

        If the FieldName is not present in the schema at all, emits UNKNOWN_FIELD
        as a Warning (not an Error -- unknown keys are discarded, not fatal).

        If the schema entry exists but has no constraint keys (pass-through fields
        like FilePath, DisplayName), the function returns an empty array (valid).

        Returns an empty array when the value passes all applicable checks.

    .PARAMETER Schema
        The full field schema hashtable. Typically $script:ModuleDefaults.Win32AppFieldSchema.

    .PARAMETER FieldName
        The key to look up in the schema (e.g. 'InstallExperience').

    .PARAMETER Value
        The value to validate.

    .PARAMETER FieldPath
        Dot-path displayed in the issue FieldPath property.
        Example: 'Packages[MyApp].InstallExperience'

    .OUTPUTS
        [PSCustomObject[]] array of validation issue objects (empty = valid).
        Each object has the same shape as New-ValidationIssue output.
    #>
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Schema,

        [Parameter(Mandatory = $true)]
        [string]$FieldName,

        [Parameter(Mandatory = $true)]
        $Value,

        [Parameter(Mandatory = $true)]
        [string]$FieldPath
    )

    $issues = @()

    # Unknown field
    if (-not $Schema.ContainsKey($FieldName)) {
        $p = @{
            FieldPath      = $FieldPath
            Severity       = 'Warning'
            Code           = 'UNKNOWN_FIELD'
            Message        = "'$FieldName' is not a recognized parameter. It will be ignored."
            AttemptedValue = $FieldName
            Guidance       = "Check the Config.json reference or Wrapp.Packager documentation for the correct field name. Unrecognized keys are silently dropped and never passed to Add-IntuneWin32App."
        }
        $issues += New-ValidationIssue @p
        return $issues
    }

    $entry = $Schema[$FieldName]

    # Pass-through field: schema entry exists but has no constraint keys
    $hasConstraints = $entry.AllowedValues -or $entry.Pattern -or
                      ($null -ne $entry.RangeMin) -or ($null -ne $entry.RangeMax) -or
                      $entry.Type
    if (-not $hasConstraints) {
        return $issues  # empty -- valid
    }

    $guidance = if ($entry.Guidance) { $entry.Guidance } else { '' }

    # Type check: Integer
    if ($entry.Type -eq 'Integer') {
        if ($Value -isnot [int] -and $Value -isnot [long]) {
            $p = @{
                FieldPath      = $FieldPath
                Severity       = 'Error'
                Code           = 'WRONG_TYPE'
                Message        = "'$FieldName' requires an integer value, but '$Value' ($($Value.GetType().Name)) was provided."
                AttemptedValue = $Value
                Guidance       = $guidance
            }
            $issues += New-ValidationIssue @p
            return $issues  # no point range-checking a non-integer
        }

        $intVal = [int]$Value
        $rangeMin = if ($null -ne $entry.RangeMin) { [int]$entry.RangeMin } else { $null }
        $rangeMax = if ($null -ne $entry.RangeMax) { [int]$entry.RangeMax } else { $null }

        if ($null -ne $rangeMin -and $null -ne $rangeMax) {
            if ($intVal -lt $rangeMin -or $intVal -gt $rangeMax) {
                $p = @{
                    FieldPath      = $FieldPath
                    Severity       = 'Error'
                    Code           = 'OUT_OF_RANGE'
                    Message        = "'$FieldName' value $intVal is out of the allowed range ($rangeMin-$rangeMax)."
                    AttemptedValue = $intVal
                    Guidance       = $guidance
                }
                $issues += New-ValidationIssue @p
            }
        }
        elseif ($null -ne $rangeMin -and $intVal -lt $rangeMin) {
            $p = @{
                FieldPath      = $FieldPath
                Severity       = 'Error'
                Code           = 'OUT_OF_RANGE'
                Message        = "'$FieldName' value $intVal is below the minimum of $rangeMin."
                AttemptedValue = $intVal
                Guidance       = $guidance
            }
            $issues += New-ValidationIssue @p
        }
        elseif ($null -ne $rangeMax -and $intVal -gt $rangeMax) {
            $p = @{
                FieldPath      = $FieldPath
                Severity       = 'Error'
                Code           = 'OUT_OF_RANGE'
                Message        = "'$FieldName' value $intVal exceeds the maximum of $rangeMax."
                AttemptedValue = $intVal
                Guidance       = $guidance
            }
            $issues += New-ValidationIssue @p
        }

        return $issues
    }

    # Type check: Boolean
    if ($entry.Type -eq 'Boolean') {
        if ($Value -isnot [bool]) {
            $p = @{
                FieldPath      = $FieldPath
                Severity       = 'Error'
                Code           = 'WRONG_TYPE'
                Message        = "'$FieldName' requires a boolean value (true or false), but '$Value' ($($Value.GetType().Name)) was provided."
                AttemptedValue = $Value
                AllowedValues  = @('true', 'false')
                Guidance       = $guidance
            }
            $issues += New-ValidationIssue @p
        }
        return $issues
    }

    # URL pattern check
    if ($entry.Type -eq 'Url' -or $entry.Pattern) {
        # Empty/null URL values are valid (field is optional) - skip validation
        if ($Value -is [string] -and [string]::IsNullOrWhiteSpace($Value)) {
            return $issues
        }
        $pattern = if ($entry.Pattern) { $entry.Pattern } else { '^(http[s]?|[s]?ftp[s]?):\/\/[^\s,]+$' }
        $code    = if ($entry.Type -eq 'Url') { 'INVALID_URL' } else { 'INVALID_PATTERN' }

        if ($Value -is [string] -and $Value -notmatch $pattern) {
            $p = @{
                FieldPath      = $FieldPath
                Severity       = 'Error'
                Code           = $code
                Message        = "'$FieldName' value '$Value' is not a valid URL."
                AttemptedValue = $Value
                Guidance       = $guidance
            }
            $issues += New-ValidationIssue @p
        }
        return $issues
    }

    # Allowed values (enum) check
    if ($entry.AllowedValues -and @($entry.AllowedValues).Count -gt 0) {
        $allowed = @($entry.AllowedValues)
        if ($Value -notin $allowed) {
            $p = @{
                FieldPath      = $FieldPath
                Severity       = 'Error'
                Code           = 'INVALID_ENUM_VALUE'
                Message        = "'$FieldName' value '$Value' is not a recognized option. Valid values are: $($allowed -join ', ')."
                AttemptedValue = $Value
                AllowedValues  = $allowed
                Guidance       = $guidance
            }
            $issues += New-ValidationIssue @p
        }
    }

    return $issues
}
