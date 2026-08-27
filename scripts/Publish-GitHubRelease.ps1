<#
.SYNOPSIS
    Publishes a packed Wrapp release to GitHub: pushes the source branch and
    creates the tagged release with its installer assets.

.DESCRIPTION
    Assumes scripts\Publish-Release.ps1 has already produced the artifacts in
    the releases directory, and that `gh` is authenticated as the account that
    owns -Repo (check with `gh auth status`).

    Assets uploaded:
      WrappApp-win-Setup.exe    one-click per-user installer
      WrappApp-win.msi          full wizard MSI (per-machine capable)
      WrappApp-<version>-full.nupkg   Velopack package for an update feed

    The release body is the top section of CHANGELOG.md.

.PARAMETER Repo
    owner/name of the GitHub repository, e.g. badhostname/wrapp.

.PARAMETER Branch
    Local branch to push as the repository's main branch. Defaults to
    'public-main' (the squashed, history-free snapshot).

.PARAMETER Version
    Release version. Defaults to VersionPrefix from the csproj.

.PARAMETER SkipPush
    Create the release only; do not push the branch.

.PARAMETER ForcePush
    Allow replacing the remote branch when it is not an ancestor of the local
    one. The snapshot branch is regenerated as a fresh orphan commit for each
    release, so its history intentionally diverges from what is already
    published and a normal push is rejected as non-fast-forward. The push
    still uses --force-with-lease pinned to the exact remote commit read
    moments earlier, so it aborts if anyone else pushed in between.

.EXAMPLE
    .\Publish-GitHubRelease.ps1 -Repo badhostname/wrapp
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Repo,
    [string] $Branch = 'public-main',
    [string] $Version,
    [switch] $SkipPush,
    [switch] $ForcePush
)

$ErrorActionPreference = 'Stop'
$repoRoot    = Split-Path -Parent $PSScriptRoot
$releasesDir = Join-Path $repoRoot 'releases'

function Write-Step { param([string] $Text) Write-Host "`n== $Text" -ForegroundColor Cyan }
function Write-Note { param([string] $Text) Write-Host "   $Text" -ForegroundColor DarkGray }

<#
    Windows PowerShell 5.1 turns a native command's REDIRECTED stderr into
    NativeCommandError records; under $ErrorActionPreference='Stop' that is
    fatal even when the exit code is 0 - and tools like gh use stderr for
    ordinary answers ("release not found"). Run those probes through here:
    stderr is merged as plain text, nothing throws, and the caller decides
    based on the exit code.
#>
function Invoke-Probe {
    param([Parameter(Mandatory)][string] $Exe,
          [Parameter(Mandatory)][string[]] $Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Exe @Arguments 2>&1 | ForEach-Object { "$_" }
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
    } finally { $ErrorActionPreference = $previous }
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI not found. Install it, then run: gh auth login"
}

# ---- account must own the target repo ----
$whoami  = Invoke-Probe gh @('api', 'user', '--jq', '.login')
if ($whoami.ExitCode -ne 0) { throw "gh is not authenticated. Run: gh auth login" }
$account = $whoami.Output.Trim()
$owner   = $Repo.Split('/')[0]
if ($account -ne $owner) {
    throw "gh is authenticated as '$account' but the target repo is owned by '$owner'. Run: gh auth login  (and pick the $owner account)"
}
Write-Note "Authenticated as $account"

# ---- version ----
if (-not $Version) {
    $csproj  = Join-Path $repoRoot 'src\Wrapp.GUI\Wrapp.GUI.csproj'
    $Version = ([xml](Get-Content $csproj)).Project.PropertyGroup.VersionPrefix |
               Where-Object { $_ } | Select-Object -First 1
}
$tag = "v$Version"
Write-Note "Version: $Version  (tag $tag)"

# ---- assets ----
# Velopack's GitHub source reads releases.win.json from the release's own
# assets and then fetches each package it names from that SAME release, so
# the manifest is republished per release listing ONLY what is attached
# here: the new full package and, when present, the delta from the previous
# version. Shipping the local feed's manifest verbatim would advertise every
# historical package and 404 on the ones not uploaded.
$fullPkg  = Join-Path $releasesDir "WrappApp-$Version-full.nupkg"
$deltaPkg = Join-Path $releasesDir "WrappApp-$Version-delta.nupkg"
$manifest = Join-Path $releasesDir 'releases.win.json'
foreach ($required in @($fullPkg, $manifest,
                        (Join-Path $releasesDir 'WrappApp-win-Setup.exe'),
                        (Join-Path $releasesDir 'WrappApp-win.msi'))) {
    if (-not (Test-Path $required)) { throw "Missing asset: $required. Run scripts\Publish-Release.ps1 first." }
}

$uploadNames = @("WrappApp-$Version-full.nupkg")
if (Test-Path $deltaPkg) { $uploadNames += "WrappApp-$Version-delta.nupkg" }

$feed  = [System.IO.File]::ReadAllText($manifest) | ConvertFrom-Json
$kept  = @($feed.Assets | Where-Object { $uploadNames -contains $_.FileName })
if ($kept.Count -eq 0) { throw "releases.win.json lists no entry for $Version - repack before publishing." }
$scopedManifest = Join-Path ([IO.Path]::GetTempPath()) "releases.win.json"
[System.IO.File]::WriteAllText(
    $scopedManifest,
    (@{ Assets = $kept } | ConvertTo-Json -Depth 8),
    (New-Object System.Text.UTF8Encoding $false))
Write-Note ("manifest scoped to {0}: {1}" -f $Version, (($kept | ForEach-Object { $_.Type }) -join ' + '))

$assets = @(
    (Join-Path $releasesDir 'WrappApp-win-Setup.exe'),
    (Join-Path $releasesDir 'WrappApp-win.msi'),
    $fullPkg
)
if (Test-Path $deltaPkg) { $assets += $deltaPkg }
$assets += $scopedManifest

foreach ($a in $assets) {
    Write-Note ("{0}  {1:N1} MB" -f (Split-Path $a -Leaf), ((Get-Item $a).Length / 1MB))
}

# ---- release notes: the top CHANGELOG section ----
$changelog = [System.IO.File]::ReadAllText((Join-Path $repoRoot 'CHANGELOG.md'))
$sections  = [regex]::Matches($changelog, '(?ms)^## \[.+?$.*?(?=^## \[|\z)')
if ($sections.Count -eq 0) { throw "Could not read a version section from CHANGELOG.md" }
$notesPath = Join-Path ([IO.Path]::GetTempPath()) "wrapp-gh-notes-$Version.md"
[System.IO.File]::WriteAllText($notesPath, $sections[0].Value.Trim(), [Text.UTF8Encoding]::new($false))
Write-Note "Release notes: $((Get-Content $notesPath).Count) lines"

# ---- repo must exist ----
$repoProbe = Invoke-Probe gh @('repo', 'view', $Repo, '--json', 'name')
if ($repoProbe.ExitCode -ne 0) {
    throw "Repository '$Repo' not found (or not visible to $account). Create it first: gh repo create $Repo --public"
}

# ---- push the branch ----
if (-not $SkipPush) {
    Write-Step "Pushing $Branch -> $Repo (main)"
    $remoteUrl = "https://github.com/$Repo.git"
    $existing  = Invoke-Probe git @('-C', $repoRoot, 'remote', 'get-url', 'public')
    if ($existing.ExitCode -eq 0) {
        if ($existing.Output.Trim() -ne $remoteUrl) { git -C $repoRoot remote set-url public $remoteUrl }
    } else {
        git -C $repoRoot remote add public $remoteUrl
    }
    # A regenerated orphan snapshot shares no ancestry with what is already
    # published, so a plain push is rejected. Decide deliberately.
    $localSha  = (Invoke-Probe git @('-C', $repoRoot, 'rev-parse', $Branch)).Output.Trim()
    $lsRemote  = Invoke-Probe git @('-C', $repoRoot, 'ls-remote', 'public', 'refs/heads/main')
    $remoteSha = if ($lsRemote.Output -match '^([0-9a-f]{40})') { $Matches[1] } else { $null }

    $pushArgs = @('-C', $repoRoot, 'push', 'public', "${Branch}:refs/heads/main")
    if ($remoteSha) {
        $isAncestor = (Invoke-Probe git @('-C', $repoRoot, 'merge-base', '--is-ancestor', $remoteSha, $localSha)).ExitCode -eq 0
        if (-not $isAncestor) {
            if (-not $ForcePush) {
                throw @"
Remote main ($($remoteSha.Substring(0,7))) is not an ancestor of $Branch ($($localSha.Substring(0,7))).
That is expected when the snapshot branch was regenerated: it is a fresh
orphan commit each time. Re-run with -ForcePush to replace the remote branch
(the push is lease-pinned to $($remoteSha.Substring(0,7)), so it aborts if the
remote changed in the meantime).
"@
            }
            Write-Note "Replacing remote main $($remoteSha.Substring(0,7)) -> $($localSha.Substring(0,7)) (lease-pinned)"
            $pushArgs += "--force-with-lease=refs/heads/main:$remoteSha"
        }
    }

    git @pushArgs
    if ($LASTEXITCODE -ne 0) { throw "git push failed." }
    Write-Note "Pushed."
}

# ---- create the release ----
Write-Step "Creating release $tag on $Repo"
# 'release list' answers with an empty set on a fresh repo - unlike
# 'release view', which reports a missing release on stderr.
$existing = Invoke-Probe gh @('release', 'list', '--repo', $Repo, '--json', 'tagName', '--jq', '.[].tagName')
if ($existing.ExitCode -ne 0) { throw "Could not list releases on ${Repo}: $($existing.Output)" }
if (($existing.Output -split "`n" | ForEach-Object { $_.Trim() }) -contains $tag) {
    throw "Release $tag already exists on $Repo. Delete it first (gh release delete $tag --repo $Repo) or bump the version."
}

gh release create $tag @assets --repo $Repo --title "Wrapp $Version" --notes-file $notesPath
if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }

Write-Step 'Verifying uploaded assets'
# Build the jq program as one argument: PowerShell splits an inline
# string containing \( ... ) interpolation into several args before gh
# ever sees it, and gh then rejects the extras.
$jq = '.assets[] | "  " + .name + "  " + (.size/1048576 | floor | tostring) + " MB  state=" + .state'
$view = Invoke-Probe gh @('release', 'view', $tag, '--repo', $Repo, '--json', 'assets', '--jq', $jq)
if ($view.ExitCode -ne 0) { Write-Warning "Could not verify assets: $($view.Output)" }
else { Write-Host $view.Output }

Remove-Item $notesPath -Force -ErrorAction SilentlyContinue
Write-Host "`nDone: https://github.com/$Repo/releases/tag/$tag" -ForegroundColor Green
