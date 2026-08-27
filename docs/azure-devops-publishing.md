# Publishing Wrapp to Azure DevOps

Azure DevOps has **no equivalent of GitHub Releases** (a tag-anchored page with
notes and downloadable assets). The DevOps feature literally named "Releases"
(classic Release pipelines) is deployment orchestration - stages, approvals,
environments - not asset hosting. What DevOps *does* offer maps like this:

| GitHub release piece        | Azure DevOps equivalent                                        |
| --------------------------- | -------------------------------------------------------------- |
| Repo + tag                  | Azure Repos - push `main` and the `v1.0.0` tag                 |
| Attached binaries           | **Azure Artifacts → Universal Packages** (closest analogue)    |
| The Velopack `.nupkg` files | Also valid on a DevOps **NuGet feed** (they are real nupkgs)   |
| Release notes page          | No equivalent - notes ride inside the package / repo CHANGELOG |
| "Latest" download URL       | No equivalent - consumers query the feed for latest version    |

> **Update-feed caveat:** an Artifacts feed can NOT serve as Wrapp's Velopack
> `UpdateFeedUrl`. Velopack needs a plain https directory or UNC share exposing
> `releases.win.json` + the packages; DevOps feeds don't expose that layout.
> DevOps is the *distribution/archival* channel; the auto-update feed stays a
> UNC share / local path / static https host.

---

## 1. One-time setup

### 1.1 Install the Azure CLI + DevOps extension

On an unmanaged machine:

```powershell
winget install --id Microsoft.AzureCLI -e
# restart the terminal so az is on PATH, then:
az extension add --name azure-devops
```

**On a managed image this often fails with `No package found matching input
criteria`** - not because the package is missing, but because the `winget`
community source is not registered (`winget source list` shows only `msstore`
and `winget-font`). Re-adding that source needs admin. The admin-free route is
to extract the CLI's MSI payload with an *administrative install*, which
unpacks files without writing to Program Files or the machine registry:

```powershell
$msi    = "$env:TEMP\azure-cli.msi"
$target = "$env:USERPROFILE\Tools\AzureCLI"
Invoke-WebRequest "https://aka.ms/installazurecliwindowsx64" -OutFile $msi -UseBasicParsing
New-Item -ItemType Directory -Force $target | Out-Null
msiexec /a "$msi" TARGETDIR="$target" /qn          # no elevation required

# Put az on the user PATH (persists for new terminals)
$bin = "$target\Microsoft SDKs\Azure\CLI2\wbin"
[Environment]::SetEnvironmentVariable(
    "Path", ([Environment]::GetEnvironmentVariable("Path","User").TrimEnd(';') + ";$bin"), "User")
$env:Path += ";$bin"                                # current session

az version
az extension add --name azure-devops                # extensions live in ~\.azure, no admin needed
```

Uninstall = delete `$target` and remove the PATH entry; nothing else is touched.

> `Invoke-WebRequest` needs `-UseBasicParsing` on Windows PowerShell 5.1 -
> without it the legacy IE engine tries to prompt and fails in non-interactive
> shells.

Docs: <https://learn.microsoft.com/cli/azure/install-azure-cli-windows> and
<https://learn.microsoft.com/azure/devops/cli/>.

Git needs nothing extra - Git Credential Manager (bundled with Git for
Windows) authenticates to `dev.azure.com` natively via browser sign-in.

### 1.2 Create the Personal Access Token (PAT)

Create at `https://dev.azure.com/{org}/_usersSettings/tokens`
(User settings → Personal access tokens → New Token).

Scopes (custom-defined - grant only these):

| Scope         | Level        | Needed for                                                |
| ------------- | ------------ | --------------------------------------------------------- |
| **Code**      | Read & write | `git push` of the repo + tags (if not using browser auth)  |
| **Packaging** | Read & write | Publishing the Universal Package into an existing feed     |

(*Packaging → manage* is only needed to create or administer feeds. In a
shared team project that is usually the project administrators' job, so
read & write is the right level for publishing.)

Set a short expiry (30–90 days). Never commit it; hand it to tools via the
environment for the session only:

```powershell
$env:AZURE_DEVOPS_EXT_PAT = "<paste PAT>"   # az devops commands read this
```

### 1.3 Existing team project - what you do and do not create

Wrapp lives inside an **existing shared team project**; nothing here creates a
project (that needs funding and other people). Two things matter instead:

**The repo** - already created as `wrapp` inside the team project. Its URL is
`{org}/{project}/_git/wrapp`. A project name with spaces must be URL-encoded
in the git remote (`Endpoint Engineering` → `Endpoint%20Engineering`); the
publish script does this for you.

**The feed** - check what already exists before assuming you need a new one,
because creating feeds in a shared project may not be your call.

> There is **no `az artifacts feed` command** - the CLI ships only
> `az artifacts universal`. Feed enumeration is REST-only (or the portal:
> `{org}/{project}/_artifacts/feed`).

```powershell
.\scripts\Publish-AzureDevOps.ps1 -Organization https://dev.azure.com/{org} `
    -Project "{project}" -ListFeeds
```

which calls the Feed Management API for you, or by hand:

```powershell
$headers = @{ Authorization = 'Basic ' + [Convert]::ToBase64String(
    [Text.Encoding]::ASCII.GetBytes(":$env:AZURE_DEVOPS_EXT_PAT")) }

# project-scoped feeds
(Invoke-RestMethod -UseBasicParsing -Headers $headers `
  -Uri "https://feeds.dev.azure.com/{org}/{project}/_apis/packaging/feeds?api-version=7.1-preview.1").value |
  Select-Object name, id

# organization-scoped feeds (note: no project segment)
(Invoke-RestMethod -UseBasicParsing -Headers $headers `
  -Uri "https://feeds.dev.azure.com/{org}/_apis/packaging/feeds?api-version=7.1-preview.1").value |
  Select-Object name, id
```

**Feed scope matters for publishing.** A project-scoped feed needs
`--scope project --project {project}`; an organization-scoped feed needs
`--scope organization` and **no** `--project` argument. Mixing them fails to
resolve the feed. `-ListFeeds` reports each feed's scope so you can pass
`-FeedScope` correctly.

- **A suitable feed exists** → publish into it. Package names namespace
  themselves, so `wrapp` sits harmlessly beside other teams' packages.
- **No feed, and you have permission** → project → **Artifacts** →
  **Create Feed**, e.g. `wrapp-releases`, project-scoped. Feed creation is
  portal-only; the CLI has no `feed create` command.
- **No feed and no permission** → ask the project administrators for a
  project-scoped feed (or contributor rights on an existing one). Until then
  step 2 still works on its own - the repo push needs no feed.

> **Shared-project caution:** everyone with project access can read this repo.
> `defaults.local.json` and `policy/policies.local.json` are gitignored and
> stay local - verified before each push with
> `git ls-files | Select-String "defaults\.local|policies\.local"` (must
> return nothing).

---

## 2. Push the codebase (README + CHANGELOG ride along)

`README.md` and `CHANGELOG.md` live at the repo root, so pushing the repo
gives DevOps its landing page and changelog automatically - Azure Repos
renders the root `README.md` on the repo's Files page.

```powershell
# 'azure' as the remote name keeps 'origin' (GitHub) untouched
git remote add azure https://dev.azure.com/{org}/{project}/_git/wrapp
git push azure main --tags            # brings v1.0.0 and all prior tags
```

Two things to expect on an empty pre-created repo:

- **Default branch.** Azure Repos points a new empty repo at the
  organization's configured default branch name. If that is not `main`, the
  Files page keeps showing an empty branch after the push - fix it once in
  **Repos → Branches**, hover `main` → **Set as default branch**.
- **Branch policies.** A shared project may enforce policies on the default
  branch that reject direct pushes. If the push is rejected for policy, push a
  branch and open a PR instead:
  `git push azure main:refs/heads/import/wrapp` then open the PR in the portal.

First push pops a browser sign-in (Git Credential Manager). To use the PAT
instead: username = anything, password = the PAT.

## 3. Publish the release binaries - Universal Package (recommended)

`scripts\Publish-AzureDevOps.ps1` does both steps - repo push and package
publish - against the existing project:

```powershell
$env:AZURE_DEVOPS_EXT_PAT = "<PAT>"

# Discover what feeds exist, and at which scope
.\scripts\Publish-AzureDevOps.ps1 -Organization https://dev.azure.com/{org} `
    -Project "{project}" -ListFeeds

# Push the repo and publish the current version's binaries
.\scripts\Publish-AzureDevOps.ps1 -Organization https://dev.azure.com/{org} `
    -Project "{project}" -Feed {feed}            # add -FeedScope organization
                                                 # for an org-scoped feed
```

It reads the version from `Wrapp.GUI.csproj`, stages the MSI + Velopack
full/delta + `CHANGELOG.md`, refuses to run if a required artifact is missing
(run `Publish-Release.ps1` first), and encodes spaces in the project name for
the git remote. `-SkipRepo` publishes binaries only; omitting `-Feed` pushes
the repo only.

The equivalent by hand - package names must be lowercase, versions SemVer:

```powershell
$v = "1.0.0"
$stage = Join-Path $env:TEMP "wrapp-release-$v"
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item C:path	owrapp\releases\WrappApp-win.msi            $stage
Copy-Item C:path	owrapp\releases\WrappApp-$v-delta.nupkg     $stage
Copy-Item C:path	owrapp\releases\WrappApp-$v-full.nupkg      $stage
Copy-Item C:path	owrapp\CHANGELOG.md                         $stage

$publish = @{
    organization = "https://dev.azure.com/{org}"
    project      = "{project}"        # omit this line for an org-scoped feed
    scope        = "project"          # ...and set this to "organization"
    feed         = "{feed}"
    name         = "wrapp"
    version      = $v
    description  = "Wrapp $v - MSI + Velopack full/delta packages"
    path         = $stage
}
az artifacts universal publish @publish
```

Consumers (or a future you on another machine) pull it back with:

```powershell
az artifacts universal download --organization https://dev.azure.com/{org} `
    --project "{project}" --scope project --feed {feed} `
    --name wrapp --version 1.0.0 --path .\wrapp-1.0.0
```

Notes:

- A published version is **immutable** - a fixed build gets a new version
  (`1.0.1`), never a re-publish of `1.0.0`.
- Size is a non-issue (feeds accept far larger than the ~300 MB staged here).
- The feed UI (Artifacts → the feed → `wrapp`) lists versions with their
  descriptions - that page is the closest thing to a "releases" page DevOps
  has. Bookmark it as the release index for the team.
- In a shared feed, `wrapp` coexists with other teams' packages; retention
  policies on the feed apply to it too, so check the feed's retention settings
  if old versions must stay downloadable.

## 4. Alternative: NuGet feed for the Velopack packages only

The Velopack full/delta packages are real NuGet packages, so `dotnet nuget`
(already installed with the SDK - no az CLI required) can push them:

```powershell
$addSource = @{
    name     = "wrapp-devops"
    username = "az"                          # any non-empty string
    password = "<PAT>"
}
dotnet nuget add source "https://pkgs.dev.azure.com/{org}/{project}/_packaging/{feed}/nuget/v3/index.json" `
    --name $addSource.name --username $addSource.username `
    --password $addSource.password --store-password-in-clear-text

dotnet nuget push C:path	owrapp\releases\WrappApp-1.0.0-full.nupkg  --source wrapp-devops --api-key az
dotnet nuget push C:path	owrapp\releases\WrappApp-1.0.0-delta.nupkg --source wrapp-devops --api-key az
```

Caveats: the MSI can't ride a NuGet feed (Universal Packages cover it), and
`--store-password-in-clear-text` writes the PAT into the user-level
`NuGet.Config` - remove the source (`dotnet nuget remove source wrapp-devops`)
when done, or prefer the Universal Package route.

## 5. Per-release routine (after `Publish-Release.ps1`)

```powershell
$env:AZURE_DEVOPS_EXT_PAT = "<PAT>"
.\scripts\Publish-AzureDevOps.ps1 -Organization https://dev.azure.com/{org} `
    -Project "{project}" -Feed {feed}
```

That pushes `main` + tags and publishes the new version's package. Both halves
are safe to re-run: git no-ops when already up to date, and a package version
that already exists fails loudly instead of overwriting.

## 6. Troubleshooting the push

### VS403694 - "matches the policy-specified pattern '*.exe'"

The project enforces the **File path validation** repository policy ("Block
pushes from introducing file paths that match the specified patterns"). Wrapp
vendors five signed PSAppDeployToolkit executables under
`modules/psadt-template/`, so every push carrying that history is rejected.

Deleting the files today does **not** help: the policy inspects every commit
in the push, and the historical commit that added them still matches.

**The repo-level toggle is greyed out on purpose.** When a repository policy
is configured at the **All Repositories** scope, an individual repository
cannot relax it - per Microsoft, when settings exist at more than one level
"the system honors the most restrictive setting." Repo scope can only *add*
restrictions. There is no per-repo exemption list for this policy, so the
change has to happen where the policy is defined, by a Project Administrator
(or someone holding **Edit policies** on that scope):
**Project Settings → Repositories → All Repositories → Policies**.

Inspect what is actually in force before raising the request - policies, unlike
feeds, do have CLI support:

```powershell
az repos policy list --org https://dev.azure.com/{org} --project "{project}" --output table
az repos policy show --id <id>  --org https://dev.azure.com/{org} --project "{project}"
```

A blank **Repository ID** column means the policy is cross-repo (All
Repositories); a populated one means it is scoped to a single repo. An admin
can edit it in the portal, or by JSON:

```powershell
az repos policy update --id <id> --config policy.json `
    --org https://dev.azure.com/{org} --project "{project}"
```

The three ways out, best first:

1. **Exempt the pattern or the repo.** Signed upstream binaries from a
   mainstream packaging toolkit, in a packaging tool's repo, is a defensible
   exception. Note that dropping `*.exe` at All Repositories scope affects
   every repo - the precise alternative is to turn the policy off there and
   re-apply it per repo, leaving this one out.
2. **Push a history-free snapshot** (see below) if the policy will not move.
3. **Stop vendoring the binaries** - restore the PSADT template at build time
   instead. Permanent fix, but it changes the build: `Wrapp.GUI.csproj` copies
   `modules/psadt-template/**` into the output via the `CopyPSADT` target.

Rewriting history with `git filter-repo` is possible (a single commit
introduced all five) but rewrites every commit after it, so Azure Repos and
GitHub would carry different SHAs for identical work - and the result still
does not build.

> Check the pattern list for `*.dll` too. The repo tracks 132 DLLs, which
> would be the next rejection after the executables are resolved.

### Snapshot fallback (no history, no executables)

Satisfies "the raw codebase plus README/CHANGELOG" without carrying the
blocked history:

```powershell
git checkout --orphan devops-snapshot
git rm -r --cached . | Out-Null
git add .
git reset -- modules/psadt-template            # drop the vendored toolkit
git commit -m "Wrapp source snapshot"
git push azure devops-snapshot:refs/heads/main
git checkout main                              # back to the real branch
```

Document in the README that `modules/psadt-template/` is omitted from the
Azure Repos mirror, or a clone from it cannot build PSADT bundles.

### "! [rejected] main -> main (fetch first)"

The portal created the repo with an initial commit you do not have. Once the
policy issue is settled:

```powershell
git fetch azure
git push azure main --force-with-lease        # fine for a fresh mirror
```

## 7. What this does not change

GitHub (`origin`) stays the primary remote and keeps its Releases; the `azure`
remote is additive. Wrapp's auto-update feed is likewise untouched - Velopack
still reads `UpdateFeedUrl` from a UNC share, local path, or static https
host, because an Artifacts feed cannot serve `releases.win.json`.
