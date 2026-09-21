# ADR 001 - UI toolkit surface

**Status:** Accepted
**Date:** 2026-04-14

## Context

The pre-audit review flagged concern over UI toolkit sprawl. An earlier audit report
listed three toolkits (WPF-UI, MaterialDesignThemes, iNKORE) and called this
an "audit red flag" - reviewers were expected to ask why the app pulled in
three overlapping control libraries when one usually suffices.

Investigation at that time found:
- iNKORE was a transitive reference only (no direct NuGet reference, no DLLs
  in the output directory, no XAML / C# usages).
- A stale comment in `App.xaml.cs` referenced "iNKORE 0.10.2.1" as the source
  of a `ComboBoxHelper.UpdateCornerRadius` NRE, but no loaded assembly
  contained such a type. The guard was dead code.

The dead guard was removed. Actual current surface is **two toolkits**.

## Decision

Wrapp ships with exactly **two** WPF UI toolkits, with a narrow and documented
split of responsibilities:

### WPF-UI (`Wpf.Ui`) - version 4.2.0

Used for the **app shell and primary control surface**:

| Usage | Namespace |
|---|---|
| `FluentWindow` - main window chrome (Mica backdrop, custom TitleBar) | `xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"` |
| `TitleBar` | same |
| `ContentDialogHost` - hosts all modal dialogs (see `FluentDialog.cs`) | same |
| `SymbolIcon` - Fluent-style icons across toolbars and buttons | same |
| `ProgressRing` - indeterminate progress | same |
| `TextBox` - Wpf.Ui-styled text input (used in a few fields that want the fluent styling) | same |
| `ControlsDictionary` / `ThemesDictionary` - theme resource dictionaries merged in `App.xaml` | same |

WPF-UI is the default choice for new controls and dialogs. When an existing
native WPF control (`<Button>`, `<TextBox>`, `<DataGrid>`) suffices, we use it
unmodified.

### MaterialDesignThemes - version 5.3.0

Used for **one specific control** that WPF-UI does not provide:

| Usage | Namespace |
|---|---|
| `materialDesign:Clock` - analog clock picker used in `DateTimePickerField.xaml` | `xmlns:materialDesign="http://materialdesigninxaml.net/winfx/xaml/themes"` |
| `materialDesign:CustomColorTheme` - narrow resource dictionary enabling the Clock's theming | same |

MaterialDesign is **not** used for any other control. No Material-themed
buttons, cards, icons, or dialogs.

## Consequences

### Positive
- Two toolkits, not three. Audit concern reduced.
- Each toolkit has a clear, stated purpose: WPF-UI = chrome + dialogs + Fluent
  icons; MaterialDesign = Clock only.
- No visible theming clash because MaterialDesign's usage is isolated to a
  small control inside a standalone picker view.
- iNKORE is fully removed - confirmed by package manifest, transitive tree,
  DLL output, and reflection scan for `ComboBoxHelper`.

### Negative
- MaterialDesignThemes is a substantial package (multiple MB of resources) for
  a single clock control. If a Fluent-style clock picker becomes available
  in WPF-UI, we should migrate and drop the reference entirely.
- Two xmlns prefixes (`ui:` and `materialDesign:`) in `DateTimePickerField.xaml`
  add minor cognitive load; this is acceptable given the control's isolation.

### When to revisit this decision

- **Drop MaterialDesignThemes** if/when WPF-UI ships a `Clock` control or a
  suitable alternative is added to the codebase.
- **Add a third toolkit only with an ADR amendment** justifying the additional
  surface. Pull in specific controls rather than adopting a whole toolkit when
  possible.
- **Replace WPF-UI** only under strong pressure (e.g. toolkit abandonment).
  This is a deep-roots change - `FluentDialog`, `ContentDialogHost`,
  `FluentWindow`, and theming all depend on it.

## Verification

```powershell
# Confirm current surface from the .csproj
Select-String -Path src\Wrapp.GUI\Wrapp.GUI.csproj -Pattern "PackageReference Include"

# Confirm iNKORE is genuinely absent (no hits expected)
dotnet list src\Wrapp.GUI\Wrapp.GUI.csproj package --include-transitive `
    | Select-String -Pattern "inkore"

# Confirm actual XAML usage (output should match the two tables above)
Get-ChildItem src\Wrapp.GUI -Recurse -Filter *.xaml `
    | Select-String -Pattern '<ui:\w+|<materialDesign:\w+' -AllMatches `
    | ForEach-Object { $_.Matches } | Select-Object -ExpandProperty Value | Sort-Object -Unique
```

## References

- Package versions: `src/Wrapp.GUI/Wrapp.GUI.csproj`
- FluentDialog host wiring: `src/Wrapp.GUI/Services/FluentDialog.cs`
- Only Material usage: `src/Wrapp.GUI/Controls/DateTimePickerField.xaml`
- Previous stale-comment guard (removed): see git history for `App.xaml.cs`
  commit "iNKORE dead-code cleanup".
