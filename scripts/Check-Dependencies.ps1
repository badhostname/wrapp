<#
.SYNOPSIS
    Audits every Wrapp dependency channel: NuGet CVEs, outdated packages, and
    vendored component versions (Monaco, MinGit, PSADT, IntuneWin32App, WebView2 SDK).

.DESCRIPTION
    One command answers "are we exposed and what needs a bump?":

      1. NuGet vulnerability scan  - dotnet list package --vulnerable --include-transitive
                                     (GitHub Advisory Database via nuget.org)
      2. NuGet outdated report     - dotnet list package --outdated
      3. Vendored inventory        - versions parsed from the tree itself, compared
                                     against upstream (npm / GitHub releases) with -Online

    Exit code 1 when any vulnerable package is found (CI-friendly); 0 otherwise.
    The update procedures for each channel are documented in
    docs/dependency-servicing.md - this script is the detection half.

.PARAMETER Online
    Also query upstream registries (npmjs, GitHub releases) for the latest
    vendored-component versions. Best effort - failures are reported, not fatal.

.EXAMPLE
    .\scripts\Check-Dependencies.ps1 -Online
#>
[CmdletBinding()]
param(
    [switch]$Online
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$guiProj  = Join-Path $repoRoot 'src\Wrapp.GUI\Wrapp.GUI.csproj'
$testProj = Join-Path $repoRoot 'tests\Wrapp.GUI.Tests\Wrapp.GUI.Tests.csproj'
$hasVulnerable = $false

function Write-Section([string]$Title) {
    Write-Host ''
    Write-Host ("=" * 70) -ForegroundColor DarkGray
    Write-Host " $Title" -ForegroundColor Cyan
    Write-Host ("=" * 70) -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------
# 1. NuGet vulnerability scan (the CVE check)
# ---------------------------------------------------------------------------
Write-Section 'NuGet vulnerability scan (GitHub Advisory Database)'
foreach ($proj in @($guiProj, $testProj)) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($proj)
    $out = & dotnet list $proj package --vulnerable --include-transitive 2>&1 | Out-String
    if ($out -match 'has the following vulnerable packages') {
        $script:hasVulnerable = $true
        Write-Host "[$name] VULNERABLE PACKAGES FOUND:" -ForegroundColor Red
        Write-Host $out
    }
    elseif ($out -match 'has no vulnerable packages') {
        Write-Host "[$name] OK - no known vulnerable packages" -ForegroundColor Green
    }
    else {
        Write-Host "[$name] scan inconclusive (restore needed or nuget.org unreachable):" -ForegroundColor Yellow
        Write-Host $out
    }
}

# ---------------------------------------------------------------------------
# 2. NuGet outdated report
# ---------------------------------------------------------------------------
Write-Section 'NuGet outdated packages (direct references)'
& dotnet list $guiProj package --outdated

# ---------------------------------------------------------------------------
# 3. Vendored / non-NuGet component inventory
# ---------------------------------------------------------------------------
Write-Section 'Vendored component inventory'

$inventory = @()

# Monaco editor -- version marker maintained by the re-vendor procedure
# (docs/dependency-servicing.md). The loader.js header is a fallback only:
# since monaco-editor 0.53 the shipped AMD loader carries its own version
# string (0.42.0-dev...), not the package version.
$monacoMarker = Join-Path $repoRoot 'src\Wrapp.GUI\Assets\monaco\VERSION.txt'
$loaderPath = Join-Path $repoRoot 'src\Wrapp.GUI\Assets\monaco\vs\loader.js'
$monacoVersion = '(not found)'
if (Test-Path $monacoMarker) {
    $monacoVersion = (Get-Content $monacoMarker -Raw).Trim()
}
elseif (Test-Path $loaderPath) {
    $header = (Get-Content $loaderPath -TotalCount 5) -join ' '
    if ($header -match 'Version:\s*([0-9]+\.[0-9]+\.[0-9]+)') { $monacoVersion = $Matches[1] }
}
$inventory += [pscustomobject]@{
    Component = 'Monaco editor'
    Current   = $monacoVersion
    Source    = 'src\Wrapp.GUI\Assets\monaco\vs (vendored from npm monaco-editor)'
}

# MinGit -- delivered via the Git-Windows-Minimal NuGet package
$csproj = Get-Content $guiProj -Raw
$minGit = if ($csproj -match 'Git-Windows-Minimal"\s+Version="([^"]+)"') { $Matches[1] } else { '(not found)' }
$inventory += [pscustomobject]@{
    Component = 'MinGit (Git-Windows-Minimal)'
    Current   = $minGit
    Source    = 'NuGet package reference in Wrapp.GUI.csproj'
}

# WebView2 SDK (the RUNTIME is Evergreen: Microsoft patches it on user machines)
$wv2 = if ($csproj -match 'Microsoft\.Web\.WebView2"\s+Version="([^"]+)"') { $Matches[1] } else { '(not found)' }
$inventory += [pscustomobject]@{
    Component = 'WebView2 SDK (runtime is Evergreen)'
    Current   = $wv2
    Source    = 'NuGet package reference in Wrapp.GUI.csproj'
}

# PowerShell SDK (source of most transitive surface)
$psSdk = if ($csproj -match 'Microsoft\.PowerShell\.SDK"\s+Version="([^"]+)"') { $Matches[1] } else { '(not found)' }
$inventory += [pscustomobject]@{
    Component = 'Microsoft.PowerShell.SDK'
    Current   = $psSdk
    Source    = 'NuGet package reference in Wrapp.GUI.csproj'
}

# PSADT template -- vendored module
$psadtPsd1 = Get-ChildItem (Join-Path $repoRoot 'modules\psadt-template\PSAppDeployToolkit') -Filter '*.psd1' -ErrorAction SilentlyContinue | Select-Object -First 1
$psadtVersion = '(not found)'
if ($psadtPsd1) {
    $psd1Text = Get-Content $psadtPsd1.FullName -Raw
    if ($psd1Text -match "ModuleVersion\s*=\s*'([^']+)'") { $psadtVersion = $Matches[1] }
}
$inventory += [pscustomobject]@{
    Component = 'PSAppDeployToolkit (PSADT)'
    Current   = $psadtVersion
    Source    = 'modules\psadt-template (vendored)'
}

# IntuneWin32App -- vendored module, version-named folder
$intuneWin32 = Get-ChildItem (Join-Path $repoRoot 'modules\IntuneWin32App') -Directory -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty Name
$inventory += [pscustomobject]@{
    Component = 'IntuneWin32App module'
    Current   = if ($intuneWin32) { $intuneWin32 } else { '(not found)' }
    Source    = 'modules\IntuneWin32App (vendored)'
}

$inventory | Format-Table -AutoSize -Wrap

# ---------------------------------------------------------------------------
# 4. Upstream latest-version lookups (best effort)
# ---------------------------------------------------------------------------
if ($Online) {
    Write-Section 'Upstream latest versions (best effort)'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    $lookups = @(
        @{ Name = 'Monaco editor';       Current = $monacoVersion
           Fetch = { (Invoke-RestMethod 'https://registry.npmjs.org/monaco-editor/latest' -TimeoutSec 15).version } },
        @{ Name = 'Git for Windows';     Current = $minGit
           Fetch = { (Invoke-RestMethod 'https://api.github.com/repos/git-for-windows/git/releases/latest' -TimeoutSec 15).tag_name } },
        @{ Name = 'PSAppDeployToolkit';  Current = $psadtVersion
           Fetch = { (Invoke-RestMethod 'https://api.github.com/repos/PSAppDeployToolkit/PSAppDeployToolkit/releases/latest' -TimeoutSec 15).tag_name } },
        @{ Name = 'IntuneWin32App';      Current = $intuneWin32
           # Repo publishes tags without GitHub releases -> /releases/latest 404s
           Fetch = { (Invoke-RestMethod 'https://api.github.com/repos/MSEndpointMgr/IntuneWin32App/tags' -TimeoutSec 15)[0].name } }
    )

    foreach ($l in $lookups) {
        try {
            $latest = & $l.Fetch
            $marker = if ("$latest" -match [regex]::Escape("$($l.Current)")) { 'current' } else { 'CHECK' }
            $color  = if ($marker -eq 'current') { 'Green' } else { 'Yellow' }
            Write-Host ("{0,-22} local: {1,-12} upstream: {2,-14} [{3}]" -f $l.Name, $l.Current, $latest, $marker) -ForegroundColor $color
        }
        catch {
            Write-Host ("{0,-22} lookup failed: {1}" -f $l.Name, $_.Exception.Message) -ForegroundColor Yellow
        }
    }
}

# ---------------------------------------------------------------------------
# Result
# ---------------------------------------------------------------------------
Write-Host ''
if ($hasVulnerable) {
    Write-Host 'RESULT: vulnerable packages present - see docs/dependency-servicing.md for the patch runbook.' -ForegroundColor Red
    exit 1
}
Write-Host 'RESULT: no known-vulnerable NuGet packages.' -ForegroundColor Green
exit 0
