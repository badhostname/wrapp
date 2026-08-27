<#
.SYNOPSIS
    Workstream D3: builds a distributable Wrapp release (Setup.exe + MSI +
    Velopack update packages) from the current tree.

.DESCRIPTION
    Pipeline: tests -> fresh self-contained publish -> release notes from the
    top CHANGELOG section -> `vpk pack` (delta against the previous release
    found in -ReleasesDir) -> optional copy to the update feed share.

    The publish stage always uses a FRESH staging directory, which structurally
    prevents the PreserveNewest zombie-file problem documented in
    docs/dependency-servicing.md (stale Monaco chunks etc.).

    Prereqs: dotnet SDK, and the Velopack CLI (`dotnet tool install -g vpk`).

.PARAMETER ReleasesDir
    Local canonical release store. vpk reads the previous release from here to
    build the delta package and writes the new artifacts into it. Keep this
    directory intact between releases (back it up; without the previous full
    package no delta can be produced).

.PARAMETER FeedDir
    Optional. UNC share / folder serving as the update feed. After a
    successful pack, releases.win.json + packages are mirrored here.

.PARAMETER SkipTests
    Skips the test run (use only when the tree was just validated).

.EXAMPLE
    .\scripts\Publish-Release.ps1

.EXAMPLE
    .\scripts\Publish-Release.ps1 -FeedDir \\fileserver\wrapp-updates
#>
[CmdletBinding()]
param(
    [string]$ReleasesDir = (Join-Path $PSScriptRoot '..\releases'),
    [string]$FeedDir,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$csprojPath = Join-Path $repoRoot 'src\Wrapp.GUI\Wrapp.GUI.csproj'

# ---------------------------------------------------------------------------
# Perf gate (production-readiness audit P4): surface this machine's stall and
# startup evidence before shipping. Informational, not blocking -- but a
# non-zero [STALL] count on the build machine deserves a look at app.log
# before the release goes out.
# ---------------------------------------------------------------------------
$appLog = Join-Path $env:LOCALAPPDATA 'Wrapp\app.log'
if (Test-Path $appLog) {
    $stallCount = @(Select-String -Path $appLog -Pattern '[STALL]' -SimpleMatch).Count
    $lastPerf = Select-String -Path $appLog -Pattern '[PERF] startup' -SimpleMatch | Select-Object -Last 1
    Write-Host "==> Perf gate: $stallCount [STALL] line(s) in current app.log"
    if ($lastPerf) { Write-Host "==> Perf gate: last startup -- $($lastPerf.Line -replace '^.*\[PERF\] ','')" }
    if ($stallCount -gt 0) {
        Write-Warning "Perf gate: UI stalls were recorded on this machine. Review app.log before shipping if unexpected."
    }
}

# ---------------------------------------------------------------------------
# 0. Resolve the SemVer2 version from the csproj (VersionPrefix + VersionSuffix)
# ---------------------------------------------------------------------------
$csproj = [xml](Get-Content $csprojPath -Raw)
$prefix = ($csproj.Project.PropertyGroup.VersionPrefix | Where-Object { $_ }) | Select-Object -First 1
$suffix = ($csproj.Project.PropertyGroup.VersionSuffix | Where-Object { $_ }) | Select-Object -First 1
if (-not $prefix) { throw "VersionPrefix not found in $csprojPath" }
if ($prefix -match '^\d+\.\d+\.\d+\.\d+$') {
    throw "VersionPrefix '$prefix' is 4-part; Velopack requires SemVer2 (see Workstream D1)."
}
$version = if ($suffix) { "$prefix-$suffix" } else { $prefix }
Write-Host "==> Packaging Wrapp $version" -ForegroundColor Cyan

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "The Velopack CLI is not installed. Run: dotnet tool install -g vpk"
}

# WiX opens the .msi through the Windows Installer engine; when the msiserver
# service is stopped and cannot auto-start (locked-down/sandboxed sessions),
# the MSI build dies with WIX0223 "Windows Installer service failed to start".
# Nudge it up-front so the failure (if any) is immediate and explicit.
$msiserver = Get-Service msiserver -ErrorAction SilentlyContinue
if ($msiserver -and $msiserver.Status -ne 'Running') {
    try { Start-Service msiserver -ErrorAction Stop }
    catch { throw "The Windows Installer service (msiserver) is stopped and could not be started -- the MSI build would fail (WIX0223). Start it and retry." }
}

# ---------------------------------------------------------------------------
# 1. Tests
# ---------------------------------------------------------------------------
if (-not $SkipTests) {
    Write-Host '==> Running test suite' -ForegroundColor Cyan
    dotnet test (Join-Path $repoRoot 'tests\Wrapp.GUI.Tests\Wrapp.GUI.Tests.csproj') --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; aborting release.' }
}

# ---------------------------------------------------------------------------
# 2. Fresh publish (never reuse a publish folder -- zombie-file class)
# ---------------------------------------------------------------------------
$staging = Join-Path ([IO.Path]::GetTempPath()) "wrapp-release-$version"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
Write-Host "==> Publishing to $staging" -ForegroundColor Cyan
$publishArgs = @(
    'publish', $csprojPath
    '-c', 'Release'
    '-r', 'win-x64'
    '--self-contained', 'true'
    '-o', $staging
    '--nologo', '-v', 'quiet'
)
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# Org-data guard: defaults.local.json is the gitignored per-org provisioning
# file; the csproj copies it to output when present so DEV builds come
# pre-provisioned. It must NEVER ship in a release artifact -- techs receive
# it through their own IT channel (first-run gate / Settings import).
$orgDefaults = Join-Path $staging 'defaults.local.json'
if (Test-Path $orgDefaults) {
    Remove-Item $orgDefaults -Force
    Write-Host '==> Stripped defaults.local.json from staging (org data never ships)' -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# 3. Release notes = the top CHANGELOG section (same slice the in-app
#    What's-New popup shows for this version)
# ---------------------------------------------------------------------------
$notesPath = Join-Path ([IO.Path]::GetTempPath()) "wrapp-release-notes-$version.md"
$changelog = Get-Content (Join-Path $repoRoot 'CHANGELOG.md')
$sectionLines = New-Object System.Collections.Generic.List[string]
$inSection = $false
foreach ($line in $changelog) {
    if ($line -match '^##\s*\[') {
        if ($inSection) { break }
        if ($line -notmatch [regex]::Escape("[$version]")) {
            throw "Top CHANGELOG section is not [$version] -- add the entry before releasing."
        }
        $inSection = $true
    }
    if ($inSection -and $line.Trim() -ne '---') { $sectionLines.Add($line) }
}
if (-not $inSection) { throw 'No CHANGELOG section found.' }
Set-Content -Path $notesPath -Value ($sectionLines -join "`n") -Encoding utf8
Write-Host "==> Release notes: $($sectionLines.Count) lines from CHANGELOG [$version]" -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# 4. vpk pack (delta against previous release in $ReleasesDir)
# ---------------------------------------------------------------------------
New-Item -ItemType Directory -Force $ReleasesDir | Out-Null
Write-Host "==> vpk pack -> $ReleasesDir" -ForegroundColor Cyan
$vpkArgs = @(
    'pack'
    '--packId', 'WrappApp'
    '--packVersion', $version
    '--packDir', $staging
    '--mainExe', 'Wrapp.exe'
    '--packTitle', 'Wrapp'
    '--packAuthors', 'Wrapp Project'
    '--icon', (Join-Path $repoRoot 'src\Wrapp.GUI\Assets\burrito.ico')
    # Branded install card (scripts\Build-InstallSplash.ps1); bar color =
    # dark-theme AccentBrush. Setup.exe's one-click path only -- update
    # applies are silent and the MSI uses the wizard below.
    '--splashImage', (Join-Path $repoRoot 'src\Wrapp.GUI\Assets\install-splash.png')
    '--splashProgressColor', '#9ac9cf'
    '--releaseNotes', $notesPath
    # --- MSI: the FULL Windows wizard (welcome -> license -> install scope
    #     per-user/per-machine -> progress -> finish), branded via the WiX
    #     bitmaps from scripts\Build-MsiArt.ps1. This is the installer to hand
    #     to technicians; Setup.exe stays the silent/one-click per-user path.
    '--msi', 'true'
    '--instLocation', 'Either'
    # !! THE NEXT TWO ARGUMENTS LOOK SWAPPED AND MUST STAY THAT WAY !!
    # vpk 1.2.0 crosses these options: --msiBanner writes the WixUI_Bmp_DIALOG
    # stream (the 493x312 welcome/finish background) and --msiLogo writes
    # WixUI_Bmp_BANNER (the 493x58 top strip). Proven by extracting the Binary
    # streams from a built MSI and comparing sizes (2026-08-07); it is also why
    # velopack master renamed them to --msiTopBanner/--msiDialogBackground.
    # The Verify-MsiBitmaps step after the pack asserts the result, so a vpk
    # upgrade that fixes the mapping fails the build loudly instead of shipping
    # scrambled artwork.
    '--msiBanner', (Join-Path $repoRoot 'src\Wrapp.GUI\Assets\msi-dialog.bmp')
    '--msiLogo',   (Join-Path $repoRoot 'src\Wrapp.GUI\Assets\msi-banner.bmp')
    # WIZARD TEXT IS CLIPPED, NOT SCROLLED. These land in fixed-size MSI Text
    # controls (dialog units, from Velopack's WiX templates) -- overflow is cut
    # off with an ellipsis, as shipped in 0.6.309:
    #   welcome    -> WelcomeDlg Description, 220x150 DU ~= 12 lines
    #   conclusion -> ExitDialog OptionalText, 220x80 DU  ~=  6 lines
    # A line holds ~55-60 characters at the default 8pt font. Count blank lines
    # too. Keep the conclusion to five lines or fewer.
    '--instWelcome', (Join-Path $repoRoot 'src\Wrapp.GUI\Assets\installer-welcome.txt')
    '--instConclusion', (Join-Path $repoRoot 'src\Wrapp.GUI\Assets\installer-conclusion.txt')
    # Velopack picks the license renderer by EXTENSION (RTF or Markdown), so
    # the extensionless repo LICENSE can't be passed directly -- Assets keeps
    # a .md copy (refresh it if LICENSE changes).
    #
    # MARKDOWN SUBSET: the .md is converted to RTF with Markdig's DEFAULT
    # (CommonMark-only) pipeline. Pipe TABLES are a Markdig extension that is
    # NOT enabled, so a table renders as raw pipe characters in the installer
    # (shipped that way in 0.6.307 -- fixed in 0.6.308). Stick to headings,
    # paragraphs, bold and bullet lists, all of which convert cleanly. The
    # file is end-user-visible: no maintainer notes in it.
    '--instLicense', (Join-Path $repoRoot 'src\Wrapp.GUI\Assets\installer-license.md')
    '--outputDir', $ReleasesDir
)
vpk @vpkArgs
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed.' }

# ---------------------------------------------------------------------------
# 4b. Verify the wizard bitmaps landed in the right streams
#     (guards the deliberately-swapped --msiBanner/--msiLogo arguments above)
# ---------------------------------------------------------------------------
$msiPath = Join-Path $ReleasesDir 'WrappApp-win.msi'
if (Test-Path $msiPath) {
    Add-Type -AssemblyName System.Drawing
    $expected = @{ 'WixUI_Bmp_Banner' = @(493, 58); 'WixUI_Bmp_Dialog' = @(493, 312) }
    # MSI's OpenDatabase rejects a non-canonical path (the default ReleasesDir
    # is "<scripts>\..\releases", and the '..' segment is enough) with a
    # MISLEADING DISP_E_TYPEMISMATCH, as if the ARGUMENTS were wrong. Full-path
    # it first. Cost two red herrings across the 0.6.318 and 0.6.319 packs.
    $msiFullPath = [IO.Path]::GetFullPath($msiPath)
    $inst = New-Object -ComObject WindowsInstaller.Installer
    $db = $inst.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $inst, @($msiFullPath, 0))
    $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db,
        @("SELECT Name, Data FROM Binary WHERE Name='WixUI_Bmp_Dialog' OR Name='WixUI_Bmp_Banner'"))
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
    $seen = @{}
    while ($true) {
        $rec = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $rec) { break }
        # Indexed COM properties take the index as an argument ARRAY.
        $name = $rec.GetType().InvokeMember('StringData', 'GetProperty', $null, $rec, @(1))
        $seen[$name] = $rec.GetType().InvokeMember('DataSize', 'GetProperty', $null, $rec, @(2))
    }
    foreach ($name in $expected.Keys) {
        if (-not $seen.ContainsKey($name)) { throw "MSI is missing the $name bitmap." }
    }
    # Sizes distinguish the two: the 493x312 background is ~5x the 493x58 strip.
    if ($seen['WixUI_Bmp_Dialog'] -lt $seen['WixUI_Bmp_Banner']) {
        throw "MSI wizard bitmaps are swapped (dialog=$($seen['WixUI_Bmp_Dialog'])B, banner=$($seen['WixUI_Bmp_Banner'])B). " +
              "vpk's --msiBanner/--msiLogo mapping likely changed -- swap the two arguments in this script."
    }
    Write-Host "==> MSI wizard bitmaps verified (background $($seen['WixUI_Bmp_Dialog'])B, banner $($seen['WixUI_Bmp_Banner'])B)" -ForegroundColor Cyan
}

# ---------------------------------------------------------------------------
# 5. Optional: mirror to the update feed
# ---------------------------------------------------------------------------
if ($FeedDir) {
    Write-Host "==> Mirroring feed to $FeedDir" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force $FeedDir | Out-Null
    # Everything the updater needs: the feed manifest + packages. Setup.exe /
    # MSI are included so the share also serves first-time installs.
    Copy-Item (Join-Path $ReleasesDir '*') $FeedDir -Force
}

Remove-Item $staging -Recurse -Force
Remove-Item $notesPath -Force
Write-Host "==> Done. Artifacts in $ReleasesDir" -ForegroundColor Green
Get-ChildItem $ReleasesDir |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 6 Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}} |
    Format-Table -AutoSize
