<#
.SYNOPSIS
    Generates src\Wrapp.GUI\Assets\install-splash.png - the branded card shown
    by Velopack's Setup.exe during installation.

.DESCRIPTION
    Velopack's installer shows a borderless window rendering this image
    (transparency supported) with a progress bar along the bottom; the bar
    color comes from `vpk pack --splashProgressColor` (Publish-Release.ps1
    passes Wrapp's dark-theme accent #9ac9cf). Update APPLIES never show this
    (they run silent since 0.6.302) - this card is for first installs only.

    Layout: dark rounded card (matches the app's dark theme), burrito logo,
    "Wrapp" title, accent subtitle, empty band at the bottom where the
    progress bar renders.

.EXAMPLE
    .\scripts\Build-InstallSplash.ps1
#>
[CmdletBinding()]
param(
    [string]$SourcePng,
    [string]$OutPng,
    [string]$Subtitle = 'Installing...'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $SourcePng) { $SourcePng = Join-Path $scriptDir '..\src\Wrapp.GUI\Assets\burrito.png' }
if (-not $OutPng)    { $OutPng    = Join-Path $scriptDir '..\src\Wrapp.GUI\Assets\install-splash.png' }

$W = 440; $H = 330; $radius = 18
$cardBg     = [System.Drawing.Color]::FromArgb(255, 0x1E, 0x1E, 0x1E)
$cardBorder = [System.Drawing.Color]::FromArgb(255, 0x3A, 0x3A, 0x3A)
$titleColor = [System.Drawing.Color]::FromArgb(255, 0xF5, 0xF5, 0xF5)
$subColor   = [System.Drawing.Color]::FromArgb(255, 0x9A, 0xC9, 0xCF)   # dark-theme AccentBrush

function New-RoundedRectPath([System.Drawing.RectangleF]$r, [float]$rad) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $rad * 2
    $p.AddArc($r.X, $r.Y, $d, $d, 180, 90)
    $p.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $p.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90)
    $p.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

$bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
try {
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Card (1px inset so the border stroke isn't clipped)
    $rect = New-Object System.Drawing.RectangleF(1, 1, ($W - 2), ($H - 2))
    $path = New-RoundedRectPath $rect $radius
    $bg = New-Object System.Drawing.SolidBrush($cardBg)
    $pen = New-Object System.Drawing.Pen($cardBorder, 1.5)
    $g.FillPath($bg, $path)
    $g.DrawPath($pen, $path)

    # Logo
    $logo = [System.Drawing.Image]::FromFile((Resolve-Path $SourcePng).Path)
    try {
        $ls = 132
        $g.DrawImage($logo, [int](($W - $ls) / 2), 42, $ls, $ls)
    }
    finally { $logo.Dispose() }

    # Title + subtitle, centered
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $titleFont = New-Object System.Drawing.Font('Segoe UI Semibold', 30, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Point)
    $subFont   = New-Object System.Drawing.Font('Segoe UI', 12.5, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Point)
    $titleBrush = New-Object System.Drawing.SolidBrush($titleColor)
    $subBrush   = New-Object System.Drawing.SolidBrush($subColor)
    $g.DrawString('Wrapp', $titleFont, $titleBrush, [System.Drawing.RectangleF]::new(0, 186, $W, 60), $fmt)
    $g.DrawString($Subtitle, $subFont, $subBrush, [System.Drawing.RectangleF]::new(0, 244, $W, 34), $fmt)
    # Bottom band (~40px) intentionally empty: Velopack renders the progress
    # bar along the bottom edge of the window.
}
finally { $g.Dispose() }

$resolvedOut = Join-Path (Resolve-Path (Split-Path $OutPng)).Path (Split-Path $OutPng -Leaf)
$bmp.Save($resolvedOut, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"Wrote $resolvedOut ($([math]::Round((Get-Item $resolvedOut).Length / 1KB, 1)) KB, ${W}x${H})"
