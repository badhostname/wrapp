<#
.SYNOPSIS
    Rebuilds src\Wrapp.GUI\Assets\burrito.ico from burrito.png with the full
    Windows icon size set.

.DESCRIPTION
    Windows requires an app ICO to carry at least 16, 24, 32, 48 and 256px
    frames (256 PNG-compressed) - the shell picks the exact size the taskbar/
    Explorer needs at the current DPI and only scales DOWN. A single-frame ICO
    is why taskbar icons revert to the generic window glyph after DPI/monitor
    re-evaluation (lock-unlock, restart). See
    https://learn.microsoft.com/en-us/windows/apps/design/style/iconography/app-icon-construction

    Frames <= 64px are written as classic 32bpp BGRA BMP entries (with empty
    AND mask; alpha carries transparency) for maximum shell compatibility;
    the 256px frame is PNG-compressed per the standard.

.EXAMPLE
    .\scripts\Build-AppIcon.ps1
#>
[CmdletBinding()]
param(
    [string]$SourcePng,
    [string]$OutIco,
    [int[]]$BmpSizes = @(16, 20, 24, 32, 48, 64),
    [int]$PngSize    = 256
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# $PSScriptRoot is not reliable in param defaults under Windows PowerShell 5.1
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $SourcePng) { $SourcePng = Join-Path $scriptDir '..\src\Wrapp.GUI\Assets\burrito.png' }
if (-not $OutIco)    { $OutIco    = Join-Path $scriptDir '..\src\Wrapp.GUI\Assets\burrito.ico' }

function New-ResizedBitmap([System.Drawing.Image]$src, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($src, 0, 0, $size, $size)
    }
    finally { $g.Dispose() }
    return $bmp
}

# 32bpp BGRA ICO BMP frame: BITMAPINFOHEADER (biHeight = 2x for the AND mask),
# XOR data bottom-up, then an all-zero 1bpp AND mask padded to 32-bit rows.
function Get-IcoBmpFrame([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $pixels = New-Object byte[] ($stride * $s)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    }
    finally { $bmp.UnlockBits($data) }

    $andRowBytes = [int]([math]::Ceiling($s / 32.0) * 4)
    $xorSize = $s * $s * 4
    $andSize = $andRowBytes * $s

    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($ms)
    # BITMAPINFOHEADER
    $w.Write([int]40); $w.Write([int]$s); $w.Write([int]($s * 2))
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]0)
    $w.Write([int]($xorSize + $andSize)); $w.Write([int]0); $w.Write([int]0)
    $w.Write([int]0); $w.Write([int]0)
    # XOR (bottom-up BGRA)
    for ($y = $s - 1; $y -ge 0; $y--) { $w.Write($pixels, $y * $stride, $s * 4) }
    # AND mask (all zero - alpha channel governs transparency)
    $w.Write((New-Object byte[] $andSize))
    $w.Flush()
    return $ms.ToArray()
}

function Get-IcoPngFrame([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return $ms.ToArray()
}

$srcPath = (Resolve-Path $SourcePng).Path
$src = [System.Drawing.Image]::FromFile($srcPath)
try {
    $frames = @()
    foreach ($size in $BmpSizes) {
        $bmp = New-ResizedBitmap $src $size
        # [byte[]] cast: PS 5.1 unrolls the returned byte[] into Object[],
        # which BinaryWriter.Write silently mis-binds against.
        try { $frames += ,@{ Size = $size; Data = [byte[]](Get-IcoBmpFrame $bmp) } }
        finally { $bmp.Dispose() }
    }
    $png = New-ResizedBitmap $src $PngSize
    try { $frames += ,@{ Size = $PngSize; Data = [byte[]](Get-IcoPngFrame $png) } }
    finally { $png.Dispose() }
}
finally { $src.Dispose() }

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
# ICONDIR
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }   # 0 means 256
    $w.Write([byte]$dim); $w.Write([byte]$dim)
    $w.Write([byte]0); $w.Write([byte]0)                  # colors, reserved
    $w.Write([int16]1); $w.Write([int16]32)               # planes, bitcount
    $w.Write([int]$f.Data.Length); $w.Write([int]$offset)
    $offset += $f.Data.Length
}
foreach ($f in $frames) { $w.Write([byte[]]$f.Data) }
$w.Flush()

$resolvedOut = Join-Path (Resolve-Path (Split-Path $OutIco)).Path (Split-Path $OutIco -Leaf)
[System.IO.File]::WriteAllBytes($resolvedOut, $out.ToArray())

"Wrote $resolvedOut ($([math]::Round((Get-Item $resolvedOut).Length / 1KB, 1)) KB) with frames: $(($frames | ForEach-Object { $_.Size }) -join ', ')"
