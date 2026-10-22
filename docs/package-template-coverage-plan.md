# Package template coverage & placeholder expansion - analysis and plan

Status: IMPLEMENTED (suite 961/961; template test classes serialized via
[Collection("Templates")] - both swap the static TemplateDir seam)
Baseline: 0.6.325-beta

## How the flows work today (verified)

**Script templates** - the complete flow the user expects everywhere:
`LoadScriptTemplate` ([TemplateService.cs:363](../src/Wrapp.GUI/Services/TemplateService.cs))
reads the file and immediately runs `ApplyTokens` →
`PlaceholderService.Expand(content, app)`: every `{{Name}}`-style
placeholder (built-in and custom) is replaced at apply time. Save is
verbatim by design (no reverse-tokenization - [TemplateService.Save.cs:15-21](../src/Wrapp.GUI/Services/TemplateService.Save.cs)).

**Package templates** - three verified gaps:

1. **No placeholder expansion on apply.** `ApplyPackageTemplate`
   ([TemplateService.cs:383](../src/Wrapp.GUI/Services/TemplateService.cs))
   sets string values verbatim (`kv.Value?.GetValue<string>()`). A saved
   `{{Company}} Portal` stays literal in the target package.
2. **Collections are invisible.** Both the save-side enumeration
   (`EnumerateTemplateProps`, string/int/bool only -
   [TemplateService.Save.cs:119-126](../src/Wrapp.GUI/Services/TemplateService.Save.cs))
   and the apply-side switch handle only scalars. Everything stored in
   `ObservableCollection`s never reaches a template:
   - Intune: **Categories, ScopeTags, CustomReturnCodes, Dependencies,
     Supersedence** ([AppConfigModel.Intune.cs:196-202](../src/Wrapp.GUI/Models/AppConfigModel.Intune.cs))
   - SCCM: **InstallBehaviors, Dependencies, Supersedence**
     ([AppConfigModel.Sccm.cs:98-102](../src/Wrapp.GUI/Models/AppConfigModel.Sccm.cs))
3. **The package name can never travel.** `AppName` is in the save-side
   excluded set ([Save.cs:56-63](../src/Wrapp.GUI/Services/TemplateService.Save.cs))
   *and* the apply-side skip set - there is no way to opt in.

**Assignment / deployment templates** share gap 1: loaded verbatim, no
expansion (`LoadAssignmentTemplate` / `LoadDeploymentTemplate`).

The save dialog already has per-field checkboxes
(`TemplateFieldChoice`, used by IntuneView/SCCMView `SavePackageTemplateAsAsync`),
so "check a box to include the name" fits the existing UX - the name
just has to become a listed field.

## Design

- **Save**: extend the enumeration to include `ObservableCollection<T>`
  properties (entry types already mark UI-only state `[JsonIgnore]`, so
  `JsonSerializer.SerializeToNode` round-trips cleanly). Collections
  display as "N item(s)" in the dialog, unchecked by default (like
  metadata). `AppName` leaves the excluded set and appears as an
  unchecked choice - checking it stores the current name (placeholders
  and all) in the template.
- **Apply**: `ApplyPackageTemplate` gains the `AppSection` parameter and
  expands EVERY string it writes through `ApplyTokens` - scalar fields,
  `AppName`, and the string members of collection entries (reflected
  generically). Collections REPLACE the target's collection (sparse
  semantics unchanged: a key absent from the template leaves the target
  untouched; an empty saved collection explicitly empties it).
- **Assignments/deployments**: same expansion on their string fields at
  load, callers pass the app section.
- Sparse-template compatibility is preserved: existing template files
  keep working; old files simply contain no collection keys.

## Test plan

Extend `TemplateServiceSaveTests`: collection round-trip (save chosen →
apply → entries equal, `IsSelected` never serialized); placeholder
expansion on apply for scalars + collection entry strings + AppName
(uses the existing Placeholders test seams); AppName appears as an
unchecked choice and applies only when present in the file; absent keys
leave target state untouched; verbatim-on-save invariant unchanged.
