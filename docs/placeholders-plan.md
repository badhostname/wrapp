# Workstream P — Placeholders: custom tokens, live values view, replace-in-place

Status: PLANNED (analysis complete 2026-08-12; no implementation)
Also covers: exposing log-redaction patterns in Settings (the ***ORG-REDACTED*** question).

---

## 0. The redaction question (answered, and folded into this plan)

`***ORG-REDACTED***` is NOT hardcoded knowledge of the organization. Two
layers scrub every log line before it reaches disk or the Logs view
(`AppLogger.Redact`, Infrastructure/AppLogger.cs:370):

1. **Built-in security scrubbers** (always on, org-agnostic): JWTs, `Bearer`
   / `Basic` auth headers, `client_secret` in any casing, OAuth form tokens
   (`access_token=` etc.), and DevOps `pat=` query strings → `***REDACTED***`.
2. **Org patterns**: the `SensitivePatterns` array of case-insensitive
   regexes from **defaults.local.json** → `***ORG-REDACTED***`. Loaded every
   launch (App.xaml.cs:224) and again on org-defaults import. The values on
   this machine came from the org defaults file generated/imported earlier —
   the org "knew itself" because its own file says so.

Gap (user-confirmed): none of this is visible in Settings. Fixed by P2 below
— the Placeholders/Org view shows the active pattern list with its source.

## 1. Current token mechanics (reviewed)

Two unrelated syntaxes exist today:

| Syntax | Tokens | Expanded by | Consumed at |
|---|---|---|---|
| `{{Name}}` double-brace | 12: Company, Name, Version, DotVersion, Language, Date, Author, EXEFile, MSIFile, GUID, TagFolder, LocalAppFolder | `TemplateService.ApplyTokens` (~:535) and `BundleService.ApplyTokens` (~:343) — two near-identical implementations | Template application (Scripts / Intune / SCCM / assignment / deployment templates), Add-Package metadata defaults (Intune/SCCM VMs), bundle creation script bootstrap, PSADT `Invoke-AppDeployToolkit.ps1` substitution; module side re-expands `{{TagFolder}}` (`New-DetectionRuleFromConfig`) |
| `{X}` single-brace | DirectoryFormat: 5 (Company, Name, Version, DotVersion, Language); Vault templates: 6 (Tenant, AppId, AppName, PackageName, Date, Author) | `BundleService.ResolveSubDirectory` / `VaultPathTemplate.Resolve` | Bundle folder layout; vault write paths + PR branch/title/description |

Expansion is **one-shot at apply time**; nothing re-expands later. Values are
never sanitized in `{{X}}` expansion (they go into scripts/fields verbatim);
`{X}` vault values are path-sanitized.

Documentation status (confirmed): all 12 `{{X}}` tokens are documented in ONE
place users can reach — the Scripts view's token-reference glyph →
`Help.Scripts.TemplateTokens` (guardrail-tested against the code's token
list since Workstream H), plus the two Settings metadata hints. The `{X}`
families are documented in their Settings help keys. There is **no unified,
live-valued view** — that is this workstream's centerpiece.

## 2. Design decisions

1. **Name: "Placeholders."** Agreed that "tokens" collides with auth tokens.
   UI copy: *Placeholders*; code: `PlaceholderService`, `CustomPlaceholder`.
2. **Syntax: keep `{{Name}}` (double-brace), NOT `{name}` single-brace.**
   The proposal sketched `{tokenname}`, but single braces are unsafe in the
   two highest-value targets: PowerShell scripts (script blocks, hashtables
   — `if ($x) { ... }` would be a minefield of false matches) and any JSON
   content. Double-brace is already the app's template syntax, already
   documented, and never occurs naturally in PowerShell or JSON. Custom and
   built-in placeholders share one syntax and one resolver.
3. **Reserved names**: the 12 built-ins are reserved case-insensitively;
   custom names validate `[A-Za-z0-9_-]{1,64}` and must be unique.
   Built-ins always win at resolution — an org file cannot shadow `{{Name}}`.
4. **Replacement rules** (per proposal, encoded in the resolver):
   - known + non-empty value → replaced;
   - known + empty value → left literal (visible TODO);
   - unknown name → left literal untouched.
   Single-pass, non-recursive (a value containing `{{X}}` is emitted
   verbatim — prevents cycles and injection-style surprises; documented).

## 3. Data model & storage

```csharp
class CustomPlaceholder {
    string Name;        // unique, validated, case-insensitive key
    string Value;       // plaintext here ONLY when !IsSensitive
    bool   IsSensitive; // value lives DPAPI-encrypted in the sidecar
    string Comment;     // operator note ("Intune pilot group id")
}
```

- **Plain placeholders** → `AppSettings.Placeholders` (settings.json):
  portable, exported, org-seedable.
- **Sensitive values** → `placeholders.secure.json` sidecar beside
  settings.json: `{ "Name": "dpapi:v2:..." }` via the existing
  `SecretProtection` envelope, written under `CrossProcessLock` +
  `AtomicFile` like every other store. settings.json keeps the placeholder
  ROW (name, IsSensitive=true, comment) but an empty Value — so preferences
  stay fully portable while values stay machine-bound (exactly the
  tenant-ClientSecret pattern, but in a sidecar so the secure blob never
  travels inside the portable file at all).
- **Org defaults** gain an optional `Placeholders` block (name/value/comment;
  `IsSensitive` allowed only as a names-only declaration — org files never
  carry secret VALUES, consistent with the existing "secrets are per-user"
  rule). Seeding = add-missing, never overwrite (UpsertTenant semantics).
- **Export/import** (`SettingsPortability`): plain placeholders travel;
  sensitive rows export with empty values; the sidecar is never exported.
  Import preserves the local sidecar (same as trust tokens today).

## 4. PlaceholderService (the single resolver)

New `Services/PlaceholderService.cs`, and the ONLY expansion implementation —
`TemplateService.ApplyTokens` and `BundleService.ApplyTokens` become thin
delegates to it (kills today's duplication; customs automatically join every
existing expansion site, which is what makes org-token templates work on
import with zero extra wiring).

```csharp
record PlaceholderInfo(string Name, string? Value, PlaceholderKind Kind, bool IsSensitive);
enum PlaceholderKind { BuiltIn, Custom, Org }

IReadOnlyList<PlaceholderInfo> Snapshot();       // built-ins resolved LIVE from the
                                                 // active bundle + settings (Date/Author computed)
string Expand(string text, out ExpandReport r);  // rules from §2.4
ExpandReport { int Replaced; string[] LeftEmpty; string[] LeftUnknown; bool TouchedSensitive; }
```

Built-in values resolve live: `{{Name}}`/`{{Company}}`/versions/files/GUID
from the active bundle's `AppSection` (observable — the Settings view's
grayed rows update in real time as the General view edits fields),
`{{TagFolder}}`/`{{LocalAppFolder}}` from settings, `{{Date}}`/`{{Author}}`
computed at expansion.

## 5. Settings → new "Placeholders" tab (P2)

Three cards:

1. **Placeholders table** — one grid, two row classes:
   - *Built-in rows*: grayed, read-only, live values (e.g. Name = Wireshark,
     updating as General changes), Kind column "Built-in".
   - *Custom rows*: editable Name/Value/Sensitive/Comment; Add/Remove with
     the standard selection pattern; sensitive values masked with the
     PasswordBox + "(stored)" affordance the tenant secret grid already
     uses; Save flows through `SavePreferencesAsync` (DPAPI at save, like
     tenant secrets).
2. **Effective configuration (read-only)** — two collapsible JSON viewers:
   - *Active preferences*: `SettingsPortability.BuildExportJson(settings)` —
     already strips secrets/trust tokens, exactly what a "what's in effect"
     view should show. Rendered in the existing read-only Monaco JSON
     surface pattern (ConfigJsonView's editor, read-only flag).
   - *Active org defaults*: the resolved `defaults.local.json` content +
     its source path (`DefaultsLoader.FindDefaultsFile()`), or "none".
3. **Log redaction (read-only)** — closes the ORG-REDACTED gap: lists the
   built-in scrubber categories (fixed strings) and the active org
   `SensitivePatterns` with their source file. New
   `AppLogger.GetActiveRedactionSummary()` accessor. (Stretch, P5: a
   user-extendable `RedactionPatterns` list in preferences, merged with the
   org set.)

## 6. Replace-in-place actions (P3)

One shared command core: `ReplacePlaceholdersAsync(scope)` → expand every
string field in the scope → show a summary dialog BEFORE mutating:
"N occurrences of K placeholders will be replaced; M left (no value); J
unknown left as-is" — listing the placeholder names per bucket; when any
replaced placeholder `IsSensitive`, the dialog warns that its plaintext will
be written into the bundle (Config.json / script) and requires the explicit
confirm. On apply: mutate via the normal observable properties (dirty
tracking, validation, and git history all behave as if typed).

Scopes and surfaces (each a small toolbar button + help key):

| Surface | Scope walked |
|---|---|
| Scripts view (per tab) | The current editor buffer (Monaco get → expand → set; File History provides rollback) |
| General | `AppSection` string fields |
| Intune view | Selected package: all string fields + its assignments |
| SCCM view | Selected package: all string fields + its deployments |
| Assignment / Deployment dialogs | The single entry being edited |

Field walking is explicit per-type maps (house style — mirrors the
serializers), not reflection: predictable, testable, and skips fields where
substitution is nonsense (PackageId, enums, booleans, IconFile paths).

Template IMPORT continues to expand automatically (no button needed there) —
customs simply participate because the resolver is shared.

## 7. Security posture

- Sensitive values: DPAPI sidecar (CurrentUser + app entropy), never in
  settings.json, never exported, never in org files.
- **Auto-redaction synergy**: every sensitive placeholder VALUE is
  `Regex.Escape`d and registered with `AppLogger` alongside the org patterns
  — a sensitive value can never appear in a log even after it has been
  replaced into a command line that gets logged.
- Materialization warning: replacing a sensitive placeholder into bundle
  content is allowed (that is its purpose) but always explicit (§6 dialog).
- Expansion is non-recursive and values are inserted verbatim — no nested
  expansion, no escaping surprises; scripts get exactly the stored value.
- Reserved built-ins prevent org files from hijacking core substitutions.

## 8. Phasing & sizing

| Phase | Content | Size |
|---|---|---|
| P1 | `PlaceholderService` + model + sidecar store + settings/org/export round-trip; ApplyTokens delegation; resolver + persistence tests | M |
| P2 | Settings "Placeholders" tab (live table + JSON viewers + redaction card); help keys | M |
| P3 | Replace-in-place: summary dialog + Scripts/General/Intune/SCCM/dialog scopes; per-scope tests on the field maps | M-L |
| P4 | Org `Placeholders` block seeding + sensitive-value redaction registration + portability rules | S |
| P5 | Stretch: user-extendable redaction patterns; `{X}` families listed (read-only) in the tab for one-stop docs | S |

Guardrail notes: every new UI element gets help keys (HelpKeyReferenceTests
enforces); the token-list drift tripwire extends to assert the built-in
placeholder list in help matches `PlaceholderService`'s reserved set.

## 9. Decisions for the user

1. **Syntax**: recommend keeping `{{Name}}` for customs (see §2.2) — the
   single-brace `{name}` sketch is unsafe inside PowerShell/JSON content.
2. **Name**: "Placeholders" (recommended) vs "Variables".
3. Whether sensitive placeholders may be replaced into scripts at all, or
   only into non-script fields (recommend: allowed everywhere with the
   explicit warning — IDs and paths are the common case, true secrets are
   the operator's call).
