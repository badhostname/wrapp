function Get-SafeDateTime {
    <#
    .SYNOPSIS
        Parses a date-time string and rejects the Intune MinValue sentinel
        when -RejectMinValue is specified.

    .DESCRIPTION
        Phase 14 (D-7). Set-CMAppDeployment carried two near-identical
        try/Get-Date + Year-le-1 blocks (one for AvailableDateTime, one for
        DeadlineDateTime). The MinValue check exists because Intune's ASAP
        sentinel is `0001-01-01T00:00:00.000Z` (`DateTime.MinValue`), which
        the SCCM cmdlet underflows when converting to UTC in any negative
        timezone. Centralising the parse + sentinel check here means the
        check is enforced consistently anywhere a Config-supplied datetime
        flows into a ConfigMgr cmdlet.

    .PARAMETER InputValue
        The string to parse. Empty / whitespace returns the DefaultIfUnset.
        Use named parameter form -InputValue, since "Input" is reserved.

    .PARAMETER DefaultIfUnset
        Value to return when InputValue is empty, unparseable, or the
        MinValue sentinel (with -RejectMinValue). Defaults to
        [datetime]::MinValue, but callers commonly pass $null to signal
        "treat as unset".

    .PARAMETER RejectMinValue
        When set, year <= 1 inputs (the Intune ASAP sentinel) are treated
        as unset and DefaultIfUnset is returned instead of the parsed value.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()][AllowEmptyString()]
        [string]$InputValue,

        $DefaultIfUnset = ([datetime]::MinValue),

        [switch]$RejectMinValue
    )

    if ([string]::IsNullOrWhiteSpace($InputValue)) { return $DefaultIfUnset }
    try {
        $dt = [datetime]::Parse($InputValue, [System.Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        return $DefaultIfUnset
    }
    if ($RejectMinValue -and $dt.Year -le 1) { return $DefaultIfUnset }
    return $dt
}
