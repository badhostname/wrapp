# Tooltip / documentation coverage report — all views, flows, and pop-ups

Audited: 2026-08-11 (post-0.6.318). ~560 interactive elements across 26 XAML
files + 11 code-built dialogs. Effective coverage at audit time: **~44%**
(210 of ~475 non-exempt elements).

## The propagation mechanism (why some inputs are covered "invisibly")

`App.xaml` attaches a Loaded handler to the `FieldLabel` TextBlock style;
`App.xaml.cs` copies the label's `Help.*` tooltip to the **first input control
in the label's logical parent**. Consequences found by the audit:

| Pattern | Input covered? |
|---|---|
| `FieldLabel` label + input as siblings | YES (auto) |
| Label wrapped in a horizontal "label + ⚠ Required" panel | **NO** — input is a sibling of the wrapper, not the label. *This was the reported App Name bug.* |
| `FieldLabelRow Help=...` | **NO** — tooltip landed on its internal TextBlock only, systematically (~40 inputs) |
| Non-`FieldLabel` plain labels (Tools, Settings Key Vault/Updates) | NO |
| Buttons | NEVER (not an input type) |
| Input with a `DisabledReason`-bound tooltip | Shows nothing while enabled |

**Structural fixes applied (0.6.319):**
1. `FieldLabelRow` now propagates its `Help` to the sibling input, same as
   plain labels (~46 inputs fixed in one file).
2. The propagation helper falls back to the grandparent — only at inputs
   *after* the label's own panel — fixing every "label + Required badge" row
   (App Name in Intune/SCCM, DT Name, Install Command, ...).
3. Bound tooltips are never clobbered; those inputs use
   `TargetNullValue={StaticResource Help.X}` so static help shows while the
   field is enabled and the dynamic reason shows while disabled.

## Summary counts (at audit time, before fixes)

| Bucket | Count |
|---|---:|
| Elements enumerated | ~560 |
| Covered — explicit `Help.*` on the element | 148 |
| Covered — auto label→input propagation | ~62 |
| Label-only (help existed, input got nothing) | ~46 |
| Hardcoded literals (drift risk, now keyed) | 17 |
| Uncovered, justified "none needed" | ~85 |
| Genuine gaps | ~201 |

"None needed" rules applied consistently: dialog Close/Cancel/OK buttons,
grid row-select checkboxes paired with a Remove button, search boxes carrying
placeholder text, read-only display/copy fields with an adjacent label and a
section-level help key, and picker cards whose dialog text explains them.

## Ranked gap clusters (fix order; 1–2 are the structural fixes above)

| # | Cluster | Elements | Resolution |
|---:|---|---:|---|
| 1 | FieldLabelRow no propagation | ~46 | FIXED structurally |
| 2 | Label+badge rows miss propagation | ~12 | FIXED structurally |
| 3 | ToolsView — zero tooltips | 41 | new `Help.Tools.*` field keys + wiring |
| 4 | Settings Preferences defaults — no per-field help | ~60 | mostly reuse existing Intune/SCCM keys |
| 5 | Add/Remove/Browse buttons on every card | ~42 | shared `Help.Common.Grid.Add/.Remove` + specific keys |
| 6 | Editable DataGrid cells inherit nothing from headers | ~36 | explicit cell/ElementStyle tooltips |
| 7 | Sidebar nav — 12 items, zero tooltips | 12 | `Help.Main.Nav.*` one-liners |
| 8 | DisabledReason bindings shadow static help | ~24 | `TargetNullValue` pattern |
| 9 | Key Vault + Updates tabs (non-FieldLabel labels) | 11 | `Help.Settings.KeyVault.*` / `.Updates.*` |
| 10 | Undocumented dialogs (NestedGroupBrowser, AppPicker, SaveTemplate, FileHistory Restore, ConfigJson Apply Changes, GitHistory double-click) | ~14 | overview keys + consequential-action tooltips |
| 11 | 17 hardcoded literals | 17 | moved into HelpContent keys |
| 12 | Context-menu items (Restore/Sync menus) | ~19 | Overwrite/AddMissing keys |

## Per-view findings (element → state → resolution)

Notation: AUTO = covered by propagation · LABEL-ONLY = fixed by structural
fix · LIT = literal converted to a key · NEW = key created in the wiring pass
· REUSE = wired to an existing key · NONE = deliberately no tooltip (reason).

### MainWindow
- Titlebar New/Open/Save/Save As, path buttons, jobs/about/action-needed: already keyed.
- Framework indicator → NEW `Help.Main.Toolbar.ScriptFramework`. Path text → NONE (tooltip is the full path).
- Account button (signed out) → NEW `Help.Main.Account.Button` via TargetNullValue.
- Account popup: Sign in/out, switch account, different account, DevOps sign in/out → NEW `Help.Main.Account.*`; token/claims/forget already keyed; id text → NONE (tooltip is the value).
- 12 sidebar nav items → NEW `Help.Main.Nav.{Section}`.
- Error badges → NEW `Help.Common.ErrorBadge`; Inventory match badge → `Help.Inventory.MatchBadge`; warning badges already keyed.
- Save strip: Save Bundle → REUSE toolbar key; Save Settings → NEW `Help.Settings.SaveSettings`.
- Job progress bar → NONE (non-interactive, status text adjacent).

### GeneralView
- All App Info inputs AUTO/explicit; icon Browse keyed.
- Browse installer → NEW `Help.General.AppInfo.BrowseInstaller`; Generate GUID → NEW `.GenerateGuid`.
- Detect Running: Add/Remove → NEW keys; three grid columns → NEW per-column keys; row-select → NONE.
- Framework read-only box → NONE (display; label tooltip suffices).

### IntuneView
- Toolbar + tenants panel largely keyed; save-template glyphs LIT → NEW keys; Restore menu items → NEW Overwrite/AddMissing keys.
- Tenants panel Name/TenantId/AuthFlow inputs → NEW keys.
- App Name input: LABEL-ONLY → fixed structurally. Icon path + Browse + Use App Icon → REUSE IconFile key + NEW UseAppIcon.
- Target Tenant combo → REUSE `Help.Intune.TargetTenants`.
- AzCopy Window Style combo → NEW key (only genuinely unkeyed behavior field).
- Categories/ScopeTags/ReturnCodes/Dependencies/Supersedence: Add/Remove buttons + grid columns → NEW keys (incl. ReturnCodes Code/Type, Dependencies AppName/AutoInstall, Supersedence AppName/Type); row-selects → NONE; package-list context menu → REUSE toolbar keys.

### SCCMView
- Mirror of IntuneView: App Name / DT Name / Install Command LABEL-ONLY → fixed structurally; sites panel inputs → NEW keys; save-template glyphs → NEW; Target Site combo → REUSE; Use App Icon → NEW; Install Behaviors / Dependencies / Supersedence buttons + columns → NEW keys.
- Logon Requirement (DisabledReason-bound) → TargetNullValue + existing key.

### IntuneAssignmentDialog
- The worst FieldLabelRow cluster: 14 inputs, all target keys pre-existing → fixed structurally + TargetNullValue where bound.
- Add Assignment / Template / Label / Save-as-template → NEW keys.

### SCCMDeploymentDialog
- Same shape: Collection input fixed structurally; Add/Template/Label/Save-as-template → NEW; Deadline hint binding → TargetNullValue; pickers AUTO.

### DetectionView
- Cells were LABEL-ONLY via headers (headers never reach cells): Name/Symbol/Path/Command/Property/Operator/Value cells → explicit REUSE of the header keys; lock glyphs LIT → NEW keys; Add Expression/Test, Remove tests → NEW; expression boxes → REUSE `Help.Detection.Expression` + NEW `.Key`.

### RunView
- Target radios → NEW `Help.Run.Target`; mode radios (Validate/Package/Full — the most consequential controls in the app) → NEW per-mode keys; Cancel → NEW; Start/enable-toggles/log already keyed; progress grid/log list → NONE (read-only, section help covers).

### ToolsView (was zero tooltips)
- Every input/button keyed: decrypt path/browse/key-source radios/key/IV/vault ids/CSV/Decrypt button; inspect path, key-material group, vault filename, Copy Keys, Save to Vault (REUSE `Help.Tools.Vault`); batch folder/Scan/Recursive/Status column/Vault Name/Save to Vault. Read-only inspect rows and self-describing columns → NONE; batch Cancel → NONE (enabled only while scanning).

### SettingsView
- General/Bundle inputs AUTO; PSADT Browse → REUSE path key; Reset buttons keyed.
- Domains/Tenants/Sites grid columns + Add buttons → NEW per-column keys; Pull-from-bundle LIT → NEW keys; Client Secret/Cert Thumbprint (bound) → TargetNullValue + NEW keys.
- Endpoint fields → NEW keys (lifted from inline text).
- Package/metadata/assignment/deployment defaults (~60 fields) → REUSE the matching per-field Intune/SCCM keys; assignment FieldLabelRows get Help=; restart-grace fields TargetNullValue.
- Key Vault (8 fields) + Updates (4 controls) → NEW `Help.Settings.KeyVault.*` / `.Updates.*` keys, literals lifted.
- Defaults tab buttons → REUSE OrgImport/Portability keys. Nav TabItems → NONE (each tab's first card has section help).

### InventoryView
- Platform radios / target combo → NEW; filter popup groups (intent, assignments, architecture, min OS, size, relationships, clear) → NEW keys; search box → NONE (placeholder + help button); ~55 read-only detail boxes → NONE (adjacent labels + section keys); action buttons already keyed.

### RegistryBrowserDialog
- Path bar → NEW `Help.RegistryBrowser.PathBar` (the reported "path bars" pattern); search box → REUSE `.Search`; grids/tree → NONE (overview + instructions cover).

### NestedGroupBrowserDialog
- Was 100% help-free: overview SectionHeader + tree + `[Circular]` marker → NEW keys; read-only detail boxes → NONE.

### Small dialogs & windows
- SaveTemplateWindow: name + field-picker → NEW keys (unchecked-fields semantics are non-obvious); description/Save/Cancel → NONE.
- AppPickerDialog: overview → NEW (explains it lists apps in the target tenant/site); search/checkboxes → NONE.
- Icon/MsiIcon pickers, ActionPickerDialog → NONE (self-describing by design).
- DateTimePickerField: ASAP button → NEW (Intune sentinel vs SCCM current-UTC asymmetry); calendar → NEW (low); digit boxes/Set → NONE.
- SplashWindow: framework cards → NEW (choice is irreversible); bundle cards → NONE.
- LogsView: Clear → NEW (clears view, not file); filter box → NONE.
- ScriptsView: template combo/save glyphs/token glyph/tabs → NEW + REUSE TemplateTokens; Refresh Editor → NEW shared `Help.Common.RefreshEditor` (also ConfigJsonView).
- ConfigJsonView: Apply Changes → NEW (most consequential unlabelled button); Sync Domains menu items → NEW Overwrite/AddMissing.
- GitHistoryView: Refresh + double-click-to-diff → NEW; DiffWindow file chips → NEW; FileHistoryWindow Restore → NEW (destructive-adjacent).

### Code-built popups
- About, jobs flyout, collision dialog (renders `Help.Run.CollisionCheck`), plan dialog, claims viewers, What's-New, save prompts, exception dialog: covered or NONE-justified; jobs flyout info + Clear completed → NEW keys.

## Guardrails

`HelpKeyReferenceTests` enforces: every referenced key exists, every authored
key is reachable, no org values, operator/token lists match code. Any future
tooltip gap of the "dead key" or "orphan key" kind fails the build.
