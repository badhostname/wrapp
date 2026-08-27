# Wrapp — Cleanup Backlog

> Companion to [`CODEBASE_MAP.md`](./CODEBASE_MAP.md). Drives future cleanup cycles toward the
> principle: **unused code goes away; repetitive patterns become shared resources; folders/files
> stay easy to traverse.**
>
> **Generated:** 2026-06-23 from a full subsystem sweep. Dead-code items were **reference-verified**
> (`grep` across `src/Wrapp.GUI`); confidence is noted per item. Nothing here has been removed yet —
> this is the work list.

---

## A. Dead code (remove)

**Verified ✅ = only the declaration (and at most a doc `<see cref>`) exists in `src/`; no call site.**
Each is a small, low-risk deletion. Recommended order: delete, build, run the test suite.

| # | Symbol | File | Status | Note |
|---|---|---|---|---|
| 1 | `MsalAuthService.HasCachedTokenAsync()` | `Services/MsalAuthService.cs` | ✅ verified | VMs use `TryAcquireTokenSilent*` / `GetCachedAccountsAsync`. |
| 2 | `MsalAuthService.AcquireTokenForResourceAsync(string)` | `Services/MsalAuthService.cs` | ✅ verified | Generic resource-token path; DevOps now uses `DevOpsAuthService`. |
| 3 | `AppInventoryService.DownloadAppContentAsync(...)` | `Services/AppInventoryService.ContentDownload.cs` | ✅ verified | Live import uses `DownloadRawContentAsync`. CHANGELOG claims "preserved for full-clone" but that flow calls the Raw variant. |
| 4 | `AppInventoryService.GetGroupMemberCountAsync(...)` | `Services/AppInventoryService.Groups.cs` | ✅ verified | Counts come from `BatchFetchMemberCountsAsync` in bulk. |
| 5 | `AppInventoryService.ClearNestedGroupCache(tenantId)` | `Services/AppInventoryService.Groups.cs` | ✅ verified | Refresh clears the cache inline instead. |
| 6 | `AppInventoryService.ClearCache()` | `Services/AppInventoryService.cs` | ✅ verified | No `.ClearCache()` invocation anywhere. |
| 7 | `ScriptFrameworkProvider.GetWorkspaceScripts(fw)` | `Services/ScriptFrameworkProvider.cs` | ✅ verified | Thin alias of `GetBundleScripts`. |
| 8 | `ScriptFrameworkProvider.SupportsRepair(fw)` | `Services/ScriptFrameworkProvider.cs` | ✅ verified | Repair handled elsewhere; predicate unused. |
| 9 | `TemplateService.GetEmbeddedAssignments(TemplateInfo)` | `Services/TemplateService.cs` | ✅ verified | No caller. |
| 10 | `GitService.GetLastCommitInfoAsync(string)` | `Services/GitService.cs` | ✅ verified | "Last saved … by …" formatter has no caller. |
| 11 | `MonacoService.SetReadOnlyAsync(bool)` | `Services/MonacoService.cs` | ✅ verified | Diff uses `MonacoDiffService`; single editor never set read-only. |
| 12 | `EncryptionKeyStoreService.ApproveCurrentKeyVaultUrl()` | `Services/EncryptionKeyStoreService.cs` | ✅ verified | Only a doc `<see cref>` references it; `SettingsViewModel` assigns the hash via `ComputeKeyVaultUrlHash` directly. |
| 13 | `FileDialogService.SaveFile(filter, defaultName)` | `Services/FileDialogService.cs` | ✅ verified | Callers use `Microsoft.Win32.SaveFileDialog` inline. |
| 14 | `WindowHelper.GetHwnd(Window?)` | `Helpers/WindowHelper.cs` | ✅ verified | Only `GetMainWindowHwnd` / `PreventCloseWhile` are used. |
| 15 | `SecretProtection.TryEncrypt(string?, out string)` | `Models/AppSettings.cs` | ✅ verified | No external caller; live code uses `Encrypt`. |
| 16 | `PowerShellTokenBridge.InjectToken(Runspace, MsalTokenResult)` | `Services/PowerShellTokenBridge.cs` | ⚠️ likely | The single-`Runspace` overload; only the `RunspacePool` overload is called. Confirm no reflection use. |
| 17 | `SecretProtection.ResolveTenantSecret(string?, string?)` | `Models/AppSettings.cs` | ⚠️ likely | String overload; live callers use the `SecureString?` overload (Phase-15). Public API — confirm intent before removing. |

### Corrected false-positives (do **NOT** remove — verified live)

These were flagged by the sweep but reference-checking found real callers:

- `TempWorkspaceService.CreateAsync()` / `AcquireLock()` — **3 callers** (`SplashViewModel`,
  `MainViewModel`, `InventoryViewModel.Import`). This is the new-bundle workspace path.
- `IconService.FilesAreIdentical(a, b)` — called from `GeneralViewModel.InstallerHelpers.cs:135`.

> **Process note for future cycles:** always reference-verify a flagged symbol before deleting.
> A C#-only grep falsely flags XAML-bound members (converters, model `Is*Missing`/`ErrorCount`
> computed props, enums reached via string `Parse`). Those are **not** dead.

---

## B. Duplication → extract to shared resources

Ordered by value (impact × safety). Each names the copies and a suggested home.

### High value

1. **Monaco WebView2 bootstrap is triplicated.** `MonacoService`, `MonacoTabService`,
   `MonacoDiffService` each repeat identical `userDataFolder`, `--disable-gpu` options,
   `CreateAsync`/`EnsureCoreWebView2Async`, `monaco.local` virtual-host mapping, the
   `NavigationCompleted` handshake, `SetThemeAsync`, and the `LayoutAsync()` pair. The HTML
   `<head>`/create-options block is ~95% shared between the single + tab editors.
   → **Extract `MonacoHost`** (`Task<CoreWebView2> InitAsync(WebView2)`, shared
   `GetBaseEditorHtml(theme,bg)`, shared `SetThemeAsync`). *Largest real duplication in the repo.*

2. **PowerShell token-injection script/object duplicated 4×.** The `$Global:AccessToken` /
   `AuthenticationHeader` / `AccessTokenTenantID` param-block + `PSCustomObject` shape appears in
   `PowerShellTokenBridge.InjectToken`, `InjectMsalApp`, `PowerShellService.PackageAsync`, and
   `RunScriptWithTokenAsync`. → **Centralize** `BuildTokenInjectionScript()` +
   `BuildTokenPSObject(token)` in `PowerShellTokenBridge`.

3. **Field "required/missing" validation is split three ways.** Hand-coded `Is*Missing` /
   `ErrorCount` computed props (with repeated `OnXChanged → OnPropertyChanged(nameof(ErrorCount))`
   plumbing) on `IntunePackageEntry`/`SCCMPackageEntry`/`SCCMDeploymentEntry`/`AssignmentEntry`,
   **plus** the declarative `FieldDependencyRules`/`FieldStateProvider`/`FieldValidators`
   framework, **plus** inline URL/range checks (`IsInformationURLInvalid`,
   `IsMaxInstallTimeOutOfRange`) that duplicate `FieldValidators.ValidateUrl`/`ValidateInt`.
   → **Migrate the hand-written checks onto** `FieldDescriptor(Required: true)` +
   `FieldState.ValidationError`, driving `ErrorCount` from `FieldStateAccessor`.

4. **Entry projection/clone duplicated** between `SettingsService.SavePreferencesAsync`
   (`IntuneTenantEntry → SavedTenantEntry`, etc.) and `PreferencesSync.CloneTenant`/`CloneSite`
   (same field lists, same `DeploymentGroups` copy). Field-drift here risks **dropping a secret
   or tenant field on save**. → **Shared `TenantEntryMappings`** used by both.

### Medium value

5. **Token replacement `{{…}}` in 3 places** — `BundleService.ApplyTokens`,
   `TemplateService.ApplyTokens` (identical 10-token set), and the inline PSADT form. →
   `Helpers/TemplateTokens.Apply(content, app)`; both services delegate.

6. **File-signature sniffing (ZIP/MSI/EXE/CAB magic) in 3 places inside `IntuneWinService`**
   (`TryKeyQuick`, `ValidateDecryptedFile`, `DetectExtension`). →
   `FileSignature.Detect(ReadOnlySpan<byte>)`.

7. **`Sanitize` in 3 subtly-different forms** — `BundleService.Sanitize` (now strips `..`/`\`),
   `FileNameSanitizer.Sanitize`, `VaultPathTemplate.Sanitize`. The divergence is partly
   intentional (path-preserving vs single-component), but `VaultPathTemplate` could reuse
   `FileNameSanitizer` after its `..` strip. → Consolidate on `FileNameSanitizer` with a
   "preserve separators" option; document why any remaining variant exists.

8. **Dirty-tracking reimplemented 3×** — `GeneralViewModel` (timer), `SettingsViewModel`
   (`TakeSnapshot`/`CheckForChanges`, 750ms), `PreferencesViewModel` (`SerializeSnapshot`). →
   Shared `SnapshotDirtyTracker` (timer + serialize-and-compare).

9. **Per-tenant connection/token logic in 3 VMs** — `TenantsViewModel.TestConnectionAsync`,
   `RunViewModel.RefreshConnectionStatusAsync`/`SignInTenantAsync`,
   `InventoryViewModel.RefreshTargets`. The MSAL-acquire + expiry-compute kernel is common. →
   Shared `TenantConnectionService`.

10. **Status/enum → brush maps in ≥3 places** — `CommitFileChange` (frozen brush cache +
    hex `StatusColor`), `ConnectionStateToBrushConverter`/`PackageOutcomeToBrushConverter`
    (resource-key lookup), `DeploymentPlanRenderer`/`JobStepTreeRenderer` (own `TryBrush` +
    badge palette, with a comment admitting it's "duplicated from RunViewModel"),
    `HelpMarkdownRenderer.BadgeColors`. → Shared `StatusBrushes` (resource-key lookup + frozen
    fallback) + `WpfResourceHelpers.TryBrush`.

### Lower value / housekeeping

11. **`AtomicFile` body copy-pasted 3×** (`WriteAllText`/`WriteAllBytes`/`WriteAllTextAsync`) —
    extract a private `Commit(path, writeTemp)`. Same-file, trivial.
12. **`PreferencesSync` AddMissing/Overwrite shape repeated 3×** (tenants/sites/domains) —
    generic `AddMissing<T>(target, source, keySelector, clone)`.
13. **MSI P/Invoke open→view→fetch boilerplate 3×** in `MsiPropertyService` — private
    `RunScalarQuery(path, openMode, query)`.
14. **`MsalAuthService` "match cached account by tenant, else first"** copied across 3–4 acquire
    methods — private `SelectAccountForTenant(IEnumerable<IAccount>)`.
15. **`TemplateService` private `Str(node,key)`** duplicates `JsonObjectExtensions.Str` — delegate.
16. **`JsonObjectExtensions` vs `JsonElementExtensions` naming drift** (`Str/Int/Bool` vs
    `GetStringOr/GetIntOr/GetBoolOr`; `Object` lacks `Int64`, `Element` lacks `EnumOr`/`StrArray`).
    Can't merge (distinct BCL types) but should be **named consistently** and co-located under
    `Helpers/Json/`.
17. **`HelpMarkdownRenderer` repeats a 15-line `using`-alias block** across its 4 partials →
    move to a `<Using Alias>` in the csproj or `GlobalUsings`.

---

## C. Structural / traversal notes

- **`SecretProtection` lives inside `Models/AppSettings.cs`.** It's a static crypto toolbox, not a
  model. Consider moving it to `Services/Infrastructure/SecretProtection.cs` for discoverability
  (it's security-critical and currently easy to miss).
- **`ConfigFileService.Serializers.cs` ends with a malformed/truncated doc-comment block**
  (a `<summary>` with no method following). Not dead code — a documentation defect to fix.
- **Stale doc:** `src/Wrapp.GUI/docs/codebase-audit.md` describes the old `IntunePackager` layout
  and is superseded by [`CODEBASE_MAP.md`](./CODEBASE_MAP.md). Delete or mark as archived.
- **`TargetCheckItem`** is declared in the `Wrapp.ViewModels` namespace but lives under `Models/` —
  pick one (it's a UI-only VM helper; `ViewModels/` fits better).

---

## D. Suggested cycle order

1. **Cycle 1 — dead code (Section A).** Mechanical, low-risk; delete items 1–15, build, run tests.
   Resolve ⚠️ 16–17 first.
2. **Cycle 2 — high-value extractions (B1–B4).** Each is a focused PR with its own tests
   (Monaco host, PS token script, field-validation migration, entry mapping).
3. **Cycle 3 — medium dedup (B5–B10)** and **structural moves (C).**
4. **Cycle 4 — housekeeping (B11–B17).**

Every cycle ends green against `dotnet test tests/Wrapp.GUI.Tests` (current baseline: 580+ passing).
