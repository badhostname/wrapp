# SCCM Inventory Detail Rework

## Current Issues

1. **Missing data**: Dependencies, supersedence, requirements, detection rules, categories, dates all empty
2. **No platform-specific visibility**: Intune-specific sections (developer, owner, notes, scope tags) show blank for SCCM
3. **Wrong buttons**: Download .intunewin shown for SCCM apps (impossible), Save Icon shown but SCCM has no icon API
4. **Uninstall command bug**: Script searches for `InstallCommandLine` for both install AND uninstall
5. **Missing SCCM-specific fields**: ObjectPath (folder), Technology, ContentLocation, MaxExecuteTime, InstallBehavior, LogonRequirement, IsSuperseded, IsExpired, IsEnabled

## Data Sources

The richest source is `SDMPackageXML` on each app -- an XML blob containing deployment types, detection rules, dependencies, supersedence, install/uninstall commands, requirements. This needs XML parsing.

### What to fetch:

**From Get-CMApplication:**
- DateCreated, DateLastModified
- IsSuperseded, IsExpired, IsEnabled
- ObjectPath (console folder)
- LocalizedCategoryInstanceNames (categories)
- NumberOfDeploymentTypes

**From Get-CMDeploymentType (first DT):**
- Technology (MSI, Script, AppV)
- Parse SDMPackageXML for:
  - InstallCommandLine, UninstallCommandLine, RepairCommandLine
  - Detection rules (MSI product code, registry, file, script)
  - MaxExecuteTime, EstimatedInstallTime
  - InstallBehavior, LogonRequirement
  - ContentLocation

**From SDMPackageXML:**
- Dependencies (DeploymentTypeRule references)
- Supersedence (Supersedes nodes)
- Requirements (OS version, disk space, etc.)

**From Get-CMApplicationDeployment:**
- CollectionName, CollectionID
- DesiredConfigType (1=Required, 2=Available)
- StartTime, EnforcementDeadline
- OverrideServiceWindow, UserUIExperience

## Detail Pane Changes

### Hide Intune-only sections for SCCM:
- Developer, Owner, Notes, Information URL, Privacy URL, IsFeatured
- Scope Tags
- Return Codes
- Download .intunewin button
- Save Icon button (SCCM has no icon API)

### Show SCCM-specific sections:
- Console Folder (ObjectPath)
- Technology (MSI/Script/AppV)
- Content Location (UNC path)
- Install Behavior (ForUser/ForSystem)
- Logon Requirement
- Max Execute Time
- Status flags (IsSuperseded, IsExpired, IsEnabled)

### Shared sections (show for both):
- App info (name, publisher, version, description)
- Created/Modified dates
- Install/Uninstall commands
- Detection rules (parse from SDMPackageXML)
- Dependencies (parse from SDMPackageXML)
- Supersedence (parse from SDMPackageXML)
- Assignments (collections for SCCM, groups for Intune)
- Export JSON, Import to Wrapp (metadata only for SCCM)

## Implementation Priority

1. Fix the PS script to fetch more data (dates, categories, status flags, folder)
2. Parse SDMPackageXML for detection rules, dependencies, supersedence
3. Add platform visibility toggles in XAML
4. Disable/hide Intune-only buttons for SCCM
5. Add SCCM-specific fields to the detail pane
