# Wrapp.Packager Module Readiness Assessment
**Date:** 2026-03-11 | **Module Version:** 3.2.0 | **Target Rename:** Wrapp.Module

This document covers the full state of the PowerShell module, its dependencies, Config.json
contract, GUI sync status, and a plan for making the module fully production-ready for both
CLI and GUI usage.

---

## 1. Architecture Overview

```
Wrapp GUI (WPF)
    |
    v
PowerShellService.cs  -->  RunspacePool  -->  Wrapp.Packager module
    |                                              |
    +-- MsalAuthService.cs (token acquisition)     +-- IntuneWin32App 1.5.0 (vendored)
    +-- PowerShellTokenBridge.cs (token injection)  +-- ConfigurationManager (SCCM Console)
    +-- ConnectionChecker.cs (preflight probes)     +-- Write-Log (CMTrace XML)
    |                                              |
    v                                              v
Config.json  <----  BundleService.cs          Graph API / SCCM WMI
```

**Three entry points:**
1. **Wrapp GUI** -- RunViewModel calls Invoke-IntunePackager / Invoke-SCCMPackager via RunspacePool
2. **CLI (Intune)** -- IntunePackager.ps1 wrapper script
3. **CLI (SCCM)** -- SCCMPackager.ps1 wrapper script

All three consume the same Config.json and Wrapp.Packager module.

---

## 2. Intune Workflow -- 13 Phases

| Phase | Function | What Happens | Status Reportable |
|-------|----------|-------------|-------------------|
| 1 | Invoke-IntunePackager | Load Config.json | File parse |
| 2 | Test-PackagerConfig | Validate config schema | Errors/Warnings/Issues |
| 3 | Initialize-LogFile | Create CMTrace log | Log path |
| 4 | Update-ConfigSchema | Auto-generate AppeaseGUID | Migration applied |
| 5 | Resolve-TenantId | Detect tenant from registry or param | Tenant ID |
| 6 | Import IntuneWin32App | Unblock files, import 1.5.0 module | Module loaded |
| 7 | Resolve-CreatorName | Get AD display name | Creator name |
| 8 | Connect-IntunePackager | Auth (Interactive/DeviceCode/Secret/Cert) | Auth success |
| 8.5 | Test-IntunePackagerPreflight | Graph probe, categories, tags, groups, filters, deps, cycles | Issues[] |
| 9 | Test-Win32AppCollisions | Check existing app names | Collisions[] |
| 10 | New-IntuneWin32Package | Create .intunewin file | Path, Size |
| 11 | Add/Update-IntuneWin32AppFromConfig | Create or update app + detection + icon + deps + supersedence | DeployedApps{} |
| 11.5 | Set-Win32AppDependency/Supersedence | Link relationships | Applied/Errors |
| 12 | Set-Win32AppAssignment | Create assignments per intent/type/group | Applied/Skipped/Errors |
| 13 | Return result | $Result object | Success, DeployedApps, Collisions, LogFile, Errors |

**Return object:**
```powershell
@{ Success=[bool]; DeployedApps=[hashtable]; Collisions=[array]; LogFile=[string]; Errors=[List[string]] }
```

**Module readiness: COMPLETE** -- All 13 phases implemented with validation mode (-Validate).

---

## 3. SCCM Workflow -- 12 Phases

| Phase | Function | What Happens | Status Reportable |
|-------|----------|-------------|-------------------|
| 1 | Invoke-SCCMPackager | Load Config.json | File parse |
| 2 | Test-PackagerConfig | Validate config (ScriptType='SCCMPackager') | Errors/Warnings/Issues |
| 3 | Initialize-LogFile | Create CMTrace log | Log path |
| 4 | Update-ConfigSchema | Auto-generate AppeaseGUID | Migration applied |
| 5 | Resolve domain | Read $env:USERDNSDOMAIN, load Domain config | Domain name |
| 6 | Unblock files | Remove Zone.Identifier streams | Files unblocked |
| 7 | Resolve-CreatorName | Get AD display name | Creator name |
| 8 | Connect-SCCMPackager | Import ConfigurationManager module, detect site | SiteCode, SiteDrive |
| 8.5 | Test-SCCMPackagerPreflight | Site connectivity, DP groups, collections, deps, icons | Issues[] |
| 9 | Test-CMAppCollisions | Check existing app names in SCCM | Collisions[] |
| 10a | New-CMDetectionFromConfig | Inject config into DetectScript.ps1 | Script content |
| 10b | Add-CMAppFromConfig | Create app + deployment type + install behaviors + deps | CM app object |
| 10c | Set-CMContentDistribution | Distribute to DP groups | Success/Errors |
| 11 | Set-CMAppDeployment | Create deployments to collections | Applied/Skipped/Errors |
| 12 | Return result | $Result object | Success, DeployedApps, Collisions, LogFile, Errors |

**Return object:** Same structure as Intune.

**Module readiness: COMPLETE** -- All 12 phases implemented with validation mode.

---

## 4. Config.json Field Map -- Complete Reference

### 4.1 App Section (shared across targets)

| Field | Type | Required | Consumed By |
|-------|------|----------|------------|
| Company | string | YES | Both (Publisher) |
| Name | string | YES | Both (base name) |
| DotVersion | string | YES | Both (AppVersion / SoftwareVersion) |
| Version | string | YES | Both (folder paths) |
| AppeaseGUID | string | AUTO | Intune (Notes JSON), auto-generated |
| Language | string | NO | GUI/templates only |
| EXEFile | string | NO | Module validation (determines package type) |
| MSIFile | string | NO | Module validation (determines package type) |
| Dependencies | object[] | NO | SCCM fallback (Add-CMAppFromConfig) |
| DetectRunning | object[] | NO | SCCM fallback (install behaviors) |
| IconFile | string | NO | GUI only (not consumed by module) |
| URL | string | NO | Documentation only |

### 4.2 Script.Detect (detection template data)

| Field | Type | Purpose |
|-------|------|---------|
| Expression_Default | string | Boolean expression combining test symbols |
| Expression_{variant} | string | Per-PackageOption variant expressions |
| Tests[] | object[] | Path/Command-based detection tests |
| Tests[].Symbol | string | Variable name for expression evaluation |
| Tests[].Path | string | File/registry path (mutually exclusive with Command) |
| Tests[].Command | string | PS command to execute (mutually exclusive with Path) |
| Tests[].Property | string | Property to evaluate on result |
| Tests[].Operator | string | Comparison operator (-ge, -gt, -le, -lt, -eq, -ne) |
| Tests[].Value | string | Expected value |

### 4.3 Script.IntunePackager

| Field | Type | Required | Purpose |
|-------|------|----------|---------|
| Tag | string | YES | Log file naming |
| TerminateOnCollision | bool | NO | Abort on any name collision |
| Categories | string[] | NO | Default categories for all packages |
| Packages[] | object[] | YES | Package definitions (see 4.5) |

### 4.4 Script.SCCMPackager

| Field | Type | Required | Purpose |
|-------|------|----------|---------|
| Tag | string | YES | Log file naming |
| Packages[] | object[] | YES | Package definitions (see 4.6) |

### 4.5 Intune Package Fields

| Field | Type | Required | Inheritance | IntuneWin32App Param |
|-------|------|----------|-------------|---------------------|
| AppName | string | YES | - | DisplayName |
| Comment | string | NO | "{Company} {AppName}" | Description |
| IconFile | string | NO | - | Icon (via New-IntuneWin32AppIcon) |
| PackageOption | string | NO | - | Appended to commands |
| UpdateMode | string | NO | 'Create' | Routing: Add vs Set |
| ExistingAppID | string | COND | - | ID (for Update/UpdateContent) |
| InstallCommand | string | YES* | - | InstallCommandLine |
| UninstallCommand | string | YES* | - | UninstallCommandLine |
| InstallExperience | string | NO | - | InstallExperience |
| RestartBehavior | string | NO | - | RestartBehavior |
| MaximumInstallationTimeInMinutes | int | NO | 60 | MaximumInstallationTimeInMinutes |
| AllowAvailableUninstall | bool | NO | false | AllowAvailableUninstall |
| Architecture | string | NO | Pkg > Tenant > 'x64' | RequirementRule |
| MinimumSupportedWindowsRelease | string | NO | Pkg > Tenant > 'W11_21H2' | RequirementRule |
| Categories | string[] | NO | Pkg > IntunePackager-level | CategoryName |
| ScopeTags | string[] | NO | Pkg > Tenant-level | ScopeTagName |
| Developer | string | NO | Defaults.psd1 | Developer |
| Owner | string | NO | 'Digital Workplace' | Owner |
| CompanyPortalFeaturedApp | bool | NO | false | CompanyPortalFeaturedApp |
| InformationURL | string | NO | - | InformationURL |
| PrivacyURL | string | NO | - | PrivacyURL |
| DetectionRules | object[] | NO | Falls back to Script.Detect | DetectionRule |
| AdditionalRequirementRules | object[] | NO | - | AdditionalRequirementRule |
| CustomReturnCodes | object[] | NO | - | ReturnCode |
| Dependencies | object[] | NO | Max 100 | Add-IntuneWin32AppDependency |
| Supersedence | object[] | NO | Max 10 | Add-IntuneWin32AppSupersedence |

### 4.6 SCCM Package Fields

| Field | Type | Required | CM Cmdlet Param |
|-------|------|----------|----------------|
| AppName | string | YES | Name (New-CMApplication) |
| Name | string | YES | DeploymentTypeName (Add-CMScriptDeploymentType) |
| AppComment | string | NO | Comment |
| Icon | string | NO | IconLocationFile |
| Publisher | string | NO | Manufacturer |
| SoftwareVersion | string | NO | SoftwareVersion |
| Owner | string | NO | Owner |
| Description | string | NO | Description |
| PackageOption | string | NO | Injected into detection script |
| InstallCommand | string | YES | InstallCommand |
| UninstallCommand | string | NO | UninstallCommand |
| RepairCommand | string | NO | RepairCommand |
| InstallationBehaviorType | string | NO | InstallationBehaviorType |
| LogonRequirementType | string | NO | LogonRequirementType |
| UserInteractionMode | string | NO | UserInteractionMode |
| EstimatedRuntimeMins | int | NO | EstimatedRuntimeMins |
| MaximumAllowedRuntimeMins | int | NO | MaximumRuntimeMins |
| SlowNetworkDeploymentMode | string | NO | SlowNetworkDeploymentMode |
| ContentFallback | bool | NO | AllowClientsToUseFallbackSourceLocation |
| InstallBehaviors | object[] | NO | Add-CMDeploymentTypeInstallBehavior |
| Dependencies | object[] | NO | Set-CMAppDependencyFromConfig |

### 4.7 IntuneTenant Section

| Field | Type | Required | Purpose |
|-------|------|----------|---------|
| {Key} = TenantID GUID | - | - | Dictionary key |
| Comment | string | NO | Description |
| Name | string | NO | GUI display name (not consumed by module) |
| Domain | string | YES* | Must match Domain section key -- **MISSING FROM GUI** |
| ClientID | string | COND | App registration (required for Secret/Cert) |
| AuthFlow | string | NO | Interactive/DeviceCode/ClientSecret/ClientCert |
| ClientSecret | string | COND | When AuthFlow=ClientSecret |
| CertThumbprint | string | COND | When AuthFlow=ClientCert (40-char hex) |
| Architecture | string | NO | Default for packages |
| MinimumSupportedWindowsRelease | string | NO | Default for packages |
| IntuneWinPath | string | YES | Output path for .intunewin files |
| IconFolder | string | NO | Icon file location |
| ScopeTags | string[] | NO | Default scope tags |
| Assignments | object[] | NO | Assignment rules (see 4.8) |

### 4.8 Intune Assignment Fields

| Field | Type | Required | Condition |
|-------|------|----------|-----------|
| AppName | string | YES | Must match a Package.AppName |
| Intent | string | YES | available/required/uninstall/availablewithoutenrollment |
| Type | string | YES | AllDevices/AllUsers/Group |
| GroupID | string | COND | When Type=Group (GUID or name) |
| GroupMode | string | COND | When Type=Group: include/exclude |
| Notification | string | NO | showAll/showReboot/hideAll |
| DeliveryOptimizationPriority | string | NO | notConfigured/foreground |
| AvailableTime | string | NO | DateTime (not for uninstall) |
| DeadlineTime | string | NO | DateTime (only for required) |
| UseLocalTime | string | NO | true/false (only for required) |
| EnableRestartGracePeriod | bool | NO | Only for required |
| RestartGracePeriodInMinutes | int | NO | 1-20160 (requires grace enabled) |
| RestartCountDownDisplayInMinutes | int | NO | 1-240 (requires grace enabled) |
| RestartNotificationSnoozeInMinutes | int | NO | 1-712 (requires grace enabled) |
| FilterName | string | NO | Assignment filter name |
| FilterMode | string | COND | Include/Exclude (requires FilterName) |
| AutoUpdateSupersededApps | string | NO | Only with Intent=available |

### 4.9 SCCMSite Section

| Field | Type | Required | Purpose |
|-------|------|----------|---------|
| {Key} = SiteCode | - | - | Dictionary key (e.g., "CB1") |
| Comment | string | NO | Description |
| AppFolder | string | NO | SCCM console folder path |
| IconFolder | string | NO | Icon file location |
| DeploymentGroups | string[] | YES | DP group names for content distribution |
| Deployments | object[] | NO | Deployment rules (see 4.10) |

### 4.10 SCCM Deployment Fields

| Field | Type | Required | CM Cmdlet Param |
|-------|------|----------|----------------|
| AppName | string | YES | ApplicationName |
| Collection | string | YES | CollectionName |
| Comment | string | NO | Comment |
| DeployAction | string | NO | DeployAction (Install/Uninstall) |
| DeployPurpose | string | NO | DeployPurpose (Required/Available) |
| UserNotification | string | NO | UserNotification |
| AvailableDateTime | string | NO | AvailableDateTime |
| DeadlineDateTime | string | NO | DeadlineDateTime |
| TimeBaseOn | string | NO | TimeBaseOn (LocalTime/UTC) |
| ApprovalRequired | bool | NO | ApprovalRequired |
| OverrideServiceWindow | bool | NO | OverrideServiceWindow |
| RebootOutsideServiceWindow | bool | NO | RebootOutsideServiceWindow |
| SendWakeupPacket | bool | NO | SendWakeupPacket |

### 4.11 Domain Section

| Field | Type | Required | Purpose |
|-------|------|----------|---------|
| {Key} = Domain FQDN | - | - | Dictionary key |
| isDistPath | string | YES | UNC path to app distribution root |
| AppFolder | string | YES | Relative path under isDistPath |
| TagFolder | string | NO | Log file output path |

---

## 5. IntuneWin32App 1.5.0 API Surface (Vendored Dependency)

### Functions Used by Wrapp.Packager

| Wrapp.Packager Function | IntuneWin32App Cmdlet | Purpose |
|---------------------|----------------------|---------|
| Connect-IntunePackager | Connect-MSIntuneGraph | Auth (4 flows) |
| Invoke-TokenRefreshIfNeeded | Test-AccessToken, Connect-MSIntuneGraph -Refresh | Token refresh |
| New-IntuneWin32Package | New-IntuneWin32AppPackage | Create .intunewin |
| Add-IntuneWin32AppFromConfig | Add-IntuneWin32App | Create app |
| Add-IntuneWin32AppFromConfig | New-IntuneWin32AppIcon | Icon object |
| Add-IntuneWin32AppFromConfig | New-IntuneWin32AppRequirementRule | Base requirement |
| Update-IntuneWin32AppFromConfig | Set-IntuneWin32App | Update metadata |
| Update-IntuneWin32AppFromConfig | Update-IntuneWin32AppPackageFile | Update binary |
| Set-Win32AppAssignment | Add-IntuneWin32AppAssignmentAllDevices | Assign all devices |
| Set-Win32AppAssignment | Add-IntuneWin32AppAssignmentAllUsers | Assign all users |
| Set-Win32AppAssignment | Add-IntuneWin32AppAssignmentGroup | Assign to group |
| Set-Win32AppDependencyFromConfig | New-IntuneWin32AppDependency | Create dep object |
| Set-Win32AppDependencyFromConfig | Add-IntuneWin32AppDependency | Apply deps |
| Set-Win32AppSupersedenceFromConfig | New-IntuneWin32AppSupersedence | Create supersedence object |
| Set-Win32AppSupersedenceFromConfig | Add-IntuneWin32AppSupersedence | Apply supersedence |
| Test-Win32AppCollisions | Get-IntuneWin32App | Query by name |
| Test-IntunePackagerPreflight | Get-IntuneWin32App | Verify connectivity |
| Test-IntunePackagerPreflight | Get-IntuneWin32AppCategory | Validate categories |
| Remove-IntuneWin32AppSafe | Get-IntuneWin32App, Remove-IntuneWin32App | Safe deletion |
| New-DetectionRuleFromConfig | New-IntuneWin32AppDetectionRuleMSI | MSI detection |
| New-DetectionRuleFromConfig | New-IntuneWin32AppDetectionRuleFile | File detection |
| New-DetectionRuleFromConfig | New-IntuneWin32AppDetectionRuleRegistry | Registry detection |
| New-DetectionRuleFromConfig | New-IntuneWin32AppDetectionRuleScript | Script detection |
| New-RequirementRuleFromConfig | New-IntuneWin32AppRequirementRuleFile | File requirement |
| New-RequirementRuleFromConfig | New-IntuneWin32AppRequirementRuleRegistry | Registry requirement |
| New-RequirementRuleFromConfig | New-IntuneWin32AppRequirementRuleScript | Script requirement |
| New-ReturnCodeFromConfig | New-IntuneWin32AppReturnCode | Return code |
| Resolve-EntraGroupId | Invoke-IntuneGraphRequest | Group lookup |

### ConfigurationManager Cmdlets Used by Wrapp.Packager

| Wrapp.Packager Function | CM Cmdlet |
|---------------------|-----------|
| Connect-SCCMPackager | Import-Module, Get-PSDrive |
| Add-CMAppFromConfig | New-CMApplication, Get-CMApplication, Move-CMObject |
| Add-CMAppFromConfig | Add-CMScriptDeploymentType, Get-CMDeploymentType |
| Add-CMAppFromConfig | Add-CMDeploymentTypeInstallBehavior |
| Set-CMAppDeployment | New-CMApplicationDeployment |
| Test-CMAppCollisions | Get-CMApplication |
| Test-SCCMPackagerPreflight | Get-CMSite, Get-CMDistributionPointGroup, Get-CMCollection, Get-CMApplication |
| Set-CMAppDependencyFromConfig | Get-CMApplication, Get-CMDeploymentType |
| Set-CMAppDependencyFromConfig | New-CMDeploymentTypeDependencyGroup, Add-CMDeploymentTypeDependency |
| Set-CMContentDistribution | Start-CMContentDistribution |

---

## 6. GUI/Module Sync Issues

### CRITICAL

**6.1 IntuneTenant.Domain field missing from GUI model**
- Config.Template.json documents `Domain` as required per tenant
- Module's Invoke-IntunePackager Phase 5 reads `IntuneTenant.{id}.Domain` to resolve DomainConfig
- GUI's IntuneTenantEntry class (AppConfigModel.cs) does NOT define this property
- ConfigFileService does NOT serialize/deserialize it
- **Impact:** Module will fail at Phase 5 if Domain field is missing from Config.json
- **Fix:** Add Domain property to IntuneTenantEntry, wire in ConfigFileService read/write, add UI field

### MODERATE

**6.2 Assignment AppName field naming**
- Config.Template documents TargetAppID/TargetAppName as alternatives for dependencies
- GUI serializes assignments with `AppName` only (not TargetAppName)
- Module reads `AppName` from assignments -- this works, but template docs are misleading
- **Fix:** Update Config.Template.json to match actual field name

**6.3 GUI-produced fields not in template**
- App.IconFile, AssignmentEntry.Label, SCCMDeploymentEntry.Label, IntuneTenantEntry.Name
- Module silently ignores these (no breakage)
- **Fix:** Document these GUI-only fields in template

### LOW

**6.4 Detection Rules RawJson storage**
- GUI stores complex detection rules as RawJson strings for round-trip fidelity
- Module parses these correctly via New-DetectionRuleFromConfig multi-type dispatch
- No action needed; pattern is working

---

## 7. Status Reporting for GUI

The GUI's RunViewModel needs to track progress through each phase. Current status reporting
capabilities in the module:

### What the module provides today

| Mechanism | Format | GUI Access |
|-----------|--------|------------|
| Write-Log | CMTrace XML to file | Read log file, parse entries |
| Console output | [timestamp] [LEVEL] message | Capture PS output streams |
| Return object | $Result hashtable | Read after completion |
| Validation Issues | FieldPath/Severity/Code/Message/Guidance | Structured for data binding |

### What the GUI needs for real-time phase tracking

The RunViewModel currently parses PS output streams for phase markers. The module's Write-Log
writes to both console and file. The PhaseDetector service in the GUI parses these console
lines to update the phase progress UI.

**Current phase detection pattern:** The module logs phase-start messages like
"Phase N: Description" which PhaseDetector matches.

**Recommendation:** This pattern works. No changes needed for basic progress tracking.
For richer status (per-package progress bars, upload percentage), the module would need to
emit structured progress records via Write-Progress or a custom output channel.

---

## 8. Plan: Module Readiness Work

### Phase A: Fix Critical Sync Issues

1. **Add IntuneTenant.Domain to GUI model**
   - Add `Domain` property to IntuneTenantEntry in AppConfigModel.cs
   - Add serialization in ConfigFileService (read/write)
   - Add UI field in SettingsView.xaml or TenantsView.xaml
   - Add validation: Domain must match a key in Config.Domain

2. **Update Config.Template.json**
   - Document GUI-only fields (IconFile, Label, Name)
   - Correct TargetAppID/TargetAppName documentation to match actual usage

### Phase B: Module Rename (Wrapp.Packager -> Wrapp.Module)

1. Rename module directory and manifest
2. Update .psd1 (ModuleName, RootModule, GUID)
3. Update CLI wrappers (IntunePackager.ps1, SCCMPackager.ps1) import paths
4. Update GUI's PowerShellService module import path
5. Add Invoke-WrappPackager with -Target (Intune/SCCM) routing

### Phase C: Production Hardening

1. **Pester tests** for validation functions (Test-PackagerConfig, Invoke-FieldSchema)
2. **Structured errors** with error IDs for programmatic handling
3. **Config schema versioning** (SchemaVersion field, migration chain)
4. **Progress reporting** via Write-Progress for GUI phase tracking

### Phase D: PSADT Extension (Future)

1. Add PSADT workflow support alongside Appease
2. Detect Invoke-AppDeployToolkit.ps1 presence in source folder
3. Adjust InstallCommand/UninstallCommand generation for PSADT pattern
4. No Config.json changes needed (commands are already freeform strings)

---

## 9. Validation Constants Reference

### Intune Enums (Defaults.psd1)

| Constant | Values |
|----------|--------|
| AuthFlows | Interactive, DeviceCode, ClientSecret, ClientCert |
| Architecture | x64, x86, arm64, x64x86, AllWithARM64 |
| MinOS | W10_1607 through W11_22H2 (14 values) |
| UpdateModes | Create, Update, UpdateContent |
| DetectionTypes | MSI, File, Registry, Script |
| RequirementTypes | File, Registry, Script |
| ReturnCodeTypes | success, softReboot, hardReboot, retry, failed |
| DependencyTypes | AutoInstall, Detect |
| SupersedenceTypes | Replace, Update |
| AssignmentIntents | available, required, uninstall, availablewithoutenrollment |
| AssignmentTypes | AllDevices, AllUsers, Group |
| FilterModes | Include, Exclude |
| ComparisonOperators | equal, notEqual, greaterThanOrEqual, greaterThan, lessThanOrEqual, lessThan |
| InstallExperience | system, user |
| RestartBehavior | allow, basedOnReturnCode, suppress, force |

### SCCM Enums (Defaults.psd1)

| Constant | Values |
|----------|--------|
| InstallationBehaviorType | InstallForUser, InstallForSystem |
| LogonRequirementType | OnlyWhenUserLoggedOn, WhetherOrNotUserLoggedOn, OnlyWhenNoUserLoggedOn |
| UserNotification | DisplayAll, DisplaySoftwareCenterOnly, HideAll |
| DeployAction | Install, Uninstall |
| DeployPurpose | Required, Available |

### Limits

| Limit | Value |
|-------|-------|
| Max Dependencies | 100 per app |
| Max Supersedence | 10 per app |
| Max Install Timeout | 1440 minutes (24 hours) |
| Restart Grace Period | 1-20160 minutes (1-14 days) |
| Countdown Display | 1-240 minutes |
| Snooze Duration | 1-712 minutes |
| Log File Rotation | 5 MB |

### Validation Issue Codes

| Code | Meaning |
|------|---------|
| MISSING_REQUIRED | Required field absent or empty |
| INVALID_ENUM_VALUE | Value not in allowed set |
| INVALID_URL | URL pattern validation failed |
| INVALID_PATTERN | Regex pattern failed |
| OUT_OF_RANGE | Numeric value outside min/max |
| WRONG_TYPE | Value is not expected .NET type |
| UNKNOWN_FIELD | Unrecognized parameter key |
| SELF_REFERENCE | App references itself in dependency/supersedence |
| CIRCULAR_DEP | Circular dependency chain detected |
| MUTUAL_SUPERSEDENCE | A supersedes B and B supersedes A |
| GUID_FORMAT | GUID format invalid |
| CERT_FORMAT | Certificate thumbprint format invalid |
| CERT_NOT_FOUND | Certificate not in local stores |
| ENTITY_NOT_FOUND | Referenced entity not found in Graph/SCCM |
| COUNT_EXCEEDED | Array exceeds maximum length |
| REQUIRES_FIELD | Field X requires field Y |
| INVALID_COMBINATION | Combination of field values not allowed |
| SCHEDULE_ORDER | Deadline not after available time |
| RANGE_EXCEEDED | One value exceeds another (e.g., countdown > grace) |

---

## 10. File Quick Reference

### Module Files
| File | Purpose |
|------|---------|
| 2.3/Script/Wrapp.Packager/Wrapp.Packager.psd1 | Module manifest |
| 2.3/Script/Wrapp.Packager/Wrapp.Packager.psm1 | Module loader (dot-sources Public/ + Private/) |
| 2.3/Script/Wrapp.Packager/Config/Defaults.psd1 | All defaults, validation constants, field schema |
| 2.3/Script/Wrapp.Packager/Public/*.ps1 | 16 exported functions |
| 2.3/Script/Wrapp.Packager/Private/*.ps1 | 17 internal functions |

### Vendored Dependencies
| File | Purpose |
|------|---------|
| 2.3/Script/Modules/IntuneWin32App/1.5.0/ | 39 public + 24 private functions, native OAuth |

### CLI Wrappers
| File | Purpose |
|------|---------|
| 2.3/Script/IntunePackager.ps1 | CLI entry point for Intune |
| 2.3/Script/SCCMPackager.ps1 | CLI entry point for SCCM |

### Config Files
| File | Purpose |
|------|---------|
| 2.3/Script/Config.json | Active working config |
| 2.3/Script/Config.Template.json | Annotated template with documentation |

### GUI Integration Points
| File | Purpose |
|------|---------|
| GUI/.../Services/PowerShellService.cs | RunspacePool, module import, script execution |
| GUI/.../Services/PowerShellTokenBridge.cs | Token injection into runspace |
| GUI/.../Services/ConnectionChecker.cs | Graph/SCCM connectivity probes |
| GUI/.../Services/PhaseDetector.cs | Parse PS output for phase progress |
| GUI/.../ViewModels/RunViewModel.cs | Orchestrates packaging run, displays phases |
| GUI/.../Models/AppConfigModel.cs | Config.json C# model |
| GUI/.../Services/ConfigFileService.cs | Config.json read/write |
| GUI/.../Services/BundleService.cs | Bundle export (Config.json + scripts + icon) |
