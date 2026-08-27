function Test-SafeUncPath {
    <#
    .SYNOPSIS
        Validates that a path is a well-formed UNC path with no traversal segments.

    .DESCRIPTION
        Phase 11 hardening (S-1). Config.json carries domain TagFolder paths that
        flow into Join-Path calls. Without a guard, a malicious or malformed
        config could produce log writes outside the intended share. This helper
        rejects:
          - empty / whitespace
          - non-UNC roots (anything not starting with \\server\share)
          - any '..' segment
          - paths whose Path.GetFullPath canonicalisation differs (catches
            mixed-separator traversal and embedded normalisations)

    .OUTPUTS
        [bool] $true when safe to use, $false otherwise.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param([Parameter(Mandatory)][string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    if ($Path -notmatch '^\\\\[^\\]+\\[^\\]+') { return $false }
    if ($Path -match '(^|[\\/])\.\.([\\/]|$)') { return $false }
    try {
        $full = [System.IO.Path]::GetFullPath($Path)
        return $full.TrimEnd('\') -ieq $Path.TrimEnd('\')
    } catch { return $false }
}

function Test-SafeRelativePath {
    <#
    .SYNOPSIS
        Validates that a path is a safe relative path (no traversal, not rooted).

    .DESCRIPTION
        Phase 11 hardening (S-7). Config.json carries icon paths that get
        Join-Path'd against content-distribution UNC roots. A rooted or
        traversal-laced value could resolve outside the bundle and feed an
        arbitrary file to the SCCM cmdlet. Empty string is allowed (caller
        decides whether absence is meaningful).

    .OUTPUTS
        [bool] $true when safe to use, $false otherwise.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $true }
    if ($Path -match '(^|[\\/])\.\.([\\/]|$)') { return $false }
    if ($Path -match '^[a-zA-Z]:') { return $false }
    if ($Path.StartsWith('\\')) { return $false }
    return $true
}
