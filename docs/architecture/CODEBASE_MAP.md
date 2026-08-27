# Wrapp — Codebase Map

> **Purpose:** A navigable reference to the main folders, files, types, and methods of
> `Wrapp.GUI`, and how they relate. Written to be read without deep prior knowledge of the
> codebase, and to anchor future cleanup cycles (dead-code removal, de-duplication).
>
> **Generated:** 2026-06-23. Supersedes the stale `src/Wrapp.GUI/docs/codebase-audit.md`
> (dated 2026-03-11, describes the pre-`wrapp` `IntunePackager` layout).
>
> Companion doc: [`CLEANUP_BACKLOG.md`](./CLEANUP_BACKLOG.md) — verified dead code + duplication targets.

---

## 1. What Wrapp is

A single self-contained WPF (.NET 8) desktop app that wraps installers and authors **Intune
Win32 apps** and **SCCM applications** from one bundle. Two halves:

- **Wrapp.GUI** — the C#/WPF operator surface (this map).
- **Wrapp.Packager** — a first-party PowerShell module that does the Graph / ConfigMgr work,
  driven in-process via a hosted PowerShell 7.4 runspace.

The GUI pre-acquires MSAL tokens and injects them into the PowerShell runspace, so the module
rarely re-authenticates.

---

## 2. Repository layout

```
wrapp/
├── src/Wrapp.GUI/          # the desktop app (≈40k LOC C# + XAML)
│   ├── Services/    (52 files, ~15.7k LOC)  business logic, no UI
│   │   └── Infrastructure/ (10)             cross-cutting: logging, clock, atomic IO, scopes
│   ├── ViewModels/  (29 files, ~13.2k LOC)  MVVM, one per screen (+ 2 base classes)
│   ├── Views/       (51 files, ~4.1k LOC)   XAML + thin code-behind
│   ├── Models/      (32 files, ~3.3k LOC)   Config.json/settings.json mirror + POCOs/records
│   │   └── Enums/   (3)
│   ├── Helpers/     (16 files, ~2.2k LOC)   converters, field-state engine, JSON ext, markdown
│   ├── Controls/    (8)                      reusable UserControls (DateTimePicker, headers)
│   ├── Converters/  (1)                      JobContext renderer converter
│   ├── Templates/   (4 + subdirs)            embedded script/package/assignment templates
│   ├── Themes/      (3)                      Dark.xaml / Light.xaml
│   └── Assets/, Help/
├── tests/Wrapp.GUI.Tests/   (xUnit, 580+ tests)
├── modules/                 Wrapp.Packager + vendored IntuneWin32App + framework templates
├── tools/IconBrowser/       dev utility
└── docs/                    architecture docs (this folder) + ADRs
```

### Conventions to know before reading

- **Partial-class splitting by concern.** Big services/VMs are split across files with a
  `.Concern.cs` suffix, e.g. `MsalAuthService.{Cache,Flows,Helpers,Wam}.cs`,
  `ConfigFileService.{Migrations,Parsers,Serializers}.cs`,
  `AppInventoryService.{ContentDownload,Groups,PsObjectMapping,Sccm}.cs`,
  `GeneralViewModel.{FileOps,InstallerDrop,InstallerHelpers,SaveDialogs}.cs`. Treat each group
  as **one type**.
- **Statics for stateless services** (`BundleService`, `ConfigFileService`, `IntuneWinService`,
  `SettingsService`, `GitService`, `VaultPathTemplate`, all of `Infrastructure/`), instances for
  stateful ones (`MsalAuthService`, `PowerShellService`, `AppInventoryService`,
  `BackgroundJobTracker`).
- **MVVM via CommunityToolkit.Mvvm** — `[ObservableProperty]`, `[RelayCommand]`. UI-only model
  members are `[JsonIgnore]`.

---

## 3. Architecture at a glance

```
        Views (XAML + thin code-behind)
              │   bind / commands
              ▼
        ViewModels  ── StatusViewModelBase (busy/status/RunBusyAsync)
              │        PackageViewModelBase (Intune/SCCM shared package logic, via composition)
              │        PropertyRelay (child→parent change forwarding)
              ▼
        Services  ────────────────────────────────────────────────┐
          Auth · PowerShell bridge · Inventory · Bundle/Config ·   │ Models  (Config/settings
          Packaging · Settings/Secrets/Vault · Git · Monaco/UI     │         mirror + POCOs)
              │                                                     │ Helpers (field-state,
              ▼                                                     │         JSON, converters)
        Infrastructure/  (AppLogger, SystemClock, AtomicFile,      │
          OperationScope, MsalRefreshRegistry, FileNameSanitizer)  ┘
```

The **central UI hub** is `GeneralViewModel` (the loaded bundle); most other VMs depend on it.
The **central data type** is `AppConfigModel` (the in-memory `Config.json`).

---

## 4. Subsystems

### 4.1 Auth (MSAL)

| File(s) | Responsibility |
|---|---|
| `MsalAuthService.{cs,Cache,Flows,Helpers,Wam}` | Singleton for all 4 auth flows (Interactive/WAM, DeviceCode, ClientSecret, ClientCert) with DPAPI-encrypted cross-process token cache + silent refresh. |
| `DevOpsAuthService` | Separate, isolated MSAL public client for Azure DevOps (vault). Own cache file. |
| `JwtDecoder` | **Presentation-only** JWT payload decode for the account flyout (never a security decision). |

Key entry points on `MsalAuthService`: `InitializeForTenantAsync(tenant, …)` (canonical setup),
`AcquireTokenAsync(forceRefresh)`, `TryAcquireTokenSilentForTenantAsync(tenantId)` (inventory hot
path), `GetCachedAccountsAsync` / `ForgetAccountAsync` / `SignOutAsync`, `static
ResolveOrganizationNameAsync`. Depends on `SecretProtection`, `AtomicFile`,
`PlatformConfig.MsalCachePath`, `MsalRefreshRegistry`. Used by `AccountViewModel`, `RunViewModel`,
`TenantsViewModel`, `InventoryViewModel`, `AppInventoryService`.

### 4.2 PowerShell bridge

| File | Responsibility |
|---|---|
| `PowerShellService` | Hosts a `RunspacePool` with Wrapp.Packager; runs packaging/validation/defaults/SCCM probes; injects MSAL tokens. |
| `PowerShellTokenBridge` | Converts `MsalTokenResult` → the `$Global:` vars IntuneWin32App expects; registers an opaque refresh handle. |
| `InvokeWrappTokenRefreshCommand` | `PSCmdlet` (`Invoke-WrappTokenRefresh`) that does a silent forced refresh via the handle — never exposes the live MSAL client. |
| `MsalRefreshRegistry` *(Infra)* | Holds `(IPCA, IAccount, scopes)` keyed by GUID so scripts get a handle, not the client. |

`PowerShellService` entry points: `InitializeAsync`, `PackageAsync(...)` (streams all PS streams to
`IProgress`, injects refresh handle, kills child procs on cancel), `ValidateConfigAsync`,
`LoadDefaultsAsync`, `RunScriptWithTokenAsync` (inventory workhorse), `TestSccmConnectivityAsync`,
`RecyclePool`.

### 4.3 Inventory (Graph + ConfigMgr)

`AppInventoryService.{cs,ContentDownload,Groups,PsObjectMapping,Sccm}` — bridge for Intune (Graph)
and SCCM (ConfigMgr) inventory with per-tenant/site/app caches. Entry points:
`GetIntuneAppsAsync`, `PreloadIntuneDetailsAsync` (`$batch`), `GetIntuneAppDetailAsync`,
`FetchIconBase64Async`, `GetSccmAppsAsync` / `GetSccmAppDetailAsync`, `DownloadRawContentAsync`,
`ResolveNestedGroupsForTenantAsync` (BFS nested-group membership), `ClearCache`. Used by
`InventoryViewModel.*`, `IntuneView`, `SCCMView`, `AppPickerDialog`.

### 4.4 Bundle / Config / Packaging

| File(s) | Responsibility | Key methods |
|---|---|---|
| `BundleService` | Bundle on-disk layout: create tree, write scripts/shortcuts/icons, populate from decrypted content. | `CreateBundleAsync`, `ResolveSubDirectory`, `GetBundleRoot`, `FindConfigJson`, `DetectInstallersInBinaryFolder`, `PopulateFromDecryptedContentAsync`, `Sanitize` |
| `BundlePaths` | Single source of truth for the `Script/ B/ Files/ Shortcuts/` layout. | `ConfigJson`, `ScriptDir`, `BinaryFolder` |
| `ConfigFileService.{cs,Migrations,Parsers,Serializers}` | Load/save `Config.json` ↔ `AppConfigModel`; control-char repair, schema-version guard, legacy migrations, `ref:settings` secret sentinel. | `LoadAsync` / `DeserializeFromJson`, `SaveAsync` / `SerializeToJson` |
| `IntuneWinService` | Pure-C# `.intunewin`: inspect, AES-256-CBC decrypt, key-probe, extract, finalize, scrape identity. | `InspectPackage`, `DecryptAsync`, `TryKeyQuick`, `ExtractAndDecryptAsync`, `ValidateDecryptedFile`, `FinalizeDecryptedOutput` |
| `IntuneWinDecryptOrchestrator` | Orchestrates decrypt across 5 key sources (embedded/manual/vault/brute/CSV). | `Decrypt{Embedded,KeyPair,Vault}Async`, `BruteForceDecryptAsync`, `CsvDecryptAsync` |
| `MsiPropertyService` | P/Invoke into msi.dll for MSI/MSP properties + icons. | `GetMsiMetadata`, `GetMspMetadata`, `GetIcons` |
| `PhaseDetector` | Parses PS log lines → `PhaseEvent`s (≈40 regexes) driving the Run view. | `ProcessLine`, `event PhaseChanged` |
| `ScriptFrameworkProvider` | All Appease-vs-PSADT strings (script names, commands, folders, detection). | `GetBundleScripts`, `GetShortcuts`, `Get*InstallCommand`, `DetectFromFolder`, `Parse` |
| `TemplateService` | User-customizable templates in `%LOCALAPPDATA%`; bootstrap from embedded resources. | `EnsureBuiltInTemplates`, `GetScriptTemplates`, `ApplyPackageTemplate`, `ApplyTokens` |
| `TempWorkspaceService` | Per-session `%TEMP%\Wrapp\` dirs with a `.lock` for multi-instance safety. | `CreateAsync`, `AcquireLock`, `CleanOld`, `DeleteWorkspaceBackground` |
| `DeploymentPlanRenderer` / `JobStepTreeRenderer` | Build WPF trees for the run-plan confirmation and generic job steps. | `static Render(...)` |
| `BackgroundJobTracker` | Singleton tracking all background jobs (aggregate progress, history, cancel/shutdown). | `BeginJob`→`JobHandle`, `Complete`, `Fail`, `WaitAllAsync` |

### 4.5 Settings / Secrets / Vault

| File | Responsibility |
|---|---|
| `SettingsService` | Load/save `settings.json`; projects tenant/site/domain entries to persisted form; DPAPI-encrypts secrets. `SavePreferencesAsync`, `Load`, `Save`. |
| `AppSettings` *(Models)* | The settings POCO **and** the static `SecretProtection` DPAPI toolbox (`Encrypt`/`Decrypt`, `ProtectBytes`/`UnprotectBytes`, `DecryptToSecureString`, `WithPlaintext<T>`, `ResolveTenantSecret`). |
| `EncryptionKeyStoreService` | Stores/fetches `.intunewin` keys in an Azure DevOps Git "vault" (direct or PR mode), gated by `IFeatureGate` + a TOFU URL-hash. `SaveKeysAsync`, `GetKeysAsync`, `LoadAllDevOpsKeysAsync`, `static ComputeKeyVaultUrlHash`. |
| `FeatureGateService` / `IFeatureGate` / `WrappFeatures` | Opt-in/out primitive; today gates `AzureDevOpsKeyVault`. |
| `VaultPathTemplate` | Single-brace token expander (`{Tenant}`/`{AppId}`/…) for vault paths, with path-traversal sanitisation. |
| `DefaultsLoader` / `ModuleDefaultsSeed` / `JsonDefaults` | Org defaults from `defaults.local.json`; C# mirror of `Defaults.psd1`; canonical `JsonSerializerOptions`. |

### 4.6 Git, Monaco, UI services

- **`GitService`** — git-CLI wrapper (bundled MinGit) for per-bundle history; serialised writes;
  pre-commit plaintext-secret scan. `InitAsync`, `CommitAllAsync`, `GetCommitLogAsync`,
  `GetFileContentAtCommitAsync`.
- **`MonacoService` / `MonacoTabService` / `MonacoDiffService`** — WebView2↔Monaco bridges (single
  editor, tabbed, read-only diff). `SetContentAsync`, `CreateModelAsync`/`SwitchModelAsync`,
  `SetDiffAsync`.
- **`IconService` / `IconExtractorService`** — copy/load bundle icons with dedup; extract icons
  from EXE/installer via Shell32.
- **`FileDialogService` / `FluentDialog` / `DragOverlayService`** — dialog + drag-drop helpers.
  `FluentDialog` centralizes all `ContentDialog` boilerplate (`ConfirmAsync`, `ShowInfoAsync`,
  `SaveDiscardCancelAsync`, …).

### 4.7 Infrastructure (`Services/Infrastructure/`)

Cross-cutting primitives used everywhere: `AppLogger` (async redacting rotating logger, +
`IAppLogger`/`DefaultAppLogger` mockable surface), `AtomicFile` (torn-write-safe writes),
`SystemClock` (swappable `TimeProvider` for testability), `OperationScope` (start/complete/fail
timing+correlation), `FileNameSanitizer`, `SafeFireAndForget`, `UiProgress`, `PlatformConfig` /
`IEnvironmentConfig` (env-var > appsettings > default path resolution), `MsalRefreshRegistry`.

---

## 5. ViewModels

Two base classes carry most shared mechanics:

- **`StatusViewModelBase`** — `IsBusy` / `StatusText` / `StatusIsError` + `RunBusyAsync(...)`
  (busy-toggle + try/catch/finally + cancellation handling + logging). Extended by Inventory, Run,
  Account, Tenants, GitHistory, Splash.
- **`PackageViewModelBase`** — Intune/SCCM shared package logic (duplicate-name validation,
  dependency/supersedence/icon CRUD, error counts). Consumed by **composition** via the nested
  `IntuneShared` / `SCCMShared` helpers, not inheritance.
- **`PropertyRelay`** — declaratively forwards child-VM `PropertyChanged` to parent notifications.
- **`SelectionTracker<T>`** / **`TimedFlag`** *(Helpers)* — "any selected" flags; timed UI flashes.

| ViewModel | Backs | Notable commands / methods |
|---|---|---|
| `GeneralViewModel.*` | Bundle editor (the hub) | `LoadFolderAsync`, `SaveBundleAsync`, `SaveBundleAsAsync`, `BrowseInstallerAsync`, `HandleDropAsync`, `ValidateForSaveAsync` |
| `IntuneViewModel` | Intune package tab | `AddPackage`, `OpenAssignments`, dep/supersedence/return-code/category/scope-tag CRUD |
| `SCCMViewModel` | SCCM package tab | mirror of Intune; `AddInstallBehavior`, `OpenDeployments` |
| `DetectionViewModel` | Detection-rule editor | `AddTest`, `AddExpression`, `ValidateSymbols` (in-memory only) |
| `ScriptsViewModel` | Monaco script tabs | `OnMonacoReadyAsync`, `SwitchTabAsync`, `AutoLoadAllAsync` |
| `ConfigJsonViewModel` | Raw Config.json editor | `ApplyChangesAsync`, `ShowConfigAsync` |
| `RunViewModel.*` | Packaging run + live log | `StartAsync`, `Cancel`, `RefreshConnectionStatusAsync`, `BuildPackagingRunContext`, `OnPhaseChanged` |
| `InventoryViewModel.*` | Browse/import from tenants/sites | `RefreshAsync`, `DownloadIntuneWinAsync`, `ImportToWrappAsync`, `ApplyFilter` |
| `AccountViewModel` | MSAL + DevOps auth flyout | `SignInAsync`, `SwitchAccountAsync`, `RefreshTokenAsync`, `SignInDevOpsAsync` |
| `TenantsViewModel` | Tenants/sites config + test | `AddIntuneTenant`, `TestConnectionAsync`, ~12 `Sync*FromPrefs/Defaults` |
| `SettingsViewModel` | Preferences editor | `SaveAsync`, `Reset*Async`, `EnrichTenantsFromSettings`, snapshot-diff `IsDirty` |
| `PreferencesViewModel` | Persisted defaults + 6 sub-VMs | `LoadFromSettings`, `SaveAsync`, `Clone{Tenant,Site,Domain}` |
| `MainViewModel` | Shell / navigation / New-Open | `Navigate`, `NewAsync`, `OpenAsync`, `WirePackageVms`; holds all child VMs |
| `LogsViewModel` | Live log viewer | `Clear`, `FilterPredicate` (2000-entry FIFO) |
| `ToolsViewModel` | IntuneWin inspect/decrypt/batch | `InspectPackage`, `DecryptFileAsync`, `ScanBatchAsync`, `SaveBatchToVaultAsync` |
| `GitHistoryViewModel` | Commit-history viewer | `RefreshAsync`, `ViewCommitAsync`, `PollForChangesAsync` |
| `SplashViewModel` | New/Open splash + framework pick | `NewPackage`, `OpenExistingAsync`, `CreateBundleWithFrameworkAsync` |

**Views/Controls** are mostly thin code-behind. Real logic lives in: `MainWindow.xaml.cs` (DI +
section caching + close-confirm), `RegistryBrowserDialog` (lazy registry tree), `ScriptsView`
(constructs `MonacoTabService`), `IntuneAssignmentDialog`/`SCCMDeploymentDialog` (grid editing),
`DateTimePickerField` (ISO-8601 UTC picker control).

---

## 6. Models & Helpers (high-value reference)

- **`AppConfigModel.*`** — observable mirror of every `Config.json` section. Five entry types
  (`IntunePackageEntry`, `SCCMPackageEntry`, `DetectionTest`, `AssignmentEntry`,
  `IntuneTenantEntry`) each bind a `FieldStateProvider` to a static `FieldRule[] Rules`.
- **`AppSettings`** — `settings.json` POCO + `SecretProtection` (DPAPI). `IntuneTenantEntry`
  secrets persist here as cipher; `Config.json` only stores the `ref:settings` sentinel.
- **Field-state framework** (`Helpers/`): `FieldDependencyRules` (the rule tables) →
  `FieldStateProvider` (evaluates rules vs PropertyChanged, reflection-cached) →
  `FieldStateAccessor`/`FieldState` (XAML-bindable enable/visible/required/error) +
  `FieldValidators` (per-`FieldKind` validation). This is the single most reused mechanism.
- **JSON helpers**: `JsonObjectExtensions` (`.Str/.Bool/.Int/.StrArray/.EnumOr` over mutable
  `JsonObject`, 172 call sites in the config parser) and `JsonElementExtensions`
  (`.GetStringOr/.GetIntOr/.GetBoolOr` over read-only `JsonElement`, used by inventory).
- **`Converters.cs`** — 8 WPF value converters bound in `App.xaml` and views.
- **`HelpMarkdownRenderer.*`** — purpose-built Markdown→themed FlowDocument (replaced MdXaml).
- **`DateTimeFormats`** — canonical ISO/SCCM/Intune date constants + parse helpers.

---

## 7. Key end-to-end flows (relationships)

- **Save a bundle:** `GeneralViewModel.SaveBundleAsync` → `ConfigFileService.SaveAsync`
  (atomic, secret sentinel) + `BundleService.CreateBundleAsync` (folders, scripts via
  `ScriptFrameworkProvider`, icon via `IconService`) → `GitService.CommitAllAsync`.
- **Run a packaging job:** `RunViewModel.StartAsync` → `BuildPackagingRunContext` →
  `MsalAuthService` token → `PowerShellService.PackageAsync` (streams to `PhaseDetector` →
  `OnPhaseChanged` → `PackageProgress`) under a `BackgroundJobTracker` job.
- **Import from Intune:** `InventoryViewModel.ImportToWrappAsync` → `AppInventoryService`
  (download blob) → `IntuneWinDecryptOrchestrator` (decrypt via `IntuneWinService` +
  `EncryptionKeyStoreService` vault keys) → `BundleService.PopulateFromDecryptedContentAsync`.
- **Secret lifecycle:** typed into `PasswordBox` → `IntuneTenantEntry.ClientSecret`
  (`SecureString`) → `SettingsService` DPAPI-encrypts to `settings.json` →
  `SecretProtection.WithPlaintext` unwraps only at the MSAL boundary.

---

## 8. Where to look first

| I want to change… | Start here |
|---|---|
| Bundle on-disk layout | `BundleService`, `BundlePaths` |
| Config.json read/write | `ConfigFileService.*`, `AppConfigModel.*` |
| A new auth behavior | `MsalAuthService.*`, `AccountViewModel` |
| Inventory queries | `AppInventoryService.*`, `InventoryViewModel.*` |
| `.intunewin` decrypt | `IntuneWinService`, `IntuneWinDecryptOrchestrator` |
| Conditional field enable/require | `FieldDependencyRules`, `FieldStateProvider` |
| A new dialog | `FluentDialog`, the `Views/*Dialog.xaml(.cs)` |
| Cross-cutting (log/clock/IO) | `Services/Infrastructure/` |
