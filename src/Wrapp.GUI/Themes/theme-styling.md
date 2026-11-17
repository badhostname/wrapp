# Theme-Based Control Styling in Wrapp.GUI

How we theme Wpf.Ui and native WPF controls without breaking their default visual structure.

---

## Core Principle: Override Resource Keys, NOT ControlTemplates

Wpf.Ui controls (TextBox, ComboBox, ToggleSwitch, Button, etc.) use named
resource keys internally for colors. Their ControlTemplates reference these keys
via `{DynamicResource KeyName}`. By redefining those keys in our theme
dictionaries (`Dark.xaml` / `Light.xaml`), we change colors while preserving the
original layout, sizing, animations, and visual states.

**DO:** Override resource keys by name in the theme ResourceDictionary.
**DO NOT:** Replace the ControlTemplate unless there is no other option.

Replacing a ControlTemplate means you own the ENTIRE visual tree -- sizing,
layout, VisualStateManager states, accessibility focus visuals, etc. This is
fragile and will break whenever the library updates its internal template.

---

## How It Works

### 1. Identify the internal resource key names

Wpf.Ui embeds XAML templates inside `Wpf.Ui.dll`. The templates reference
DynamicResource keys by name (no namespace prefix). For example, the TextBox
template references `TextControlBackground`, `TextControlForeground`, etc.

To find the key names:
- Search the Wpf.Ui GitHub repo for the control's XAML:
  `https://github.com/lepoco/wpfui/tree/main/src/Wpf.Ui/Controls`
- Or decompile Wpf.Ui.dll with ILSpy/dnSpy and inspect the BAML resources.
- Key naming conventions:
  - TextBox: `TextControl*` (e.g., TextControlBackground, TextControlBorderBrushFocused)
  - ComboBox: `ComboBox*` (e.g., ComboBoxBackground, ComboBoxDropDownBackground)
  - ToggleSwitch: `ToggleSwitch*` (e.g., ToggleSwitchFillOn, ToggleSwitchKnobFillOff)
  - Accent fills: `AccentFillColor*Brush` (e.g., AccentFillColorDefaultBrush)
  - General fills: `ControlFillColor*Brush` (e.g., ControlFillColorDefaultBrush)

### 2. Define overrides in theme dictionaries

Each theme file (`Dark.xaml`, `Light.xaml`) is a flat ResourceDictionary.
Add a `SolidColorBrush` with the exact same `x:Key` as the Wpf.Ui internal key:

```xml
<!-- In Dark.xaml -->
<SolidColorBrush x:Key="TextControlBackground"  Color="#333b3d"/>
<SolidColorBrush x:Key="ComboBoxDropDownBackground"  Color="#21292c"/>
```

Because the theme dictionaries are merged AFTER `ui:ThemesDictionary` and
`ui:ControlsDictionary` in `App.xaml`, our definitions win (last-writer-wins in
merged dictionaries loaded by `ThemeService.SetTheme()`).

### 3. Load order in App.xaml

```xml
<ui:ThemesDictionary Theme="Dark"/>   <!-- Wpf.Ui base theme (defines keys) -->
<ui:ControlsDictionary/>              <!-- Wpf.Ui control templates (uses keys) -->
<!-- Our theme dictionaries are loaded at runtime by ThemeService -->
```

`ThemeService.SetTheme()` removes the previous theme dictionary and adds the new
one to `Application.Current.Resources.MergedDictionaries`, overriding the keys.

---

## Control-Specific Reference

### TextBox / PasswordBox

Key pattern: `TextControl*`

| Key | Purpose |
|-----|---------|
| TextControlBackground | Normal background |
| TextControlBackgroundPointerOver | Hover background |
| TextControlBackgroundFocused | Focused background |
| TextControlBackgroundDisabled | Disabled background |
| TextControlForeground | Normal text |
| TextControlForegroundPointerOver | Hover text |
| TextControlForegroundFocused | Focused text |
| TextControlForegroundDisabled | Disabled text |
| TextControlBorderBrush | Normal border |
| TextControlBorderBrushPointerOver | Hover border |
| TextControlBorderBrushFocused | Focused border |
| TextControlBorderBrushDisabled | Disabled border |
| TextControlPlaceholderForeground | Placeholder text |
| ControlFillColorInputActiveBrush | Active input fill |

### ComboBox

Key pattern: `ComboBox*`

| Key | Purpose |
|-----|---------|
| ComboBoxBackground | Normal background |
| ComboBoxBackgroundPointerOver | Hover background |
| ComboBoxBackgroundFocused | Focused background |
| ComboBoxBackgroundDisabled | Disabled background |
| ComboBoxDropDownBackground | Dropdown popup background |
| ComboBoxDropDownBorderBrush | Dropdown popup border |
| ComboBoxItemBackgroundSelected | Selected item background |
| ComboBoxItemForeground | Item text color |
| ComboBoxItemForegroundSelected | Selected item text |
| ComboBoxForeground | ComboBox text |
| ComboBoxForegroundDisabled | Disabled text |
| ComboBoxDropDownGlyphForeground | Chevron glyph |

### ToggleSwitch

Key pattern: `ToggleSwitch*`

| Key | Purpose |
|-----|---------|
| ToggleSwitchFillOn | On-state track fill |
| ToggleSwitchFillOnPointerOver | On + hover |
| ToggleSwitchFillOnPressed | On + pressed |
| ToggleSwitchFillOnDisabled | On + disabled |
| ToggleSwitchFillOff | Off-state track fill |
| ToggleSwitchFillOffPointerOver | Off + hover |
| ToggleSwitchFillOffPressed | Off + pressed |
| ToggleSwitchFillOffDisabled | Off + disabled |
| ToggleSwitchStrokeOn/Off/... | Track border for each state |
| ToggleSwitchKnobFillOn/Off/... | Thumb fill for each state |

### Accent Colors

| Key | Purpose |
|-----|---------|
| AccentFillColorDefaultBrush | Primary accent fill |
| AccentFillColorSecondaryBrush | Hover accent |
| AccentFillColorTertiaryBrush | Pressed accent |
| AccentFillColorDisabledBrush | Disabled accent |
| SystemAccentBrush | System-level accent |
| TextOnAccentFillColorPrimaryBrush | Text on accent surfaces |
| TextOnAccentFillColorSecondaryBrush | Secondary text on accent |

### General Control Fills

| Key | Purpose |
|-----|---------|
| ControlFillColorDefaultBrush | Default fill (buttons, etc.) |
| ControlFillColorSecondaryBrush | Hover fill |
| ControlFillColorTertiaryBrush | Pressed fill |
| ControlFillColorDisabledBrush | Disabled fill |

### Popup / Flyout / Dialog

| Key | Purpose |
|-----|---------|
| FlyoutBackground | Flyout popup background |
| FlyoutBorderBrush | Flyout popup border |
| ContextMenuBackground | Context menu background |
| ContextMenuBorderBrush | Context menu border |
| SolidBackgroundFillColorBaseBrush | Solid surface base |
| LayerFillColorDefaultBrush | Layer surface fill |
| ContentDialogBackground | ContentDialog background |
| ContentDialogBorderBrush | ContentDialog border |

---

## WPF Native Controls (SystemColors Overrides)

Some native WPF controls (DataGrid, Calendar, ListBox) use `SystemColors` keys.
Override them the same way:

```xml
<SolidColorBrush x:Key="{x:Static SystemColors.HighlightBrushKey}"     Color="#1f3035"/>
<SolidColorBrush x:Key="{x:Static SystemColors.HighlightTextBrushKey}" Color="#ffffff"/>
<SolidColorBrush x:Key="{x:Static SystemColors.WindowBrushKey}"        Color="#333b3d"/>
<SolidColorBrush x:Key="{x:Static SystemColors.WindowTextBrushKey}"    Color="#ffffff"/>
<SolidColorBrush x:Key="{x:Static SystemColors.ControlBrushKey}"       Color="#333b3d"/>
<SolidColorBrush x:Key="{x:Static SystemColors.ControlTextBrushKey}"   Color="#ffffff"/>
```

These affect:
- **DataGrid**: Row selection highlight, header text
- **Calendar**: Day selection highlight, navigation text
- **ListBox / ListView**: Item selection
- **TreeView**: Item selection

---

## Calendar (Special Case)

The WPF Calendar control does NOT use Wpf.Ui resource keys (Wpf.Ui does not
restyle it). The Calendar uses:

1. **Direct properties**: `Background`, `Foreground`, `BorderBrush` -- set via
   implicit Style setters.
2. **SystemColors**: `HighlightBrushKey` for selected days,
   `HighlightTextBrushKey` for selected day text.
3. **Hardcoded VisualStateManager colors**: "Today" indicator and hover
   highlight use hardcoded colors in the default ControlTemplate.

**Recommended approach** (minimal, non-breaking):
```xml
<!-- App.xaml implicit style -- property setters only, NO ControlTemplate -->
<Style TargetType="Calendar">
    <Setter Property="Background" Value="{DynamicResource CalendarBgBrush}"/>
    <Setter Property="Foreground" Value="{DynamicResource CalendarFgBrush}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource CalendarBorderBrush}"/>
    <Setter Property="BorderThickness" Value="0"/>
</Style>

<!-- CalendarDayButton / CalendarButton -- property setters only -->
<Style TargetType="CalendarDayButton">
    <Setter Property="Foreground" Value="{DynamicResource CalendarDayFgBrush}"/>
</Style>
<Style TargetType="CalendarButton">
    <Setter Property="Foreground" Value="{DynamicResource CalendarFgBrush}"/>
</Style>
```

Selection colors come from `SystemColors.HighlightBrushKey` (already overridden
in both theme dictionaries). The default ControlTemplate handles sizing,
hover, today indicator, and all VisualStateManager transitions.

**DO NOT** replace CalendarDayButton or CalendarButton ControlTemplates -- this
breaks sizing, loses VisualStateManager animations, and changes the visual
appearance significantly.

---

## Adding Custom Brush Keys

When you need brushes that aren't Wpf.Ui internal keys (e.g., for custom
controls like our DateTimePickerField popup), define custom keys:

```xml
<!-- Dark.xaml -->
<SolidColorBrush x:Key="CalendarBgBrush"  Color="#21292c"/>
<!-- Light.xaml -->
<SolidColorBrush x:Key="CalendarBgBrush"  Color="#f7fafb"/>
```

Reference them with `{DynamicResource CalendarBgBrush}` in XAML. The
`DynamicResource` binding ensures they update when the theme is switched.

---

## Adding a New Themed Control (Checklist)

1. Find the Wpf.Ui internal resource key names for the control
2. Add entries to BOTH `Dark.xaml` and `Light.xaml` with matching `x:Key`
3. If the control is native WPF (not Wpf.Ui), check if SystemColors overrides
   suffice; if not, add a minimal implicit Style in `App.xaml` with property
   setters only (NO ControlTemplate)
4. If you absolutely must override a ControlTemplate (extremely rare), copy the
   FULL default template from the library source and modify colors only --
   never write a simplified replacement
5. Test both Dark and Light themes
