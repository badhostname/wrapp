<#
.SYNOPSIS
    Generates the MSI wizard bitmaps used by Velopack's WiX template:
    Assets\msi-banner.bmp (493x58) and Assets\msi-dialog.bmp (493x312).

.DESCRIPTION
    The MSI is Wrapp's FULL installer experience - the standard Windows wizard
    (welcome → license → install scope (per-user / per-machine) → progress →
    finish). WiX requires exact dimensions and BMP format:
      * banner  493x58  - top strip on interior pages
      * dialog  493x312 - left sidebar art on the welcome/finish pages
    IMPORTANT layout constraint: WiX draws its own (dark) text ON TOP of these
    bitmaps - the banner's left portion carries the page title, and the dialog
    bitmap's right ~2/3 carries the welcome/finish text. Those regions are kept
    LIGHT and clear; branding lives in the banner's right end and the dialog's
    left sidebar. This is the conventional Windows-installer look, not a custom
    dark window.

.EXAMPLE
    .\scripts\Build-MsiArt.ps1
#>
[CmdletBinding()]
param(
    [string]$SourcePng,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $SourcePng) { $SourcePng = Join-Path $scriptDir '..\src\Wrapp.GUI\Assets\burrito.png' }
if (-not $OutDir)    { $OutDir    = Join-Path $scriptDir '..\src\Wrapp.GUI\Assets' }

$white    = [System.Drawing.Color]::White
$sideTop  = [System.Drawing.Color]::FromArgb(255, 0x2B, 0x53, 0x60)   # deep teal (light-theme AccentBrush family)
$sideBot  = [System.Drawing.Color]::FromArgb(255, 0x36, 0x63, 0x72)   # light-theme AccentBrush #366372
$accent   = [System.Drawing.Color]::FromArgb(255, 0x36, 0x63, 0x72)
$onDark   = [System.Drawing.Color]::FromArgb(255, 0xF7, 0xFA, 0xFB)
$onDarkSub = [System.Drawing.Color]::FromArgb(255, 0xC9, 0xE1, 0xE6)

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $g.Clear($white)
    return @{ Bitmap = $bmp; Graphics = $g }
}

$logoPath = (Resolve-Path $SourcePng).Path

# ---- Banner 493x58: white (WiX writes the page title over the LEFT side in
#      dark text), logo + wordmark at the RIGHT end, thin accent rule. ----
$c = New-Canvas 493 58
try {
    $g = $c.Graphics
    $logo = [System.Drawing.Image]::FromFile($logoPath)
    try { $g.DrawImage($logo, 381, 8, 42, 42) } finally { $logo.Dispose() }

    $titleFont = New-Object System.Drawing.Font('Segoe UI Semibold', 15, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Point)
    $titleBrush = New-Object System.Drawing.SolidBrush($accent)
    $g.DrawString('Wrapp', $titleFont, $titleBrush, 425, 15)

    $accentPen = New-Object System.Drawing.Pen((New-Object System.Drawing.SolidBrush($accent)), 2)
    $g.DrawLine($accentPen, 0, 57, 493, 57)

    $c.Bitmap.Save((Join-Path (Resolve-Path $OutDir).Path 'msi-banner.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
}
finally { $c.Graphics.Dispose(); $c.Bitmap.Dispose() }

# ---- Dialog 493x312: teal sidebar with branding on the LEFT 164px; the rest
#      stays WHITE because WiX renders the welcome/finish text over it. ----
$c = New-Canvas 493 312
try {
    $g = $c.Graphics
    $panel = New-Object System.Drawing.Rectangle(0, 0, 164, 312)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($panel, $sideTop, $sideBot, 90.0)
    $g.FillRectangle($grad, $panel)

    $logo = [System.Drawing.Image]::FromFile($logoPath)
    try { $g.DrawImage($logo, 27, 74, 110, 110) } finally { $logo.Dispose() }

    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $titleFont = New-Object System.Drawing.Font('Segoe UI Semibold', 19, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Point)
    $subFont   = New-Object System.Drawing.Font('Segoe UI', 8.5, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Point)
    $titleBrush = New-Object System.Drawing.SolidBrush($onDark)
    $subBrush   = New-Object System.Drawing.SolidBrush($onDarkSub)
    $g.DrawString('Wrapp', $titleFont, $titleBrush, [System.Drawing.RectangleF]::new(0, 196, 164, 38), $fmt)
    $g.DrawString('Application packaging', $subFont, $subBrush, [System.Drawing.RectangleF]::new(0, 232, 164, 22), $fmt)

    $c.Bitmap.Save((Join-Path (Resolve-Path $OutDir).Path 'msi-dialog.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
}
finally { $c.Graphics.Dispose(); $c.Bitmap.Dispose() }

foreach ($f in 'msi-banner.bmp', 'msi-dialog.bmp') {
    $p = Join-Path (Resolve-Path $OutDir).Path $f
    "Wrote $p ($([math]::Round((Get-Item $p).Length / 1KB, 1)) KB)"
}
