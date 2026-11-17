<#
.SYNOPSIS
    Pushes the Wrapp repo and publishes a release's binaries to an EXISTING
    Azure DevOps team project.

.DESCRIPTION
    Azure DevOps has no GitHub-Releases equivalent, so a "release" here is
    two independent actions:

        1. Repo   - git push of main + tags to Azure Repos (README.md and
                    CHANGELOG.md render on the repo's Files page).
        2. Binaries - one Universal Package per version, holding the MSI,
                    the Velopack full/delta packages, and the changelog.

    This script assumes the team project and the repo already exist (shared
    org project - nobody is creating projects here). It never creates a
    project, and it never overwrites a published package version: Universal
    Package versions are immutable, so a rebuild gets a new version.

    Authentication uses a Personal Access Token with:
        Code       - Read & write   (repo push)
        Packaging  - Read & write   (package publish)

    Supply it via -Pat, or set $env:AZURE_DEVOPS_EXT_PAT before running.

.PARAMETER Organization
    Organization URL, e.g. https://dev.azure.com/contoso

.PARAMETER Project
    The EXISTING team project name. Spaces are fine - they are URL-encoded
    for the git remote automatically.

.PARAMETER Repo
    Repository name inside the project. Defaults to 'wrapp'.

.PARAMETER Feed
    Azure Artifacts feed that receives the Universal Package. Omit to skip
    the binary publish and push the repo only. Use -ListFeeds to discover
    which feeds already exist and at which scope.

.PARAMETER FeedScope
    'project' (default) for a project-scoped feed, 'organization' for an
    organization-scoped one. -ListFeeds reports the scope of every feed it
    finds; getting this wrong makes the publish fail to resolve the feed.

.PARAMETER Version
    Release version to publish. Defaults to the VersionPrefix in
    src/Wrapp.GUI/Wrapp.GUI.csproj.

.PARAMETER PackageName
    Universal Package name (lowercase letters, digits, - _ . only).
    Defaults to 'wrapp'.

.PARAMETER RemoteName
    Local git remote name for Azure Repos. Defaults to 'azure' so the
    existing 'origin' (GitHub) is left untouched.

.PARAMETER SkipRepo
    Publish the package only; do not touch git remotes or push.

.PARAMETER ListFeeds
    List the existing Artifacts feeds (both organization- and project-scoped)
    and exit. Use this first if you do not know whether the shared project
    already has a feed you should publish into (creating one may not be your
    call). Feed enumeration has no CLI command - this goes through the
    Feed Management REST API with the PAT.

.EXAMPLE
    .\Publish-AzureDevOps.ps1 -Organization https://dev.azure.com/contoso `
        -Project "Endpoint Engineering" -ListFeeds

.EXAMPLE
    .\Publish-AzureDevOps.ps1 -Organization https://dev.azure.com/contoso `
        -Project "Endpoint Engineering" -Feed wrapp-releases

.EXAMPLE
    .\Publish-AzureDevOps.ps1 -Organization https://dev.azure.com/contoso `
        -Project "Endpoint Engineering" -Feed shared-tools -FeedScope organization
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Organization,
    [Parameter(Mandatory)][string] $Project,
    [string] $Repo        = 'wrapp',
    [string] $Feed,
    [ValidateSet('project', 'organization')]
    [string] $FeedScope   = 'project',
    [string] $Version,
    [string] $PackageName = 'wrapp',
    [string] $RemoteName  = 'azure',
    [string] $Pat,
    [switch] $SkipRepo,
    [switch] $ListFeeds
)

$ErrorActionPreference = 'Stop'
$repoRoot     = Split-Path -Parent $PSScriptRoot
$releasesDir  = Join-Path $repoRoot 'releases'
$Organization = $Organization.TrimEnd('/')

function Write-Step { param([string] $Text) Write-Host "`n== $Text" -ForegroundColor Cyan }
function Write-Note { param([string] $Text) Write-Host "   $Text" -ForegroundColor DarkGray }

# ---------------------------------------------------------------- az present
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI not found on PATH. See docs/azure-devops-publishing.md section 1.1 (a managed image without the winget community source can extract the MSI without admin)."
}
$extensions = az extension list --query "[].name" -o tsv
if ($extensions -notcontains 'azure-devops') {
    Write-Step 'Installing the azure-devops CLI extension'
    az extension add --name azure-devops --only-show-errors
}

# ------------------------------------------------------------------- auth
if ($Pat) { $env:AZURE_DEVOPS_EXT_PAT = $Pat }
if (-not $env:AZURE_DEVOPS_EXT_PAT) {
    throw "No PAT available. Pass -Pat, or set `$env:AZURE_DEVOPS_EXT_PAT (scopes: Code read/write, Packaging read/write)."
}

# ------------------------------------------------------------- feed listing
# There is NO 'az artifacts feed' command - the CLI exposes only
# 'az artifacts universal'. Feed enumeration goes through the Feed Management
# REST API on the feeds.* host, authenticated with the PAT as basic auth.
function Get-OrgName {
    param([string] $OrgUrl)
    if ($OrgUrl -match 'dev\.azure\.com/([^/]+)')      { return $Matches[1] }
    if ($OrgUrl -match 'https?://([^.]+)\.visualstudio\.com') { return $Matches[1] }
    throw "Could not parse an organization name from '$OrgUrl'. Expected https://dev.azure.com/<org>."
}

function Get-Feeds {
    param([string] $OrgName, [string] $ProjectName)

    $pair    = ":" + $env:AZURE_DEVOPS_EXT_PAT
    $headers = @{ Authorization = 'Basic ' + [Convert]::ToBase64String(
                    [Text.Encoding]::ASCII.GetBytes($pair)) }
    $results = @()

    $targets = @(
        @{ Scope = 'organization'; Uri = "https://feeds.dev.azure.com/$OrgName/_apis/packaging/feeds?api-version=7.1-preview.1" },
        @{ Scope = 'project';      Uri = "https://feeds.dev.azure.com/$OrgName/$([uri]::EscapeDataString($ProjectName))/_apis/packaging/feeds?api-version=7.1-preview.1" }
    )

    foreach ($t in $targets) {
        try {
            $response = Invoke-RestMethod -Uri $t.Uri -Headers $headers -UseBasicParsing -TimeoutSec 60
            foreach ($f in $response.value) {
                $results += [pscustomobject]@{
                    Name  = $f.name
                    Scope = $t.Scope
                    Id    = $f.id
                }
            }
        } catch {
            Write-Note "$($t.Scope)-scoped lookup failed: $($_.Exception.Message)"
        }
    }
    return $results
}

if ($ListFeeds) {
    Write-Step "Artifacts feeds visible to this PAT ($Project)"
    $feeds = Get-Feeds -OrgName (Get-OrgName $Organization) -ProjectName $Project
    if (-not $feeds) {
        Write-Note 'No feeds found. Either none exist, or the PAT lacks the Packaging scope.'
        Write-Note "Browse: $Organization/$([uri]::EscapeDataString($Project))/_artifacts/feed"
    } else {
        $feeds | Sort-Object Scope, Name | Format-Table -AutoSize
        Write-Note "Publish with -Feed <Name> -FeedScope <Scope>."
    }
    return
}

# ------------------------------------------------------------------ version
if (-not $Version) {
    $csproj  = Join-Path $repoRoot 'src\Wrapp.GUI\Wrapp.GUI.csproj'
    $Version = ([xml](Get-Content $csproj)).Project.PropertyGroup.VersionPrefix |
               Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { throw "Could not read VersionPrefix from $csproj - pass -Version." }
}
Write-Note "Version: $Version"

# --------------------------------------------------------------- repo push
if (-not $SkipRepo) {
    Write-Step "Pushing repository to $Project/$Repo"

    # Project names may contain spaces; the git URL needs them encoded.
    $encodedProject = [uri]::EscapeDataString($Project)
    $remoteUrl      = "$Organization/$encodedProject/_git/$Repo"

    $existing = git -C $repoRoot remote get-url $RemoteName 2>$null
    if ($LASTEXITCODE -eq 0) {
        if ($existing -ne $remoteUrl) {
            Write-Note "Updating remote '$RemoteName': $existing -> $remoteUrl"
            git -C $repoRoot remote set-url $RemoteName $remoteUrl
        } else {
            Write-Note "Remote '$RemoteName' already points at $remoteUrl"
        }
    } else {
        Write-Note "Adding remote '$RemoteName' -> $remoteUrl"
        git -C $repoRoot remote add $RemoteName $remoteUrl
    }

    git -C $repoRoot push $RemoteName main --tags
    if ($LASTEXITCODE -ne 0) {
        throw "git push failed. If prompted for credentials, use any username and the PAT as the password."
    }
    Write-Note "Pushed main + tags."
    Write-Note "If the project's default branch is not 'main', set it once in Repos > Branches > (main) > Set as default branch."
}

# ---------------------------------------------------------- package publish
if (-not $Feed) {
    Write-Step 'No -Feed given - skipping the binary publish'
    Write-Note 'Run with -ListFeeds to see the feeds this project already has.'
    return
}

Write-Step "Staging release artifacts for $Version"
$artifacts = @(
    @{ Path = Join-Path $releasesDir 'WrappApp-win.msi';                 Required = $true  },
    @{ Path = Join-Path $releasesDir "WrappApp-$Version-full.nupkg";     Required = $true  },
    @{ Path = Join-Path $releasesDir "WrappApp-$Version-delta.nupkg";    Required = $false },
    @{ Path = Join-Path $repoRoot   'CHANGELOG.md';                      Required = $true  }
)

$stage = Join-Path ([IO.Path]::GetTempPath()) "wrapp-adopublish-$Version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

foreach ($a in $artifacts) {
    if (Test-Path $a.Path) {
        Copy-Item $a.Path $stage
        Write-Note ("staged {0} ({1:N1} MB)" -f (Split-Path $a.Path -Leaf), ((Get-Item $a.Path).Length / 1MB))
    } elseif ($a.Required) {
        throw "Missing required artifact: $($a.Path). Run scripts\Publish-Release.ps1 first."
    } else {
        Write-Note "skipped (absent): $(Split-Path $a.Path -Leaf)"
    }
}

Write-Step "Publishing universal package '$PackageName' $Version to the $FeedScope-scoped feed '$Feed'"

# An organization-scoped feed must NOT be given --project; a project-scoped
# one requires it. Passing the wrong pair fails to resolve the feed.
$publish = @{
    organization = $Organization
    scope        = $FeedScope
    feed         = $Feed
    name         = $PackageName.ToLowerInvariant()
    version      = $Version
    description  = "Wrapp $Version - MSI + Velopack full/delta packages"
    path         = $stage
}
if ($FeedScope -eq 'project') { $publish.project = $Project }

az artifacts universal publish @publish
if ($LASTEXITCODE -ne 0) {
    throw @"
Publish failed. Common causes:
  * The version already exists - Universal Package versions are immutable, so
    bump the version and repackage rather than republishing.
  * Wrong feed scope - re-run with -ListFeeds and match the Scope column.
  * The PAT lacks the Packaging (read & write) scope.
"@
}

Remove-Item $stage -Recurse -Force
Write-Host "`nDone. $PackageName $Version is in feed '$Feed' ($FeedScope scope)." -ForegroundColor Green

$projectArg = if ($FeedScope -eq 'project') { "--project `"$Project`" " } else { '' }
Write-Note "Consumers: az artifacts universal download --organization $Organization $projectArg--scope $FeedScope --feed $Feed --name $($PackageName.ToLowerInvariant()) --version $Version --path .\wrapp-$Version"
