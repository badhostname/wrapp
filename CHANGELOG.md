# Wrapp Changelog

All notable changes to Wrapp are documented here. Versioning follows
SemVer2 (`MAJOR.MINOR.PATCH`); the update pipeline orders releases by this
version.

---

## [1.0.3] - 2026-08-27

**First public release.** Wrapp is a Windows desktop app for building,
validating, and shipping Win32 application packages to Microsoft Intune and
SCCM/ConfigMgr — the whole flow from installer to deployed app in one tool.

### The packaging flow

- **Bundles**: every app lives in a bundle folder — metadata, scripts,
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
  offline). PSADT v4 bundles are a first-class alternative — the framework
  template ships in the box.
- **Detection**: PowerShell script, MSI product code, file, or registry
  detection, plus a detection-tag system for script-based state marking on
  endpoints.
- **Config JSON**: one JSON document per bundle drives the entire package —
  editable as a form or as raw JSON, always in sync.
- **Run**: end-to-end packaging from the GUI — `.intunewin` creation,
  upload to Intune via Microsoft Graph, app creation with detection,
  requirements, dependencies and supersedence, assignment creation; or SCCM
  application creation, content distribution, and deployments. Powered by
  the bundled **Wrapp.Packager** PowerShell module, which is fully
  CLI-capable for automation without the GUI.
- **Templates everywhere**: package templates, assignment templates, and
  deployment templates — sparse (only checked fields apply), hierarchical
  (a package template can carry its assignments/deployments), with
  placeholder expansion on import.
- **Validation badges**: errors (blocking) and warnings (amber,
  non-blocking) tracked from the navigation rail down through package rows,
  buttons, dialogs, and individual fields — including duplicate
  name/target detection across enabled packages.

### Views

- **General** — bundle picker and app metadata.
- **Intune** — per-tenant package configuration and assignments.
- **SCCM** — per-site package configuration and deployments.
- **Detection / Scripts / JSON** — detection rules, script editing
  (Monaco), and the raw config document.
- **Run** — the packaging pipeline with live output and background jobs
  (progress, per-job details, exportable facts).
- **Inventory** — a live catalog browser for deployed Win32 apps: full
  detail panes (program, requirements, detection, scope tags), assignments
  with **nested Entra ID group expansion** (a view the Intune console
  doesn't offer), dependency and supersedence graphs including the reverse
  direction ("depended on by" / "superseded by"), icon and `.intunewin`
  download, JSON export per app or for the **entire tenant catalog** in one
  run, and import-to-Wrapp / full clone of any deployed app.
- **Tools** — utilities including `.intunewin` decryption and inspection.
- **Logs** — live application log with filtering; CMTrace-compatible
  logging throughout the module.
- **Git History** — the bundle's commit timeline with double-click diffs.
- **Settings** — preferences, tenants, sites, domains, endpoints, Key
  Vault, updates, placeholders, provisioning.

### Enterprise readiness

- **Policy engine**: every setting is administrator-controllable via
  registry policy (`HKLM/HKCU\Software\Policies\Wrapp`) — delivered by
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
  or Light base — JSON data only, safe by construction.
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
  folder — UNC share, local path, or static https.

### Bundled components

.NET 8 (WPF), WPF-UI, MaterialDesignThemes, CommunityToolkit.Mvvm,
Microsoft.Identity.Client (+ broker), Microsoft PowerShell SDK 7.4,
WebView2 + Monaco editor (offline), Velopack, embedded MinGit, and the
vendored modules: **Wrapp.Packager** (ours), IntuneWin32App 1.5.0, the
PSADT v4 framework template, and the Appease packaging-environment
template. See `THIRD-PARTY-NOTICES.txt` for licenses.
