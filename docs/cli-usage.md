# Wrapp CLI usage

The Wrapp GUI is a thin shell over the **`Wrapp.Packager`** PowerShell module.
Every packaging run the GUI performs is one call to the same module function the
CLI uses — `Invoke-WrappPackaging` — so a broken or unavailable UI can be
replaced by a single PowerShell command with **identical behavior** (routing,
collision handling, per-tenant passes, key capture, logging).

This is the "UI/CLI parity" contract (Phase 17, Workstream P). If it behaves one
way in the GUI, it behaves the same way here.

---

## Prerequisites

- **Windows PowerShell 5.1** or **PowerShell 7+**.
- The `Wrapp.Packager` module. In a Wrapp bundle it lives at
  `…\Modules\Wrapp.Packager\Wrapp.Packager.psd1`; in the repo it's at
  `modules\Wrapp.Packager\Wrapp.Packager.psd1`.
- A bundle `Config.json` (the same file the GUI reads/writes).
- Network + credentials for the target tenant(s)/site(s).

Import the module by its **manifest** (`.psd1`) — not the `.psm1` — so the
public functions and their legacy aliases are exported:

```powershell
Import-Module 'C:\Path\To\Modules\Wrapp.Packager\Wrapp.Packager.psd1' -Force
```

---

## The one command you need: `Invoke-WrappPackaging`

Reads a bundle's `Config.json`, groups its packages by their configured target
(each Intune package's `TenantId`, each SCCM package's `SiteCode`), and runs one
pass per target — exactly what the GUI does.

```powershell
Invoke-WrappPackaging -ConfigPath 'C:\pkg\7-Zip\Script\Config.json'
```

### Parameters

| Parameter | Type | Notes |
|---|---|---|
| `-ConfigPath` | string (required) | Path to the bundle's `Config.json`. |
| `-Target` | `Intune` \| `SCCM` \| `Both` | Default `Intune`. |
| `-TenantIds` | string[] | Optional Intune tenant filter. Omit to process **every** tenant the config routes packages to. The GUI passes its enabled/connected subset here. |
| `-SiteCodes` | string[] | Optional SCCM site filter (same semantics as `-TenantIds`). |
| `-PackageNames` | string[] | Optional package filter, applied before routing. |
| `-Validate` | switch | Validation only — no changes made to Intune/SCCM. |
| `-LogPath` | string | Override the log file location. Default: `%LOCALAPPDATA%\Wrapp\Logs\<Tag>.log`. |

### Return value

A single aggregate object:

```
Success       [bool]      True when every executed pass succeeded
TenantResults [hashtable] TenantId -> per-tenant Invoke-WrappIntune result
SiteResults   [hashtable] SiteCode -> per-site Invoke-WrappSccm result
Errors        [string[]]  Flattened per-pass errors
```

Each per-pass result carries its own `Success`, `DeployedApps`, `Collisions`,
`LogFile`, and `Errors`.

### Examples

```powershell
# Everything the config routes, all tenants (Intune):
Invoke-WrappPackaging -ConfigPath 'C:\pkg\7-Zip\Script\Config.json'

# One specific tenant only:
Invoke-WrappPackaging -ConfigPath 'C:\pkg\7-Zip\Script\Config.json' `
    -TenantIds 'bfb009f8-08d9-452d-97f5-6061ec1d0b39'

# Validate without making changes:
Invoke-WrappPackaging -ConfigPath 'C:\pkg\7-Zip\Script\Config.json' -Validate

# Only certain packages:
Invoke-WrappPackaging -ConfigPath 'C:\pkg\Suite\Script\Config.json' `
    -PackageNames '7-Zip','Notepad++'

# SCCM (or both channels):
Invoke-WrappPackaging -ConfigPath 'C:\pkg\7-Zip\Script\Config.json' -Target Both

# Inspect the outcome:
$r = Invoke-WrappPackaging -ConfigPath 'C:\pkg\7-Zip\Script\Config.json'
$r.Success
$r.Errors
$r.TenantResults.Keys | ForEach-Object { "$_ => $($r.TenantResults[$_].Success)" }
```

---

## Authentication (CLI vs GUI)

Auth is **hybrid** and per-tenant:

- **CLI:** each tenant self-authenticates using its configured `AuthFlow` in
  `Config.json` (`Interactive`, `DeviceCode`, `ClientSecret`, or `ClientCert`).
  For headless/automation use `DeviceCode`, `ClientCert`, or `ClientSecret` —
  `Interactive` needs a desktop session.
- **GUI:** the app acquires one MSAL token per enabled tenant up front and hands
  the module a token map; the module skips its own auth for those tenants.

No extra CLI flags are needed — the module reads `AuthFlow` (and `ClientID` /
`CertThumbprint` / `ClientSecret`) from each tenant's config entry.

---

## Shared defaults (`user-defaults.json`)

The GUI's **Settings → Preferences** persist to `settings.json` and are also
exported to a secret-free sidecar the module reads:

```
%LOCALAPPDATA%\Wrapp\user-defaults.json
```

At each run the module layers this over its shipped `Config\Defaults.psd1`, so a
CLI run inherits the **same** package/assignment/endpoint defaults as the GUI.
Precedence, highest first:

```
bundle Config.json  >  user-defaults.json  >  module Defaults.psd1
```

The sidecar is plain JSON and hand-editable for CLI-only machines. It carries
the endpoint script paths (`Endpoint.TagFolder`, `Endpoint.LocalAppFolder`) and
the six package/metadata/assignment default sections.

---

## Function names & legacy aliases

As of module **4.0.0**, the public functions are Wrapp-branded. The pre-4.0
names still resolve as exported aliases, so existing scripts keep working.

| Function (4.0.0+) | Legacy alias |
|---|---|
| `Invoke-WrappPackaging` | *(new — orchestrator)* |
| `Invoke-WrappIntune` | `Invoke-IntunePackager` |
| `Invoke-WrappSccm` | `Invoke-SCCMPackager` |
| `Connect-WrappIntune` | `Connect-IntunePackager` |
| `Connect-WrappSccm` | `Connect-SCCMPackager` |
| `Test-WrappConfig` | `Test-PackagerConfig` |
| `Test-WrappIntunePreflight` | `Test-IntunePackagerPreflight` |
| `Test-WrappSccmPreflight` | `Test-SCCMPackagerPreflight` |

`Invoke-WrappIntune` / `Invoke-WrappSccm` are the single-target orchestrators
(one tenant / one site per call). `Invoke-WrappPackaging` is the multi-target
front door that loops them — prefer it unless you deliberately want to drive a
single target.

---

## Validate a config without packaging

```powershell
# Structured validation issues (same check the GUI's "Validate" runs):
Test-WrappConfig -ConfigPath 'C:\pkg\7-Zip\Script\Config.json' -ScriptType 'IntunePackager'

# Or a full dry run through the orchestrator (no changes made):
Invoke-WrappPackaging -ConfigPath 'C:\pkg\7-Zip\Script\Config.json' -Validate
```

---

## Logs

- Per-run module log: `%LOCALAPPDATA%\Wrapp\Logs\<Tag>.log` (CMTrace format),
  or the `-LogPath` override.
- If the bundle's `Config.json` defines a `Domain.TagFolder`, a copy is also
  written there at the end of the run (best-effort).
