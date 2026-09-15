function Format-FileSize {
    <#
    .SYNOPSIS
        Converts a byte count to a human-readable size string.

    .PARAMETER Bytes
        The file size in bytes.

    .OUTPUTS
        [string] Formatted size (e.g., "12.34 MB").
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [long]$Bytes
    )

    switch ($Bytes) {
        { $_ -ge 1GB } { return '{0:N2} GB' -f ($Bytes / 1GB) }
        { $_ -ge 1MB } { return '{0:N2} MB' -f ($Bytes / 1MB) }
        { $_ -ge 1KB } { return '{0:N2} KB' -f ($Bytes / 1KB) }
        default        { return "$Bytes bytes" }
    }
}
