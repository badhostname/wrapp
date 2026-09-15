function Get-WrappUserDefaults {
    <#
    .SYNOPSIS
        Reads the per-user shared defaults file the Wrapp GUI exports
        (%LOCALAPPDATA%\Wrapp\user-defaults.json), or $null when absent.

    .DESCRIPTION
        CLI/UI parity for user-curated defaults: the GUI's Settings >
        Preferences persists package/assignment defaults and endpoint script
        paths to settings.json AND exports a secret-free projection to
        user-defaults.json. This function is the module's read side; the
        result is layered over Config\Defaults.psd1 by
        Merge-WrappUserDefaults at run start. Precedence overall:
        bundle Config.json > user-defaults.json > Defaults.psd1.

        The file is also hand-editable for CLI-only installs; a corrupt or
        missing file quietly resolves to $null (module defaults apply).

    .PARAMETER Path
        Override the default location (used by tests).
    #>
    [CmdletBinding()]
    param(
        [string]$Path
    )

    if (-not $Path) {
        if (-not $env:LOCALAPPDATA) { return $null }
        $Path = Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Wrapp\user-defaults.json'
    }

    if (-not (Test-Path -Path $Path)) { return $null }

    try {
        return Get-Content -Raw -Path $Path | ConvertFrom-Json
    }
    catch {
        Write-Log "user-defaults.json unreadable ($Path): $_" -Type 2
        return $null
    }
}
