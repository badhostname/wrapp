<#
.SYNOPSIS
    Publishes a packed Wrapp release to GitHub: pushes the source branch and
    creates the tagged release with its installer assets.

.DESCRIPTION
    Assumes scripts\Publish-Release.ps1 has already produced the artifacts in
    the releases directory.

    Talks to GitHub through its REST API with Invoke-RestMethod, not the
    GitHub CLI: PowerShell uses the Windows certificate store, so it works
    behind a TLS-inspecting proxy where `gh` (Go's own trust store) fails
    with "certificate signed by unknown authority", and it needs no separate
    `gh auth login`. The token comes from the first of:

      1. -Token
      2. $env:GH_TOKEN, then $env:GITHUB_TOKEN
      3. `gh auth token` when the GitHub CLI is installed and signed in
      4. the credential Git Credential Manager holds for github.com - the
         same one `git push` uses, so anyone who can push can release

    The token's account must own -Repo.

    Assets uploaded:
      WrappApp-win-Setup.exe    one-click per-user installer
      WrappApp-win.msi          full wizard MSI (per-machine capable)
      WrappApp-<version>-full.nupkg   Velopack package for an update feed
      WrappApp-<version>-delta.nupkg  when the previous release was packed here
      releases.win.json         feed manifest scoped to this release

    The release body is the top section of CHANGELOG.md.

    Re-running finishes an interrupted publish. The ~300 MB of packages is
    the fragile part, so a release that already exists for the tag is
    resumed: complete assets are skipped, anything GitHub left in its
    'starter' state is deleted and sent again, and the run only succeeds
    once every asset reads back as uploaded at its full size.

.PARAMETER Repo
    owner/name of the GitHub repository, e.g. badhostname/wrapp.

.PARAMETER Branch
    Local branch to push as the repository's main branch. Defaults to
    'public-main' (the squashed, history-free snapshot).

.PARAMETER Version
    Release version. Defaults to VersionPrefix from the csproj.

.PARAMETER Token
    GitHub token to use instead of the lookup chain above.

.PARAMETER SkipPush
    Create the release only; do not push the branch.

.PARAMETER ForcePush
    Allow replacing the remote branch when it is not an ancestor of the local
    one. The snapshot branch is regenerated as a fresh orphan commit for each
    release, so its history intentionally diverges from what is already
    published and a normal push is rejected as non-fast-forward. The push
    still uses --force-with-lease pinned to the exact remote commit read
    moments earlier, so it aborts if anyone else pushed in between.

.PARAMETER DryRun
    Resolve the token, check the account, repo, assets and notes, then stop
    before pushing or creating anything.

.EXAMPLE
    .\Publish-GitHubRelease.ps1 -Repo badhostname/wrapp -ForcePush
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Repo,
    [string] $Branch = 'public-main',
    [string] $Version,
    [string] $Token,
    [switch] $SkipPush,
    [switch] $ForcePush,
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$repoRoot    = Split-Path -Parent $PSScriptRoot
$releasesDir = Join-Path $repoRoot 'releases'
$api         = 'https://api.github.com'

function Write-Step { param([string] $Text) Write-Host "`n== $Text" -ForegroundColor Cyan }
function Write-Note { param([string] $Text) Write-Host "   $Text" -ForegroundColor DarkGray }

<#
    Windows PowerShell 5.1 turns a native command's REDIRECTED stderr into
    NativeCommandError records; under $ErrorActionPreference='Stop' that is
    fatal even when the exit code is 0. Run native probes through here:
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

<#
    Asks Git Credential Manager for the github.com credential without any
    prompt. Goes through System.Diagnostics.Process with an ASCII writer over
    raw stdin so git receives exactly "protocol=https\nhost=github.com\n\n":
    piping a string from PowerShell 5.1 writes CRLF, and the default
    StandardInput writer prepends the console encoding's byte-order mark;
    either way git answers "refusing to work with credential missing
    protocol field".
#>
function Get-GitCredentialToken {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'git'
    $psi.Arguments = 'credential fill'
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables['GCM_INTERACTIVE'] = 'never'
    $psi.EnvironmentVariables['GIT_TERMINAL_PROMPT'] = '0'
    $p = [System.Diagnostics.Process]::Start($psi)
    $stdin = New-Object System.IO.StreamWriter($p.StandardInput.BaseStream, (New-Object System.Text.ASCIIEncoding))
    $stdin.NewLine = "`n"
    $stdin.WriteLine('protocol=https')
    $stdin.WriteLine('host=github.com')
    $stdin.WriteLine('')
    $stdin.Flush()
    $stdin.Close()
    $out = $p.StandardOutput.ReadToEnd()
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) { return $null }
    $line = ($out -split "`n" | Where-Object { $_ -like 'password=*' } | Select-Object -First 1)
    if (-not $line) { return $null }
    return $line.Substring('password='.Length).Trim()
}

function Invoke-GitHub {
    param(
        [Parameter(Mandatory)][string] $Method,
        [Parameter(Mandatory)][string] $Uri,
        [object] $Body,
        [string] $InFile,
        [string] $ContentType = 'application/json; charset=utf-8'
    )
    $call = @{
        Method     = $Method
        Uri        = $Uri
        Headers    = $script:headers
        TimeoutSec = 3600
    }
    if ($InFile) {
        $call.InFile = $InFile
        $call.ContentType = $ContentType
    } elseif ($null -ne $Body) {
        $call.Body = [System.Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 8))
        $call.ContentType = $ContentType
    }
    Invoke-RestMethod @call
}

<#
    Uploads one release asset by STREAMING it from disk through HttpClient.

    Invoke-RestMethod -InFile cannot be used for these: on Windows PowerShell
    5.1 it buffers the whole body in memory before sending, and a 160 MB
    package through a proxy lands as an asset GitHub leaves in its 'starter'
    state - the record exists, the bytes never arrived (that is exactly how
    the first 1.0.10 publish failed). StreamContent sends from the FileStream
    with a known Content-Length and no buffering.

    Retries a few times: a dropped connection mid-upload leaves a partial
    asset behind, which GitHub then rejects as already_exists, so each retry
    deletes the stale record first.
#>
function Send-GitHubAsset {
    param(
        [Parameter(Mandatory)][string] $UploadBase,
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $AssetsUri,
        [int] $Attempts = 3
    )
    Add-Type -AssemblyName System.Net.Http
    $name = Split-Path $Path -Leaf
    $type = if ($name -like '*.json') { 'application/json' } else { 'application/octet-stream' }
    $uri  = "$UploadBase`?name=$([uri]::EscapeDataString($name))"

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        # Clear anything left by a previous attempt (or a previous run).
        $stale = @(Invoke-GitHub -Method Get -Uri "$AssetsUri`?per_page=100") | Where-Object { $_.name -eq $name }
        foreach ($s in $stale) {
            Write-Note "removing incomplete $name (state $($s.state))"
            Invoke-GitHub -Method Delete -Uri "$api/repos/$Repo/releases/assets/$($s.id)" | Out-Null
        }

        $handler = New-Object System.Net.Http.HttpClientHandler
        $handler.UseProxy = $true
        $handler.UseDefaultCredentials = $true
        $client = New-Object System.Net.Http.HttpClient($handler)
        $client.Timeout = [TimeSpan]::FromHours(2)
        $client.DefaultRequestHeaders.Authorization =
            New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $script:Token)
        $client.DefaultRequestHeaders.UserAgent.ParseAdd('wrapp-publish')
        $client.DefaultRequestHeaders.Accept.ParseAdd('application/vnd.github+json')
        $client.DefaultRequestHeaders.Add('X-GitHub-Api-Version', '2022-11-28')
        $stream = [System.IO.File]::OpenRead($Path)
        try {
            $content = New-Object System.Net.Http.StreamContent($stream)
            $content.Headers.ContentType = New-Object System.Net.Http.Headers.MediaTypeHeaderValue($type)
            $content.Headers.ContentLength = $stream.Length
            $sw = [Diagnostics.Stopwatch]::StartNew()
            Write-Note ("uploading {0} ({1:N1} MB){2}" -f $name, ($stream.Length / 1MB),
                        $(if ($attempt -gt 1) { " - attempt $attempt" } else { '' }))
            $response = $client.PostAsync($uri, $content).GetAwaiter().GetResult()
            $payload  = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if (-not $response.IsSuccessStatusCode) {
                throw "HTTP $([int]$response.StatusCode) $($response.ReasonPhrase): $payload"
            }
            $asset = $payload | ConvertFrom-Json
            # 'uploaded' is the only state that means the bytes are there.
            if ($asset.state -ne 'uploaded') { throw "GitHub left $name in state '$($asset.state)'" }
            Write-Note ("  {0}: {1:N1} MB in {2:N0}s" -f $name, ($asset.size / 1MB), $sw.Elapsed.TotalSeconds)
            return $asset
        }
        catch {
            if ($attempt -eq $Attempts) { throw "Upload of $name failed after $Attempts attempt(s): $($_.Exception.Message)" }
            Write-Warning "Upload of $name failed (attempt $attempt): $($_.Exception.Message)"
            Start-Sleep -Seconds (5 * $attempt)
        }
        finally {
            $stream.Dispose()
            $client.Dispose()
            $handler.Dispose()
        }
    }
}

<# GET that treats 404 as $null instead of an error. #>
function Get-GitHubOrNull {
    param([Parameter(Mandatory)][string] $Uri)
    try { return Invoke-GitHub -Method Get -Uri $Uri }
    catch {
        $status = $null
        if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        if ($status -eq 404) { return $null }
        throw
    }
}

# ---- token ----
Write-Step 'Resolving GitHub token'
$source = $null
if ($Token) { $source = '-Token' }
elseif ($env:GH_TOKEN)     { $Token = $env:GH_TOKEN;     $source = 'GH_TOKEN' }
elseif ($env:GITHUB_TOKEN) { $Token = $env:GITHUB_TOKEN; $source = 'GITHUB_TOKEN' }
elseif (Get-Command gh -ErrorAction SilentlyContinue) {
    $probe = Invoke-Probe gh @('auth', 'token')
    if ($probe.ExitCode -eq 0 -and $probe.Output.Trim()) { $Token = $probe.Output.Trim(); $source = 'gh auth token' }
}
if (-not $Token) {
    $Token = Get-GitCredentialToken
    if ($Token) { $source = 'Git Credential Manager (github.com)' }
}
if (-not $Token) {
    throw "No GitHub token found. Pass -Token, set GH_TOKEN, sign in with `gh auth login`, or push to github.com once so Git Credential Manager stores a credential."
}
$script:headers = @{
    Authorization          = "Bearer $Token"
    Accept                 = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent'           = 'wrapp-publish'
}
Write-Note "Token from $source"

# ---- account must own the target repo ----
$account = (Invoke-GitHub -Method Get -Uri "$api/user").login
$owner   = $Repo.Split('/')[0]
if ($account -ne $owner) {
    throw "The token belongs to '$account' but the target repo is owned by '$owner'. Pass -Token for the $owner account."
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
$notes = $sections[0].Value.Trim()
if ($notes -notmatch ('^## \[' + [regex]::Escape($Version) + '\]')) {
    throw "Top CHANGELOG section is not [$Version] - add the entry before releasing."
}
$notes = ($notes -replace '(?ms)\s*---\s*\z', '').Trim()
Write-Note "Release notes: $(($notes -split "`n").Count) lines"

# ---- repo must exist ----
if (-not (Get-GitHubOrNull -Uri "$api/repos/$Repo")) {
    throw "Repository '$Repo' not found (or not visible to $account). Create it first on github.com."
}

# ---- an existing release for this tag is resumed, not refused ----
# A publish that dies part-way through the uploads (the 160 MB packages are
# the whole risk) leaves the release created and some assets in GitHub's
# 'starter' state. Re-running must finish the job rather than demand the
# release be deleted first, so the tag is looked up and reused. Assets that
# are already complete are left alone; anything else is re-uploaded.
$release = Get-GitHubOrNull -Uri "$api/repos/$Repo/releases/tags/$tag"
if ($release) {
    Write-Note "Release $tag already exists (id $($release.id)) - resuming its uploads"
}

if ($DryRun) {
    Write-Host "`nDry run: token, account, repo, assets and notes check out. Nothing pushed or created." -ForegroundColor Green
    Remove-Item $scopedManifest -Force -ErrorAction SilentlyContinue
    return
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
    if ($remoteSha -and $remoteSha -ne $localSha) {
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

    if ($remoteSha -eq $localSha) {
        Write-Note "Remote main is already $($localSha.Substring(0,7)); nothing to push."
    } else {
        $push = Invoke-Probe git $pushArgs
        if ($push.ExitCode -ne 0) { throw "git push failed:`n$($push.Output)" }
        Write-Note "Pushed."
    }
}

# ---- create or update the release ----
$target = (Invoke-Probe git @('-C', $repoRoot, 'rev-parse', $Branch)).Output.Trim()
if ($release) {
    if ($release.body -ne $notes) {
        Write-Step "Refreshing the notes on $tag"
        $release = Invoke-GitHub -Method Patch -Uri "$api/repos/$Repo/releases/$($release.id)" -Body @{ body = $notes }
    }
} else {
    Write-Step "Creating release $tag on $Repo"
    $release = Invoke-GitHub -Method Post -Uri "$api/repos/$Repo/releases" -Body @{
        tag_name         = $tag
        target_commitish = $target
        name             = "Wrapp $Version"
        body             = $notes
        draft            = $false
        prerelease       = $false
    }
}
Write-Note "Release id $($release.id)"

# ---- assets ----
$assetsUri  = "$api/repos/$Repo/releases/$($release.id)/assets"
$uploadBase = $release.upload_url -replace '\{\?name,label\}$', ''

# Sweep out every incomplete asset ONCE, before the upload loop, so the loop's
# skip test can only ever match a complete one. Deciding per asset mid-run is
# what let the 1.0.10 publish walk past a Setup.exe that was still in
# 'starter' state and then fail verification at the end.
$stale = @(Invoke-GitHub -Method Get -Uri "$assetsUri`?per_page=100") |
         Where-Object { $_.state -ne 'uploaded' }
if ($stale.Count -gt 0) {
    Write-Step 'Clearing incomplete assets from the previous attempt'
    foreach ($s in $stale) {
        Write-Note "removing $($s.name) (state $($s.state))"
        Invoke-GitHub -Method Delete -Uri "$api/repos/$Repo/releases/assets/$($s.id)" | Out-Null
    }
}

Write-Step 'Uploading assets'
$present = @(Invoke-GitHub -Method Get -Uri "$assetsUri`?per_page=100")
foreach ($path in $assets) {
    $name = Split-Path $path -Leaf
    $size = (Get-Item $path).Length
    $have = $present | Where-Object { $_.name -eq $name -and $_.state -eq 'uploaded' -and $_.size -eq $size } |
            Select-Object -First 1
    if ($have) {
        Write-Note "skip $name (already uploaded)"
        continue
    }
    Send-GitHubAsset -UploadBase $uploadBase -Path $path -AssetsUri $assetsUri | Out-Null
}

Write-Step 'Verifying uploaded assets'
$uploaded = @(Invoke-GitHub -Method Get -Uri "$assetsUri`?per_page=100")
foreach ($a in $uploaded) {
    Write-Note ("{0,-34} {1,8:N1} MB  {2}" -f $a.name, ($a.size / 1MB), $a.state)
}
# An asset that exists but is not 'uploaded' is worse than a missing one:
# Velopack and the download links would both 404 on it.
$problems = New-Object System.Collections.Generic.List[string]
foreach ($path in $assets) {
    $name = Split-Path $path -Leaf
    $a = $uploaded | Where-Object { $_.name -eq $name } | Select-Object -First 1
    if (-not $a) { $problems.Add("$name is missing") }
    elseif ($a.state -ne 'uploaded') { $problems.Add("$name is in state '$($a.state)'") }
    elseif ($a.size -ne (Get-Item $path).Length) { $problems.Add("$name is $($a.size) B, expected $((Get-Item $path).Length) B") }
}
if ($problems.Count -gt 0) {
    throw "The release is incomplete - re-run this script to finish it:`n  " + ($problems -join "`n  ")
}

Remove-Item $scopedManifest -Force -ErrorAction SilentlyContinue
Write-Host "`nDone: $($release.html_url)" -ForegroundColor Green
