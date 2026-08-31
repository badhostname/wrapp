# Delivering Wrapp as an installed app (Workstream D)

Wrapp ships through [Velopack](https://velopack.io/): one `vpk pack` produces a
one-click `Setup.exe`, a real MSI for org deployment, full + delta update
packages, and the feed manifest (`releases.win.json`) the in-app updater reads.

Build everything with:

```powershell
.\scripts\Publish-Release.ps1                                   # artifacts -> releases\
.\scripts\Publish-Release.ps1 -FeedDir \\fileserver\wrapp-updates   # + mirror to the feed
```

The script refuses to run if the top CHANGELOG section doesn't match the csproj
version - the same section becomes the release notes and the in-app What's-New
popup.

**Keep `releases\` intact between releases.** vpk builds each delta against the
previous full package found there; lose it and the next release ships
full-download only.

## Identity and layout

| Thing | Value | Notes |
|---|---|---|
| packId | `WrappApp` | Fixed forever. Deliberately NOT `Wrapp`: the per-user install root is `%LocalAppData%\{packId}` and `%LOCALAPPDATA%\Wrapp` is Wrapp's *data* root (settings, templates, locks, logs). The two trees never overlap, which is also why existing portable-build users keep all their settings when switching to the installed app. |
| Install root (per-user) | `%LocalAppData%\WrappApp` | `current\` (app files), `packages\`, `Update.exe`. |
| Versioning | SemVer2, e.g. `0.6.297-beta` | See D1 (CHANGELOG header). Velopack rejects 4-part versions. |

## Two delivery modes

**Mode A - per-user install, in-app updates (default).**
Deploy `WrappApp-win.msi` (or `Setup.exe`) per-user via Intune/SCCM. Technicians
get continuous updates from the feed: Wrapp checks at launch and, in mode
`Auto`, the update is **required** - the technician must update-and-restart or
close Wrapp (same blocking UX as the liability waiver). A failed download fails
open (the app keeps running the current version and retries next launch), so a
broken feed cannot lock the fleet out.

**Mode B - per-machine install, org-pushed updates.**
Install the MSI per-machine (elevated, lands under Program Files). Program
Files is not user-writable, so in-app updating cannot apply there - push new
MSI versions through Intune/SCCM supersedence instead, and seed update mode
`NotifyOnly` (technicians see "update available", the org delivers it) or
`Disabled`.

msiexec reference:

```powershell
# Per-user, silent
msiexec /i WrappApp-win.msi /qn
# Per-machine to a custom location (secure property override)
msiexec /i WrappApp-win.msi /qn VELOPACK_INSTALLDIR="D:\Apps\Wrapp"
```

## The update feed

The feed is static files: `releases.win.json` + `.nupkg` packages. Anything
that serves files works:

- **UNC share** (zero infra): `\\fileserver\wrapp-updates`. Grant the release
  pipeline write access and technicians read-only - the feed delivers
  executable code, so its ACL is part of your security boundary.
- **HTTPS static hosting** (Azure Storage static site, IIS, GitHub Releases).
  Plain `http://` is rejected by the app.
- **Local folder** (`C:\...`) - same-machine trust; intended for update-flow
  testing and offline/sneakernet feeds.
- **Azure DevOps**: no anonymous static hosting - have a pipeline copy the
  artifacts to a share or storage account instead.

Configure per profile in Settings → Updates, or org-wide via
`defaults.local.json`:

```json
"Update": {
  "FeedUrl": "\\\\fileserver\\wrapp-updates",
  "Mode": "Auto"
}
```

**Trust model.** The feed URL is remote-code-execution-equivalent, so it gets
the same SEC-1 treatment as the Key Vault URL: each technician approves it once
per machine (DPAPI trust token; prompted on Save or via the status-bar action
indicator). Seeding the URL through `defaults.local.json` pre-fills but never
pre-approves. Velopack verifies package SHAs against the feed manifest.

## Code signing

Artifacts are currently unsigned: expect SmartScreen friction on `Setup.exe`
outside managed environments (Intune/SCCM deployment of the MSI is unaffected
in practice). Before any public-facing distribution, sign with Authenticode -
`vpk pack --signParams` / `--azureTrustedSignFile` handle exe + msi signing in
the pack step.

## What's-New notes

The CHANGELOG rides inside `Wrapp.dll` as an embedded resource. On the first
launch after a version change - Velopack update, org-pushed MSI, or manual
copy - Wrapp shows the sections newer than the last version the user dismissed.
No action needed at release time beyond writing the CHANGELOG entry (the
release script enforces that).

## D7 validation checklist (run once per delivery mode)

1. Fresh per-user MSI install → launch → org defaults seeded, feed approval
   prompt appears once.
2. Publish v(n+1) to the feed → relaunch → background download → "Update
   ready" → restart applies; What's-New shows exactly the new sections.
3. Delta actually used (check `%LocalAppData%\WrappApp\packages` and the size
   in the updater log).
4. `NotifyOnly` and `Disabled` modes behave as labeled.
5. Per-machine MSI: install, verify Program Files layout, supersede with the
   next MSI, confirm settings survive.
6. Two instances running during an update: staged apply lands after the LAST
   instance exits; no mixed-version corruption of `%LOCALAPPDATA%\Wrapp`.
7. Uninstall (Apps & Features): app tree removed, `%LOCALAPPDATA%\Wrapp` data
   preserved.
8. Unsigned SmartScreen behavior documented for your rollout channel.
