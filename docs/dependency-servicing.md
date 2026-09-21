# Dependency servicing - how to patch Wrapp's dependencies (CVE fast path)

One page answering: *what ships inside Wrapp, how do I know something needs patching,
and what are the exact steps to patch it.*

**Detection is one command:**

```powershell
.\scripts\Check-Dependencies.ps1 -Online
```

It scans NuGet packages (direct + transitive) against the GitHub Advisory Database,
lists outdated direct references, prints the vendored-component inventory (Monaco,
MinGit, PSADT, IntuneWin32App, WebView2 SDK), and - with `-Online` - compares vendored
versions against upstream. Exit code 1 when anything vulnerable is found, so it can
gate CI.

---

## The dependency channels

Wrapp has **four distinct delivery channels**. Knowing which channel a component is in
tells you the patch procedure:

| Channel | Components | Patch mechanism | Who patches user machines |
|---|---|---|---|
| **NuGet packages** | MVVM Toolkit, WPF-UI, MaterialDesign, MSAL (×3), **PowerShell SDK**, **WebView2 SDK**, **Git-Windows-Minimal (MinGit)** | Version bump in csproj + lock regen | Us (next release) |
| **Vendored assets** | **Monaco** (`Assets\monaco\vs`), **PSADT** (`modules\psadt-template`), **IntuneWin32App** (`modules\IntuneWin32App`) | Replace the vendored tree | Us (next release) |
| **Evergreen runtime** | **WebView2 runtime** (the actual browser engine) | Nothing - Microsoft auto-updates it machine-wide | Microsoft, automatically |
| **First-party** | Wrapp.Packager, Appease template | Normal development | Us |

Two supply-chain protections are already in place and must be preserved on every update:

- **`packages.lock.json` is committed** and `RestorePackagesWithLockFile` is on - a
  tampered or changed same-version package fails the build (`RestoreLockedMode` in CI).
  Every NuGet bump must regenerate the lock files (see below) and commit them.
- The **navigation allow-list + settings hardening** on WebView2 (Workstream G) is
  independent of versions - asset updates must not touch `MonacoHost`.

---

## Runbook per channel

### 1. NuGet package CVE (most common - this is the fast path)

Real example: 2026-07-28, `dotnet list package --vulnerable` reported High advisories in
`System.Text.Json 8.0.4`, `System.Security.Cryptography.Xml 8.0.1`, `System.IO.Packaging
8.0.0` - all transitive.

```powershell
# 1. Find WHO pulls the vulnerable package
dotnet nuget why src/Wrapp.GUI/Wrapp.GUI.csproj System.Text.Json
#    -> Microsoft.PowerShell.SDK 7.4.4 (all three, in this case)

# 2. Prefer bumping the DIRECT package that owns the transitive
#    (edit the Version= in src/Wrapp.GUI/Wrapp.GUI.csproj)
#    Microsoft.PowerShell.SDK: stay on the 7.4.x LTS line -> 7.4.18

# 3. Regenerate BOTH lock files (required - locked mode fails otherwise)
dotnet restore src/Wrapp.GUI/Wrapp.GUI.csproj --force-evaluate
dotnet restore tests/Wrapp.GUI.Tests/Wrapp.GUI.Tests.csproj --force-evaluate

# 4. Verify clean + green
dotnet list src/Wrapp.GUI/Wrapp.GUI.csproj package --vulnerable --include-transitive
dotnet build src/Wrapp.GUI/Wrapp.GUI.csproj
dotnet test tests/Wrapp.GUI.Tests/Wrapp.GUI.Tests.csproj

# 5. Commit csproj + BOTH packages.lock.json files, bump version, publish release
```

**If the direct owner has no patched release yet:** add a direct `PackageReference` to
the patched transitive version in `Wrapp.GUI.csproj` (direct wins over transitive).
Mark it with a comment naming the advisory so it can be removed when the owner ships.

**Version-line rules:** `Microsoft.PowerShell.SDK` stays on the current LTS line
(7.4.x); MSAL packages move together (all three share a version); `WebView2` SDK can
trail the runtime safely (the runtime is Evergreen - SDK bumps are for APIs, rarely
CVEs on our side).

### 2. Monaco (vendored, `src\Wrapp.GUI\Assets\monaco\vs`)

Current: see `src\Wrapp.GUI\Assets\monaco\VERSION.txt` - the marker file the
re-vendor procedure below maintains. (The `loader.js` header can no longer be
trusted for this: since monaco-editor 0.53 the shipped AMD-compat loader carries
its own version string, `0.42.0-dev...`, not the package version.) Monaco runs
inside the hardened WebView2 with no external navigation, which shrinks its
practical attack surface - but it still executes JS against user-authored
script content.

Since 0.53 the `min/vs` folder is an **ESM chunk build with an AMD-compat shim**
(hashed chunk filenames, `assets/` + `nls/` subfolders) rather than the classic
AMD tree. Wrapp's host pages (`Assets\monaco\*.js`) still bootstrap through
`require.config({paths:{vs:...}})` + `require(['vs/editor/editor.main'])`, which
the shim supports - verified working as of 0.56.0. Workers are spawned as
blob-wrapped `importScripts` of same-origin chunk files, which the strict CSP on
the host pages already permits (`worker-src blob:` + `script-src monaco.local`).

```powershell
# 1. Get the release (npm tarball, no npm install needed)
Invoke-WebRequest https://registry.npmjs.org/monaco-editor/-/monaco-editor-<VER>.tgz -OutFile monaco.tgz
tar -xzf monaco.tgz            # extracts to package/

# 2. Replace the vendored tree with the MINIFIED build only, and stamp the marker
Remove-Item src\Wrapp.GUI\Assets\monaco\vs -Recurse
Copy-Item package\min\vs src\Wrapp.GUI\Assets\monaco\vs -Recurse
Set-Content src\Wrapp.GUI\Assets\monaco\VERSION.txt '<VER>' -NoNewline

# 3. Re-apply the trim. A fresh Monaco ships ~100 language definitions plus the
#    TypeScript/CSS/HTML language services (~16 MB, ~90 files) that Wrapp never
#    uses. The script keeps powershell/json/xml (+ built-in plaintext) and
#    refuses to delete anything editor.main.js statically depends on.
#    MonacoAssetTests fails the build if this step is skipped.
.\scripts\Trim-Monaco.ps1        # add -WhatIf first to preview

# 4. Purge the stale copy in the build output - Content items are copied with
#    PreserveNewest and deleted/renamed files LINGER (same zombie-file class the
#    csproj already guards against for Wrapp.Packager). Do the same for any
#    existing publish folder before re-publishing.
Remove-Item src\Wrapp.GUI\bin\*\net8.0-windows\monaco\vs -Recurse -Force

# 5. Validate (csproj globs Assets\monaco\vs\** automatically - no csproj edit)
#    Manual pass required: all four editor surfaces (Scripts tabs, Config JSON,
#    diff view, history view), DPI/multi-monitor drag, Refresh Editor button.
#    If the editor comes up blank, diff the loader handshake (require.config)
#    and check the WebView2 devbuild console for CSP violations first.
```

**Do not touch** `MonacoHost` or the host pages (`Assets\monaco\*.html/.js`) during
an asset swap - asset version and host hardening are deliberately independent.

### 3. MinGit (NuGet: `Git-Windows-Minimal`)

Git CVEs (e.g. the recurring `git config`/hook injection classes) land here. This is a
**community-maintained repack** of MinGit - verify it has picked up the patched
git-for-windows release before relying on it:

```powershell
# Compare: NuGet package version vs git-for-windows latest release tag
.\scripts\Check-Dependencies.ps1 -Online
```

- Package updated → normal NuGet bump (runbook #1; the csproj `CopyMinGit` targets
  pick the new payload up automatically).
- Package lagging a security release → vendor MinGit directly: download
  `MinGit-<ver>-64-bit.zip` from git-for-windows releases into `modules\mingit\`,
  point the `CopyMinGit`/`CopyMinGitPublish` targets at it, drop the PackageReference.
  (Half a session; only worth it under an active CVE.)
- Validate: bundle git history/commit flows (`GitService` smoke: open bundle → edit →
  save → History window shows the commit).

### 4. PSADT template (vendored, `modules\psadt-template`)

Current: **4.1.8**. This ships INTO user bundles (PSADT framework bundles), so a PSADT
CVE is a downstream-artifact concern, not just an app concern.

- Download the new PSADT release template, replace `modules\psadt-template\` contents
  (preserving any wrapp-specific files - diff first: `git diff --stat` after copy).
- Validate: create a PSADT-framework bundle, check the Deploy tab loads
  `Invoke-AppDeployToolkit.ps1`, package it, and run the packaged artifact in a sandbox.
- Note: existing bundles keep their copied template version - a PSADT CVE fix means
  users should also *upgrade-save* affected bundles (the upgrade flow re-copies the
  vendored template only for missing files; call this out in release notes when it matters).

### 5. IntuneWin32App module (vendored, `modules\IntuneWin32App\1.5.0`)

- Replace the version-named folder, update the two `CopyIntuneWin32App*` targets in
  `Wrapp.GUI.csproj` (path contains the version), validate an Intune publish run.

### 6. WebView2 runtime - explicitly nothing to do

The runtime browser engine (the thing browser CVEs actually hit) is **Evergreen**:
Microsoft updates it on user machines independent of Wrapp releases. Our SDK
reference only needs bumping for API features. This is the best-patched component in
the entire stack precisely because we don't own it.

---

## Cadence

- **Monthly** (or before each release): `.\scripts\Check-Dependencies.ps1 -Online`.
- **On a published CVE** for anything in the table: run the matching runbook the same
  day - every path above is designed to be executable in under an hour, most in minutes.
- After ANY dependency change: build + full test suite + version bump + CHANGELOG entry
  naming the advisory, then re-publish the release build (`dotnet publish -c Release
  -r win-x64 --self-contained true`) - the release folder ships the old binaries until
  you do.
