# Wrapp Changelog

All notable changes to Wrapp are documented here. Versioning follows
SemVer2 (`MAJOR.MINOR.PATCH`); the update pipeline orders releases by this
version.

---

## [1.0.6] - 2026-09-15

### Fixed

- **Intune assignments were skipped with "GUID mismatch" on every new app.**
  Since the per-package Notes field arrived, the packager forwarded the raw
  package note as an override on top of the tracking JSON it had just built
  from it. An empty note was stripped as blank, so the app was created with
  no Notes at all; a typed note shipped as plain text. Either way the
  assignment step could not find the bundle GUID in the app and stopped.
  Notes (and the orchestrator's IsEnabled flag, which logged a spurious
  "not a recognized parameter" warning) are now handled keys, and a lint
  test keeps every package key the function reads out of the override
  path. Apps created by 0.6.329 through 1.0.5 carry no tracking Notes;
  re-publishing them in update mode writes the JSON.
- **Closing with nothing to save could leave the window unclosable.** When
  the close guard had no dirty scopes and no jobs it completed
  synchronously, and the confirmed close re-entered the original Closing
  dispatch and threw. The close now yields once before it runs.
- **Job cards no longer risk a cross-thread crash.** Registering,
  completing and failing a background job, and adding a fact to its
  details card, now marshal to the UI thread themselves instead of
  relying on every caller to do it; a job finishing on a worker thread
  while the jobs popup had the card open could throw.
- **Help paragraphs that start with a button token** (`[btn:Save]`)
  render as written; the legacy "Label: value" preprocessor no longer
  bolds the token's prefix.
- **Long view titles trim instead of pushing the header's buttons off the
  edge.** The view header lays out its right-hand content first and gives
  the title what is left.

### Changed

- A XAML lint test now catches the scroll helper being attached to
  anything but a ScrollViewer, where it is a silent no-op.

---

## [1.0.5] - 2026-08-27

### Fixed

- The sidebar toggle points its arrow the way the rail will move, and shares
  a nav row's box, so it sits on the same left rule as the icons when
  expanded and the same centre line when collapsed.

---

## [1.0.4] - 2026-08-27

### Added

- **GitHub release feeds.** The update feed URL now accepts a GitHub
  repository (`https://github.com/owner/repo`) alongside UNC shares, local
  folders, and static HTTPS directories. GitHub releases are not a browsable
  directory, so these feeds resolve through the releases API. The repository
  must be public: no access token is sent, and Wrapp does not store secrets
  for the update path.

### Changed

- Navigation rows are a fixed height with larger icons and labels, and the
  error/warning badges are drawn as an overlay, so a row no longer changes
  size when a count appears or clears. Collapsed rows centre their icon with
  the badge stack riding its edge. The sidebar toggle shows an arrow that
  points the way it will move.
- View headers are a uniform height, so titles sit on the same baseline
  whether or not a view has header buttons.

### Fixed

- Release notes embedded in the update manifest no longer mangle non-ASCII
  characters (the changelog was read with the wrong encoding at pack time).
- Help popups, the background-jobs list, and the commit history scroll with
  the same gentle wheel step as the rest of the app.
- Removed a no-op handler that re-applied an already-set brush for every code
  block rendered in a help popup.

---

## [1.0.3] - 2026-08-27

**First public release.** Wrapp is a Windows desktop app for building,
validating, and shipping Win32 application packages to Microsoft Intune and
SCCM/ConfigMgr - the whole flow from installer to deployed app in one tool.

### The packaging flow

- **Bundles**: every app lives in a bundle folder - metadata, scripts,
  detection, icon, and source files together, tracked by an embedded git
  repository (full history and diffs in-app, no git install required).
- **App info**: name, publisher, versions (dot and underscore forms with
  one-click conversion), categories, owner/developer fields, and
  end-user-visible fields labeled with where they surface (Company Portal /
  Software Center). Icons come from an in-app icon editor with a
  spectrum/opacity picker, an icon library, or file import.
- **Install/uninstall scripts**: generated from a template catalog (silent
  MSI, EXE with parameters, MSI+MSP patch, MSIX bundle, winget,
  uninstall-then-install variants) with token replacement, or authored
  directly in the built-in Monaco editor (the VS Code editor, fully
  offline). PSADT v4 bundles are a first-class alternative - the framework
  template ships in the box.
- **Detection**: PowerShell script, MSI product code, file, or registry
  detection, plus a detection-tag system for script-based state marking on
  endpoints.
- **Config JSON**: one JSON document per bundle drives the entire package -
  editable as a form or as raw JSON, always in sync.
- **Run**: end-to-end packaging from the GUI - `.intunewin` creation,
  upload to Intune via Microsoft Graph, app creation with detection,
  requirements, dependencies and supersedence, assignment creation; or SCCM
  application creation, content distribution, and deployments. Powered by
  the bundled **Wrapp.Packager** PowerShell module, which is fully
  CLI-capable for automation without the GUI.
- **Templates everywhere**: package templates, assignment templates, and
  deployment templates - sparse (only checked fields apply), hierarchical
  (a package template can carry its assignments/deployments), with
  placeholder expansion on import.
- **Validation badges**: errors (blocking) and warnings (amber,
  non-blocking) tracked from the navigation rail down through package rows,
  buttons, dialogs, and individual fields - including duplicate
  name/target detection across enabled packages.

### Views

- **General** - bundle picker and app metadata.
- **Intune** - per-tenant package configuration and assignments.
- **SCCM** - per-site package configuration and deployments.
- **Detection / Scripts / JSON** - detection rules, script editing
  (Monaco), and the raw config document.
- **Run** - the packaging pipeline with live output and background jobs
  (progress, per-job details, exportable facts).
- **Inventory** - a live catalog browser for deployed Win32 apps: full
  detail panes (program, requirements, detection, scope tags), assignments
  with **nested Entra ID group expansion** (a view the Intune console
  doesn't offer), dependency and supersedence graphs including the reverse
  direction ("depended on by" / "superseded by"), icon and `.intunewin`
  download, JSON export per app or for the **entire tenant catalog** in one
  run, and import-to-Wrapp / full clone of any deployed app.
- **Tools** - utilities including `.intunewin` decryption and inspection.
- **Logs** - live application log with filtering; CMTrace-compatible
  logging throughout the module.
- **Git History** - the bundle's commit timeline with double-click diffs.
- **Settings** - preferences, tenants, sites, domains, endpoints, Key
  Vault, updates, placeholders, provisioning.

### Enterprise readiness

- **Policy engine**: every setting is administrator-controllable via
  registry policy (`HKLM/HKCU\Software\Policies\Wrapp`) - delivered by
  Group Policy (ADMX/ADML templates included), Intune, or the offline
  `Apply-WrappPolicy.ps1` script for disconnected fleets. Mandated values
  lock their controls with a padlock and a "Managed by your organization"
  indicator; sections and tabs can be hidden outright. Policy changes are
  detected live and surface a restart-to-apply prompt.
- **Organization defaults**: a JSON seed file provisions tenants, sites,
  domains, placeholders, and preferences on first run; keyed lists merge so
  user-added entries survive.
- **Placeholders**: reusable `{{tokens}}` across scripts and fields, with
  per-user encrypted storage for sensitive values and redaction of secrets
  from every log line (extensible regex patterns).
- **Custom themes**: import `.wrapptheme.json` color overlays on the Dark
  or Light base - JSON data only, safe by construction.
- **Authentication**: Microsoft Entra ID via MSAL with Windows broker
  (WAM); client secrets are stored per-user with DPAPI encryption and are
  never provisionable via policy or seed files.
- **Key Vault**: optional publishing of package encryption keys to an
  Azure DevOps repository, with pull-request mode for protected branches.

### Distribution and updates

- **Velopack** update pipeline: delta updates (a few MB per release, with
  the full package rebuilt locally and hash-verified), full packages, a
  one-click Setup.exe, a portable ZIP, and a full MSI wizard build for
  technician/endpoint-management deployment. The update feed is a plain
  folder - UNC share, local path, or static https.

### Bundled components

.NET 8 (WPF), WPF-UI, MaterialDesignThemes, CommunityToolkit.Mvvm,
Microsoft.Identity.Client (+ broker), Microsoft PowerShell SDK 7.4,
WebView2 + Monaco editor (offline), Velopack, embedded MinGit, and the
vendored modules: **Wrapp.Packager** (ours), IntuneWin32App 1.5.0, the
PSADT v4 framework template, and the Appease packaging-environment
template. See `THIRD-PARTY-NOTICES.txt` for licenses.
