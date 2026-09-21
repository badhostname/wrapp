# Enterprise Policy & Theming Plan

**Status:** PLANNED (research + audit complete, no implementation yet)
**Date:** 2026-08-24
**Scope:** Two workstreams that make Wrapp enterprise-ready: (1) an administrator
policy/lockdown layer (ADMX + registry + offline script) over settings, and
(2) custom importable themes backed by a full theme-integrity cleanup.

This plan is grounded in two full code audits (settings/org-defaults
architecture; theme/hardcoded-value sweep) summarized inline. File:line
references are as of commit `d4e06df` (0.6.331-beta).

---

## Part 0 - Audit findings that drive the design

### Settings architecture (what exists)

- `AppSettings` is a flat POCO: 34 top-level properties + 6 nested default
  blocks (57 leaves) + 4 list properties. One singleton instance registered in
  `CompositionRoot` (`CompositionRoot.cs:48`); every consumer reads fields
  directly. No per-key accessor, no change events, no schema version.
- `SettingsService.Load()` runs at `App.xaml.cs:348`; everything downstream
  (theme, trace, placeholders, composition root, gates) reads the same object.
  **One overwrite immediately after Load propagates everywhere.**
- Org defaults (`defaults.local.json`, searched exe-dir → install root →
  `%LOCALAPPDATA%\Wrapp` → `%ProgramData%\Wrapp` → src) seed **once per
  profile** (`OrgDefaultsSeeded` latch) and only onto factory-default values -
  a user edit always wins (`OrgDefaultsSeeder.SeedString/SeedBlock`).
- `PlatformConfig.Resolve` already implements a precedence chain
  (env var → appsettings.json → default) and its doc comment names GPO as the
  intended enterprise channel for the env-var tier.
- Existing lock-adjacent machinery to reuse:
  - `FieldStateProvider`/`FieldStateAccessor` - per-field `IsEnabled` /
    `IsVisible` / `DisabledReason`, already bound at 13 sites in SettingsView,
    with `ToolTipService.ShowOnDisabled` wired app-wide.
  - `IFeatureGate`/`FeatureGateService` - "is this feature allowed" +
    `DescribeWhyDisabled` + a `NotifyChanged` rebroadcast channel.
  - `SettingsPortability.StrippedProperties` - the exact list of
    never-portable properties (trust tokens, gate state, secrets).
- Three hazards a policy layer must handle:
  1. `SettingsPortability.ApplyImported` is an **unfiltered reflection
     overwrite** of every writable property (import must not clobber policy).
  2. TOFU trust tokens (`UpdateFeedTrustToken`, `KeyVaultRepoUrlHash`) are
     per-user DPAPI - **cannot be pre-provisioned**; a mandated feed URL still
     prompts every new profile unless policy explicitly bypasses TOFU.
  3. `AppSettings` has no `INotifyPropertyChanged` - live policy refresh needs
     its own broadcast (FeatureGate's channel is the precedent).
- Registry usage today: read-only detection-rule browser only. House style is
  `RegistryKey.OpenBaseKey(hive, RegistryView.Registry64)`
  (`RegistryTreeNode.cs:67`) - reuse verbatim so a 32-bit publish reads the
  same keys.

### Theme architecture (what exists)

- `Themes/Dark.xaml` + `Themes/Light.xaml`: **141 keys each, perfectly
  symmetric** (verified zero asymmetries). Only 5 non-brush resources
  (`PopupShadow` effect + 4 accent `Color`s).
- Live switching works: 954 of 955 theme-brush references are
  `DynamicResource` (single leak: `MainWindow.xaml:313` `PopupShadow` via
  StaticResource). Dictionary swap is clean remove-then-add; Monaco re-themes
  via `App.ThemeChanged`.
- **Custom themes are impossible today**: `App.ApplyTheme` is literally
  `themeName == "Light" ? "Light" : "Dark"` with a hardcoded accent ternary
  (`App.xaml.cs:221/228`), a pack-URI-only load path, and
  `ThemeOptions = new[] { "Dark", "Light" }`.
- **~175 genuine hardcoded color violations** (121 XAML + ~95 C#, minus
  legitimate cases like the color-picker spectrum). Concentrated:
  InventoryView (28), MainWindow (19, incl. a dark-only amber update bar),
  DeploymentPlanRenderer (17, has a resource helper it bypasses),
  HelpMarkdownRenderer badge palette (12), CommitFileChange frozen static
  brushes (11, architecturally untheme-able), the same drag-overlay block
  copy-pasted into 4 views, `RunViewModel` `Brushes.White` log lines
  (white-on-white in Light).
- Missing semantic keys for recurring literals: success-foreground, code-hint
  teal, link blue, magenta/purple/slate badges, notification-bar trio,
  drag-overlay, and the 10-color help-badge palette.
- Branding is compile-time: accent hex lives in **four** disconnected places
  (both theme files, `App.xaml.cs:228`, `Publish-Release.ps1:167`) plus
  palette constants inside `Build-InstallSplash.ps1` / `Build-MsiArt.ps1`.
  Rebranding today = edit source + rebuild.

### Prior art (pattern we follow)

Chromium/Edge/Firefox: the app **never talks to AD/Intune** - it reads plain
registry values under `Software\Policies\<Vendor>\<App>`. ADMX/ADML is only
the Group Policy authoring surface; domain GPO, Intune ADMX ingestion, and an
offline script all write the **same keys**. Conventions adopted:

- `HKLM` mandatory > `HKCU` mandatory > `Recommended` subkey (default the
  user may change).
- **A mandatory policy value implies the lock** - enforced value + disabled
  control + "Managed by your organization" affordance. No separate lock flags
  to drift.
- A transparency surface listing effective policies (`chrome://policy` →
  Wrapp's existing *Effective configuration* card).
- Firefox's `policies.json` as a file-based authoring format - here consumed
  by the offline script, not read at runtime (registry stays the single
  runtime source of truth; see Decision D2).

---

## Part 1 - Managed policy layer

### 1.1 Registry contract

```
HKLM\SOFTWARE\Policies\Wrapp          ← machine mandatory (wins)
HKCU\SOFTWARE\Policies\Wrapp          ← user mandatory
HKLM\SOFTWARE\Policies\Wrapp\Recommended   ← machine recommended
HKCU\SOFTWARE\Policies\Wrapp\Recommended   ← user recommended
```

Value naming = `AppSettings` property names verbatim (`UpdateFeedUrl` REG_SZ,
`EnableAzureDevOpsKeyVault` REG_DWORD…). Nested blocks = subkey per block,
value per leaf (`…\Wrapp\IntunePackageDefaults\InstallExperience`) - finer
than the org-seeder's all-or-nothing blocks, deliberately: an admin mandates
`InstallExperience=system` without freezing the timeout.

Meta-policies (no AppSettings counterpart):

| Value | Type | Effect |
|---|---|---|
| `OrgDefaultsPath` | REG_SZ | Prepended to `DefaultsLoader.CandidatePaths()` - policy-controlled path/filename for the org defaults JSON |
| `ThemeFilePath` | REG_SZ | Org theme JSON to load (Part 2) |
| `DisableSettingsImport` | DWORD | Hides/blocks the Export/Import settings card |
| `DisableOrgDefaultsImport` | DWORD | Hides/blocks the org-defaults Import card |
| `HiddenSections\<Name>` = 1 | subkey values | Hide nav sections (names = `NavigationSection` enum: Inventory, Tools, GitHistory, …) |
| `HiddenSettingsTabs\<Name>` = 1 | subkey values | Hide Settings tabs (KeyVault, Updates, Provisioning, …) |
| `HiddenSettings\<Key>` = 1 | subkey values | Hide an individual setting control entirely |

**Lock vs hide semantics (explicit requirement):**
- Mandatory value present ⇒ control **visible but disabled**, value enforced,
  padlock glyph + "Managed by your organization" tooltip (reuses the
  `FieldLabelRow` padlock + `DisabledReason` pattern).
- `HiddenSettings`/`HiddenSettingsTabs`/`HiddenSections` ⇒ control/tab/section
  **not rendered at all**. Both can combine (hidden + mandated).

**Never policy-controllable** (excluded in the catalog, enforced by test):
`KeyVaultRepoUrlHash`, `UpdateFeedTrustToken`, `GateState`,
`OrgDefaultsSeeded`, `LastRunVersion`, `LastSeenChangelogVersion`,
`TenantNameCache`, any `ClientSecret`. This is exactly
`SettingsPortability.StrippedProperties` - reuse it as the single exclusion
source.

**Lists** (tenants/sites/domains/placeholders) are *not* per-value policies.
They flow through `OrgDefaultsPath` → the existing `DefaultsLoader`/seeder
pipeline, which already models exactly these lists. One path policy replaces
dozens of unmaintainable list policies.

### 1.2 New components

```
src/Wrapp.GUI/Services/Policy/
  PolicyCatalog.cs      ← declarative: Key, RegistryName, Kind (String/Bool/Int/Enum),
                          SettingsPath, Category, Tier, AdmxDisplayName, AdmxExplain
  IPolicyStore.cs       ← read abstraction (GetValue, EnumerateValues, subkeys)
  RegistryPolicyStore.cs← OpenBaseKey(hive, Registry64); reads all 4 roots
  InMemoryPolicyStore.cs← test double (no HKLM writes in tests)
  PolicyService.cs      ← builds PolicySnapshot at startup; query API
  PolicySnapshot.cs     ← Mandatory{key→value}, Recommended{…}, HiddenSections,
                          HiddenSettingsTabs, HiddenSettings, OrgDefaultsPath,
                          ThemeFilePath, AnyManaged, SourceOf(key)
```

`PolicyService` query surface (all string-keyed by catalog key):

```csharp
bool   IsManaged(string key);          // mandatory value present
bool   IsHidden(string key);           // HiddenSettings / tab / section
object? MandatedValue(string key);
string ManagedReason(string key);      // "Managed by your organization (machine policy)"
bool   IsSectionHidden(NavigationSection s);
```

The catalog is the **single source of truth**: ADMX/ADML, the offline script's
schema, the docs table, and the runtime reader are all generated from or
validated against it. A contract test asserts:
- every catalog `SettingsPath` resolves to a real `AppSettings` property of
  the declared kind;
- no catalog entry names an excluded property;
- generated ADMX contains exactly the catalog (see 1.5).

### 1.3 Application points (from the seam analysis)

1. **Startup overlay** - `App.xaml.cs`, immediately after
   `SettingsService.Load()` (line 348):

   ```csharp
   var policy = PolicyService.Initialize();           // reads registry once
   policy.ApplyRecommended(settings);                 // SeedString semantics: only onto factory values
   policy.ApplyMandatory(settings);                   // unconditional overwrite
   ```

   Mandatory values **write through** to `settings.json` on the next save.
   (Deliberate divergence from Chrome's never-persist model: the existing
   dirty-diff tracker serializes the live POCO against the disk snapshot every
   750 ms - an in-memory-only overlay would flag permanent phantom changes.
   Write-through also means a lifted policy leaves the last org value in
   place, which is predictable. Documented as Decision D1.)

2. **Org-defaults interplay** - `OrgDefaultsSeeder.Apply` skips
   policy-managed keys (precedence: policy > user edit > org file > factory).
   `OrgDefaultsPath` policy prepends to `DefaultsLoader.CandidatePaths()`.

3. **Re-assertion at the three POCO write paths** (audit Seam 2):
   - `SettingsViewModel.SaveAsync` → `policy.ApplyMandatory(_settings)` before
     `SettingsService.Save` (belt; UI already disables locked fields).
   - `SettingsPortability.ApplyImported` → after the reflection loop,
     re-assert mandatory values and skip import of hidden/locked keys
     (closes hazard 1).
   - `OrgDefaultsSeeder` per item 2.

4. **Feature gate** - `FeatureGateService.IsEnabled` consults
   `PolicyService` for feature-level kills (`EnableAzureDevOpsKeyVault`
   mandated off, sections hidden). Reuses its existing `DescribeWhyDisabled`
   and `NotifyChanged` plumbing.

5. **Refresh model: restart-to-apply (v1).** Policy is read once at startup.
   `gpupdate` + app restart applies changes - the Chromium model for
   non-dynamic policies, and honest given `AppSettings` has no change
   notification (hazard 3). A `RegNotifyChangeKeyValue` watcher that shows a
   "Policy changed - restart Wrapp to apply" banner is a v2 nicety, not v1.

### 1.4 UI

- **`PolicyUiState` accessor on `SettingsViewModel`** (indexer, mirroring the
  `FieldStates[...]` XAML idiom):
  `IsEnabled="{Binding Policy[UpdateMode].IsEditable}"`,
  `Visibility="{Binding Policy[UpdateMode].Visibility}"`,
  `ToolTip="{Binding Policy[UpdateMode].Reason}"`.
  Applied to: the Updates card, Key Vault card, Endpoint card, Bundle card,
  the six preference blocks, theme picker, and the Provisioning import cards.
  Locked field affordance = existing `FieldLabelRow` padlock (`E72E`) +
  tooltip; `ShowOnDisabled` is already wired globally.
- **"Managed by your organization" banner** at the top of SettingsView when
  `AnyManaged` (building glyph `EC02` / `E821`, `InputBgBrush` pill -
  consistent with existing banner styles).
- **Effective-policy card** - extend the existing *Effective configuration*
  card (Provisioning tab) with a policy table: key, effective value, source
  (Machine / User / Recommended / not set). This is Wrapp's `chrome://policy`.
- **Nav hiding** - the 12 `NavItem` RadioButtons in `MainWindow.xaml` gain
  `Visibility` bound to `MainViewModel.SectionVisibility[...]`;
  `GetOrCreatePage` refuses hidden sections; if the current section becomes
  hidden at startup, navigate to General. Hidden ≠ disabled: policy-hidden
  sections are simply absent.
- **Settings tab hiding** - same binding on the 10 `TabItem`s.

### 1.5 ADMX / ADML + generation

- `policy/Wrapp.admx` + `policy/en-US/Wrapp.adml`, generated by
  `scripts/Build-Admx.ps1` from a JSON export of `PolicyCatalog` (a unit test
  regenerates and diffs, so catalog↔ADMX drift fails the build - same
  contract-test pattern as `HelpKeyReferenceTests`).
- Categories: `Wrapp` → Updates / Key Vault / Endpoints / Defaults (Intune) /
  Defaults (SCCM) / Provisioning / Appearance / Interface.
- Policy classes: `Machine` and `User` both, matching the two hives.
  Recommended variants under a "Recommended" child category writing to
  `\Recommended`.
- Element mapping: strings → `text`, bools → checkbox (`enabledValue` /
  `disabledValue` DWORD), enums (`UpdateMode`, `Theme`, intents…) →
  `dropdownList` with the exact string values, section/tab hiding →
  `list` elements writing the `Hidden*` subkeys.
- Distribution: ship `policy/` in the repo + release asset; document both
  Central Store deployment (`\\domain\SYSVOL\...\PolicyDefinitions`) and
  Intune ADMX ingestion.

### 1.6 Offline application (no-connectivity fleets)

`scripts/Apply-WrappPolicy.ps1` (+ `policy/policies.sample.json` +
`policy/policies.schema.json`):

```
Apply-WrappPolicy.ps1 -PolicyFile policies.json [-Scope Machine|User]
Apply-WrappPolicy.ps1 -Export effective.json        # dump current registry policy
Apply-WrappPolicy.ps1 -Clear [-Scope ...]           # remove all Wrapp policy values
```

- Validates the JSON against the schema (generated from the catalog), then
  writes **the same registry values** GPO would - one runtime code path, two
  provisioning paths. Requires elevation for Machine scope; idempotent;
  logs a transcript.
- Deployment vectors: SCCM package / Intune Win32 app / MDT task sequence /
  image bake / manual elevated run - precisely the tooling Wrapp's audience
  already operates.
- Runtime does **not** read `policies.json` directly (Decision D2): ProgramData
  ACLs are weaker than `HKLM\Software\Policies` (admin-only by OS default),
  and one source of truth beats two.

### 1.7 Security decisions (explicit, not slipped in)

- **D1 - write-through persistence** of mandated values (see 1.3.1).
- **D2 - registry is the only runtime policy source**; files are script input.
- **D3 - machine-policy TOFU bypass.** A feed/vault URL mandated from **HKLM**
  skips the per-user TOFU approval gate:
  `IsFeedTrusted(url, token) || Policy.IsMachineMandated("UpdateFeedUrl", url)`.
  Rationale: writing HKLM requires local admin - a strictly stronger authority
  than the per-user DPAPI blob TOFU protects. **HKCU-mandated URLs do NOT
  bypass** (HKCU is writable by the user's own processes; bypassing there
  would let malware self-approve a feed). Gate dialogs show "approved by
  organization policy" instead of prompting.
- **D4 - close the Key Vault URL validation gap** found in audit:
  `KeyVaultRepoUrl` gets an https-scheme + host-shape validator (today only
  pattern-matched), applied to user input, org seed, and policy alike.
- **D5 - theme files are data, not code**: JSON colors only, never XAML
  (`XamlReader` on arbitrary files is an `ObjectDataProvider` code-execution
  vector). Schema-validated against the key catalog.

### 1.8 Policy surface tiers (initial ADMX content)

- **Tier 1 (security, ship first):** `UpdateFeedUrl`, `UpdateMode`,
  `KeyVaultRepoUrl`, `EnableAzureDevOpsKeyVault`, `KeyVaultPathTemplate`,
  `KeyVaultManualPathTemplate`, `KeyVaultUsePullRequests`,
  `DisableSettingsImport`, `DisableOrgDefaultsImport`, `OrgDefaultsPath`.
- **Tier 2 (operational):** `EndpointTagFolder`, `EndpointLocalAppFolder`,
  `PsadtTemplatePath`, `DirectoryFormat`, `IconFolderName`, the six default
  blocks (57 leaves), `VerboseUiTrace`, `HiddenSections`, `HiddenSettingsTabs`.
- **Tier 3 (appearance):** `Theme`, `ThemeFilePath`.

---

## Part 2 - Custom themes + theme integrity

### 2.1 Theme file format (JSON, not XAML - Decision D5)

`*.wrapptheme.json`:

```json
{
  "$schema": "https://…/wrapptheme.schema.json",
  "Name": "Contoso Blue",
  "BaseTheme": "Dark",
  "MonacoTheme": "vs-dark",
  "Colors": {
    "AccentBrush": "#2D6BC4",
    "AccentHoverBrush": "#3F7DD6",
    "AppBgBrush": "#101418",
    "...": "any of the documented theme keys"
  },
  "ShadowOpacity": 0.45
}
```

- `BaseTheme` picks the compiled Dark/Light dictionary as the starting layer
  (and the Wpf.Ui `ApplicationTheme` hint); `Colors` overlays
  `SolidColorBrush`es for any subset of catalog keys. Unknown keys → rejected
  with a named error; unparsable colors → rejected. Frozen brushes.
- Discovery: `%LOCALAPPDATA%\Wrapp\Themes\*.wrapptheme.json` (user imports) +
  `ThemeFilePath` policy (org). Import button copies + validates (same
  pattern as org-defaults import).

### 2.2 Engine changes

- New `Services/ThemeService.cs` extracted from `App.ApplyTheme`:
  - `IReadOnlyList<ThemeChoice> Available()` - Dark, Light, discovered customs.
  - `Apply(ThemeChoice)` - builds merged dictionary (base + overlay), swaps in
    `MergedDictionaries`, applies accent **read from the resulting
    dictionary's `AccentBrush`** (kills the `App.xaml.cs:228` ternary - one of
    four accent hardcodes), raises `ThemeChanged` with the Monaco theme.
  - `SettingsViewModel.ThemeOptions` becomes `ThemeService.Available()`;
    `AppSettings.Theme` stores name or custom-file stem; unknown value falls
    back to Dark with a log line (today: silent).
- `MainWindow.xaml:313` `PopupShadow` StaticResource → DynamicResource
  (the single live-switch leak).

### 2.3 Key catalog completion (new keys, added to BOTH dictionaries)

`SuccessFgBrush` (#6FD46F/dark-appropriate light value), `CodeHintBrush`
(#4EC9B0), `LinkBrush` (#569CD6), `MagentaBadgeBrush`, `PurpleBadgeBrush`,
`SlateBadgeBrush`, `NotificationBarBgBrush`/`NotificationBarFgBrush`/
`NotificationBarAccentBrush` (the MainWindow amber trio - needs a designed
light variant), `DragOverlayBrush`, `DragOverlayFgBrush`, and 10
`HelpBadge<Color>Brush` keys replacing the literal palette in
`HelpMarkdownRenderer`.

**New contract test `ThemeParityTests`:** loads both dictionaries, asserts
identical key sets and that every key the catalog documents exists - pins the
audit's healthiest finding (perfect symmetry) forever.

### 2.4 Hardcoded-value cleanup (the ~175)

Ordered by audit's worst-offenders table; each step is mechanical
`#hex`/`White` → `{DynamicResource <key>}` or `TryFindResource` swaps:

1. **C1 - XAML views (121 hits):** InventoryView pills (28 - the enum→brush
   converters that already do this correctly exist in `Converters.cs`; the
   DataTemplates simply bypass them), MainWindow (19), ToolsView (10),
   Intune/SCCM/GeneralView drag overlays (the same block copy-pasted 4× -
   extract one shared `DragOverlayControl`, which also retires the
   Job of keeping four copies in sync), SettingsView code hints (6),
   LogsView line styles (4), DetectionView, Splash, Git views, dialogs.
2. **C2 - C# renderers (~54 real hits):** `DeploymentPlanRenderer` badge
   palette → resource keys via its own existing helper;
   `HelpMarkdownRenderer(.Inlines)` badge palette → the new `HelpBadge*` keys
   (keeps hex passthrough for explicit `[badge:Text:#hex]`);
   `CommitFileChange` frozen static brushes → status→key converter (frozen
   statics can never re-theme); `RunViewModel` log-severity `Brushes.*` →
   `ErrorBrush`/`WarningBrush`/`TextPrimaryBrush`/`TextSecondaryBrush`
   lookups (fixes white-on-white in Light today).
3. **C3 - guard test `ThemeHardcodeLintTests`:** scans all non-`Themes/` XAML
   for color literals in paint attributes, with an explicit allowlist
   (color-picker spectrum, `DisabledHatchBrush`, `Transparent`). The cleanup
   becomes a ratchet: new hardcodes fail the build.

Explicitly allowed to stay literal: ColorPickerPanel hue spectrum,
`IconTileRenderer` PNG palettes (renders image assets, not UI), documented
fallbacks after `TryFindResource(...) ?? Brushes.X`.

### 2.5 Theme Studio - live in-app theme editor (second half of Phase P4)

A modeless owned tool window (`SaveTemplateWindow` shell pattern) for
creating/editing `.wrapptheme.json` files. Core insight: since 954/955 brush
references are `DynamicResource` and switching is a dictionary swap, **the
running app is the live preview** - no preview pane; the editor re-applies an
overlay (debounced ~80 ms) and every open view repaints while the user drags.

- **Left pane:** a color card per theme key (swatch + friendly name + hex +
  WCAG grade), grouped by the audit's semantic groups; the 52 Wpf.Ui control
  internals collapse under *Advanced*.
- **Right pane:** the icon creator's existing `ColorPickerPanel` (spectrum +
  hue + opacity) reused as-is, plus a hex field; fixes its hardcoded
  `#9AC9CF` DarknessStart while integrating. A component-samples strip covers
  states the app may not currently show (pressed buttons, both badges).
- **Derived shades:** `ThemeKeyCatalog` gains a derivation rule per linked
  key (hover/pressed/soft = HSL offsets from a parent). Editing `AccentBrush`
  recomputes its family; overriding any card detaches it. ~20 real decisions
  instead of 141; exports stay sparse (decided keys only).
- **Contrast guardrails:** catalog-declared contrast pairs (text/bg,
  accent-fg/accent, badge fg/bg, log text/log bg) graded live and re-checked
  on theme import (warnings, not blocks).
- **Mechanics:** `ThemeService.Preview(overlay)` / `EndPreview()` (close
  without save = revert); Save serializes sparse JSON to
  `%LOCALAPPDATA%\Wrapp\Themes\`, sets `AppSettings.Theme`, refreshes the
  picker. New-from Dark/Light/current; Import/Export use the schema-validated
  format the `ThemeFilePath` policy distributes. Policy-mandated `Theme`
  opens the Studio read-only with the managed banner.
- **Entry points:** Settings → General "Create custom theme…" beside the
  picker; Edit/Duplicate/Delete on custom themes.
- **Tests:** catalog completeness vs. both dictionaries, derivation math,
  sparse round-trip, contrast calculator.

Note: the hardcode cleanup (2.4) matters more because of the Studio - every
un-tokenized color is a spot the live preview visibly fails to repaint,
turning remaining violations into bugs you can see.

### 2.6 Branding (stretch, Phase 5)

Move the scattered build-time palette into `branding.json`
(accent light/dark, splash card colors, MSI side-panel colors, wordmark
text, source PNG path); `Build-AppIcon/InstallSplash/MsiArt.ps1` and
`Publish-Release.ps1 --splashProgressColor` read it. Rebranding becomes:
replace one PNG + one JSON, re-run three scripts. Product name / assembly
rename stays a source-level change (documented, out of scope).

---

## Part 3 - Phasing, tests, risks

### Phases

| Phase | Content | Size |
|---|---|---|
| **P0** | Seams: `IPolicyStore` + catalog + `PolicyService` (read-only, logs what *would* apply), `ThemeService` extraction with identical behavior. No user-visible change. | S |
| **P1** | Policy application: startup overlay, seeder/import/save re-assertion, exclusion contract tests, D3 TOFU branch + D4 vault-URL validation. | M |
| **P2** | Policy UI: `Policy[...]` bindings across Settings cards, managed banner, effective-policy card, nav + tab hiding. Help docs. | M |
| **P3** | ADMX/ADML generation + drift test, `Apply-WrappPolicy.ps1` + schema + sample, admin guide (`docs/policy-admin-guide.md`). | M |
| **P4** | Themes: key catalog additions + parity test, JSON theme engine + import UI + `ThemeFilePath` policy, then cleanup C1→C2→C3 (can ship across several releases; C3 ratchet lands with the first batch). | L |
| **P5** | Branding `branding.json` build-script consolidation. Optional policy-change watcher banner. | S |

Each phase is releasable alone; P0–P2 make lockdown real for a pilot org
before ADMX exists (registry keys can be set by script from day one).

### Test plan

- `PolicyCatalogTests`: every entry maps to a real property; exclusion list
  honored; kinds match property types.
- `PolicyServiceTests` (InMemoryPolicyStore): precedence HKLM>HKCU>
  Recommended; mandatory overwrite vs recommended seed-only; hidden sets;
  `ApplyImported` re-assertion; seeder skip.
- `AdmxDriftTests`: regenerate ADMX from catalog, diff against committed file.
- `ThemeParityTests`, `ThemeHardcodeLintTests` (ratchet),
  `ThemeFileTests`: schema validation, unknown-key rejection, overlay
  correctness, accent extraction, fallback on missing/corrupt file.
- Existing suites must stay green - notably `SettingsPortability` round-trip
  and gate tests, which the TOFU branch touches.

### Risks / open questions

1. **Locked-value UX in child collections** (tenant grid cells) - v1 locks at
   card granularity for the list-bearing tabs (grid read-only when the org
   list is policy-fed); per-cell locks are follow-up.
2. **`SettingsPortability` import of a policy-locked profile** - decided:
   import proceeds, policy re-asserts, a summary line reports skipped keys.
3. **Light-theme values for new keys** (notification bar, success-fg) need a
   deliberate design pass, not just hex inversion.
4. **Recommended-tier semantics for blocks**: per-leaf recommended values use
   `SeedString` semantics per leaf, unlike the org file's all-or-nothing
   blocks - intentional, documented in the admin guide.
5. **Monaco/WebView2 pages** are out of theme scope v1 beyond the existing
   vs/vs-dark mapping (hardened host pages are off-limits per project
   constraints); a custom theme picks whichever base is closer.

### Definition of done (per the original ask)

- Admin can lock any Tier-1/2 setting (visible-but-disabled with managed
  tooltip) **and** hide menus/tabs/sections entirely - via GPO, Intune, or an
  offline elevated script writing identical registry keys.
- Org defaults JSON path/filename is policy-controllable.
- Custom themes: importable JSON, org-distributable via policy, safe by
  construction; Dark/Light untouched by default.
- No hardcoded visual value outside the documented allowlist; enforced by a
  ratchet test.
- ADMX/ADML generated from one catalog; drift breaks the build.
