# Wrapp Test Coverage

**Snapshot as of 0.6.0.0176** - 297 tests / 0 failures across 11 test files in `tests/Wrapp.GUI.Tests/`.

This document describes what each test file guards, organised by the workflow it protects. Read top-to-bottom for a flow-by-flow picture of which regressions the suite would catch.

---

## TL;DR - what's covered and what isn't

| Area | Status |
|---|---|
| Config.json parse / serialize / round-trip | ✅ Deep - 66 tests including malformed + legacy + security |
| Field validation (Int / Guid / URL / Date / ComboBox / Required) | ✅ Deep - 22 tests with boundary + adversarial cases |
| Field state dependency engine (enable/disable/required cascades) | ✅ Moderate - 11 tests |
| Help Markdown renderer (tokenizer + legacy-line preprocessor) | ✅ Deep - 30 tests (0176) |
| Inventory clipboard formatter + per-section shapes | ✅ Deep - 17 tests (0176) |
| IValueConverters (9 converters) | ✅ Deep - 38 tests (0176) |
| FieldStateAccessor lazy-cache indexer | ✅ Deep - 10 tests (0176) |
| Validation-issue routing (path → nav section) | ✅ Moderate - 7 tests |
| Inventory filter predicates | ✅ Moderate - 6 tests |
| Inventory model default values + computed properties | ✅ Moderate - 23 tests |
| `.intunewin` file I/O + key-based decryption | ✅ Moderate - 7 tests |
| Module defaults bootstrap | ✅ Moderate - 9 tests |
| Log entry model | ✅ Moderate - 8 tests |
| Inventory service (Graph + PowerShell orchestration) | ❌ Not yet - Tier 2 |
| MSAL auth flows | ❌ Not yet - Tier 2 |
| Git history operations | ❌ Not yet - Tier 2 |
| PowerShell module (`Wrapp.Packager`) | ❌ Not yet - Tier 3 (deferred) |

---

## Test Files in Detail

### 1. `ConfigFileServiceTests.cs` - 31 tests
**Guards**: the Config.json read/write pipeline - the most critical data-integrity surface in the app.

- **Minimal deserialize (4 tests)**: minimal valid JSON populates `AppName` / `Version` / empty top-level collections; any fundamentally invalid input throws.
- **Section parsing (5 tests)**: `DetectRunning` rows parse with all fields intact; IntuneTenant entries get their dict key copied to the `Key` property; `Comment` sub-keys don't leak into the tenant list; SCCM sites preserve `DeploymentGroups` lists; Intune packages round-trip.
- **Round-trips (14 tests)**: App / DetectRunning / IntuneTenant (redacts secret) / SCCMSite / IntunePackage (dependencies / assignments / scope tags / return codes / supersedence) / SCCMPackage (site code + deployments) all go through `SerializeToJson` → `DeserializeFromJson` and match field-for-field.
- **Migrations (5 tests)**: legacy `TargetTenants[]` → `TenantId` string, legacy `TargetSites[]` → `SiteCode`, tenant-scoped `Assignments[]` → per-package `Assignments`, site-scoped `Deployments[]` → per-package, orphaned assignments with no matching package don't crash.
- **Serialisation shape (2 tests)**: output is indented JSON, all top-level sections always emitted.
- **New-format (1 test)**: tenant section without legacy Assignments produces an empty package.Assignments collection.

### 2. `ConfigFileServiceMalformedTests.cs` - 35 tests *(new in 0176)*
**Guards**: malformed input, wrong-shape sections, legacy bundle layouts, and the type-coercion contract.

- **Hard failures (11 tests)**: empty string / whitespace-only / unparseable / truncated-mid-key / truncated-mid-value all throw. `null`/`"string"`/`42`/`[]`/`true` at root throw `InvalidOperationException`.
- **Schema version guard (3 tests)**: `SchemaVersion: 99` refuses to load with a message mentioning "newer version" + "schema v99"; missing SchemaVersion is treated as legacy v1 (loads); matching SchemaVersion loads normally.
- **Missing sections (2 tests)**: empty JSON object `{}` produces a default model; app-only JSON produces empty tenant/site/domain collections.
- **Wrong-shape section tolerance (6 tests)**: `SCCMSite: null`, `IntuneTenant: [...]`, `Domain: "text"`, etc. - sections with the wrong JSON type are silently skipped rather than thrown on.
- **Per-entry defence (2 tests)**: non-object entries inside a section dict are dropped individually; `Comment` sub-keys never produce entries.
- **Unknown properties (2 tests)**: unknown top-level and in-section properties are ignored (forward-compatibility).
- **Type-mismatch contract (2 tests)**: `Str()` is strict (throws on `Name: 12345`); `Bool()` is tolerant (falls back to default on `"yes"` string). Both behaviours pinned so a future change is deliberate.
- **Adversarial content round-trip (3 tests)**: unicode + emoji (`café ☕ 日本語 🎉`) survives SerializeToJson → DeserializeFromJson; paths with embedded quotes and backslashes round-trip; 10k-char strings aren't truncated.
- **Legacy migrations (3 tests)**: empty `TargetTenants: []` doesn't crash; multi-tenant array picks the first for `TenantId`; assignments with blank/missing `AppName` skip cleanly.
- **Full legacy-bundle smoke (1 test)**: realistic pre-0.6 Config.json with no SchemaVersion, `TargetTenants`-array-per-package, tenant-scoped Assignments, plus an unknown future field - all load end-to-end with migrations applied correctly.

### 3. `HelpMarkdownRendererTests.cs` - 30 tests *(new in 0176)*
**Guards**: the custom Markdown → FlowDocument pipeline (replacement for MdXaml introduced in 0163).

- **Headings (4 tests)**: `#` / `##` / `###` parse to the right levels; extra whitespace in `## ` is tolerated; `#heading` without space is treated as paragraph (matches CommonMark).
- **Fenced code blocks (4 tests)**: language tag captured; no-language blocks get empty string; unterminated fences still emit a `CodeBlock` (defensive, no runaway parsing); `#` chars inside a code block are NOT reparsed as headings.
- **Tables (2 tests)**: pipe-tables emit correct row arrays; separator rows (`|---|---|` or `|:---|:---:|---:|` alignment form) are dropped.
- **Lists (3 tests)**: bullet lists produce unordered `ListBlock`; numbered lists produce ordered `ListBlock`; switching list types flushes the first list and starts a new one (no merging).
- **Blockquotes + HR (4 tests)**: `>` produces a `QuoteBlock`; `---`, `***`, `___` all emit `HorizontalRuleBlock`.
- **Paragraphs (3 tests)**: blank-line-separated paragraphs produce distinct blocks; consecutive non-blank lines merge into one paragraph (soft-wrap); empty or whitespace-only input returns an empty list.
- **Full document order (1 test)**: Heading → Paragraph → Heading → List → CodeBlock preserved in that exact order.
- **PreprocessLegacyFieldLines (9 tests)**: short `FieldName: value` lines get bold-wrapped; heading / bullet / numbered / code-fence / table / emphasized-label / long-label / URL lines are left untouched.

### 4. `ClipboardSectionTests.cs` - 17 tests *(new in 0176)*
**Guards**: the generic `ClipboardSection.Flat` / `ListOfObjects` formatter from 0174 plus the per-section `Build*Body` shapes.

- **Flat formatter (6 tests)**: emits `Label: Value` per non-empty field; null + empty strings are skipped; bools normalise to Yes/No; zero ints are treated as "unset" and dropped; empty input returns `""`; trailing newlines are trimmed.
- **ListOfObjects formatter (4 tests)**: empty collection returns `"None"` (or custom empty text); multi-item output separates cards by blank line and indents `"  Label: Value"`; per-item missing fields are omitted individually.
- **Build*Body regression tests (7 tests)**: `BuildDependedOnByBody` includes App ID and Auto Install (the user-reported 0172 bug, now fixed and locked in); `BuildDependenciesBody` returns "None" when empty; `BuildSupersedenceBody` emits `Uninstall Previous` with correct Yes/No; `BuildAppInfoBody` includes Intune-only fields on Intune apps and SCCM-only fields on SCCM apps - and crucially excludes the other platform's fields; `BuildAssignmentsBody` includes the `Source` field (the 0174 fix).

### 5. `ConvertersTests.cs` - 38 tests *(new in 0176)*
**Guards**: the 9 `IValueConverter` implementations in `Helpers/Converters.cs` (except 2 that require `Application.Current` and are deliberately skipped).

- **IntToVisibilityConverter (5 tests)**: 1/5 → Visible, 0/-1 → Collapsed; non-int (null/string) collapses defensively; `ConvertBack` throws `NotSupportedException`.
- **InvertBoolConverter (3 tests)**: `true↔false` both `Convert` and `ConvertBack`; non-bool passes through unchanged.
- **InverseBoolToVisibilityConverter (3 tests)**: `true → Collapsed`, `false → Visible`, null/non-bool → Visible (assume "disabled → show overlay").
- **StringMatchToVisibilityConverter (9 tests)**: exact match, case-insensitive match, pipe-separated OR (`"embedded|manual"`), non-match collapses, null value collapses.
- **EmptyToVisibilityConverter (4 tests)**: value → Visible, empty/whitespace/null → Collapsed.
- **EmptyToPlaceholderConverter (4 tests)**: value passes through, empty/whitespace/null → `"-"` placeholder.
- **MarkdownToPlainTextConverter (10 tests)**: strips `**bold**`, `*italic*`, `` `code` `` individually and in combination; preserves plain text; null input returns null; empty input returns empty; standalone `*` in `2 * 3` is NOT consumed as italic; bold is stripped before italic (so `**hello**` → `hello`, not `*hello* *hello*` → `hellohello` nonsense).

### 6. `FieldValidatorsTests.cs` - 22 tests (6 original + 16 edge cases in 0176)
**Guards**: per-field value validation dispatched by `FieldKind`.

- **Original cases (6 tests)**: int bounds with fixed min/max; valid GUID vs malformed; Required empty vs populated; ISO-date valid vs gibberish; URL http vs ftp; ComboBox membership.
- **Int boundary edges (7 tests)**: exactly at min, exactly at max, one above/below, negative bounds; int overflow (`2147483648`) rejected; decimals rejected; leading/trailing whitespace tolerated.
- **GUID form edges (5 tests)**: empty (ok - required handled elsewhere); truncated; braces form `{...}` (ok); no-hyphen form (ok).
- **URL security edges (7 tests)**: http + https + query-string ok; `file:///` rejected; `javascript:alert(1)` rejected (XSS); relative paths rejected; missing scheme rejected.
- **ComboBox edges (3 tests)**: case-insensitive match works; empty allowed list permits anything; substring match is rejected (so `"Req"` doesn't match `"Required"`).
- **Nullability edges (2 tests)**: null raw value is treated as empty; whitespace-only Required value is treated as empty (because Validate trims first).
- **ISO-date edges (3 tests)**: offset-bearing timestamps ok; date-only form ok (DateTimeOffset accepts); non-ISO `17/04/2025` rejected.

### 7. `FieldStateProviderTests.cs` - 11 tests
**Guards**: the field dependency engine that cascades enable/disable/required/visible state between related fields (e.g., "if InstallBehavior = System then RestartBehavior must be set").

- Observable state cascading: when a parent field changes, dependent children get the right enable/disable/required flags.
- Rule evaluation order: multiple rules targeting the same child resolve deterministically.
- Cycle/self-reference guards.

### 8. `FieldStateAccessorTests.cs` - 10 tests *(new in 0176)*
**Guards**: the XAML-friendly indexer wrapper that gives WPF a single observable `FieldState` per field name.

- **Lazy creation + caching (6 tests)**: first access creates a new state; second access returns the same instance (binding-critical); different keys return different instances; key matching is case-insensitive; `Tracked` reflects exactly the keys that have been accessed; `Tracked` is empty before any access.
- **FieldState observability (3 tests)**: default values (`IsEnabled=true`, `IsVisible=true`, `IsRequired=false`, all error fields null); `IsVisible` change fires `PropertyChanged` for both `IsVisible` AND the computed `Visibility` property; every `[ObservableProperty]` raises change notification.
- **Isolation (1 test)**: two separate accessor instances don't leak state.

### 9. `ValidationIssueTests.cs` - 7 tests
**Guards**: the routing table that maps a `ValidationIssue.Path` (e.g., `"App.Name"`, `"IntunePackager.Packages[0].AppName"`) to the navigation section the user needs to open to fix it (General, Intune, SCCM).

- Path prefix matching: `App.` → General, `IntunePackager.` → Intune, `SCCMPackager.` → SCCM.
- Unknown prefixes: default routing.
- Empty / null paths: don't crash.

### 10. `InventoryFilterStateTests.cs` - 6 tests
**Guards**: the filter predicate state object used by InventoryView (platform toggle, app-has-dependencies / has-supersedence checkboxes).

- Matching predicate returns correct subset; cache invalidation fires `PropertyChanged` when a filter flag changes.

### 11. `InventoryModelTests.cs` - 23 tests
**Guards**: domain-model defaults + computed properties on the inventory types.

- `NestedGroupNode` construction, parent/child links, AllNestedGroupNames enumeration.
- `InventoryAssignmentInfo` search-text composition, sorting.
- `InventoryRelationshipInfo` `AutoInstallLabel` / `UninstallLabel` computed properties.
- `ContentDownloadResult` default values.

### 12. `IntuneWinServiceTests.cs` - 7 tests
**Guards**: `.intunewin` file I/O - zip inspection, decryption-with-fake-keys, binary signature detection (MZ header for EXE, PK for zip).

- Uses real temp files (no mocking); decryption uses a fake AES key to verify the crypto path without needing a real Intune-signed blob.

### 13. `LogEntryTests.cs` - 8 tests
**Guards**: the `LogEntry` POCO used by the Logs view - timestamp parsing, level enum, formatted text property.

### 14. `ModuleDefaultsTests.cs` - 9 tests
**Guards**: the module-defaults bootstrap (seeding `%LOCALAPPDATA%\Wrapp\Templates\` from embedded resources on first run) and structured-defaults JSON loading.

---

## What's not covered (and why)

### Tier 2 - Orchestration services (deferred)
- **`AppInventoryService`** - caching behavior, relationship classification, group resolution. Requires a fake `PowerShellService` + fake Graph client. Estimate: ~25 tests, 3-5 days including fake infrastructure. Would catch regressions in the Graph-integration layer that the current C# tests can't reach.
- **`MsalAuthService`** - Interactive / DeviceCode / ClientSecret / ClientCert flows. Singleton pattern with no DI; would need refactoring to an interface first. High value but expensive.
- **`GitService`** - `InitAsync`, `CommitAllAsync`, `GetCommitHistoryAsync`. Testable with temp dirs and real `git` CLI; estimate ~6 tests, 1.5 days.

### Tier 3 - PowerShell module (deferred, low priority)
- **`Wrapp.Packager`** - 16 exported functions, mostly thin Graph API wrappers. Pure-logic candidates: `Test-PackagerConfig`, `Test-IntunePackagerPreflight`, `Test-Win32AppCollisions`. Would need Pester and mocked MSAL tokens. Estimate ~15 tests, 5-7 days. ROI is low because most failures are caught by C# tests at the call site, and real integration tests run against a tenant in the pipeline.

### Not worth testing directly
- **XAML files and view code-behind** - declarative bindings are covered by model/converter tests; code-behind is mostly InitializeComponent + event handlers that need a WPF runtime harness.
- **`App.xaml.cs`** theme-switch plumbing - better exercised by manual smoke tests when changing themes.

---

## How to run

```powershell
# All tests
dotnet test tests/Wrapp.GUI.Tests/Wrapp.GUI.Tests.csproj --nologo

# One file
dotnet test tests/Wrapp.GUI.Tests/Wrapp.GUI.Tests.csproj --nologo --filter "FullyQualifiedName~HelpMarkdownRendererTests"

# One test method
dotnet test tests/Wrapp.GUI.Tests/Wrapp.GUI.Tests.csproj --nologo --filter "FullyQualifiedName=Wrapp.Tests.ClipboardSectionTests.BuildDependedOnByBody_ContainsAppIdAndAutoInstall"
```

Expected: `Passed! - Failed: 0, Passed: 297, Skipped: 0, Total: 297` in ~130 ms.
