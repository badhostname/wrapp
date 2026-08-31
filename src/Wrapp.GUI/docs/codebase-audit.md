# Codebase Audit -- IntunePackager Repository
**Date:** 2026-03-11
**Scope:** Full repository at the repository root

---

## Repository Size Breakdown

| Directory | Size | Purpose |
|-----------|------|---------|
| `_ref/UniGetUI/` | 1.9 GB | Reference repo (inspiration/research) |
| `GUI/src/Wrapp.GUI/bin/` | 1.1 GB | Build output (Debug + Release + Publish) |
| `IntuneManagement/` | 120 MB | Reference repo (MSAL auth patterns) |
| `packagefactory/` | 49 MB | Reference repo (package templates) |
| `2.3/Script/` | 11 MB | PowerShell module + scripts |
| `GUI/src/Wrapp.GUI/obj/` | 20 MB | Build cache |
| `IntuneWin32App/` | 933 KB | Source copy of vendored module |
| **Total** | **~3.2 GB** | |

---

## FINDINGS: Items to Remove

### REMOVE -- Unused NuGet Package (currently: no, see correction below)

~~MaterialDesignThemes 5.3.0~~ -- **CORRECTION: This IS actively used.** The DateTimePickerField
custom control (`Controls/DateTimePickerField.xaml`) uses MaterialDesignThemes for its Calendar
and Clock controls. It imports `xmlns:materialDesign` namespace and references `CustomColorTheme`,
`CalendarAssist`, `materialDesign:Clock`, and `MaterialDesignFlatMidBgButton` style. The
code-behind also imports `MaterialDesignThemes.Wpf`. This package must stay.

### REMOVE -- Obsolete Vendored Module: IntuneWin32App 1.4.4

| | |
|---|---|
| **Path** | `2.3/Script/Modules/IntuneWin32App/1.4.4/` |
| **Files** | 75 |
| **Size** | ~1.5 MB |
| **Reason** | Fully replaced by v1.5.0. No code anywhere references 1.4.4. Uses obsolete MSAL.PS auth pattern. |
| **Risk** | None. v1.5.0 is the only version imported by Wrapp.Packager. |

### REMOVE -- Deprecated Vendored Module: MSAL.PS

| | |
|---|---|
| **Path** | `2.3/Script/Modules/MSAL.PS.deprecated/` |
| **Files** | 51 |
| **Size** | ~1 MB |
| **Reason** | Not imported by any script or module. Already marked deprecated by directory name. Was removed from Wrapp.Packager when IntuneWin32App moved to native OAuth in v1.5.0. |
| **Risk** | None. |

### REMOVE -- Backup Monolith Scripts

| File | Size | Reason |
|------|------|--------|
| `2.3/Script/IntunePackager.v2.ps1` | 72 KB (1181 lines) | Original v2.0 monolith. Fully replaced by Wrapp.Packager module. Not referenced anywhere. |
| `2.3/Script/SCCMPackager.v1.ps1` | 17 KB (455 lines) | Original v1.6 SCCM monolith. Fully replaced by Wrapp.Packager SCCM functions. Not referenced anywhere. |

### REMOVE -- Empty Directory

| | |
|---|---|
| **Path** | `2.3/B/` |
| **Reason** | Completely empty. Appears to be a leftover placeholder. |

### REMOVE -- Unused Asset Files

| File | Reason |
|------|--------|
| `GUI/src/Wrapp.GUI/Assets/burrito-256.png` | Not referenced in any XAML or code. Same content as burrito.png. |
| `GUI/src/Wrapp.GUI/Assets/burrito-lores.ico` | Not referenced in any XAML or code. burrito.ico is the active app icon. |

### REMOVE -- Reference Repositories (Decision Required)

These are cloned repos used for research/reference during development. None are referenced by
build processes or runtime code. They account for **2.07 GB** combined.

| Directory | Size | Contents | Verdict |
|-----------|------|----------|---------|
| `_ref/UniGetUI/` | 1.9 GB | UniGetUI package manager (WPF reference impl) | Safe to remove. Research-only. |
| `IntuneManagement/` | 120 MB | Intune settings management tool (MSAL auth patterns) | Safe to remove. Referenced only in docs. |
| `packagefactory/` | 49 MB | Package template tooling | Safe to remove. Not used by build or runtime. |
| `IntuneWin32App/` | 933 KB | Source copy of the 1.5.0 module | Safe to remove. Already vendored at `2.3/Script/Modules/IntuneWin32App/1.5.0/`. |

**Note:** If you want to keep these for future reference, consider moving them to a separate
location outside the project tree.

### CONSIDER REMOVING -- Development Utility

| | |
|---|---|
| **Path** | `GUI/get-symbols.ps1` |
| **Size** | 1.4 KB |
| **Purpose** | Wpf.Ui reflection utility for discovering SymbolRegular enum values |
| **Verdict** | Dev-only tool. Not needed at runtime. Low priority but not part of the deliverable. |

---

## FINDINGS: Items to Keep (Confirmed Active)

### GUI -- NuGet Packages (9 packages, all actively used)

| Package | Version | Used By |
|---------|---------|---------|
| CommunityToolkit.Mvvm | 8.4.0 | All 13 ViewModels (ObservableObject, RelayCommand, ObservableProperty) |
| Wpf.Ui | 4.2.0 | All XAML views (FluentWindow, TitleBar, controls, theming) |
| MaterialDesignThemes | 5.3.0 | DateTimePickerField control (Calendar, Clock) |
| Microsoft.Web.WebView2 | 1.0.3800.47 | MonacoService, MonacoTabService, MonacoDiffService, ConfigJsonView, ScriptsView |
| Microsoft.PowerShell.SDK | 7.4.4 | PowerShellService, PowerShellTokenBridge, ConnectionChecker |
| Microsoft.Identity.Client | 4.67.2 | MsalAuthService (all 4 auth flows) |
| Microsoft.Identity.Client.Broker | 4.67.2 | MsalAuthService (WAM broker) |
| Microsoft.Identity.Client.Extensions.Msal | 4.67.2 | MsalAuthService (DPAPI token cache) |
| Git-Windows-Minimal | 2.53.0 | GitService (bundled MinGit for git operations) |

### GUI -- Source Files (67 .cs files, 23 .xaml files -- all active)

| Category | Count | Status |
|----------|-------|--------|
| ViewModels | 13 | All wired in App.xaml.cs |
| Services | 21 | All instantiated or used statically |
| Models | 12 | All referenced by services/VMs |
| Views (sections) | 10 | All in MainWindow navigation |
| Views (dialogs) | 6 | All opened by ViewModels |
| Views (windows) | 2 | MainWindow + SplashWindow |
| Controls | 1 | DateTimePickerField |
| Helpers | 1 | ScrollBubbling |
| Infrastructure | 2 | GlobalUsings.cs, AssemblyInfo.cs |
| Templates (embedded) | 4 | Appease, Install, Uninstall, Detect scripts |
| Themes | 2 | Dark.xaml, Light.xaml |
| Assets (used) | 2 | burrito.ico, burrito.png |
| Config | 1 | appsettings.json |

### GUI -- Styles, Converters, Brushes (all active)

- **5 value converters** (CountToVisibility, InverseBoolToVisibility, BoolToVis, ConnectionStateToBrush, OutcomeToBrush) -- all referenced in XAML
- **17+ named styles** (NavItem, FieldLabel, FormCard, AccentButtonStyle, ToolbarBtn, etc.) -- all referenced
- **72+ theme brushes per theme** (surface, status, DataGrid, text, Wpf.Ui overrides) -- all referenced
- **2 thickness resources** (FieldRowMargin, FieldColGap) -- both referenced
- **1 geometry resource** (DisabledHatchBrush) -- referenced in SCCMView, IntuneAssignmentDialog

### GUI -- Documentation (all relevant)

| File | Purpose |
|------|---------|
| `GUI/docs/file-drop-icon-bundle-flow.md` | Bundle workflow reference |
| `GUI/src/Wrapp.GUI/docs/account-ui-comparison.md` | Auth UI design decisions |
| `GUI/src/Wrapp.GUI/docs/tenant-site-persistence.md` | Persistence architecture audit |
| `GUI/src/Wrapp.GUI/Themes/theme-styling.md` | Theme system reference |

### GUI -- Tests (4 test classes)

| File | Status |
|------|--------|
| `GUI/tests/Wrapp.GUI.Tests/ConfigFileServiceTests.cs` | Active |
| `GUI/tests/Wrapp.GUI.Tests/LogEntryTests.cs` | Active |
| `GUI/tests/Wrapp.GUI.Tests/ModuleDefaultsTests.cs` | Active |
| `GUI/tests/Wrapp.GUI.Tests/ValidationIssueTests.cs` | Active |

### GUI -- Publish Output (330 MB total)

| Item | Size | Verdict |
|------|------|---------|
| Wrapp.exe | 222 MB | Expected for self-contained .NET 8 WPF + WebView2 + MSAL + PS SDK |
| Wrapp.pdb | 281 KB | Debug symbols (can exclude from distribution if desired) |
| mingit/ | 108 MB | Required by GitService. Bundled via NuGet. Cannot easily slim down. |
| runtimes/ | 12 KB | PS module metadata. Harmless, auto-included by .NET SDK. |
| app.log | 1.8 KB | Runtime artifact. Regenerated on launch. |

### PowerShell Module -- Wrapp.Packager v3.2.0 (all active)

| Category | Count | Status |
|----------|-------|--------|
| Public functions | 16 | All exported in manifest, all called |
| Private functions | 17 | All dot-sourced, all called by public functions |
| Config | 1 | Defaults.psd1 |
| Module files | 2 | .psd1 manifest + .psm1 loader |

### PowerShell -- Top-Level Scripts (all active)

| Script | Status |
|--------|--------|
| IntunePackager.ps1 | Active CLI wrapper |
| SCCMPackager.ps1 | Active CLI wrapper |
| Appease.ps1 | Active base framework (1745 lines) |
| InstallScript.ps1 | Active template |
| UninstallScript.ps1 | Active template |
| DetectScript.ps1 | Active template |
| UpdaterScript.ps1 | Active (app update orchestrator, dot-sources UpdaterCore) |
| UpdaterCore.ps1 | Active (update engine, 1732 lines) |
| Config.json | Active working config |
| Config.Template.json | Active documented template |
| SETUP.md | Active setup guide |
| Readme.rtf | Active documentation |

### PowerShell -- Vendored Module (keep)

| Module | Version | Status |
|--------|---------|--------|
| IntuneWin32App | 1.5.0 | Required. Imported by Invoke-IntunePackager at runtime. |

### PowerShell -- Shortcuts (all active)

| Shortcut | Purpose |
|----------|---------|
| Appease Console.lnk | Launch Appease.ps1 |
| IntunePackager.lnk | Launch IntunePackager.ps1 |
| IntunePackager - Validate.lnk | Launch validation mode |

---

## Summary: Cleanup Impact

| Category | Items | Space Saved |
|----------|-------|-------------|
| Reference repos (_ref, IntuneManagement, packagefactory, IntuneWin32App root) | 4 dirs | ~2.07 GB |
| IntuneWin32App 1.4.4 | 75 files | ~1.5 MB |
| MSAL.PS.deprecated | 51 files | ~1 MB |
| Backup scripts (.v2, .v1) | 2 files | ~89 KB |
| Unused assets (burrito-256, burrito-lores) | 2 files | ~60 KB |
| Empty directory (2.3/B/) | 1 dir | 0 |
| Dev utility (get-symbols.ps1) | 1 file | 1.4 KB |
| **Total removable** | **~131 items** | **~2.07 GB** |

### What stays clean

- All 67 C# source files are actively used
- All 23 XAML files are actively used
- All 9 NuGet packages are actively used
- All 33 PowerShell module functions are actively used
- All styles, converters, brushes, and themes are actively used
- All embedded templates are actively used
- All documentation is relevant

The codebase is well-structured with no dead code in the source files themselves. The cleanup
items are all peripheral: old vendored modules, backup scripts, reference repos, and a couple
of unused asset variants.
