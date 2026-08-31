# Wrapp Roadmap - v0.3 and Beyond

## Approved for Implementation (v0.3.0)

### Badges / Error Counts
- Nav sidebar badges on Intune/SCCM tabs showing total warning+error count across all packages and assignments
- Per-package badge in the package list showing errors in that package + its assignments
- Badge on the Assignments button (when package selected) showing total assignment errors
- Per-assignment badge showing errors specific to that assignment
- Same pattern for SCCM deployments

### Single Package Retry
- On the Run tab, allow re-running a single failed package without resetting all state
- "Retry" button per-package in the results summary

### Assignment/Deployment Templates
- Save assignment field presets as named templates
- Load template to pre-fill new assignment/deployment
- Per-tenant or global templates

### Config Schema Gap Fixes
- Expose UseAzCopy, AzCopyWindowStyle fields in Intune packages
- Expose UnattendedInstall/UnattendedUninstall in Intune packages
- Add URL pattern validation for InformationURL/PrivacyURL
- Add MaxInstallTime range validation [1-1440]
- Add Architecture/MinWindowsRelease fallback chain awareness
- Expose Console logging config

### Log Search/Filter
- Ctrl+F in Logs tab to filter log lines by keyword
- Highlight matching lines

### Copy Assignments/Deployments
- Clone an existing assignment/deployment row (same as existing package clone)
- Pre-populate all fields from source

### Deployment Plan Preview
- Before Run, show summary of what will happen
- X packages to Y tenants, Z assignments
- Highlight new vs update vs skip
- Confirm before execution

### Refactoring
- Extract shared ViewModel logic into PackageViewModelBase<TPackage>
- Consolidate converters into ConverterLibrary.cs
- Create reusable DataGrid templates in App.xaml
- Standardize error handling with structured ValidationIssue everywhere
- Rename TenantCheckItem to SelectionCheckItem

---

## Future Ideas (Not Yet Approved)

### Deployed Apps Browser
- Query Graph API for existing Intune Win32 apps
- Query ConfigurationManager for existing SCCM apps
- Show name, version, assignment count
- Compare local config vs deployed version
- Bulk delete stale versions

### Batch Mode
- Queue multiple bundles for sequential deployment
- Select folder of bundles and run them all
- Per-bundle retry logic

### Version Increment Button
- One-click bump DotVersion (1.0.0 -> 1.0.1)
- Auto-sync underscore version

### Keyboard Shortcuts
- Ctrl+N = New Bundle
- Ctrl+O = Open Bundle
- Ctrl+S = Save Bundle
- Ctrl+Shift+S = Save As
- F5 = Run

### Local Detection Test
- Run detection script locally before deploying
- Show pass/fail result inline

### Export Features
- Export Assignment Matrix as CSV
- Export Deployment Report as CSV
- Generate Deployment Plan document

### Config Comparison
- Compare local config vs deployed app metadata
- Side-by-side diff of critical fields
