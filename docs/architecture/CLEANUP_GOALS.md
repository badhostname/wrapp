# Wrapp — Cleanup Goal List

> The execution plan for the cleanup. Built from [`CLEANUP_BACKLOG.md`](./CLEANUP_BACKLOG.md)
> and [`CODEBASE_MAP.md`](./CODEBASE_MAP.md).
>
> **Generated:** 2026-06-23.

---

## 0. Prime directive (read before touching any goal)

> **Every goal here removes or replaces code. None may change observable behavior or weaken a
> security control.** Where the code is used, and *how* it is used, must be fully captured first
> and provably restored after — otherwise the goal is not done.

This is enforced by a fixed four-step protocol applied to **every** goal:

1. **CAPTURE (where + how).** Re-run the goal's *Usage probe* to list the current call sites and
   what each one relies on. The codebase moves between cycles — if reality differs from what this
   doc records, **stop and re-plan the goal**. Do not proceed on stale call-site assumptions.
2. **PIN (characterization).** Before a *refactor*, ensure a test pins the current behavior (use an
   existing test if one covers it; otherwise write a characterization test **first**, red→green,
   per the `tdd` discipline). The pinned behavior is the contract the replacement must reproduce.
3. **PRESERVE.** Satisfy the goal's **Preservation Contract** — the enumerated behaviors and
   security properties that must be identical afterward. Every original call site must be rewired to
   the replacement with byte-for-byte equivalent behavior. A removal is valid **only if usage == 0**.
4. **VERIFY.** `dotnet test tests/Wrapp.GUI.Tests` green (baseline 580+), solution builds, plus the
   goal-specific checks. For security-tagged 🔒 goals, the security check is a **gate**, not a nicety.

### Per-goal Definition of Done (checklist)

- [ ] Usage probe re-run; call sites match this doc (or goal re-planned)
- [ ] Behavior pinned by a test (existing or newly written) — for refactors
- [ ] Preservation Contract items all satisfied
- [ ] Every call site rewired (refactor) **or** usage confirmed zero (removal)
- [ ] 🔒 security check passed (where tagged)
- [ ] Build + full test suite green
- [ ] CHANGELOG entry added; BUILD bumped

**🔒 = security-sensitive. Treat the security note as a hard gate.**

---

## Group 1 — Dead-code removal

These have **no behavior to preserve** — the whole point is that nothing uses them. The discipline
here is the *restore safeguard*: **re-prove zero usage immediately before deleting.** If the probe
finds any caller, the item is not dead — stop and reclassify.

**Shared usage probe (run for each symbol; expect only the declaration, at most one doc `<see cref>`):**
```bash
grep -rn --include=*.cs --include=*.xaml "\bSYMBOL\b" src/Wrapp.GUI
```

### G1.1 — Remove unambiguously dead members (✅ verified, low risk) — ✅ DONE (0.6.0.0241)

Delete these 13; build; run tests. One commit, or one per file.

| Symbol | File |
|---|---|
| `MsalAuthService.HasCachedTokenAsync` | `Services/MsalAuthService.cs` |
| `AppInventoryService.DownloadAppContentAsync` | `Services/AppInventoryService.ContentDownload.cs` |
| `AppInventoryService.GetGroupMemberCountAsync` | `Services/AppInventoryService.Groups.cs` |
| `AppInventoryService.ClearNestedGroupCache` | `Services/AppInventoryService.Groups.cs` |
| `AppInventoryService.ClearCache` | `Services/AppInventoryService.cs` |
| `ScriptFrameworkProvider.GetWorkspaceScripts` | `Services/ScriptFrameworkProvider.cs` |
| `ScriptFrameworkProvider.SupportsRepair` | `Services/ScriptFrameworkProvider.cs` |
| `TemplateService.GetEmbeddedAssignments` | `Services/TemplateService.cs` |
| `GitService.GetLastCommitInfoAsync` | `Services/GitService.cs` |
| `MonacoService.SetReadOnlyAsync` | `Services/MonacoService.cs` |
| `WindowHelper.GetHwnd` | `Helpers/WindowHelper.cs` |
| `FileDialogService.SaveFile` | `Services/FileDialogService.cs` |
| `PowerShellTokenBridge.InjectToken(Runspace, …)` | `Services/PowerShellTokenBridge.cs` (re-confirm: keep the `RunspacePool` overload) |

**Preservation Contract:** none beyond "no caller exists." **Verify:** probe each → 0 callers;
build; tests green.

### G1.2 🔒 — Remove dead **security-adjacent** members (verify they are not a live control) — ✅ DONE (0.6.0.0241)

These touch auth/secrets/trust. Removing a security *control* that is still wired would silently
weaken the app, so the bar is higher: confirm not only "no caller" but "no behavior depends on it."

| Symbol | File | Why extra care |
|---|---|---|
| `MsalAuthService.AcquireTokenForResourceAsync` | `Services/MsalAuthService.cs` | Generic resource-token path. Confirm DevOps tokens flow only via `DevOpsAuthService` and nothing reflectively calls this. |
| `EncryptionKeyStoreService.ApproveCurrentKeyVaultUrl` | `Services/EncryptionKeyStoreService.cs` | This is the **TOFU approve** step. The only ref is a doc `<see cref>`; `SettingsViewModel` assigns the hash via `ComputeKeyVaultUrlHash`. **Confirm the TOFU approval still happens** (the hash is still written on user consent) before deleting the method — do not remove the approval *flow*, only the unused helper. |
| `SecretProtection.TryEncrypt` | `Models/AppSettings.cs` | Encryption helper. Confirm `Encrypt` (not `TryEncrypt`) is the live path everywhere secrets are sealed. |
| `SecretProtection.ResolveTenantSecret(string?, string?)` | `Models/AppSettings.cs` | The plaintext-string overload. Confirm every caller uses the `SecureString?` overload (Phase-15 hardening) so removing the string overload cannot push a caller back onto a managed-string secret path. ⚠️ public API — keep if any external/contract use is intended. |

**🔒 Security check (gate):** after removal, the TOFU URL-hash approval still occurs on consent;
secret sealing still uses DPAPI `Encrypt`; no secret is ever resolved through a managed `string`
path that the removal re-exposed. Existing `SecretProtectionTests` / `EncryptionKeyStoreUrlHashTests`
stay green.

---

## Group 2 — High-value behavior-preserving extractions

Each replaces N copies with one shared resource. The shared resource must be a **drop-in** for every
copy. Pin behavior first.

### G2.1 — Extract `MonacoHost` (de-triplicate WebView2 bootstrap) — ✅ DONE (0.6.0.0244, GUI-verified 2026-06-25)

- **Usage probe:** `grep -rn "EnsureCoreWebView2Async\|monaco.local\|userDataFolder" src/Wrapp.GUI/Services/Monaco*.cs`
- **Where/how used:** `MonacoService` (single editor → `ConfigJsonView`), `MonacoTabService`
  (tabbed → `ScriptsView`), `MonacoDiffService` (read-only diff → `DiffWindow`, `FileHistoryWindow`).
  Each repeats: `userDataFolder = %LOCALAPPDATA%\Wrapp\WebView2`, `--disable-gpu
  --disable-gpu-compositing`, `CreateAsync`/`EnsureCoreWebView2Async`, `monaco.local` virtual-host
  map, `NavigationCompleted` TaskCompletionSource handshake, `SetThemeAsync`, `LayoutAsync()`.
- **Preservation Contract:**
  - Identical `userDataFolder`, env options, and virtual-host mapping (changing the data folder
    orphans cached state; changing GPU flags can break rendering on headless/RDP).
  - The init handshake still completes before content is set (no race regressions).
  - Theme + layout behavior unchanged across all three consumers (single, tabbed, diff).
  - `ContentChanged` debounce timing preserved for the single/tab editors.
- **Pin:** add a smoke/characterization test or manual run of all three editors (Config.json edit,
  Scripts tabs, a diff window) — capture that each loads, themes, and round-trips content.
- **Verify:** all three editors load and edit; theme toggle works in each; build + tests green.

### G2.2 🔒 — Centralize PowerShell token injection (de-duplicate 4 copies) — ✅ DONE (0.6.0.0242)

- **Usage probe:** `grep -rn "Global:AccessToken\|AuthenticationHeader\|AccessTokenTenantID\|WrappMsalRefreshHandle" src/Wrapp.GUI/Services`
- **Where/how used:** `PowerShellTokenBridge.InjectToken` + `InjectMsalApp`,
  `PowerShellService.PackageAsync`, `PowerShellService.RunScriptWithTokenAsync`. Each builds the same
  `$Global:` variables + `PSCustomObject` token shape that **IntuneWin32App and Wrapp.Packager
  read by name**.
- **Preservation Contract (🔒):**
  - The exact set and **names** of injected globals (`$Global:AccessToken`,
    `$Global:AuthenticationHeader`, `$Global:AccessTokenTenantID`, `$Global:WrappMsalRefreshHandle`)
    and the `PSCustomObject` field names are byte-identical — the PowerShell side matches on these
    literally; a rename silently breaks auth.
  - The refresh handle remains an **opaque GUID** — the live `IPublicClientApplication` is never
    exposed to the runspace (S-3 hardening). Centralizing must not widen what the script can reach.
  - Injected globals are still cleared in the `finally` of `PackageAsync` (no token left resident in
    the pool after a run).
- **Pin:** characterization test asserting the generated injection script + token `PSObject` contain
  exactly those names/fields (string-shape assertion), and that the handle is a GUID, not an object.
- **🔒 Security check (gate):** a packaging run still authenticates; the runspace cannot dereference
  the MSAL client; globals are cleared post-run.
- **Verify:** build + tests; one real (or mocked) `PackageAsync` still injects + clears correctly.

### G2.3 🔒 — Unify tenant/site entry projection & clone (de-duplicate field maps) — ⚖️ RE-PLANNED (0.6.0.0243)

**CAPTURE outcome:** the two mappings are NOT a clean duplication. `SettingsService`
projects to a *different type* (`SavedTenantEntry`) and **DPAPI-encrypts** the secret
(`WithPlaintext → Encrypt`, else preserve cipher); `PreferencesSync.CloneTenant` clones to
the *same type* and **shares the `SecureString` reference** + carries the cipher, with no
encryption. A shared mapper would couple the encrypt-vs-share security boundary for ~10
scalar field names — a poor risk trade under the prime directive. (Also noted: both paths
omit `Domain` and `ScopeTags`, consistently — flagged for a separate deliberate decision,
not changed here.)

**Decision:** do NOT extract the cross-type mapper. Instead, the drift risk the goal targeted
(a field/secret silently dropped) is now guarded by `PreferencesSyncTests`, which pins exactly
which fields each clone carries — including `ClientSecretCipher` and the shared `SecureString`.
Zero production-code change; the security boundary is untouched.

---

#### Original plan (superseded by the decision above)

- **Usage probe:** `grep -rn "SavedTenantEntry\|SavedSiteEntry\|CloneTenant\|CloneSite" src/Wrapp.GUI`
- **Where/how used:** `SettingsService.SavePreferencesAsync` (`IntuneTenantEntry → SavedTenantEntry`,
  `SCCMSiteEntry → SavedSiteEntry`) and `PreferencesSync.CloneTenant`/`CloneSite` — duplicated
  field-by-field copies, including `DeploymentGroups` and the secret fields.
- **Preservation Contract (🔒):**
  - **Every** field copied today is still copied — especially `ClientSecret` (`SecureString`),
    `ClientSecretCipher`, `CertThumbprint`, `ScopeTags`, `DeploymentGroups`. A dropped field on the
    save path = **silent secret/config loss**. Enumerate the field list from both copies and diff it
    into the shared mapper.
  - The secret is still DPAPI-encrypted exactly where it is today (the mapper must not change *when*
    encryption happens — `SettingsService` encrypts on save; `PreferencesSync` clones in-memory).
  - No new code path causes a `SecureString` to be materialized into a managed `string`.
- **Pin:** characterization test that round-trips a fully-populated tenant (all fields incl. cipher +
  scope tags + a site with deployment groups) through save→load and asserts no field is lost.
- **🔒 Security check (gate):** secret persistence round-trip still works (reuse
  `TenantSecretPersistenceTests` + `SecretProtectionTests`); no plaintext widening.
- **Verify:** build + tests; field-coverage diff shows the shared mapper ⊇ both originals.

### G2.4 — Migrate hand-coded field validation onto the field-state framework — ⚖️ PARTIAL (0.6.0.0245)

**Done:** the inline URL validators (`IsInformationURLInvalid` / `IsPrivacyURLInvalid`) now
delegate to a single `FieldValidators.IsHttpUrlInvalid`; pinned by `UrlValidationTests`.

**Deferred (documented decision):** migrating the `Is*Missing` / `ErrorCount` required-field
indicators onto `FieldDescriptor(Required:true)` + `FieldStateProvider` is an architectural
re-org tightly bound to XAML (amber highlights, nav badges) with real binding-breakage risk
and only modest dedup benefit. Not worth a blind change under the prime directive; left for a
deliberate future pass with the app runnable.

---

#### Original plan (the deferred part)

- **Usage probe:** `grep -rn "Is.*Missing\|ErrorCount\|HasValidationWarning\|IsInformationURLInvalid\|IsMaxInstallTimeOutOfRange" src/Wrapp.GUI/Models`
- **Where/how used:** computed `Is*Missing`/`ErrorCount`/`HasValidationWarning` props +
  `OnXChanged → OnPropertyChanged(nameof(ErrorCount))` plumbing on `IntunePackageEntry`,
  `SCCMPackageEntry`, `SCCMDeploymentEntry`, `AssignmentEntry`; inline URL/range checks. These are
  **XAML-bound** (amber highlighting, nav badges).
- **Preservation Contract:**
  - Every validation currently surfaced in the UI still fires with the same trigger and result
    (required-field amber, error counts, nav-section badges). The migration target is
    `FieldDescriptor(Required: true)` + `FieldState.ValidationError`, with `ErrorCount` driven from
    `FieldStateAccessor`.
  - XAML bindings keep working — either keep the same property names as thin shims over the framework,
    or update every `{Binding Is*Missing}` / `{Binding ErrorCount}` in `.xaml` in lockstep
    (enumerate them in the probe).
- **Pin:** characterization tests for each entry's validation truth table (extend the existing
  `FieldStateProvider`/`FieldValidators` tests) before moving logic.
- **Verify:** build + tests; manual check that amber highlight + nav badges still appear/clear.

---

## Group 3 — Medium-value extractions

Same four-step protocol. Each: capture call sites → pin → preserve → verify.

- **G3.1 — `TemplateTokens.Apply(content, app)`** (de-dup `{{…}}` in `BundleService.ApplyTokens`,
  `TemplateService.ApplyTokens`, PSADT inline). *Preserve:* the exact 10-token set and replacement
  order; `ApplyTokensPublic` keeps working for `ScriptsViewModel`.
- **G3.2 — `FileSignature.Detect(ReadOnlySpan<byte>)`** (de-dup ZIP/MSI/EXE/CAB magic in
  `IntuneWinService.TryKeyQuick`/`ValidateDecryptedFile`/`DetectExtension`). *Preserve:* identical
  signature bytes and the "large file ⇒ assume valid" fallback in `ValidateDecryptedFile`.
- **G3.3 🔒 — Consolidate `Sanitize`** (`BundleService.Sanitize`, `FileNameSanitizer.Sanitize`,
  `VaultPathTemplate.Sanitize`). *Preserve (🔒):* **the `..` and path-separator stripping is a
  security fix** (path traversal) — the consolidated form must keep stripping `..`, `\`, `/` for
  token *values* while still letting `BundleService`'s directory-format template separators through.
  Pin with the existing `BundleServiceTests` traversal tests + `VaultPathTemplateTests` before
  merging; do not regress them.
- **G3.4 — `SnapshotDirtyTracker`** (de-dup dirty tracking in General/Settings/Preferences VMs).
  *Preserve:* same debounce intervals (750ms etc.) and the same "what counts as dirty" snapshot
  fields per VM.
- **G3.5 — `TenantConnectionService`** (de-dup per-tenant connect/token/expiry in Tenants/Run/
  Inventory VMs). *Preserve:* token acquisition + expiry-countdown semantics; per-tenant caching.
- **G3.6 — `StatusBrushes` + `WpfResourceHelpers.TryBrush`** (de-dup status→brush maps). *Preserve:*
  identical resource keys and frozen-brush fallback colors across all consumers and both themes.

---

## Group 4 — Housekeeping & structure

- **G4.1** — `AtomicFile`: private `Commit(path, writeTemp)` shared by the 3 write methods. *Preserve:*
  the temp-then-`File.Replace`-with-`.bak` torn-write guarantee (this is a data-safety control).
- **G4.2** — `PreferencesSync`: generic `AddMissing<T>`/`Overwrite<T>`. *Preserve:* skip-empty-key +
  clone semantics per type.
- **G4.3** — `MsiPropertyService`: private `RunScalarQuery`. *Preserve:* handle cleanup in `finally`.
- **G4.4** — `MsalAuthService`: private `SelectAccountForTenant`. *Preserve:* "match by tenant, else
  first" exactly (account-selection correctness affects which identity signs requests).
- **G4.5** — `TemplateService.Str` → delegate to `JsonObjectExtensions.Str`.
- **G4.6** — Co-locate + consistently name `JsonObjectExtensions`/`JsonElementExtensions` under
  `Helpers/Json/`. *Preserve:* both are widely called (172 config-parser sites) — keep method names
  or update every caller in lockstep.
- **G4.7 🔒** — Move `SecretProtection` out of `Models/AppSettings.cs` to
  `Services/Infrastructure/SecretProtection.cs`. *Preserve (🔒):* **keep the public type name and
  namespace** so every caller and the DPAPI behavior are unchanged — this is a pure file move, no
  logic edit. Tests (`SecretProtectionTests`) must stay green untouched.
- **G4.8** — Fix the truncated doc-comment block at the end of `ConfigFileService.Serializers.cs`.
- **G4.9** — Archive/delete the stale `src/Wrapp.GUI/docs/codebase-audit.md` (superseded by
  `CODEBASE_MAP.md`).
- **G4.10** — Move `TargetCheckItem` to match its `Wrapp.ViewModels` namespace (it's a UI helper).

---

## Recommended cycle order

1. **Cycle 1:** G1.1 then G1.2 (dead code; G1.2 behind its 🔒 gate).
2. **Cycle 2:** G2.1–G2.4 (one PR each, each with its pinned tests).
3. **Cycle 3:** G3.* + the structural moves G4.7–G4.10.
4. **Cycle 4:** G4.1–G4.6 housekeeping.

Each cycle ends green against `dotnet test tests/Wrapp.GUI.Tests` and adds a CHANGELOG entry.

---

## Why this protocol (the user's principle, restated)

> *"Each cleanup or refactor needs to ensure that where this code is being used and how it's being
> used is restored when we replace the code, so no functionality or security is lost."*

That is enforced by **CAPTURE → PIN → PRESERVE → VERIFY** on every goal: we never delete without
re-proving zero usage, and we never refactor without first pinning current behavior in a test and
then reproducing it exactly at every call site — with security properties treated as hard gates.
