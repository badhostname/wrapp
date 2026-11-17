# Where a bundle is

A bundle is wherever its `Script\Config.json` is. Nothing stores that location
and nothing rebuilds it from the app's Company, Name or Version. This page is
the contract the GUI, the `Wrapp.Packager` module and the Appease runtime all
follow, so a bundle can be saved anywhere, moved in Explorer, and still be
found, run and packaged.

## The layout

```
<root>\                         the bundle root: the folder that holds Script\Config.json
    Script\
        Config.json             the bundle's configuration
        InstallScript.ps1       the scripts (Appease) - the editor works on these
        ...
    B\                          binaries, with any sub-folders (Appease)
    Files\                      binaries (PSADT)
    Shortcuts\                  .lnk shortcuts; their arguments are relative to the bundle
    Icon\                       the icon (or wherever App.IconFile points, relative to the root)
```

A bundle made before the `Script\` layout keeps `Config.json` and the scripts at
the root (layout `Legacy`). Saving it in Wrapp migrates it.

## The rules

1. **The root is found, never stored.** To find it from any path, look for the
   marker there, then in each parent in turn, and stop at the share or drive
   root (eight levels at most). A bundle inside a bundle resolves to the
   nearest one.
2. **Every other part of the bundle is at a fixed relative location under the
   root.** Consumers compose `root + relative`, never `share + names`.
3. **The folder names are never compared with the app's fields.** Company,
   Name, Version and DotVersion come from `Config.json`. The naming convention
   (`Settings > Bundle > Directory Format`) is a suggestion offered when a
   bundle is saved; the operator can take it or make the folders themselves.
4. **Nothing outside the bundle says where it is.** No domain, tenant,
   preference or user default is consulted to find a bundle, so a `Config.json`
   with a wrong value in some other section cannot stop a run from finding
   its own files.

## Finding a bundle

| From | Call |
|---|---|
| PowerShell, any path inside the bundle | `Resolve-WrappBundle -Path <root \| Config.json \| anything inside>` |
| The module's entry points | `Invoke-WrappPackaging`, `Invoke-WrappIntune` and `Invoke-WrappSccm` call it, so `-ConfigPath` accepts the root, the `Config.json` or any inside path |
| C#, the GUI | `BundleService.FindBundleConfig(anyPath)` returns the `Config.json`; `BundleService.GetBundleRoot(configPath)` the root |

`Resolve-WrappBundle` returns `Root`, `ConfigPath`, `ScriptDir`, `BinaryDir`
(`B` or `Files`), `ShortcutDir`, `IconDir`, `Layout` (`Script` or `Legacy`),
`Framework` (`Appease` or `PSADT`) and `App` (Company, Name, Version,
DotVersion from `Config.json`).

## What derives from the real path

- **SCCM content location** is the bundle root (`Add-CMAppFromConfig
  -PackagePath`, always supplied by `Invoke-WrappSccm`). The preflight checks
  that it exists and is a network path.
- **The Intune source** for the `.intunewin` is the bundle root; the setup
  file is `Script\InstallScript.ps1`.
- **The `.intunewin` output folder** is `Script.IntunePackager.Output.Path`
  in `Config.json`, written by Wrapp at every save from Settings (`Bundle >
  Intune Package Output`) or from the bundle's own choice in the Intune
  view. A relative value is relative to the bundle root: `..\Output` is
  "beside the bundle", an `Output` folder next to the version folder, so it
  travels with the bundle. The module (`Resolve-WrappIntuneWinOutput`) reads
  only that field; a bundle without it falls back to the tenant's old
  `IntuneWinPath`, then to beside the bundle; an unreachable root falls
  back to beside the bundle with a preflight warning. One file per run,
  shared by every package in the bundle.
- **`Domain.AppFolder`** is written on every save from the real path. When the
  bundle is under the domain's `isDistPath`, it is the bundle's parent folder
  relative to that root (`Template\Ricoh\Universal_Print_Driver_PCL6` for a
  bundle at `\\dist\packages$\Template\Ricoh\Universal_Print_Driver_PCL6\4_45_0_0`),
  so `isDistPath\AppFolder\<version folder>` is exactly the bundle whatever
  the depth. Otherwise it is the two folder names above the bundle. Each
  domain gets its own value. Nothing in the Appease template reads it; it is
  kept for scripts outside this repository.
- **The Appease runtime** on the endpoint takes `$PSScriptRoot` as the script
  folder and its parent as the bundle; the cache robocopies that bundle.

## What is deliberately not done

- No bundle path is written into `Config.json`, settings or user defaults.
- Wrapp does not move or rename bundles. Explorer does; Wrapp finds the result.
- The module never recomposes `share\Company\Name\Version`; the only
  composition left is the `.intunewin` output folder, which is a setting of
  its own (see the Settings help).
