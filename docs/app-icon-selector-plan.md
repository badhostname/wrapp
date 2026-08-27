# App Icon Selector — analysis & implementation plan

Status: PLANNED (analysis only, per direction — no implementation yet)
Date: 2026-08-11

## What exists today

- **App-level icon** (General view): 128px preview + **Browse...** button
  (`BrowseIconCommand` → `ApplyIconFile` in `GeneralViewModel.FileOps.cs:37`),
  plus drag-drop of `.png/.jpg/.jpeg/.ico` onto the view, plus automatic
  extraction from dropped installers (EXE version resources / MSI Icon table
  with the `MsiIconPickerDialog` chooser).
- `ApplyIconFile` copies the file into the bundle's icon folder
  (`IconFolderName` setting, default `Icon/`), sets `App.IconFile` to the
  relative path, and refreshes `InstallerIconSource`.
- **No way to clear** the icon: once set, the only path is overwriting with a
  different file. `App.IconFile` and the copied file linger.
- Per-package icons (Intune/SCCM views) have their own "Use App Icon" /
  Browse affordances that read the app-level icon — they inherit whatever
  this rework improves, unchanged.

## Goals (user request)

1. Icon can be **cleared/deleted** when nothing available is liked.
2. Wrapp ships a **library of generic app icons** at the best resolution we
   can produce, offered as a second selection source.
3. Browse button becomes **"Select App Icon"** opening a centered popup with
   three affordances: drag-drop zone, file browse, generic library.
4. If loading the whole library is heavy: search bar + first-50 cap, or a
   separate popup for the library.

## Library source — recommendation

"The WPF icons we already include" maps to two icon packs already in the
dependency tree (no new packages, no new assets to vendor):

| Source | Count | Form | Fit |
|---|---|---|---|
| **MaterialDesignThemes 5.3.0** (`PackIconKind`) | ~7,000 | vector path data | **Recommended** — biggest catalogue, includes app-category glyphs (browsers, media, security, dev tools) |
| WPF-UI `SymbolRegular` (Fluent) | ~2,000 | icon font glyphs | Fallback/secondary — consistent with app chrome but sparser on "app-like" imagery |
| Segoe MDL2 Assets font | ~1,300 | system font glyphs | Skip — licensing is fine for UI chrome, but shipping rendered glyphs as *app icons into Intune/SCCM* is exactly the boundary we shouldn't test; Material icons are Apache-2.0 which is unambiguous |

**Resolution:** the sources are vectors, so "max resolution" is whatever we
rasterize. Intune Company Portal displays up to 256×256; render at **512×512
PNG** (crisp on any portal/SC surface, still tiny files) via
`RenderTargetBitmap` at save time. Nothing is pre-rendered or shipped — the
"library" costs zero package size because it's the already-shipped vector
data, rasterized once when the user picks one.

**Tile treatment:** a bare monochrome glyph makes a poor Company Portal icon
(transparent PNG, invisible on light surfaces). Render the glyph white on a
rounded-rect tile with a selectable background color (small palette of ~8
brand-safe colors + the org accent, default `#9AC9CF`). This is the one
design decision worth a screenshot review before building.

## UX design

### Entry point (General view)

- **Browse... → "Select App Icon"** button (same position under the preview).
- Small **Clear** glyph button (✕) overlaid on the preview corner, visible
  only when an icon is set. Clearing: confirm → delete the file from the
  bundle's icon folder, blank `App.IconFile`, null `InstallerIconSource`,
  dirty-flag the bundle. (Per-package `IconFile` fields referencing the
  deleted file surface through existing validation.)

### The selector popup (new `AppIconSelectDialog`, FluentDialog-hosted)

Single centered dialog, three zones (mirrors the ActionPickerDialog card
pattern):

1. **Drop zone** — reuses the established drag-drop pipeline
   (`ValidateDroppedPaths` image branch + `ApplyIconFile`), with the same
   overlay affordance language as the General view.
2. **Browse file...** — existing `OpenFileDialog` flow, unchanged.
3. **Choose from library...** — opens the library picker. Given the catalogue
   size, make this its OWN dialog (the user's "separate pop-up" option) so
   the first dialog stays instant.

### The library picker (new `IconLibraryDialog`)

- Search box (filters `PackIconKind` enum names, case-insensitive substring;
  300ms debounce).
- Results in a **virtualized** `ListBox` + `WrapPanel`-style panel showing
  **50 at a time** ("Show more" appends 50) — the user's cap suggestion,
  which also makes virtualization almost moot. Each tile: 48px vector
  preview + name.
- Right rail: 128px preview of the selected glyph on the chosen tile color,
  color swatch row, **Use icon** button.
- Perf note: rendering 50 `Path` elements from `PackIconDataFactory` data is
  milliseconds; the enum name list (~7,000 strings) loads once, lazily. No
  startup cost, no shipped assets.
- On confirm: rasterize 512×512 (glyph centered at ~70% tile), save as
  `{IconFolderName}/{AppName-or-kind}.png` via the existing
  `ApplyIconFile` path so history/dirty/preview behave identically to a
  browsed file.

## Work breakdown

| # | Item | Files | Size |
|---|---|---|---|
| 1 | `ClearIconCommand` + preview overlay ✕ + file deletion | GeneralViewModel(.FileOps), GeneralView.xaml | S |
| 2 | `AppIconSelectDialog` (3-way source picker) + button rename | new Views/AppIconSelectDialog.xaml(.cs), GeneralView.xaml | M |
| 3 | `IconLibraryDialog` (search + paged tiles + color + preview) | new Views/IconLibraryDialog.xaml(.cs), small VM | M-L |
| 4 | Glyph→PNG rasterizer (`IconService.RenderGlyphTile`) | Services/IconService.cs | S |
| 5 | Help keys + tooltips for all new surfaces (guardrail tests enforce) | HelpContent.xaml | S |
| 6 | Tests: rasterizer output size/format, clear-icon leaves no orphan file, library search filter | tests | S |

Estimated: one focused session. No dependency, packaging, or settings-schema
changes.

## Open decisions (before building)

1. Tile background palette + default color (screenshot review recommended).
2. Icon filename on library pick: `{AppName}.png` (consistent with extracted
   icons — recommended) vs the glyph name.
3. Whether the Clear affordance also appears inside the selector popup
   ("Remove current icon" row) — recommended yes, both places.
