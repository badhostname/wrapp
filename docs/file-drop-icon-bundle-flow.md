# File Drop, Icon Extraction, and Bundle Save Flow

Complete reference for how Wrapp handles installer drops, icon extraction, draft vs active bundle saving, and icon selection.

---

## 1. File Drop Handling

### 1.1 Drop Zone

The drop zone is the entire `GeneralView` UserControl. XAML attributes:

```
AllowDrop="True"
DragEnter="UserControl_DragEnter"
DragOver="UserControl_DragOver"
DragLeave="UserControl_DragLeave"
Drop="UserControl_Drop"
```

**Files:** `Views/GeneralView.xaml` (lines 5-8), `Views/GeneralView.xaml.cs` (lines 19-88)

### 1.2 Validation (Before Drop)

`GeneralViewModel.ValidateDroppedPaths(string[] paths)` validates files using magic bytes -- no execution occurs.

| Type | Magic Bytes | Check |
|------|-------------|-------|
| EXE | `0x4D 0x5A` (MZ) | PE header |
| MSI/MSP | `0xD0 0xCF 0x11 0xE0` | OLE compound document |
| Folder | N/A | Must contain `Config.json` (via `BundleService.FindConfigJson`) |
| Image | N/A | `.png`, `.jpg`, `.jpeg`, `.ico` accepted by extension |

File reads use `FileShare.ReadWrite` (non-exclusive, read-only).

**File:** `ViewModels/GeneralViewModel.cs` (lines 123-146)

### 1.3 Visual Feedback Overlays

Three overlay states shown during drag:

- **Valid** (green checkmark): File type recognized, ready to drop
- **Invalid** (red X): Unsupported file type
- **Warning** (orange): Dropping installer onto an active (non-temp) bundle

The overlay uses `DragOverlayService.ApplyBlur()` to blur the background content.

**File:** `Views/GeneralView.xaml.cs` (lines 90-133)

### 1.4 Drop Handler Routing

`GeneralViewModel.HandleDropAsync(string[] paths)` routes by type:

```
FOLDER  --> Load existing bundle (FindConfigJson + LoadFromPathAsync)
EXE     --> Show "Apply Installer" vs "Extract Icon Only" dialog
MSI/MSP --> Extract MSI icon table, show MsiIconPickerDialog if icons found
IMAGE   --> ApplyIconFile() directly (no metadata change)
```

**File:** `ViewModels/GeneralViewModel.cs` (lines 535-667)

---

## 2. EXE Drop Flow

When an EXE is dropped, the user is shown a dialog with two choices:

1. **Apply Installer** - Extract metadata + icon, set up as the package installer
2. **Extract Icon Only** - Only extract and apply the icon, leave metadata untouched

### Apply Installer Path

1. `ClearInstallerFields()` resets all current installer data
2. `FileVersionInfo.GetVersionInfo(path)` extracts: ProductName, FileVersion, CompanyName
3. `IconExtractorService.Extract(path)` extracts the best-resolution icon
4. Metadata populates `App.Name`, `App.DotVersion`, `App.Version`, `App.Company`
5. Flow continues to draft/active bundle handling (Section 5)

### Extract Icon Only Path

1. `IconExtractorService.Extract(path)` extracts icon
2. Icon set on `InstallerIconSource`
3. If draft workspace: icon saved to disk immediately
4. If active bundle: icon deferred to next save
5. No metadata fields are changed

**File:** `ViewModels/GeneralViewModel.cs` (lines 565-587, 1026-1073)

---

## 3. MSI/MSP Drop Flow

1. `MsiPropertyService.GetIcons(path)` reads the MSI Icon table via Windows Installer API (P/Invoke)
2. If icons found: `MsiIconPickerDialog` displays all embedded icons for user selection
3. `MsiPropertyService.GetMsiMetadata(path)` extracts: ProductName, ProductVersion, Manufacturer
4. Selected MSI icon (if any) overrides the Shell32-extracted icon
5. Metadata populates `App.Name`, `App.DotVersion`, `App.Version`, `App.Company`
6. Flow continues to draft/active bundle handling (Section 5)

**Files:**
- `Services/MsiPropertyService.cs` (lines 99-169) - Icon table extraction
- `Views/MsiIconPickerDialog.xaml.cs` - Icon picker UI
- `Services/MsiPropertyService.cs` (lines 50-97) - Metadata extraction

---

## 4. Icon Extraction Methods

### 4.1 Shell32 API (EXE and MSI)

`IconExtractorService.Extract(string filePath)` uses `SHDefExtractIcon` from shell32.dll.

Resolution fallback chain:
1. 256x256 (modern Windows 10+ icon)
2. 48x48 (legacy)
3. 32x32 (smaller legacy)
4. GDI fallback via `System.Drawing.Icon.ExtractAssociatedIcon()`

Returns a frozen `BitmapSource` (XAML-bindable). All intermediate HICON handles are properly destroyed.

**File:** `Services/IconExtractorService.cs` (lines 33-53)

### 4.2 MSI Icon Table

`MsiPropertyService.GetIcons(string msiPath)` reads the OLE database directly.

- Opens MSI as read-only via `MsiOpenDatabase()`
- SQL query: `SELECT Name, Data FROM Icon`
- Each icon's binary data converted via `ConvertIcoToBitmapSource()`
- Multi-frame ICO files: selects largest frame by pixel area

Returns `List<(string Name, BitmapSource Icon)>`.

**File:** `Services/MsiPropertyService.cs` (lines 99-189)

### 4.3 Image File Loading

`IconService.LoadIcon(string configDir, string relativeIconPath)` loads PNG/ICO from disk.

- Uses `BitmapImage` with `CacheOption.OnLoad` (fully in-memory, file handle released)
- Returns frozen `ImageSource`

**File:** `Services/IconService.cs` (lines 71-90)

---

## 5. Draft Mode vs Active Bundle Mode

The app has two distinct saving behaviors depending on whether the workspace is temporary (draft) or a real saved bundle.

### 5.1 How Mode Is Determined

```csharp
// Draft: config path starts with temp root
private bool IsTempWorkspace()
    => _configPath.StartsWith(TempWorkspaceService.RootPath, ...);

// Active: has a config loaded from a real (non-temp) location
public bool IsActiveBundle => HasConfig && !IsTempWorkspace();
```

**File:** `ViewModels/GeneralViewModel.cs` (lines 756-763)

### 5.2 Draft Mode (Temporary Workspace)

**When:** User clicks "New Package" on splash screen, or drops an installer with no bundle loaded.

**Temp workspace created by** `TempWorkspaceService.CreateAsync()`:

```
%TEMP%\Wrapp\{GUID}\
    B/              (binaries)
    Script/         (Config.json + template scripts)
    Shortcuts/      (empty, populated on real save)
    .git/           (git repo initialized)
```

**Behavior when installer is dropped:**

1. Checks if dropped installer is identical to current B/ file (content comparison via `IconService.FilesAreIdentical()`)
2. If different: deletes old binary + old icon, copies new binary to B/
3. If same: skips binary copy, refreshes metadata only
4. Icon always saved immediately to `Icon/{SanitizedAppName}.png`
5. No deferred/pending state - all writes are immediate

**File:** `ViewModels/GeneralViewModel.cs` (lines 916-958), `Services/TempWorkspaceService.cs` (lines 18-49)

### 5.3 Active Bundle Mode

**When:** User opened an existing bundle from a real directory.

**Behavior when installer is dropped:**

1. If same app name as current bundle, shows `IconPickerDialog` (keep old icon vs use new icon)
2. All disk writes are **deferred** - nothing written until user saves
3. State stored in pending fields:
   - `_pendingInstallerPath` - source path of dropped installer
   - `_pendingIconBitmap` - icon to be written
   - `_userPickedOldIcon` - whether user chose to keep existing icon
4. UI updates immediately (metadata + icon preview)
5. Pending writes flushed during `SaveBundleAsync()` via `FlushPendingInstallerToDisk()`

**File:** `ViewModels/GeneralViewModel.cs` (lines 959-981, 1185-1239)

### 5.4 Active Bundle Warning Dialog

When dropping an installer onto an active bundle, the user sees a comparison:

| Current Bundle | New Installer |
|----------------|---------------|
| Name: Firefox  | Name: Firefox |
| Company: Mozilla | Company: Mozilla |

If the app name matches, `IconPickerDialog` shows:
- Left: Current (old) icon from the bundle
- Right: New icon extracted from the dropped installer
- User clicks to choose which to keep

**File:** `Views/IconPickerDialog.xaml.cs` (47 lines)

---

## 6. Save Bundle Flow

### 6.1 Validation

`ValidateForSaveAsync()` checks that fields required by the directory format are populated:
- Default format: `{Company}\{Name}\{Version}`
- Validates Company, Name, Version, DotVersion, Language as needed

**File:** `ViewModels/GeneralViewModel.cs` (lines 388-409)

### 6.2 Save vs Save As

| Action | Behavior |
|--------|----------|
| **Save Bundle** | Saves to current location (if active bundle) or prompts for location (if draft) |
| **Save Bundle As** | Always prompts for new output folder |

Both follow the same core flow after determining the output directory.

### 6.3 Bundle Creation

`BundleService.CreateBundleAsync()` creates the complete folder structure:

```
bundleDirectory/
    B/              (binaries - installers)
    Script/
        Config.json
        InstallScript.ps1
        UninstallScript.ps1
        DetectScript.ps1
        Appease.ps1
    Shortcuts/
        Install.cmd
        Uninstall.cmd
        Detect.cmd
    Icon/
        {AppName}.png
```

**Steps:**
1. Create directories (B/, Script/, Shortcuts/, Icon/)
2. Write `Config.json` via `ConfigFileService.SaveAsync()`
3. Write template scripts (only if missing - preserves user edits)
4. Write batch wrappers (`.cmd` files calling PowerShell scripts)
5. Write icon PNG via `SaveIconAsPng()`

**File:** `Services/BundleService.cs` (lines 62-122)

### 6.4 Template Script Processing

Scripts are embedded resources in `Wrapp.GUI.Templates.*`. Token replacement via `ApplyTokens()`:

| Token | Source |
|-------|--------|
| `{{Company}}` | App.Company |
| `{{Name}}` | App.Name |
| `{{Version}}` | App.Version (underscore format) |
| `{{DotVersion}}` | App.DotVersion |
| `{{Language}}` | App.Language |
| `{{EXEFile}}` | Installer filename |
| `{{MSIFile}}` | Installer filename |
| `{{AppeaseGUID}}` | App.AppeaseGUID |

Scripts are written only on first save. Subsequent saves preserve user edits.

**File:** `Services/BundleService.cs` (lines 141-170)

### 6.5 Flushing Deferred Changes (Active Bundle Only)

`FlushPendingInstallerToDisk(bundleDir)` runs during save:

1. Clean B/ folder (delete all files except the new installer to prevent stale binaries)
2. Copy `_pendingInstallerPath` to `B/`
3. Write `_pendingIconBitmap` to `Icon/` path
4. Clear pending state (`_pendingInstallerPath = null`, `_pendingIconBitmap = null`)

**File:** `ViewModels/GeneralViewModel.cs` (lines 1185-1239)

### 6.6 Post-Save

1. Fire `BundleSaving` event (ScriptsViewModel saves edited script content)
2. Git commit with message: `Save: {Company} {Name} {Version}`
3. If moved from temp to real location, delete temp directory

---

## 7. Icon File Naming

### 7.1 Extracted Icon Naming

`ResolveIconFileName(extractedAppName)`:

1. Sanitize app name via `BundleService.Sanitize()` (removes invalid filename chars)
2. Append `.png` extension
3. Fallback to `appIcon.png` if name is empty

Example: "Microsoft Word 2019" produces `microsoft-word-2019.png`

**File:** `ViewModels/GeneralViewModel.cs` (lines 823-828)

### 7.2 Manual Icon File (Browse/Drop)

`IconService.CopyToIconFolder()`:

1. Content deduplication (if identical file already exists, reuse path)
2. Filename collision handling: appends `_1`, `_2`, etc.
3. Preserves original extension

**File:** `Services/IconService.cs`

### 7.3 Directory Format Tokens

Output directory resolved from settings format string (default: `{Company}\{Name}\{Version}`).

Available tokens: `{Company}`, `{Name}`, `{Version}`, `{DotVersion}`, `{Language}`

**File:** `Services/BundleService.cs` (lines 16-28)

---

## 8. Image Drop Flow (PNG/JPG/ICO)

When an image file is dropped (not an installer):

1. `IconService.CopyToIconFolder(path, bundleRoot, iconFolderName)` copies file
2. `App.IconFile` set to relative path (e.g., `Icon/custom-icon.png`)
3. Image loaded to `InstallerIconSource` for preview
4. No metadata fields are changed
5. No installer binary is affected

**File:** `ViewModels/GeneralViewModel.cs` (lines 1080-1107)

---

## 9. Temp Workspace Cleanup

`TempWorkspaceService.CleanOld()` runs on app shutdown:

- Scans `%TEMP%\Wrapp\` for directories older than 24 hours
- Deletes stale temp workspaces
- Prevents disk space accumulation from abandoned drafts

**File:** `Services/TempWorkspaceService.cs` (lines 55-73)

---

## 10. State Tracking Summary

| Field | Purpose | Scope |
|-------|---------|-------|
| `_pendingInstallerPath` | Deferred installer binary path | Active bundle only |
| `_pendingIconBitmap` | Deferred icon to write | Active bundle only |
| `_userPickedOldIcon` | User chose to keep existing icon | Active bundle only |
| `_msiPickedIcon` | User-selected MSI embedded icon | Per-drop session |
| `HasPendingInstallerChanges` | Whether deferred writes exist | Active bundle only |
| `IsDirty` | Model differs from disk snapshot | Always |

Change detection: `DispatcherTimer` fires every 750ms, serializes model, compares against disk snapshot.

---

## 11. Flow Diagram

```
USER DROPS FILE
    |
    v
ValidateDroppedPaths() -- magic byte check
    |
    v
[Show Overlay: Valid / Invalid / Warning]
    |
    v
HandleDropAsync()
    |
    +---> FOLDER: FindConfigJson() --> LoadFromPathAsync()
    |
    +---> EXE: Show "Apply Installer" vs "Extract Icon Only"
    |     |
    |     +---> Icon Only: ApplyIconFromExe()
    |     +---> Apply: ApplyInstallerFile()
    |
    +---> MSI/MSP: GetIcons() --> MsiIconPickerDialog --> ApplyInstallerFile()
    |
    +---> IMAGE: ApplyIconFile()

ApplyInstallerFile()
    |
    +---> ClearInstallerFields()
    +---> Extract metadata (FileVersionInfo or MsiPropertyService)
    +---> Extract icon (IconExtractorService or MSI picked icon)
    |
    +---> [DRAFT MODE]
    |     +---> Compare binaries (skip if identical)
    |     +---> Copy installer to B/ immediately
    |     +---> Save icon to Icon/ immediately
    |
    +---> [ACTIVE BUNDLE MODE]
          +---> Show IconPickerDialog (if same app name)
          +---> Defer all writes to _pending* fields
          +---> UI updates immediately

USER CLICKS SAVE
    |
    +---> ValidateForSaveAsync()
    +---> Determine output directory
    +---> CreateBundleAsync() [Config.json, scripts, shortcuts, icon]
    +---> FlushPendingInstallerToDisk() [if active bundle]
    +---> Fire BundleSaving event [scripts saved]
    +---> Git commit
    +---> Clean temp workspace [if moved from draft]
```

---

## Key Files Reference

| Component | Path |
|-----------|------|
| Drop handler (code-behind) | `Views/GeneralView.xaml.cs` |
| Main ViewModel (all logic) | `ViewModels/GeneralViewModel.cs` |
| Icon extraction (Shell32) | `Services/IconExtractorService.cs` |
| MSI properties + icons | `Services/MsiPropertyService.cs` |
| Bundle creation | `Services/BundleService.cs` |
| Config file I/O | `Services/ConfigFileService.cs` |
| Temp workspaces | `Services/TempWorkspaceService.cs` |
| Icon utilities | `Services/IconService.cs` |
| MSI icon picker dialog | `Views/MsiIconPickerDialog.xaml.cs` |
| Icon comparison picker | `Views/IconPickerDialog.xaml.cs` |
| App settings model | `Models/AppSettings.cs` |
| Template scripts | `Templates/InstallScript.ps1`, etc. |
