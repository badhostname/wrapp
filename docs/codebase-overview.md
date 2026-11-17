# Wrapp &mdash; Codebase Overview

> Snapshot for retroactive technical planning. Corresponds to repository state at commit **`9998fe2`** (`0.6.0.0235-beta`, 2026-06-04).

---

## 1. Architecture at a glance

Wrapp is a Windows desktop application that authors and ships Windows app packages to two enterprise endpoints &mdash; Microsoft Intune (Win32 apps) and Microsoft Configuration Manager (SCCM applications) &mdash; from the same bundle. It is a single self-contained .NET 8 / WPF executable; everything it needs ships in its publish folder. No server-side pieces, no installer, no required infrastructure.

The architecture is **two cooperating processes inside one binary**:

- **Wrapp.GUI** &mdash; WPF authoring surface in C# (~50 services, ~30 viewmodels, ~30 views). Holds all editor state, owns the MSAL token cache, talks HTTP directly to Azure DevOps for the encryption-key vault, persists user preferences to `%LOCALAPPDATA%\Wrapp\`.
- **Wrapp.Packager** &mdash; first-party PowerShell module (17 public + ~30 private functions). Runs in-process via `Microsoft.PowerShell.SDK` in a `RunspacePool`. Performs the actual Graph / `IntuneWin32App` / `ConfigurationManager` work. Receives a pre-acquired MSAL token from the GUI through an opaque registry handle (Phase 12 / S-3) so it almost never re-authenticates.

The GUI is the operator-facing surface; the PowerShell module is the workhorse. The C#/PS boundary is narrow and audited: only token state, validation results, and structured progress events cross it.

**Versioning convention:** `MAJOR.MINOR.PATCH.BUILD-prerelease`. The build digit increments on every shipped change, indexed in `Wrapp.GUI.csproj` `<VersionPrefix>`. The CHANGELOG keeps a per-build entry. Current ship: `0.6.0.0235-beta`.

---

## 2. Views (12 main + ~10 dialogs)

Navigation is enum-driven via `Models/NavigationSection.cs` and a `MainViewModel` that swaps the content area. Every section view binds to its own viewmodel, partials split where the viewmodel grew past ~500 lines.

### 2.1 Main shell &mdash; `MainWindow.xaml` / `Views/SplashWindow.xaml`

- **MainWindow** &mdash; `FluentWindow` shell: left nav rail, content host, bottom status bar with the `BackgroundJobTracker` pop-up. Hosts the global progress aggregator that summarises active jobs across the app.
- **SplashWindow** &mdash; Loading frame on cold start while module probing (Wrapp.Packager + IntuneWin32App) and Monaco init run.

### 2.2 General &mdash; `GeneralView.xaml` ([`GeneralViewModel`](../src/Wrapp.GUI/ViewModels/GeneralViewModel.cs))

Bundle metadata editor: company, name, version, GUID, icon, framework selection (Appease vs PSADT), installer drag-and-drop landing pad, dependencies list, "detect running processes" list. Also the bundle file-ops surface &mdash; Save Bundle, Save As, Open Bundle, the temp-workspace draft, and the `.intunewin` import flow (decrypt + populate). The viewmodel is split across 4 partials (`.cs`, `.FileOps.cs`, `.InstallerDrop.cs`, `.InstallerHelpers.cs`, `.SaveDialogs.cs`) for navigability.

### 2.3 Intune &mdash; `IntuneView.xaml` ([`IntuneViewModel`](../src/Wrapp.GUI/ViewModels/IntuneViewModel.cs))

Full Win32 app authoring: package list, per-package metadata (`Description`, `Owner`, `Publisher`, `Architecture`, `MinOS`, `Install/Uninstall command`, `Install behavior`, return codes, dependencies, supersedence, per-tenant assignments with filters, scheduling). Drives `Test-PackagerConfig` validation through `PowerShellService.ValidateConfigAsync` and surfaces issues inline. The `IntuneAssignmentDialog` opens for per-assignment editing (filter mode, delivery optimisation, grace period, scheduled times).

### 2.4 SCCM &mdash; `SCCMView.xaml` ([`SCCMViewModel`](../src/Wrapp.GUI/ViewModels/SCCMViewModel.cs))

Symmetric for SCCM applications: package list, `New-CMApplication` metadata, deployment-type settings (install/logon/notification/user-interaction), install behaviors (processes to close), dependencies, supersedence, and per-site deployments through `SCCMDeploymentDialog`. SCCMSite entries (DeploymentGroups, AppFolder) live here too.

### 2.5 Detection &mdash; `DetectionView.xaml` ([`DetectionViewModel`](../src/Wrapp.GUI/ViewModels/DetectionViewModel.cs))

The detection / requirement rules editor. File / registry / script rules with path locking, Boolean expression combining (which rules must all-pass vs any-pass), per-property dynamic property lists pulled live from `FileVersionInfo`. Shared between Intune (Win32) and SCCM (`Add-CMScriptDeploymentType -ScriptText`) since the same rule shape underlies both.

### 2.6 Scripts &mdash; `ScriptsView.xaml` ([`ScriptsViewModel`](../src/Wrapp.GUI/ViewModels/ScriptsViewModel.cs))

Monaco-powered editor for the bundle's PowerShell scripts. Tabs:

- Appease bundles: `InstallScript.ps1`, `UninstallScript.ps1`, `DetectScript.ps1`.
- PSADT v4 bundles: `Invoke-AppDeployToolkit.ps1` plus the PSADT v4 phases.

Backed by [`MonacoTabService`](../src/Wrapp.GUI/Services/MonacoTabService.cs), [`MonacoService`](../src/Wrapp.GUI/Services/MonacoService.cs), and [`MonacoDiffService`](../src/Wrapp.GUI/Services/MonacoDiffService.cs) inside a WebView2 control. Monaco assets are vendored under `Assets/monaco/vs/**` &mdash; fully offline, no CDN.

### 2.7 ConfigJson &mdash; `ConfigJsonView.xaml` ([`ConfigJsonViewModel`](../src/Wrapp.GUI/ViewModels/ConfigJsonViewModel.cs))

Raw `Config.json` editor. Monaco-hosted, live JSON validation, full read/write of the underlying schema. The escape hatch for fields not yet surfaced in the typed editors.

### 2.8 Run &mdash; `RunView.xaml` ([`RunViewModel`](../src/Wrapp.GUI/ViewModels/RunViewModel.cs))

The packaging runner. Picks target (Intune / SCCM), tenant or site, packages to include, then dispatches `Invoke-IntunePackager` or `Invoke-SCCMPackager` through [`PowerShellService.PackageAsync`](../src/Wrapp.GUI/Services/PowerShellService.cs). Streams Information/Warning/Error/Verbose/Progress streams into a CMTrace-style log panel and a per-package phase tracker fed by [`PhaseDetector`](../src/Wrapp.GUI/Services/PhaseDetector.cs). Split across `.cs` + `.PhaseHandling.cs` + `.ConnectionStatus.cs` partials. Owns the connection-status pre-flight (MSAL token + SCCM connectivity).

### 2.9 Inventory &mdash; `InventoryView.xaml` ([`InventoryViewModel`](../src/Wrapp.GUI/ViewModels/InventoryViewModel.cs))

Browse what's already deployed. Pull list across:

- **Intune tenants** via `IntuneWin32App` module (paginated `$batch` fetch, group-name resolution, nested-group expansion via `Resolve-EntraGroupId`).
- **SCCM sites** via `Get-CMApplication` (full app + deployment metadata).

Three operations on a selected app:

1. **Import to Wrapp** (metadata-only) &mdash; creates a new bundle pre-filled with the app's properties.
2. **Full Import** &mdash; additionally downloads the `.intunewin`, decrypts via [`IntuneWinDecryptOrchestrator`](../src/Wrapp.GUI/Services/IntuneWinDecryptOrchestrator.cs), populates the binary + scripts.
3. **Download `.intunewin`** &mdash; raw download for offline analysis.

Partials: `.cs`, `.Actions.cs`, `.Import.cs`, `.ClipboardBodies.cs`.

### 2.10 Tools &mdash; `ToolsView.xaml` ([`ToolsViewModel`](../src/Wrapp.GUI/ViewModels/ToolsViewModel.cs))

Utilities tab:

- **`.intunewin` Decrypt** &mdash; given any `.intunewin`, decrypt with embedded keys / vault keys / manual key/IV / CSV list / brute force.
- **Batch Inspect** &mdash; scan a folder of `.intunewin` files, deduplicate against the Azure DevOps vault, save the new ones.
- **Encryption key vault management** &mdash; push keys to DevOps, list, dedupe.

### 2.11 Logs &mdash; `LogsView.xaml` ([`LogsViewModel`](../src/Wrapp.GUI/ViewModels/LogsViewModel.cs))

Live view of `app.log` (`%LOCALAPPDATA%\Wrapp\app.log`) plus rotated `app.1.log` &hellip; `app.5.log`. Filterable by severity. The log file is written by the asynchronous [`AppLogger`](../src/Wrapp.GUI/Services/Infrastructure/AppLogger.cs) with redaction (JWT / Bearer / Basic / ClientSecret / OAuth tokens / Azure DevOps PATs).

### 2.12 Git History &mdash; `GitHistoryView.xaml` ([`GitHistoryViewModel`](../src/Wrapp.GUI/ViewModels/GitHistoryViewModel.cs))

Per-bundle git history through vendored MinGit. Every bundle's `Script/` is its own git repo; commits are made automatically on every save. View / diff / restore from history with `FileHistoryWindow.xaml` + `DiffWindow.xaml` (the latter Monaco-diff hosted).

### 2.13 Settings &mdash; `SettingsView.xaml` ([`SettingsViewModel`](../src/Wrapp.GUI/ViewModels/SettingsViewModel.cs))

Per-user preferences:

- Account sign-in (delegates to `AccountViewModel`).
- Intune tenants list with auth-flow editor (Interactive / DeviceCode / ClientSecret / ClientCert) &mdash; backed by [`Tenants subview`](../src/Wrapp.GUI/Views/TenantsView.xaml).
- SCCM sites list.
- Domain entries (content-distribution paths, log copy targets).
- Per-category defaults (package, metadata, assignment, deployment).
- Key vault repository configuration (Azure DevOps git URL for encryption keys).

Backed by [`PreferencesViewModel`](../src/Wrapp.GUI/ViewModels/PreferencesViewModel.cs) and persisted by [`SettingsService`](../src/Wrapp.GUI/Services/SettingsService.cs) to `settings.json` (DPAPI-encrypted secrets + plaintext metadata).

### 2.14 Tenants subview &mdash; `TenantsView.xaml` ([`TenantsViewModel`](../src/Wrapp.GUI/ViewModels/TenantsViewModel.cs))

Embedded inside Settings. Tenant CRUD with the PasswordBox boundary that converts user input directly into a `SecureString` (Phase 15 / S-6).

### 2.15 Modal dialogs

| Dialog | Purpose |
|---|---|
| `ActionPickerDialog` | Generic "pick one of N actions" &mdash; used when `.intunewin` decrypt source is ambiguous and the user must choose. |
| `AppPickerDialog` | Pick a Wrapp app from an existing inventory list. |
| `IconPickerDialog` / `MsiIconPickerDialog` | Choose / extract an app icon from an `.exe` (via `IconExtractorService`) or `.msi` (via `MsiPropertyService`). |
| `IntuneAssignmentDialog` | Per-assignment editor (filter mode, group ID, scheduling, delivery optimisation). |
| `SCCMDeploymentDialog` | Per-deployment editor (collection, available/deadline, deploy action / purpose / user notification). |
| `NestedGroupBrowserDialog` | Browse nested Entra ID group membership with search. |
| `RegistryBrowserDialog` | Browse local registry to pick a key / value for a Registry detection rule. |
| `DiffWindow` / `FileHistoryWindow` | Monaco-hosted diff viewer + git history table. |

---

## 3. Services (~50)

Organised by responsibility.

### 3.1 Configuration + persistence

| Service | Role |
|---|---|
| [`ConfigFileService.cs`](../src/Wrapp.GUI/Services/ConfigFileService.cs) (+ `.Parsers`, `.Serializers`, `.Migrations`) | Load / save `Config.json` (bundle-level config). JSON &harr; `AppConfigModel`. Crash-safe via `AtomicFile`. Handles schema migrations (auto-GUID, deployment relocation, AsapValue normalisation). |
| [`SettingsService.cs`](../src/Wrapp.GUI/Services/SettingsService.cs) | Load / save `%LOCALAPPDATA%\Wrapp\settings.json`. DPAPI-encrypts client secrets via `SecretProtection` and preserves the cipher byte-for-byte on round-trip. |
| [`PreferencesSync.cs`](../src/Wrapp.GUI/Services/PreferencesSync.cs) | Sync between `PreferencesViewModel` (editable copy) and the live `AppSettings` collection. Clone tenants/sites/domains. |
| [`DefaultsLoader.cs`](../src/Wrapp.GUI/Services/DefaultsLoader.cs) | First-launch defaults seeding. Reads `defaults.local.json` (gitignored, org-specific) or falls back to `defaults.example.json`. |
| [`ModuleDefaultsSeed.cs`](../src/Wrapp.GUI/Services/ModuleDefaultsSeed.cs) | Hard-coded enum lists used when `Wrapp.Packager` is unavailable. |
| [`BundleService.cs`](../src/Wrapp.GUI/Services/BundleService.cs) | Bundle scaffolding: create folder layout, copy framework templates, write `Config.json`, save icon as PNG (with 512&times;512 downscale for SCCM). |
| [`BundlePaths.cs`](../src/Wrapp.GUI/Services/BundlePaths.cs) | Phase 13 centralised path helper: `ConfigJson(root)`, `ScriptDir(root)`, `BinaryFolder(root, fw)`. |
| [`TempWorkspaceService.cs`](../src/Wrapp.GUI/Services/TempWorkspaceService.cs) | Draft-bundle temp directories with lock files so concurrent Wrapp instances don't trample each other's drafts. |
| [`TemplateService.cs`](../src/Wrapp.GUI/Services/TemplateService.cs) | Token expansion (`{{Company}}`, `{{Name}}`, `{{Version}}`, `{{Date}}`, &hellip;) for templates and package metadata defaults. |
| [`ScriptFrameworkProvider.cs`](../src/Wrapp.GUI/Services/ScriptFrameworkProvider.cs) | Per-framework knowledge: Appease vs PSADT layouts, binary folder names, expected script names, shortcut definitions. |

### 3.2 PowerShell host

| Service | Role |
|---|---|
| [`PowerShellService.cs`](../src/Wrapp.GUI/Services/PowerShellService.cs) | Owns the `RunspacePool` (3 max). Imports Wrapp.Packager. Dispatches validation, packaging runs, inventory pulls, SCCM connectivity checks. Hosts the post-run cleanup safety net (Phase 12 / S-8). |
| [`PowerShellTokenBridge.cs`](../src/Wrapp.GUI/Services/PowerShellTokenBridge.cs) | Injects MSAL tokens into a runspace. Sets `$Global:AccessToken` / `$Global:AuthenticationHeader` / `$Global:AccessTokenTenantID` (consumed by `IntuneWin32App`). Returns an opaque GUID handle (Phase 12 / S-3) so PS scripts can refresh via the registered cmdlet without seeing the live IPCA. |
| [`InvokeWrappTokenRefreshCommand.cs`](../src/Wrapp.GUI/Services/InvokeWrappTokenRefreshCommand.cs) | `PSCmdlet` registered in every runspace. Takes `-Handle <Guid>` and calls `AcquireTokenSilent` in C#, returns a typed `PSObject`. |
| [`Infrastructure/MsalRefreshRegistry.cs`](../src/Wrapp.GUI/Services/Infrastructure/MsalRefreshRegistry.cs) | `ConcurrentDictionary<Guid, (IPCA, IAccount, scopes)>` backing the cmdlet. |
| [`PhaseDetector.cs`](../src/Wrapp.GUI/Services/PhaseDetector.cs) | Pattern-matches PowerShell verbose-stream output to detect packaging phase transitions and feed the per-package progress UI. |

### 3.3 Authentication

| Service | Role |
|---|---|
| [`MsalAuthService.cs`](../src/Wrapp.GUI/Services/MsalAuthService.cs) (+ `.Cache`, `.Flows`, `.Wam`, `.Helpers`) | MSAL client for Microsoft Graph. WAM broker by default, system-browser fallback. Per-tenant `InitializeForTenantAsync` from an `IntuneTenantEntry`. Owns the DPAPI-encrypted on-disk token cache with cross-process mutex (Phase 12 / S-4). |
| [`DevOpsAuthService.cs`](../src/Wrapp.GUI/Services/DevOpsAuthService.cs) | Separate MSAL client for Azure DevOps (different scope, uses Microsoft's `MsalCacheHelper` &mdash; already cross-process safe). |
| `SecretProtection` (inside [`Models/AppSettings.cs`](../src/Wrapp.GUI/Models/AppSettings.cs)) | DPAPI envelope (v2 magic + 16-byte entropy) for at-rest secrets and MSAL cache bytes. `WithPlaintext<T>` boundary that decrypts a `SecureString` into a `string` only inside a `Marshal::SecureStringToBSTR`/`ZeroFreeBSTR` scope. |

### 3.4 Inventory + content

| Service | Role |
|---|---|
| [`AppInventoryService.cs`](../src/Wrapp.GUI/Services/AppInventoryService.cs) (+ `.ContentDownload`, `.Groups`, `.PsObjectMapping`, `.Sccm`) | Inventory pulls. Wraps the PowerShell module's `Get-IntuneAppInventory` / `Get-SCCMAppInventory` / `Get-IntuneAppFullDetail`. Caches details. Resolves group-display-names via Graph and nested group membership. |
| [`IntuneWinService.cs`](../src/Wrapp.GUI/Services/IntuneWinService.cs) | Parses `.intunewin` archive structure. Inspects Detection.xml for embedded keys + extracts `Config.json` identity. |
| [`IntuneWinDecryptOrchestrator.cs`](../src/Wrapp.GUI/Services/IntuneWinDecryptOrchestrator.cs) | Decrypt strategies: embedded keys, vault lookup by (tenantId, appId), brute-force against the full DevOps vault, manual key/IV, CSV. |
| [`EncryptionKeyStoreService.cs`](../src/Wrapp.GUI/Services/EncryptionKeyStoreService.cs) | Azure DevOps git REST client for the encryption key vault. List / fetch / push key+IV records. The vault is the sole authoritative store (no local cache since Phase 10). |
| [`ConnectionChecker.cs`](../src/Wrapp.GUI/Services/ConnectionChecker.cs) | Pre-flight check for the Run view: token expiry, SCCM PSDrive availability, network reachability. |

### 3.5 UX infrastructure

| Service | Role |
|---|---|
| [`BackgroundJobTracker.cs`](../src/Wrapp.GUI/Services/BackgroundJobTracker.cs) + [`Models/JobHandle.cs`](../src/Wrapp.GUI/Models/JobHandle.cs) | Shared singleton that aggregates running background jobs. Drives the bottom status bar and the pop-up. `BeginJob` returns a `JobHandle` struct (Phase 13 / D-2). |
| [`JobStepTreeRenderer.cs`](../src/Wrapp.GUI/Services/JobStepTreeRenderer.cs) | Renders the per-job step tree (used by Tools.Decrypt and Inventory.Import). |
| [`DeploymentPlanRenderer.cs`](../src/Wrapp.GUI/Services/DeploymentPlanRenderer.cs) | Renders the "what will happen on this run" preview for the Run view. |
| [`Infrastructure/AppLogger.cs`](../src/Wrapp.GUI/Services/Infrastructure/AppLogger.cs) | Async file logger with rotation, redaction, and `LogEntry` event for the Logs view. Compiled regexes for JWT / Bearer / Basic / ClientSecret / OAuth tokens / PATs. |
| [`Infrastructure/OperationScope.cs`](../src/Wrapp.GUI/Services/Infrastructure/OperationScope.cs) | `using var op = OperationScope.Begin("Tools.Decrypt")` &mdash; auto-times + logs start/complete/fail. |
| [`Infrastructure/UiProgress.cs`](../src/Wrapp.GUI/Services/Infrastructure/UiProgress.cs) | Centralised `Progress<T>` allocation with marshalled SyncContext (lint rule forbids direct `new Progress<T>(...)`). |
| [`Infrastructure/SafeFireAndForget.cs`](../src/Wrapp.GUI/Services/Infrastructure/SafeFireAndForget.cs) | The deliberate fire-and-forget wrapper. Surfaces unhandled exceptions to the logger rather than the AppDomain. |
| [`Infrastructure/SystemClock.cs`](../src/Wrapp.GUI/Services/Infrastructure/SystemClock.cs) + [`FakeTimeProvider`](../tests/Wrapp.GUI.Tests/Infrastructure/FakeTimeProvider.cs) | Wall-clock test seam. |
| [`Infrastructure/AtomicFile.cs`](../src/Wrapp.GUI/Services/Infrastructure/AtomicFile.cs) | `.tmp` &rarr; `File.Replace` with `.bak` recovery for every persistence path. |
| [`Infrastructure/FileNameSanitizer.cs`](../src/Wrapp.GUI/Services/Infrastructure/FileNameSanitizer.cs) | Normalises strings into Windows-safe path segments. |
| [`Helpers/DateTimeFormats.cs`](../src/Wrapp.GUI/Helpers/DateTimeFormats.cs) | Phase 13 / D-4 canonical date format constants + parse helpers. |
| [`Helpers/WindowHelper.cs`](../src/Wrapp.GUI/Helpers/WindowHelper.cs) | Centralised WPF HWND acquisition (lint rule forbids direct `WindowInteropHelper` allocations). |

### 3.6 UI utilities

| Service | Role |
|---|---|
| [`FluentDialog.cs`](../src/Wrapp.GUI/Services/FluentDialog.cs) | `ShowInfoAsync` / `ShowSelectAsync` / `ShowContentAsync` &mdash; themed WPF-UI dialogs. |
| [`FileDialogService.cs`](../src/Wrapp.GUI/Services/FileDialogService.cs) | Open / save / browse-folder wrappers. |
| [`DragOverlayService.cs`](../src/Wrapp.GUI/Services/DragOverlayService.cs) | Live drag-and-drop overlay with target highlighting. |
| [`IconService.cs`](../src/Wrapp.GUI/Services/IconService.cs) + [`IconExtractorService.cs`](../src/Wrapp.GUI/Services/IconExtractorService.cs) + [`MsiPropertyService.cs`](../src/Wrapp.GUI/Services/MsiPropertyService.cs) | Icon discovery, extraction from `.exe` (Shell32), reading `MsiGetProperty`. |
| [`LoadingMessages.cs`](../src/Wrapp.GUI/Services/LoadingMessages.cs) | Rotating splash-screen strings. |
| [`MonacoTabService.cs`](../src/Wrapp.GUI/Services/MonacoTabService.cs) / [`MonacoService.cs`](../src/Wrapp.GUI/Services/MonacoService.cs) / [`MonacoDiffService.cs`](../src/Wrapp.GUI/Services/MonacoDiffService.cs) | WebView2 + Monaco editor host. Off-limits for routine changes (hardened WebView2 host). |
| [`GitService.cs`](../src/Wrapp.GUI/Services/GitService.cs) | Wraps the embedded MinGit. `InitAsync`, `CommitAllAsync`, `GetHistoryAsync`, `RestoreFileAsync`. |
| [`PlatformConfig.cs`](../src/Wrapp.GUI/Services/PlatformConfig.cs) | Resolves `%LOCALAPPDATA%\Wrapp\` paths for settings, MSAL cache, log file, temp workspaces. |

---

## 4. PowerShell module &mdash; `modules/Wrapp.Packager`

Standard Public/Private layout. Loaded into every PS runspace by the GUI.

### 4.1 Public functions (17)

**Orchestrators** (the GUI's only entry points):

- `Invoke-IntunePackager` &mdash; full Intune packaging pipeline (preflight &rarr; auth &rarr; loop packages &rarr; `Add-IntuneWin32AppFromConfig` or `Update-IntuneWin32AppFromConfig` &rarr; assignments).
- `Invoke-SCCMPackager` &mdash; full SCCM packaging pipeline (preflight &rarr; connect &rarr; loop packages &rarr; `Add-CMAppFromConfig` &rarr; distribute content &rarr; `Set-CMAppDeployment`).
- `Test-PackagerConfig` &mdash; schema validation. Returns structured `ValidationIssue[]` for the GUI to render inline.

**Per-package operations:**

- `Add-IntuneWin32AppFromConfig` / `Update-IntuneWin32AppFromConfig` &mdash; create or update an Intune app from a package entry.
- `Add-CMAppFromConfig` &mdash; create an SCCM app + deployment type.
- `New-IntuneWin32Package` &mdash; package a folder into a `.intunewin` (delegates to `IntuneWinAppUtil.exe`).
- `Remove-IntuneWin32AppSafe` &mdash; best-effort delete with the dependency-graph checks.
- `Set-CMAppDeployment` &mdash; create deployments to collections.
- `Set-Win32AppAssignment` &mdash; push assignments to a Win32 app.

**Auth + connectivity:**

- `Connect-IntunePackager` &mdash; hybrid MSAL auth (Interactive / DeviceCode / ClientSecret / ClientCert).
- `Connect-SCCMPackager` &mdash; imports ConfigurationManager module + detects site code.

**Preflight / collision detection:**

- `Test-IntunePackagerPreflight` / `Test-SCCMPackagerPreflight` &mdash; post-auth validation.
- `Test-Win32AppCollisions` / `Test-CMAppCollisions` &mdash; name + version collision checks before any creation.

### 4.2 Private functions (~30)

Token plumbing (`Invoke-TokenRefreshIfNeeded`), structured logging (`Write-Log` + `Redact-LogLine` + `Initialize-LogFile`), per-detection-type rule builders (`New-DetectionRuleFromConfig`, `New-CMDetectionFromConfig`, `New-RequirementRuleFromConfig`, `New-ReturnCodeFromConfig`), dependency / supersedence setters, group + tenant ID resolution (`Resolve-EntraGroupId`, `Resolve-TenantId`), inventory queries, PNG dimension reading (`Get-PngDimensions`), config-schema migration (`Update-ConfigSchema`), and the Phase 11 / 14 helpers (`Test-SafePath`, `Test-EnumField`, `Get-ConfigValue`, `Get-SafeDateTime`).

---

## 5. Primary flows

### 5.1 Cold start

1. `App.xaml` loads. Splash screen shows.
2. `App.Startup` reads `appsettings.json` &rarr; `PlatformConfig` resolves `%LOCALAPPDATA%` paths.
3. `SettingsService.LoadAsync` rehydrates tenants, sites, domains, defaults.
4. `PowerShellService` constructor sets `PSHOME` + `PSModulePath`, registers `Invoke-WrappTokenRefresh` cmdlet, opens the `RunspacePool`, imports `Wrapp.Packager`.
5. `MsalAuthService.GetCachedAccountsAsync` lists any already-signed-in accounts.
6. `MainWindow` shows with `General` view selected.

### 5.2 New bundle &rarr; save

1. User picks framework (Appease / PSADT) on `General` view. `BundleService.CreateBundleAsync` writes folder skeleton into a `TempWorkspaceService` directory.
2. User edits metadata, drops an installer (`InstallerDrop.cs` handles MSI / EXE / .intunewin), edits scripts in `ScriptsView` (Monaco buffers).
3. User clicks Save Bundle &rarr; `GeneralViewModel.SaveBundleAsync`:
   - `ValidateForSaveAsync` runs `Test-PackagerConfig` against both targets.
   - `BundleService.CreateBundleAsync` writes real `Script/Config.json` + scripts + icon.
   - `BundleSaving` event raises so Monaco buffers flush to disk.
   - `GitService.CommitAllAsync` commits via fire-and-forget.

### 5.3 Sign in + run packaging

1. User opens Account drop-down &rarr; picks a tenant &rarr; `AccountViewModel.SignInAsync` calls `MsalAuthService.InitializeForTenantAsync` then `AcquireTokenAsync` (WAM broker preferred).
2. `_ps.InjectMsalToken(token)` seeds the pool's runspaces with `$Global:AccessToken` etc.
3. User goes to Run view, picks Intune or SCCM, picks tenant/site.
4. `RunViewModel.RunAsync` &rarr; `AcquireAndInjectTokenAsync` (fresh token + handle for mid-run refresh) &rarr; `PowerShellService.PackageAsync` invokes `Invoke-{Intune,SCCM}Packager`.
5. Output streams marshal through `IProgress<string>` to the log view; the script's emitted `PSCustomObject` phase events drive `PhaseDetector` &rarr; `PackageProgressItems`.
6. Run-end: `PackageAsync`'s outer `finally` clears injected globals via a fresh `PowerShell` instance + unregisters the MSAL handle (Phase 12 / S-3 + S-8).

### 5.4 Inventory &rarr; import existing app

1. User goes to Inventory, picks an Intune tenant.
2. `InventoryViewModel.RefreshAsync` calls `AppInventoryService.GetIntuneAppsAsync` (paginated list) then `PreloadIntuneDetailsAsync` (`$batch` detail fetch) then `ResolveGroupNamesForTenantAsync` then `ResolveNestedGroupsForTenantAsync` &mdash; three background phases with progress.
3. User selects an app &rarr; clicks Import.
4. `InventoryViewModel.Import.cs`:
   - Build `AppConfigModel` from the inventory detail.
   - Full-clone path: download `.intunewin` via `AppInventoryService.DownloadRawContentAsync` &rarr; decrypt via `IntuneWinDecryptOrchestrator` (vault lookup by `(tenantId, appId)` first, brute-force fallback) &rarr; `BundleService.PopulateFromDecryptedContentAsync`.
   - Save into a new bundle root + switch to General view.

### 5.5 `.intunewin` decrypt (Tools)

1. User points Tools.Decrypt at a `.intunewin` file. Picks key source.
2. `ToolsViewModel.DecryptAsync` builds a step tree (Prepare &rarr; Keys &rarr; Attempt &rarr; Validate).
3. `IntuneWinDecryptOrchestrator` runs the chosen strategy. Brute-force iterates `EncryptionKeyStoreService.LoadAllDevOpsKeysAsync()` until one decrypts the IV+key blob successfully.
4. Output is the extracted folder; signature validation confirms it's a valid archive.

### 5.6 Settings save

1. User edits a tenant's PasswordBox &rarr; `SettingsView.xaml.cs` writes `pb.SecurePassword` directly to `entry.ClientSecret` (a `SecureString` since Phase 15).
2. User clicks Save &rarr; `SettingsViewModel.SaveAsync` &rarr; `SettingsService.SavePreferencesAsync`:
   - For each tenant: if `ClientSecret is { Length: > 0 }`, run `WithPlaintext(secret, SecretProtection.Encrypt)` to produce a `dpapi:v2:...` cipher. Else preserve the existing `ClientSecretCipher` byte-for-byte.
   - Serialise `AppSettings` JSON. Write atomically.
   - After write: dispose + null the in-memory `SecureString`, copy the cipher back onto the live entry so it survives until next save.

---

## 6. Dependencies

### 6.1 .NET runtime + NuGet packages

| Package | Version | Purpose |
|---|---|---|
| .NET | `net8.0-windows` | Target framework |
| `CommunityToolkit.Mvvm` | 8.4.0 | `[ObservableProperty]` source generator, `RelayCommand`, `ObservableObject` |
| `MaterialDesignThemes` | 5.3.0 | Material icons + a few controls |
| `WPF-UI` | 4.2.0 | Fluent window chrome, navigation rail, FluentDialog primitives |
| `Microsoft.Web.WebView2` | 1.0.3800.47 | Monaco editor host |
| `Microsoft.PowerShell.SDK` | 7.4.4 | In-process PS 7.4 runspace |
| `Microsoft.Identity.Client` | 4.67.2 | MSAL core |
| `Microsoft.Identity.Client.Broker` | 4.67.2 | WAM broker support |
| `Microsoft.Identity.Client.Extensions.Msal` | 4.67.2 | `MsalCacheHelper` (used for DevOps cache only) |
| `Git-Windows-Minimal` | 2.53.0 | Embedded MinGit (~25&nbsp;MB) |

### 6.2 Vendored modules + assets (under `modules/` and `src/Wrapp.GUI/Assets/`)

| Item | Location | Purpose |
|---|---|---|
| Wrapp.Packager | `modules/Wrapp.Packager/` | First-party PS module (this repo owns it) |
| IntuneWin32App 1.5.0 | `modules/IntuneWin32App/1.5.0/` | Microsoft-EndpointMgr Intune packaging module (vendored copy with Wrapp patches for handle-based refresh) |
| Appease v2.3 template | `modules/appease-template/` | Bundle scaffolding for the Appease framework |
| PSADT v4 template | `modules/psadt-template/` | Bundle scaffolding for PSADT v4 |
| Monaco editor | `src/Wrapp.GUI/Assets/monaco/vs/**` | Fully offline Monaco bundle &mdash; no CDN |
| Script templates | `src/Wrapp.GUI/Templates/` | Embedded resources copied to `%LOCALAPPDATA%\Wrapp\Templates` on first run |

### 6.3 External system dependencies

| Dependency | Used for | Required? |
|---|---|---|
| Microsoft Configuration Manager Admin Console | `ConfigurationManager.psd1` module + CMSITE PSDrive provider for SCCM packaging | Required for SCCM target only |
| Azure DevOps Services / Server | Git-backed encryption-key vault | Required for full-clone decrypt + key round-trip |
| Microsoft Graph (`graph.microsoft.com`) | All Intune operations | Required for Intune target |
| `IntuneWinAppUtil.exe` | `.intunewin` creation | Bundled by `IntuneWin32App` module |
| Windows DPAPI | Secret encryption (settings + MSAL cache) | Built into Windows; broken roamed profiles surface `SecretEncryptionException` |
| Windows Account Manager (WAM) | Preferred MSAL auth broker | Built into Win10 / Win11; falls back to system browser if unavailable |
| `Shell32` | Icon extraction from `.exe` | Built into Windows |

### 6.4 Output / portable layout

When published (`dotnet publish -r win-x64 --self-contained true`):

```text
Wrapp.exe                        # main executable + .NET runtime
runtimes/win/lib/net8.0/Modules/ # core PS modules
Modules/
  Wrapp.Packager/                # first-party PS module
  IntuneWin32App/1.5.0/          # vendored Intune packaging
appease-template/                # Appease bundle template
psadt-template/                  # PSADT bundle template
mingit/                          # portable git
Assets/monaco/vs/                # Monaco editor (offline)
defaults.example.json            # shipped defaults
appsettings.json                 # paths overrides
```

User-local state lives at `%LOCALAPPDATA%\Wrapp\`:

- `app.log`, `app.1.log` &hellip; `app.5.log` &mdash; rotated app logs.
- `settings.json` &mdash; preferences (DPAPI-encrypted secrets).
- `msal-cache.bin` &mdash; MSAL token cache (DPAPI-encrypted with v2 envelope).
- `Templates/` &mdash; user-editable script template overrides.
- `Temp/` &mdash; draft bundle workspaces.

---

## 7. Quality / verification surfaces

| Tool | Scope |
|---|---|
| `tests/Wrapp.GUI.Tests/` xUnit suite | 490 tests covering models, services, infrastructure, lint rules. |
| `Lint/SourceLintTests.cs` | 7 mechanical architectural invariants enforced at test time: empty-catch documentation, async-void containment, no-raw-Serialize file writes, `WindowInteropHelper` centralisation, `new Progress<T>` centralisation, `BundlePaths` (no inline `Script/Config.json`), `Test-PackagerConfig` enum-validation centralisation, PowerShell token-logging prohibition. |
| `dotnet build` | 0 warnings, 0 errors. |
| Per-phase CHANGELOG | One entry per shipped build with file references and test deltas. |

---

*This document corresponds to repository state at commit **`9998fe2`** (`0.6.0.0235-beta`, 2026-06-04).*
