# Wrapp Policy Administration Guide

**Audience:** environment administrators locking down and provisioning Wrapp
across a fleet. **Applies to:** 0.6.332+.

Wrapp follows the Chromium/Edge/Firefox policy model: **the application only
ever reads plain registry values** under `Software\Policies\Wrapp`. Domain
Group Policy, Intune (ADMX ingestion), and the offline script all *write*
those same values - Wrapp does not talk to AD or Intune and needs no
connectivity to be managed.

---

## 1. How the registry keys work

```
HKLM\SOFTWARE\Policies\Wrapp                 ← machine MANDATORY  (highest authority)
HKCU\SOFTWARE\Policies\Wrapp                 ← user MANDATORY
HKLM\SOFTWARE\Policies\Wrapp\Recommended     ← machine recommended
HKCU\SOFTWARE\Policies\Wrapp\Recommended     ← user recommended
```

- **Mandatory value ⇒ enforced + locked.** The setting takes the policy value,
  its control is disabled with a "Managed by your organization" tooltip, and
  no save/import can change it. There are no separate "lock" flags - the
  presence of the value *is* the lock (the Chrome convention; nothing can
  drift).
- **Recommended value ⇒ seeded default.** Applied only while the setting is
  still at its factory default; a technician's explicit choice always wins,
  and the control stays editable.
- **Precedence** (highest first): HKLM mandatory → HKCU mandatory →
  Recommended (machine over user) → organization defaults file → the user's
  own settings.json → factory defaults.
- **Value naming** = the setting name, verbatim (`UpdateFeedUrl` REG_SZ,
  `EnableAzureDevOpsKeyVault` REG_DWORD 0/1). The six preference-default
  blocks are subkeys with per-leaf values, e.g.
  `…\Wrapp\IntunePackageDefaults\InstallExperience` - you can mandate one
  leaf without freezing the rest of the block.
- **Hiding UI** uses three list subkeys (value name = item, data = DWORD 1):
  - `…\Wrapp\HiddenSections\Inventory = 1` - hides whole views from the nav
    rail. Names: `Intune, SCCM, Detection, Scripts, ConfigJson, Run,
    Inventory, Tools, Logs, GitHistory`. `Settings` and `General` can never
    be hidden (ignored with a log line).
  - `…\Wrapp\HiddenSettingsTabs\KeyVault = 1` - hides Settings tabs. Names:
    `Bundle, Domains, Endpoint, Intune, SCCM, KeyVault, Updates,
    Placeholders, Provisioning`.
  - `…\Wrapp\HiddenSettings\<key> = 1` - hides an individual control.
- Wrapp always reads the **64-bit registry view**, so a 32-bit install sees
  the same policy.

**Hide vs. lock:** a mandated value stays *visible but disabled* (operators
can see what the org enforces); a hidden item is *not rendered at all* and is
unreachable even programmatically (navigation to a hidden section redirects
to General). Both can be combined.

## 2. What options are available

Registry value names, grouped as in the ADMX. Types: S = REG_SZ,
D = REG_DWORD.

| Policy | T | Effect |
|---|---|---|
| **Updates** | | |
| `UpdateFeedUrl` | S | Velopack release feed (https / UNC / local; plain http rejected). Machine-mandated feeds skip the per-user approval prompt - see §5. |
| `UpdateMode` | S | `Auto` \| `NotifyOnly` \| `Disabled`. |
| **Key Vault** | | |
| `KeyVaultRepoUrl` | S | Azure DevOps repo receiving encryption keys. Must be https (validated). Machine-mandated URLs skip the approval prompt. |
| `EnableAzureDevOpsKeyVault` | D | Master switch for the vault feature. |
| `KeyVaultUsePullRequests` | D | Force PR mode (for branch-protected repos). |
| `KeyVaultPathTemplate`, `KeyVaultManualPathTemplate`, `KeyVaultPrSourceBranchTemplate`, `KeyVaultPrTitleTemplate`, `KeyVaultPrDescriptionTemplate` | S | Path/PR templates. |
| **Endpoints** | | |
| `EndpointTagFolder`, `EndpointLocalAppFolder` | S | On-endpoint paths baked into every generated script - mandate these. |
| **Bundle** | | |
| `DirectoryFormat`, `IconFolderName`, `PsadtTemplatePath` | S | Bundle output conventions. |
| **Defaults blocks** (subkeys) | | `IntunePackageDefaults`, `IntuneMetadataDefaults`, `IntuneAssignmentDefaults`, `SccmPackageDefaults`, `SccmMetadataDefaults`, `SccmDeploymentDefaults` - every string/int/bool leaf is administrable per-value. A mandated leaf locks its section in Settings. |
| **Provisioning** | | |
| `OrgDefaultsPath` | S | Full path (any filename) to the organization defaults JSON - checked before every built-in location. |
| `DisableSettingsImport` | D | Hides + blocks the Export/Import settings card. |
| `DisableOrgDefaultsImport` | D | Hides + blocks the org-defaults import card. |
| **Appearance** | | |
| `Theme` | S | `Dark`, `Light`, or a custom theme's name. Locks the picker. |
| `ThemeFilePath` | S | Path to an org `.wrapptheme.json` (see §6) - appears in the picker; combine with `Theme` to enforce it. |
| **Diagnostics** | | |
| `VerboseUiTrace` | D | Force UI trace logging on/off. |
| `RedactionPatterns\<label>` | S | Extra log-scrub regexes (§2.2). |
| **Provisioned data** | | |
| `IntuneTenants\<key>\…`, `SccmSites\<key>\…`, `Domains\<key>\…` | subkeys | Keyed entry lists - any number of tenants/sites/domains (§2.1). |
| `Placeholders\<name>` | S | Non-sensitive custom `{{tokens}}` (§2.2). |
| **Interface** | | `HiddenSections`, `HiddenSettingsTabs`, `HiddenSettings` subkeys (§1). |

With these, **every setting the Settings views expose is policy-addressable**:
scalars and the six default blocks per-value, the entry lists per-key,
placeholders and redaction by name - the only exceptions are the per-user
security state listed above, which is excluded by design.

**Never policy-controllable** (ignored if written): trust tokens
(`UpdateFeedTrustToken`, `KeyVaultRepoUrlHash`), tenant `ClientSecret`s,
`GateState`, `OrgDefaultsSeeded`, version markers, `TenantNameCache`. These
are per-machine/per-profile security state.

### 2.1 Keyed entry lists - tenants, sites, domains

The three entry lists are provisioned as **one subkey per entry** under the
mandatory root - the subkey name *is* the entry's Key, so an admin can
deliver any number of them:

```
HKLM\SOFTWARE\Policies\Wrapp\IntuneTenants\<tenant-guid>\
    Name          REG_SZ        "Contoso Production"
    Comment       REG_SZ
    ClientID      REG_SZ        app registration for Graph auth
    AuthFlow      REG_SZ        Interactive | DeviceCode | ClientSecret | Certificate
    CertThumbprint, Architecture, MinimumSupportedWindowsRelease,
    IntuneWinPath, IconFolder    REG_SZ

HKLM\SOFTWARE\Policies\Wrapp\SccmSites\<site-code>\
    Comment, AppFolder, IconFolder   REG_SZ
    DeploymentGroups                 REG_MULTI_SZ  (or "A; B" REG_SZ)

HKLM\SOFTWARE\Policies\Wrapp\Domains\<domain>\
    IsDistPath, AppFolder, TagFolder REG_SZ
```

Semantics: **merged by Key** on every launch/save - a policy entry wins for
its Key (all supplied values enforced), the technician's *other* entries
survive. `ClientSecret` is refused by both the app and the script: secrets
are per-user DPAPI and a world-readable hive can never carry one - use
Certificate or Interactive auth flows for policy-provisioned tenants, or let
technicians enter the secret once (it stays their profile's).
`HKLM` entries win over `HKCU` entries with the same Key. Keyed lists are
registry/script-provisioned (two-level structures don't map to ADMX list
elements); everything else in this guide is also in the ADMX.

### 2.2 Placeholders and log redaction

```
…\Wrapp\Placeholders\<Name>         REG_SZ = expansion value
…\Wrapp\RedactionPatterns\<Label>   REG_SZ = regular expression
```

- **Placeholders** upsert as *non-sensitive* custom `{{tokens}}` and are
  enforced on every launch/save. Reserved built-in names are rejected; a
  user's *sensitive* placeholder with the same name is never overwritten or
  converted (its plaintext lives in per-user DPAPI storage a machine policy
  has no authority over).
- **RedactionPatterns** merge with the org defaults file's
  `SensitivePatterns` and scrub every log line from startup.

Both are ADMX-expressible (list policies under Provisioning / Diagnostics).

## 3. How Wrapp checks policy before rendering

The mechanism is `PolicyService` (`Services/Policy/`), reading through an
`IPolicyStore` abstraction (`RegistryPolicyStore` in production). The startup
order is deliberate:

```
SettingsService.Load()                    settings.json → in-memory settings
PolicyService.Current                     one registry read builds the snapshot
DefaultsLoader.PolicyPathOverride = …     OrgDefaultsPath takes effect
PolicyService.ApplyRecommended(settings)  fills factory-default values only
DefaultsLoader.Load() + OrgDefaultsSeeder org file seeds (one-shot per profile)
PolicyService.ApplyMandatory(settings)    unconditional enforcement - runs LAST
ApplyTheme / windows render               everything downstream sees final values
```

Because recommended values land *before* the org-file seeder and mandatory
values land *after* it, the seeder needs no policy awareness - whatever it
wrote, the mandate wins. Mandatory values are also re-asserted:

- before **every settings save**,
- after **every settings import** (the import path otherwise overwrites every
  property wholesale),
- and validated on read: unknown value names, wrong types, out-of-set enums,
  and non-https/unsafe URLs are **ignored with an app.log line** - a
  malformed policy never half-applies.

**Refresh model: restart-to-apply, with live awareness.** The snapshot is
built once per launch. Wrapp watches the `Software\Policies` subtrees
(event-driven `RegNotifyChangeKeyValue` - no polling); when the effective
Wrapp policy drifts from the launch snapshot, the status-bar
**action-required indicator** lights up with *"Restart to apply updated
organization policy"* - resolving it opens a fresh window (new policy) and
closes the current one through the normal save-prompt flow. Rolling the
change back clears the indicator.

**Transparency:** when any policy is active, the Settings header shows a
"Managed by your organization" chip, and *Settings → Provisioning →
Effective configuration* lists every mandated value with its source hive -
Wrapp's equivalent of `chrome://policy`.

## 4. Seeding and the org defaults file

Unchanged pipeline, now policy-addressable:

- `defaults.local.json` is found at (first match wins): the **`OrgDefaultsPath`
  policy**, beside the exe, the install root, `%LOCALAPPDATA%\Wrapp`,
  `%ProgramData%\Wrapp`, dev source dir.
- Seeding runs **once per profile** and only onto factory-default values - a
  technician's edits are never overwritten. Preference blocks seed
  all-or-nothing; tenants/sites/domains surface whenever the user's own list
  is empty.
- Policy **Recommended** values behave like a per-value org default that
  re-checks every launch; policy **Mandatory** values override everything.
- `DisableOrgDefaultsImport` / `DisableSettingsImport` remove the in-app
  import surfaces for fleets where provisioning is admin-only.

## 5. Trust decisions (TOFU) under policy

Wrapp normally requires a one-time per-user approval before contacting an
update feed or key vault URL (the URL is bound to a DPAPI token). Under
policy:

- A **machine-mandated** (`HKLM`) `UpdateFeedUrl` / `KeyVaultRepoUrl` is
  trusted **without** the prompt: writing HKLM requires local admin, a
  strictly stronger authority than the per-user token.
- A **user-mandated** (`HKCU`) URL still prompts: HKCU is writable by the
  user's own processes, so bypassing there would let malware self-approve a
  feed.
- Trust tokens themselves can never be provisioned (per-user DPAPI).

## 6. Custom themes for branding

A theme is a JSON color overlay - **data, never code** (XAML import is
deliberately unsupported; it would be a code-execution vector):

```json
{
  "Name": "Contoso Blue",
  "BaseTheme": "Dark",
  "MonacoTheme": "vs-dark",
  "Colors": {
    "AccentBrush": "#2D6BC4",
    "AppBgBrush": "#101418"
  },
  "ShadowOpacity": 0.45
}
```

- `BaseTheme` (`Dark`/`Light`) supplies every key not overridden; `Colors`
  may override any documented theme key. Unknown keys and unparsable colors
  are rejected **by name**; a theme never half-applies.
- Users import via *Settings → General → Import theme* (validated, copied to
  `%LOCALAPPDATA%\Wrapp\Themes\`). Orgs distribute via the `ThemeFilePath`
  policy and can enforce with `Theme = <name>`.
- The accent color is read from the theme's `AccentBrush`, so a custom theme
  restyles every control (buttons, toggles, dialogs, selection) coherently.
- Start a theme by copying the key list from `src/Wrapp.GUI/Themes/Dark.xaml`
  (or export the sample), overriding only what your brand needs - overlays
  are sparse.

## 7. Deployment recipes

**Domain GPO:** copy `policy/Wrapp.admx` + `policy/en-US/Wrapp.adml` to the
Central Store (`\\<domain>\SYSVOL\<domain>\Policies\PolicyDefinitions\`), then
configure under *Administrative Templates → Wrapp*.

**Intune:** ingest the ADMX (Devices → Configuration → Import ADMX), or
deploy the offline script as a Win32 app / platform script.

**Offline / disconnected fleets:** `scripts/Apply-WrappPolicy.ps1` writes the
identical registry values from a `policies.json`
(`policy/policies.sample.json` is a documented starting point):

```powershell
# elevated: machine scope (recommended)
.\Apply-WrappPolicy.ps1 -PolicyFile .\policies.json

.\Apply-WrappPolicy.ps1 -Export .\effective.json   # audit what's applied
.\Apply-WrappPolicy.ps1 -Clear                     # remove all Wrapp policy
```

Deploy via SCCM package, Intune Win32 app, MDT task sequence, image bake, or
a startup script. Because it writes the same keys GPO would, moving a fleet
from offline scripting to GPO later is a no-op for Wrapp.

The policies.json carries the full surface - scalars, `Recommended`, the
`Hidden*` lists, `Placeholders`, `RedactionPatterns`, and the keyed
`IntuneTenants` / `SccmSites` / `Domains` objects (see the sample). The
script refuses `ClientSecret` values with a warning.

## 8. What the operator sees

- **Managed chip** in the Settings header whenever any policy is active;
  the full list lives in *Provisioning → Effective configuration*.
- **Tab padlocks**: a visible tab whose content is touched by policy shows a
  padlock in its header - the "see it but can't touch it" half of
  lock-vs-hide (hidden tabs are simply absent).
- **Locked fields/sections**: disabled controls with a "Managed by your
  organization" tooltip; the six preference-default sections show a padlock
  beside their headers when any of their leaves is mandated.
- **Provisioned entries**: policy tenants/sites/domains appear in the
  preference grids as read-only rows - a padlock replaces the selection
  checkbox, the row can't be edited or removed, and technicians can still
  add and manage their own entries alongside them.

## 9. Known v1 limits

- Key Vault template fields keep their feature-toggle-driven enablement in
  the UI; a mandated template value is enforced at load/save even while the
  box appears editable. (Full per-field lock chrome for the vault card is a
  follow-up.)
- Preference-default sections lock at section granularity when any of their
  leaves is mandated.
- Policy changes require an app restart (by design - see §3).
