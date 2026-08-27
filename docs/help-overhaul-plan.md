# Workstream H — Help & Documentation Overhaul

Status: PLANNED (audit complete 2026-08-11, work not started)
Scope: every user-visible help surface in Wrapp.GUI — HelpContent.xaml, tooltips,
ViewHeader/SectionHeader wiring, the About dialog, and guardrail tests.

---

## 1. How the help system works (audit baseline)

- **Single source of truth:** `src/Wrapp.GUI/Help/HelpContent.xaml` — a merged
  ResourceDictionary of `sys:String` markdown blobs, 252 keys / ~1,700 lines.
- **Three consumers of the same keys** (this is the "tied together" property —
  edit one string, every surface updates):
  1. `ViewHeader` / `SectionHeader` (`HelpKey="Help.X"`) → info button → markdown
     popup via `HelpMarkdownRenderer` + `FluentDialog`.
  2. Field tooltips: `ToolTip="{StaticResource Help.X.Y}"` (~214 uses) and
     `ctrl:FieldLabelRow Help="{StaticResource Help.X.Y}"` (~19 uses).
  3. Eight code call-sites (`TryFindResource` + `BuildFormattedPanel`) for
     side-panels, About, Background Jobs, token reference.
- **Renderer supports:** headings, bold/italic/code, tables, fenced code blocks
  (monospace — good for XML/JSON/tree examples), external hyperlinks,
  `[icon:name]` and `[badge:text:color]` tokens. No renderer work is needed for
  any content in this plan.
- **Missing-key behavior:** clicking an info button whose key doesn't exist shows
  a raw error dialog ("No help content found for key: …"). One such key is live
  today (see H1).

## 2. Audit findings (condensed)

Full agent reports are in session history; the load-bearing facts:

- **Staleness:** HelpContent.xaml last had content commits at **0.6.0.0286**;
  HEAD is 0.6.317-beta. ~30 releases (Workstreams D/G/O/U/V, Monaco trim, settings
  rail, run-flow rework) shipped with zero help updates.
- **Outright false claims** (worst first):
  - `Help.Tools.Overview`: vault described as "local DPAPI-encrypted store at
    `%LOCALAPPDATA%\Wrapp\keys\`" — the vault has been Azure-DevOps-git-backed
    since Phase 10 (0.6.0.0220). Also claims Batch Inspect exports CSV (it doesn't;
    CSV is decrypt input only).
  - `Help.Detection.Overview` + inline text `DetectionView.xaml:52`: hardcodes
    `C:\the org tag folder\Tag\DetectionResults.json` — real path is the configurable
    `{{TagFolder}}` (Settings → Endpoint), shipped default `C:\Wrapp\Tag`.
    **`C:\the org tag folder` is org data and must not ship in help text at all.**
  - "Templates are managed in Settings > Preferences" — repeated in **4 keys**;
    no such UI exists (templates come from Save/Save-As on views, stored in
    `%LOCALAPPDATA%\Wrapp\Templates\`).
  - `Help.Run.Overview` phase list "Extract → Build → Sign → Upload → Assign" —
    fictional. Real Intune steps: Collision Check → Wrapping → App Creation →
    Dependencies → Assignment; SCCM: Collision Check → Detection Script → App
    Creation → Content Distribution.
  - `Help.Settings.Overview`: describes a four-area layout that predates the
    9-tab nav rail, and claims "Preferences flow one-way" — now false
    (Pull-from-bundle exists).
  - `Help.Settings.Reset.BundleOutput`: understates Reset Defaults (it also
    resets Update feed/mode + all Key Vault fields).
  - `Help.Scripts.Overview`: tab strip "(Install / Uninstall / Detect)" — real
    order is Detect/Install/Uninstall (Appease) and Detect/Deploy (PSADT);
    "IntelliSense" overclaim post-Monaco-trim.
  - `Help.Settings.Preferences.Tenants`: references "Sync with…" button that
    only exists in dead UI; real button is "Restore..." with a 4-option menu.
  - `Help.Detection.Tests`: lists 8 operators; there are 12.
- **Dead key (user-visible bug):** `SettingsView.xaml:314` references
  `Help.Settings.Preferences.Domains` which was never authored → error dialog.
- **Dead view:** `Views/TenantsView.xaml` (696 lines) is never instantiated;
  carries 10 tooltips and 13 `Help.Tenants.*` keys.
- **Orphaned keys (authored, unreachable):** `Help.Detection.Expression`,
  `Help.Detection.Tests`, `Help.General.AppInfo.IconFile`,
  `Help.Intune.Assignments.Tenant`, `Help.SCCM.DeploymentTypeSettings.UserNotification`,
  `Help.SCCM.Deployments.ApprovalRequired`, `Help.SCCM.Deployments.Sites`,
  `Help.Settings.Preferences`, + 3 `Help.Tenants.*`.
- **Zero-coverage surfaces:** ToolsView (3 tabs, 0 tooltips), GitHistoryView
  body, LogsView (no ViewHeader at all), Settings tabs Endpoint / Key Vault /
  Updates / Defaults, run-plan dialog, collision dialog, What's-New popup,
  liability gate, all picker dialogs, Diff/FileHistory windows, SplashWindow.
- **Undocumented features (all missing):** per-package Enable/Disable, amber
  warning badges, collision pre-flight, deployment-plan confirmation, Velopack
  update system (modes/trust/mandatory close/download job), MSAL tenant
  discovery→Preferences, Pull-from-bundle, org defaults vs import/export
  semantics, `{{TagFolder}}`/`{{LocalAppFolder}}` tokens (2 of 12 tokens
  undocumented everywhere; UI hints list only 7).

---

## Phase H1 — Stop the lies (small, ship first)

Goal: no user-visible help statement is false; no error dialogs from help buttons.

1. Author `Help.Settings.Preferences.Domains` (fixes the live error dialog).
2. Correct every false claim listed above **in place** (minimal rewrites, no new
   structure): Tools vault location + CSV claim, Detection tag-folder path (help
   AND the inline `DetectionView.xaml:52` text — remove `C:\the org tag folder` org data),
   4× template-management claims, Run phase names (both target types), Settings
   overview layout + one-way claim, Reset Defaults scope, Scripts tab strip +
   IntelliSense, "Sync with…"→"Restore...", operator list 8→12, Symbol "single
   letter" claim (multi-char works; A–Z is the auto-assignment).
3. Replace the 2 hardcoded "Include this package in runs" tooltips
   (IntuneView:347, SCCMView:341) with a new shared `Help.Common.PackageEnabled`
   key (content properly authored in H5, stub now).
4. **Decision (user):** delete `TenantsView.xaml` + its 13 `Help.Tenants.*` keys
   (recommended — it is unreachable), or wire it back into navigation.
5. Delete or re-hook the other orphaned keys where the fix is one attribute
   (wire `Help.General.AppInfo.IconFile` to the Browse button,
   `Help.Intune.Assignments.Tenant` in the assignment dialog, the 2 SCCM dialog
   keys); wire `Help.Detection.Expression`/`Tests` in H2.

Estimated: ~15 key edits, 2 XAML touch-ups, 1 view deletion. No behavior change.

## Phase H2 — Wire missing affordances (structural, no prose yet)

Goal: every panel that deserves help has an info button reaching a real key.

1. **DetectionView:** convert the two plain `TextBlock` headers ("Boolean
   Expressions", tests grid) to `SectionHeader` → the two orphaned keys go live.
2. **InventoryView:** convert the 9 detail-card headers to `SectionHeader` with
   new `Help.Inventory.Detail.*` keys (stubs; filled in H5).
3. **RunView:** `SectionHeader` for target cards, progress grid, results panel.
4. **LogsView:** replace hand-rolled header with `ViewHeader`, new
   `Help.Logs.Overview`.
5. **Settings tabs** without any section help: General, Bundle, Endpoint,
   Key Vault, Updates, Defaults → `SectionHeader` per card (keys filled in H4).
6. **ToolsView:** `SectionHeader` per tab section (keys filled in H3).
7. **Dialogs:** `Help.RegistryBrowser.Overview` (has field tooltips but no
   overview); GitHistoryView body sections. Trivial pickers (App/Icon/MSI-icon/
   ActionPicker/SaveTemplate) explicitly get NO help — they're self-evident;
   record that as a deliberate convention in a comment atop HelpContent.xaml.
8. Runtime FluentDialog popups (plan dialog, collision dialog): no info button
   infrastructure exists in `ShowChoiceAsync`/`ShowSelectAsync` — instead give
   each a one-paragraph explainer INSIDE the dialog panel (BuildCollisionPanel /
   DeploymentPlanRenderer) sourced from HelpContent keys so the text stays tied.

Estimated: ~20 XAML conversions + ~25 stub keys. Mechanical.

## Phase H3 — Tools & Detection in-depth (the explicitly requested content)

Goal: the two most technical views get reference-grade help with worked examples.
All facts below verified against code by the deep-dive audit; file:line refs in
session history.

### Tools (`Help.Tools.*` — rewrite Overview + ~14 new keys)

- **Overview rewrite:** three tabs, drag-drop rules (.intunewin only; folder
  drop only on Batch), where keys live (DevOps git repo, feature gate, TOFU
  trust), what each tab is for.
- **`Help.Tools.Decrypt`:** the 5 key sources and when each is available
  (embedded/manual/vault/bruteforce/csv + CanDecrypt requirements); blob format
  diagram in a code fence:
  `[ HMAC-SHA256 (32B) ][ IV (16B) ][ AES-256-CBC ciphertext, PKCS7 ]`;
  authenticate-before-decrypt note (MAC verified for embedded/vault only);
  output behavior (ZIP → extracted folder; else extension-sniffed; `_2` suffix
  on collision). **Example CSV** (Key/IV column aliases, no quoted-comma
  support) in a code fence.
- **`Help.Tools.Inspect`:** what detection.xml is, why Inspect takes seconds
  (inner Config.json decrypt for the vault-name suggestion), Copy Keys, Save to
  Vault flow. **The requested XML example** — full fake-value `detection.xml`
  with `ApplicationInfo`/`EncryptionInfo` and the exact element names Wrapp
  reads (`EncryptionKey`, `InitializationVector`, `macKey`, `mac`, `fileDigest`,
  `fileDigestAlgorithm` — mixed casing is real and worth a note).
- **`Help.Tools.Batch`:** the three row outcomes (`OK` / `Exists` = same
  key+IV pair already in vault / `No metadata`), recursive toggle, per-row vault
  name editing, only-OK-rows-save rule.
- **`Help.Tools.Vault`** (shared by Tools + Settings Key Vault tab): **the
  requested folder-tree pipe graph** in a code fence — `/wrapp/{Tenant}/{AppId}.json`
  (auto-capture) vs `/manual/{PackageName}.json` (Tools saves), `_2` collision
  suffixes, README ignored; the **two read paths** honestly documented: fast
  lookup is hard-coded `/wrapp/{tenant}/{appId}.json` (custom templates are
  found only by the recursive crawl/brute force); crawl = one recursive listing,
  every `.json` at any depth, first-match-wins for brute force; vault file JSON
  schema example (fake values); trust approval + PR mode.
- Field tooltips for every input on all three tabs (~18 new field keys).

### Detection (`Help.Detection.*` — rewrite Overview + Tests + Expression)

- **Overview rewrite:** what this view actually authors (a script-based
  detection injected into DetectScript.ps1 → ONE PowerShell script rule in
  Intune / ScriptText in SCCM — NOT Intune native rules, which live in raw
  config `DetectionRules` and cannot be mixed with a script rule); exit-code
  contract (exit 0 + empty stdout = not installed; any stdout = installed;
  VerboseMode breaks detection — console runs only); results written to
  `<Tag folder>\DetectionResults.json` (Settings → Endpoint).
- **`Help.Detection.Tests` rewrite — "how tests evaluate true/false"** (the
  requested content):
  - Path branch: missing file/registry key → **false regardless of operator**
    ("file absent" must be expressed as `-not A` in the expression, not `-ne`).
  - Command branch: `Invoke-Expression`, then property read; falsy trap — a
    command returning `$false`/`0`/`""` reads as "no object" → false.
  - All 12 operators listed.
  - Version-aware comparison: when both sides parse as versions, the operator
    applies to the CompareTo result (−1/0/1), so `1.0.1` == `1.0.1.0` — and
    `-like`/`-match`/`-contains` are meaningless on version-looking values.
  - Worked example table: 3 tests (file version, registry value, service
    command) with sample detected values and outcomes.
- **`Help.Detection.Expression` rewrite — "how booleans combine"** (requested):
  - Symbols are textually substituted with `$True`/`$False` (longest symbol
    first) and the string is evaluated as PowerShell: `-and`, `-or`, `-not`,
    `!`, parentheses, any nesting.
  - Only tests whose symbol appears in the SELECTED expression run at all.
  - Named expressions = per-PackageOption variants (`Expression_Project`…),
    fallback to `Expression_Default`; unnamed rows are dropped on save.
  - Substring hazards: keep symbols uppercase and distinct (lowercase symbols
    can corrupt `-and`/`-or`); the auto-assigned A–Z pattern is the safe one.
  - Worked example: `A -and (B -or -not C)` walked through with a truth table.
- **Pre-doc verification task:** the registry key-level `Property=Exists`
  default (browsing a KEY, not a value) likely always evaluates false at
  runtime (`Get-ItemProperty` on a key returns no `.Exists` member). Reproduce;
  if confirmed, FIX the browse default (e.g. emit `Command: Test-Path` or a
  PSPath property check) rather than documenting a broken behavior.

Estimated: ~35 keys authored/rewritten; 1 behavior fix pending verification.

## Phase H4 — Settings deep-docs (per-tab "what/where/accepted values")

Goal (user requirement): each Settings section explains where the setting takes
effect, where it lands in settings.json / the bundle's Config.json, accepted
values, and behavior. The complete verified map exists in the audit; each tab
gets an overview key + field keys following one template:
**What it does → Where it's stored → Where it takes effect → Accepted values →
Gotchas.**

1. **Rewrite `Help.Settings.Overview`** around the 9-tab rail + the save model
   (single Save strip button, 750ms dirty diff, only Theme applies immediately,
   nothing needs a restart).
2. **General:** Theme (immediate preview, persisted on Save).
3. **Bundle:** DirectoryFormat — single-brace tokens `{Company} {Name} {Version}
   {DotVersion} {Language}`, values sanitized, drives Save-Bundle folder layout
   AND makes referenced fields required at save; IconFolderName; PSADT template
   path resolution order (setting → bundled template); each Reset button's TRUE
   scope (incl. Reset Defaults touching Updates + Key Vault; Reset All's backup
   file + what it deliberately keeps: MSAL cache, templates, encryption keys).
4. **Domains:** field meanings (IsDistPath/AppFolder/TagFolder), how rows reach
   a bundle (`Domain` root object in Config.json, Key becomes the JSON object
   key), used by Appease transcript copy.
5. **Endpoint:** `{{TagFolder}}`/`{{LocalAppFolder}}` expansion points
   (DetectScript results path, Appease logs/cache, InstallScript $LocalAppDir);
   **only affects bundles created/re-scripted afterwards**; exported to
   user-defaults.json for the module.
6. **Intune tab:** tenants grid (per-field keys exist — refresh; add: secret is
   DPAPI-encrypted, Config.json writes the `ref:settings` sentinel, never the
   secret); package defaults → applied at Add-Package time only; metadata
   defaults → **full 12-token list** (`{{Company}} {{Name}} {{Version}}
   {{DotVersion}} {{Language}} {{Date}} {{Author}} {{EXEFile}} {{MSIFile}}
   {{GUID}} {{TagFolder}} {{LocalAppFolder}}`) — also fix the 7-token inline
   hints (SettingsView:651, :992) and the ScriptsView token-reference key;
   assignment defaults + note that the assignment/deployment dialogs read
   SAVED settings (unsaved edits don't reach new assignments until Save).
7. **SCCM tab:** same treatment; sites grid incl. DeploymentGroups being
   per-bundle-edited (persisted but no grid column — say so).
8. **Key Vault:** all 6 fields with the `{Tenant} {AppId} {AppName}
   {PackageName} {Date} {Author}` token set; TOFU trust approval on Save;
   PR mode behavior (one PR per save, needs existing main); **the honest
   custom-template caveat** (fast reads use the legacy hard-coded path); fix
   the false `\n`-unescape hint at SettingsView:1199.
9. **Updates:** feed URL formats (https / UNC / local path; http rejected),
   trust approval, the three modes with REAL semantics (Auto = mandatory
   update-or-close at launch, NotifyOnly = offer, Disabled), download-as-job,
   install flow (close through save prompts, relaunch), "Check for updates
   reads saved values".
10. **Defaults:** org-defaults import = merge-never-overwrite + file becomes the
    live fallback source for tenants/sites/domains; export strips secrets/trust/
    gate state; import settings = full replace preserving per-machine trust;
    the "already customised" outcome explained (mirrors the improved dialog
    text); exported-file-as-org-defaults caveat (flat vs nested Vault/Endpoint/
    Update blocks don't map).

Estimated: ~45 keys new/rewritten + 3 inline-hint fixes.

## Phase H5 — New-feature help (close the 30-release gap)

1. `Help.Common.PackageEnabled` (checkbox, strikethrough, run skip "Disabled",
   collision pre-flight exclusion, warnings suppressed while disabled).
2. Warning badges: extend Intune/SCCM overviews + a `Help.Common.WarningBadge`
   (amber = non-blocking: no tenant/site targeted (only shown once errors are
   clear), invalid Information/Privacy URL; disabled packages never warn);
   replace the 2 hardcoded badge tooltips in MainWindow.
3. Collision pre-flight: `Help.Run.CollisionCheck` (when it runs, per-tenant
   grouping, the three outcomes and exactly what each mutates, fails-open note,
   why "proceed anyway" isn't offered) — used by Run overview AND the dialog
   explainer from H2.
4. Deployment-plan dialog: `Help.Run.PlanDialog` + fix Run overview's Start
   description (plan confirmation gate before the job).
5. Update system: `Help.Settings.Updates.*` (authored in H4) + status-bar/jobs
   text: add "update downloads" to both job-type enumerations; document the
   What's-New popup + version cards; gates/"action needed" status-bar indicator
   (`Help.Main.ActionNeeded`).
6. MSAL tenant discovery: extend `Help.Main.Account.*` — the Add-Tenant offer
   writes to BOTH the bundle and Settings→Intune (Preferences), what happens on
   decline.
7. Pull-from-bundle: keys for both buttons (add-missing + fill-blanks merge,
   DeploymentGroups union, never overwrites).
8. Run/Jobs badge legends: add "Disabled" skip reason.
9. Inventory detail cards + GitHistory + Logs overview content (stubs from H2).
10. Splash/onboarding + liability gate: one overview key each explaining the
    first-run sequence (waiver → org defaults choice → feed approval).

Estimated: ~30 keys.

## Phase H6 — About: changelog + releases link

Current About = identity header + `Help.About.Overview`. Changes:

1. **"What's new" section inside About:** reuse
   `WhatsNewService.BuildVersionCards` over the EMBEDDED changelog — latest
   ~10 versions rendered as the same themed cards as the update popup, inside
   the existing scrollable dialog. (Full history stays one click away via the
   link; rendering 100+ future versions as WPF cards would eventually drag.)
2. **"View all releases" hyperlink** under the cards AND at the bottom of the
   What's-New popup. Target = single constant `AppInfo.ReleasesUrl`, optionally
   overridable by org defaults (`Update.ReleasesUrl`) for fleets.
   **Decision (user):** default URL. Recommended (works today, private-repo
   access permitting): `https://github.com/badhostname/wrapp/blob/main/CHANGELOG.md`.
   GitHub *Releases* page would be empty (we don't publish gh releases); DevOps
   has no remote today. One-line change whenever you decide differently.
3. `Help.About.Overview` refresh (version scheme, update channel pointer).

Estimated: ~1 small service method + 2 call sites + 1 setting-ish constant.

## Phase H7 — Guardrails (so this never rots silently again)

1. **HelpKeyReferenceTests** (new test file):
   - Scan Views/Controls XAML + .cs for `HelpKey="…"`, `{StaticResource Help.*}`,
     `TryFindResource("Help.*")` literals; parse HelpContent.xaml keys.
   - Assert: every referenced key EXISTS (kills dead-key error dialogs forever).
   - Assert: every authored key is REFERENCED (orphan detection; small explicit
     allowlist for keys consumed dynamically).
2. **Drift tripwires** (cheap content assertions):
   - `Help.Detection.Tests` contains every `DetectionViewModel.ValidOperators`
     entry.
   - Token-reference help + metadata hints contain all 12 `TemplateService`
     tokens.
   - No help string contains `the org tag folder` (org data must never ship in help).
3. **Process:** add a line to the release checklist in `scripts/Publish-Release.ps1`
   header comment: "feature with UI surface ⇒ HelpContent entry in same commit".

Estimated: 1 test file (~5 tests), trivially fast.

---

## Sequencing & sizing

| Phase | Content | Size | Ships as |
|---|---|---|---|
| H1 | Stop the lies | ~15 key edits | one version bump |
| H2 | Wire affordances | ~20 XAML + 25 stubs | same or next bump |
| H3 | Tools + Detection deep docs | ~35 keys, 1 fix | own bump |
| H4 | Settings deep docs | ~45 keys | own bump |
| H5 | New-feature help | ~30 keys | own bump |
| H6 | About changelog + link | small code | own bump |
| H7 | Guardrail tests | 1 test file | with H1 or H2 |

H1+H2+H7 first (structure + honesty + the net that keeps it honest), then
content phases H3→H5 (H3 first per user emphasis), H6 anywhere.
All debug-build only (no MSI) per standing directive.

## Decisions needed from user

1. **Releases link target** — recommend GitHub CHANGELOG.md (works today).
2. **TenantsView.xaml** — delete (recommended) or resurrect.
3. **Registry key-level "Exists" default** — fix behavior (recommended) or
   document the limitation, pending reproduction.
4. Optional: whether the trivial picker dialogs really stay help-free
   (recommended: yes).
