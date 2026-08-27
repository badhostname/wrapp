# Tenant and Site Persistence Audit

## Current State (as of v0.1.0-beta)

### Storage Locations

| Location | What it stores | Encryption |
|----------|---------------|------------|
| `%LOCALAPPDATA%\Wrapp\settings.json` | "Global" saved tenants/sites (app-wide defaults) | ClientSecret: DPAPI encrypted (`dpapi:BASE64`) |
| `<bundle>\Script\Config.json` | Per-bundle tenants/sites (live config) | ClientSecret: **plaintext** |
| `SettingsViewModel.DefaultTenants[]` | Hardcoded seed values (Dev, Prod tenants; PCB, LCB, CB1 sites) | N/A |

---

## Current Flow: App Startup

```
1. SettingsService.Load()
      --> reads %LOCALAPPDATA%\Wrapp\settings.json
      --> returns AppSettings with IntuneTenants[], SccmSites[]

2. Create ViewModels
      --> GeneralViewModel(settings)
      --> TenantsViewModel(generalVm, mainVm)
      --> SettingsViewModel(settings, tenantsVm)

3. settingsVm.RestoreSavedTenants()        [FIRST CALL]
      --> if settings has tenants AND config collections are empty:
            copy settings tenants into AppConfigModel.IntuneTenants
            copy settings sites into AppConfigModel.SccmSites
      --> if BOTH settings and config are empty:
            seed from DefaultTenants[] and DefaultSites[]

4. generalVm.LoadFromPathAsync(configPath)
      --> ConfigFileService.LoadAsync() parses Config.json
      --> populates AppConfigModel.IntuneTenants and .SccmSites from JSON
      --> fires ConfigLoaded event

5. ConfigLoaded handler calls settingsVm.RestoreSavedTenants()  [SECOND CALL]
      --> if config already has tenants/sites: SKIPPED (Count > 0)
      --> if config is empty (new workspace): copies from settings.json
```

**Problem**: Step 3 runs before Config.json is loaded, so it always populates the empty
AppConfigModel from settings.json. Then step 4 loads Config.json and REPLACES the
collections. Then step 5 runs again but is skipped because collections are now populated.
The first call at step 3 is wasted work.

---

## Current Flow: New Package (Temp Workspace)

```
1. TempWorkspaceService.CreateAsync()
      --> writes blank Config.json: "{}"
      --> copies embedded template scripts

2. generalVm.LoadFromPathAsync(configPath)
      --> ConfigFileService.LoadAsync("{}") --> empty AppConfigModel
      --> fires ConfigLoaded

3. ConfigLoaded handler --> RestoreSavedTenants()
      --> Config is empty, settings has tenants --> copies settings tenants into config
      --> Config is empty, settings has sites --> copies settings sites into config
```

**Result**: New workspaces get tenants/sites from settings.json (the "global" defaults).

---

## Current Flow: Open Existing Package

```
1. generalVm.LoadFromPathAsync(configPath)
      --> ConfigFileService.LoadAsync() parses existing Config.json
      --> IntuneTenants and SccmSites populated from JSON

2. ConfigLoaded handler --> RestoreSavedTenants()
      --> Config already has tenants (Count > 0) --> SKIPPED
      --> Config already has sites (Count > 0) --> SKIPPED
```

**Result**: Existing packages use their own Config.json tenants/sites. Good.

---

## Current Flow: Save Bundle

```
1. GeneralViewModel.SaveBundleAsync()
      --> BundleService.CreateBundleAsync()
      --> ConfigFileService.SaveAsync(config, path)
            --> serializes AppConfigModel to Config.json
            --> IntuneTenants and SccmSites written to JSON (plaintext secrets)

2. MainViewModel.SaveBundleWithFeedback()
      --> after bundle save: PersistTenantSettings?.Invoke()
      --> SettingsViewModel.PersistTenantSettings()
            --> copies current AppConfigModel tenants/sites into AppSettings
            --> encrypts ClientSecret with DPAPI
            --> SettingsService.Save() --> writes settings.json
```

**Problem**: Every bundle save also overwrites the "global" settings.json tenants/sites
with whatever the current bundle has. If you delete a tenant from one bundle, that
deletion propagates to ALL future new bundles via settings.json.

---

## Current Flow: Save Settings

```
1. SettingsViewModel.Save()
      --> PersistTenantSettings()
            --> same as above: copies live tenants/sites to settings.json
      --> SettingsService.Save()
```

**Problem**: "Save Settings" button also persists tenants/sites to settings.json,
even though the user may only intend to save theme/path preferences.

---

## Issues with Current Design

### 1. Two sources of truth
Tenants/sites live in BOTH settings.json AND Config.json. The merge logic in
`RestoreSavedTenants()` tries to reconcile them but creates confusion:
- Which one is authoritative?
- When does one override the other?
- Deleting a tenant in one bundle affects future bundles via settings.json propagation

### 2. Bundle save mutates global defaults
`PersistTenantSettings()` is called after every bundle save, which overwrites
settings.json with the current bundle's tenants/sites. This means:
- Bundle A has tenants [Dev, Prod]
- User removes Prod from Bundle A, saves
- settings.json now has [Dev] only
- New Bundle B starts with [Dev] only (Prod is gone globally)

### 3. Settings save is overloaded
"Save Settings" persists theme, paths, AND tenants/sites together. The user
clicking "Save Settings" may not expect their tenant edits to become the new
global defaults.

### 4. No separation between "app defaults" and "bundle-specific" tenants
There's no concept of "these are my org's standard tenants" vs "this bundle
targets a specific subset of tenants".

### 5. Plaintext secrets in Config.json
ClientSecret is DPAPI-encrypted in settings.json but stored as plaintext in
Config.json. This is a security concern if bundles are shared.

### 6. DefaultTenants[] are hardcoded
The seed defaults (Dev/Prod tenants, PCB/LCB/CB1 sites) are hardcoded in
SettingsViewModel.cs. These are org-specific values that should not be in code.

---

## Key Code Locations

| What | File | Lines |
|------|------|-------|
| RestoreSavedTenants (merge logic) | ViewModels/SettingsViewModel.cs | 71-147 |
| PersistTenantSettings (save to settings.json) | ViewModels/SettingsViewModel.cs | 224-256 |
| DefaultTenants/DefaultSites (hardcoded seeds) | ViewModels/SettingsViewModel.cs | 161-198 |
| SavedTenantEntry / SavedSiteEntry models | Models/AppSettings.cs | 36-66 |
| IntuneTenantEntry model | Models/AppConfigModel.cs | 511-528 |
| SCCMSiteEntry model | Models/AppConfigModel.cs | 377-386 |
| Config.json parsing (tenants/sites) | Services/ConfigFileService.cs | 55-63 |
| Config.json serialization (tenants/sites) | Services/ConfigFileService.cs | 99-114 |
| TempWorkspaceService (blank config) | Services/TempWorkspaceService.cs | 18-49 |
| Bundle save (triggers PersistTenantSettings) | ViewModels/MainViewModel.cs | 456-471 |
| ConfigLoaded handler (triggers RestoreSavedTenants) | App.xaml.cs | 193-197 |
| SecretProtection (DPAPI encrypt/decrypt) | Models/AppSettings.cs | 75-119 |
| TenantsViewModel (direct reference to AppConfigModel) | ViewModels/TenantsViewModel.cs | 37-41 |
