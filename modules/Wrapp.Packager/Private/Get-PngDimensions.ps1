function Get-PngDimensions {
    <#
    .SYNOPSIS
        Reads pixel width and height from a PNG file header.

    .DESCRIPTION
        Reads the first 24 bytes of a file and verifies the PNG signature,
        then decodes the IHDR chunk's width and height (big-endian 32-bit
        ints at offsets 16-19 and 20-23 respectively). Returns $null if the
        file isn't a PNG or can't be read.

        Used by Add-CMAppFromConfig to guard against the SCCM cmdlet's
        opaque "Validation of input parameters failed" rejection of icons
        larger than the cmdlet's (undocumented) dimension cap. Reading
        from the header avoids loading System.Drawing or pulling in any
        external image library -- 24 bytes of file I/O per check.

    .PARAMETER Path
        Path to the PNG file to inspect.

    .OUTPUTS
        PSCustomObject with Width / Height properties, or $null on failure.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    try {
        $fs = [System.IO.File]::OpenRead($Path)
        try {
            $buf = New-Object byte[] 24
            $read = $fs.Read($buf, 0, 24)
            if ($read -lt 24) { return $null }

            # PNG signature: 89 50 4E 47 0D 0A 1A 0A
            if ($buf[0] -ne 0x89 -or $buf[1] -ne 0x50 -or
                $buf[2] -ne 0x4E -or $buf[3] -ne 0x47) {
                return $null
            }

            # IHDR width/height start at offset 16, big-endian 32-bit.
            # Shift+add into [int] -- avoids endianness reverse with arrays.
            $w = ([int]$buf[16] -shl 24) -bor ([int]$buf[17] -shl 16) -bor
                 ([int]$buf[18] -shl 8)  -bor  [int]$buf[19]
            $h = ([int]$buf[20] -shl 24) -bor ([int]$buf[21] -shl 16) -bor
                 ([int]$buf[22] -shl 8)  -bor  [int]$buf[23]

            return [pscustomobject]@{
                Width  = $w
                Height = $h
            }
        }
        finally {
            $fs.Dispose()
        }
    }
    catch {
        # must-stay-silent: best-effort header read. A failure means
        # callers proceed without dimension info, which falls back to
        # the cmdlet's own (opaque) validation -- same behaviour as
        # before this helper existed.
        return $null
    }
}
