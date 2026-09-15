# Production-readiness audit - findings & technical plan

Date: 2026-08-18 · Baseline: 0.6.325-beta (`48f4826` + template-coverage commit)
Method: four parallel code-audit passes (duplication/structure, security/
validation, dead code/globals/logging, periodic work/UX) + direct metrics.
Every finding carries file:line evidence; nothing here is speculative.

---

## 1. Codebase metrics

| Area | Total lines | Comment lines | Density |
|---|---|---|---|
| C# app source (226 files) | 49,753 | 9,813 - 6,207 `///` docs + 3,606 `//` narrative | **19.7%** |
| XAML (36 files) | 16,703 | 952 | 5.7% |
| Tests (77 files) | 12,759 | 1,621 | 12.7% |
| PowerShell module + scripts | 9,153 | 849 | 9.3% |
| **Whole codebase** | **88,368** | **13,235** | **15.0%** |

Largest files: SettingsViewModel 1,347 · GeneralViewModel 1,108 ·
RunViewModel 1,096 · PowerShellService 1,062 · RunViewModel.PhaseHandling
1,025 · AccountViewModel 993.

**Comment diet (requested):** the 3,606 narrative `//` lines and the
essay-length `///` blocks are the reduction target. Rules for the strip
pass (§11 P5): keep comments stating a *constraint the code cannot show*
(thread-affinity, ordering, security rationale, WPF quirks - many are
load-bearing and several were cited by the auditors as the reason bugs
were avoidable); delete history narration ("was X, now Y"), restatements
of the next line, phase/workstream numbering, and audit trail notes.
Realistic reduction: **4,500–6,000 lines** without losing any constraint
knowledge.

---

## 2. Security & validation findings (ranked by consequence)

The good news first, verified: the WebView2/Monaco surface needs **no
changes** (JSON-escaped bridge, `DenyCors` virtual host, strict CSP,
navigation allow-list, message-source checks, no host objects). The
PowerShell boundary is parameterized (token via `AddParameter`, GUID
validation, single-quote escaping everywhere but one file, guaranteed
global cleanup). DPAPI envelopes, trust-token TOFU gates, export
stripping, pre-commit secret scan, and log redaction plumbing are all
present and correct.

| ID | Finding | Evidence | Consequence |
|---|---|---|---|
| **SEC-1** | PSADT tokenization splices `App.Company/Name/DotVersion` + `Environment.UserName` into PS single-quoted literals **without quote-doubling** - the one site in the codebase missing the `'`→`''` escape | `BundleService.cs:510-515` | A hostile bundle value becomes code that runs **on deployment endpoints as SYSTEM**; also a plain bug: "O'Brien Ltd" produces a broken, sticky `Invoke-AppDeployToolkit.ps1` |
| **SEC-2** | `App.IconFile` from Config.json is `Path.Combine`d with no containment check (rooted paths pass through; `..` not collapsed) | `BundleService.cs:171-177`; read side `GeneralViewModel.InstallerHelpers.cs:66-70` | Arbitrary file-create/overwrite **with PNG content** outside the bundle on save |
| **SEC-3** | `null` collection in settings.json (`"Placeholders": null` etc.) NREs *after* a successful load - the `.bak` recovery never runs | `App.xaml.cs:323-325`, `SettingsRepair.cs:27-37` | **Unrecoverable startup crash** until the file is hand-edited |
| **SEC-4** | Org-supplied redaction regexes compiled without match timeout, executed synchronously in `Enqueue` on the calling (often UI) thread | `AppLogger.cs:363, 219, 401` | One catastrophic-backtracking pattern (typo suffices) hangs the app on the next log line |
| **SEC-5** | Org-seeded `Update.ReleasesUrl` reaches `Process.Start(UseShellExecute)` with no scheme validation and no trust gate (unlike feed/vault URLs); markdown link handler launches any scheme | `WhatsNewService.cs:41-42,161`; `HelpMarkdownRenderer.Inlines.cs:240` | Chained with SEC-7: click-to-launch of attacker URI (phishing-grade, needs a click) |
| **SEC-6** | `PlaceholderSecureStore` decrypts with plaintext-passthrough `Decrypt` instead of `DecryptAuthentic` - the sidecar has no legacy format to tolerate | `PlaceholderSecureStore.cs:36` | Integrity gap: a planted bare string is accepted as a "protected" value and expanded into bundles |
| **SEC-7** | `%ProgramData%\Wrapp` defaults probe: directory is user-creatable on stock Windows; URLs are TOFU-gated (good) but `SensitivePatterns` (→SEC-4) and `ReleasesUrl` (→SEC-5) are not | `DefaultsLoader.cs:46-47` | Local-user seeding of other profiles |
| **SEC-8** | No size/count bounds on any JSON load; Config.json path holds ~3 copies of the text + node tree on the UI thread; 100k packages would populate 100k rows | `ConfigFileService.cs:33`, `Parsers.cs:150,254,369` | OOM/derailment on hostile or corrupt bundle |
| **SEC-9** | `SchemaVersion` forward-compat guard bypassed when the value is string-typed | `ConfigFileService.cs:85-92` | Silent data loss the guard exists to prevent |
| **SEC-10** | Corrupt secure store resets to empty with no backup of the ciphertext (deliberate, but signal-free) | `PlaceholderSecureStore.cs:112-116` | Recoverable data destroyed silently |

Documented trade-offs to record, not change: tenant client secrets are not
literal-redaction-registered (contradicts the SecureString design to do
so); vault path templates are unsanitized by design (bounded by TOFU +
server-side path validation).

## 3. Correctness bugs (non-security) found by the audit

| ID | Finding | Evidence |
|---|---|---|
| **BUG-1** | `AccountViewModel` re-derives the MSAL cache path by hand - ignores `WRAPP_MSAL_CACHE_PATH`; with the override set, it reads a file nobody writes (account card silently empty) | `AccountViewModel.cs:917-919` vs `PlatformConfig.MsalCachePath` |
| **BUG-2** | `SettingsViewModel` re-derives settings.json path in one place while using `PlatformConfig.SettingsPath` in another - same class, two sources of truth | `SettingsViewModel.cs:1252-1254` vs `:178-187` |
| **BUG-3** | Dirty-tracking `CheckForChanges` swallows serializer exceptions **silently and forever**; a persistent fault means `IsDirty` stops updating and `CloseGuard` (which trusts `IsDirty()` absolutely) closes over unsaved work with no prompt | `GeneralViewModel.cs:816`, `SettingsViewModel.cs:346` vs `CloseGuard.cs:120` |
| **BUG-4** | Stale `<see cref="InstallDownloadedAndClose"/>` → CS1574 when doc generation is on | `UpdateService.cs:186` |

## 4. Duplication & structure (co-change-proven)

Git history shows the Intune/SCCM pairs are edited **together in 88–94% of
commits** - every change is made twice by hand today. Realistically
extractable: **~1,300–1,500 lines.** Ranked (full detail in the audit
transcripts; headline items):

1. **`PackageTemplateController` + `InventoryBrowseHelper` + `HelpDialog`** -
   IntuneView/SCCMView code-behind are 85% identical (262/307 lines);
   the template picker/save flow also exists a *third* time in ScriptsView
   (~120 lines). Lowest risk, highest lockstep relief (~360 lines).
2. **Shared XAML controls** - six near-identical blocks across
   IntuneView/SCCMView (~545 lines): drag overlay (byte-identical),
   package list pane (8-line delta), toolbar, connections expander,
   dependencies/supersedence grid (`AppRefGrid`).
3. **`PreferencesSync` generics** - perfect 3× triplicate (~96 lines),
   preserving `CloneTenant`'s SecureString semantics verbatim.
4. **`TenantsViewModel.SyncAsync<T>`** - 12 commands, one shape (~90 lines).
5. **Generic `PackageViewModelBase<TPkg>`** - 267 identical VM lines;
   medium risk (XAML binding names), do after 1–4 build confidence.
6. **`EntryListDialogBase<TEntry>`** - assignment/deployment dialogs
   (121 identical lines; generic-base-via-intermediate pattern).
7. Cross-cutting idioms: help-dialog boilerplate ×7, raw dispatcher
   marshalling ×19 (generalize `UiProgress`/`SafeFireAndForget`),
   DispatcherTimer boilerplate ×14 (five identical 750ms debounces),
   theme-brush fallbacks with duplicated hex ×6, editable-grid-card ×13.
8. **Defer**: PreferencesViewModel defaults-VM mirroring (~440 lines) -
   reflection there risks silent settings loss; worst risk/benefit.
9. `SyncIntuneConnections`/`SyncSccmConnections` - extract carefully; the
   PropertyChanged attach/detach symmetry is the value of centralizing.

## 5. Periodic work & efficiency

| Timer | Verdict |
|---|---|
| **SettingsViewModel 750ms** (`:293`) | Heaviest always-on tick: **3 JSON serializations + 4-5 LINQ materializations per tick, forever, no guard** - runs even if Settings was never opened. → event-driven/dirty-gated |
| **GeneralViewModel 750ms** (`:200`) | Full bundle-config serialize per tick once a bundle is open. → PropertyChanged-driven debounce |
| **Git poll 5s** (`GitHistoryViewModel:35`) | **2-3 git process spawns every 5s from any view**, forever after first bundle load. → FileSystemWatcher or active-section gate |
| **Account 60s** (`AccountViewModel:575`) | Recomputes popup-only countdown strings while the popup is closed (~99% waste). → compute on popup open; one-shot timer at expiry-15min for the refresh |
| Token countdown 15s | Already remediated; minor `.ToList()` churn only |
| BackgroundJobTracker 500ms | **Best-behaved timer in the app** - auto-start/stop; keep as the model |
| HelpMarkdownRenderer copy-flash | Allocates a new DispatcherTimer per click; `TimedFlag` exists for exactly this |

## 6. Dead code & global-state hygiene

- **Dead**: `Helpers/JsonElementExtensions.cs` (74 lines, its documented
  migration never happened), `MainViewModel.PackageFolderPath`,
  `ScheduleApplyOnExit(restart:false)` parameter path,
  `FluentDialog.ShowChoiceAsync(cancelText: null)` feature (its purpose -
  mandatory close - was deleted), two stale comments describing removed
  behavior. `ShowInfoAsync`/`ShowWarningAsync` are byte-identical (53 call
  sites choose between indistinguishable methods).
- **Clean**: all 466 help keys referenced; all 11 converters live; zero
  TODO/HACK/FIXME; zero `#pragma` suppressions; all 30 empty catches
  documented (lint-enforced) - except the three BUG-3 swallows.
- **Global-state**: `%LOCALAPPDATA%\Wrapp` re-derived in **11 files**
  (two are BUG-1/BUG-2) → expose `PlatformConfig.WrappRoot` + named
  subpaths + a lint rule (the repo's `SourceLintTests` pattern fits).
  `AddRedactionPatterns` **replaces** (name lies) and its 5-step
  org-import sequence is duplicated in two places → one
  `OrgDefaultsSeeder.ApplyImported`. The sensitive-placeholder redaction
  projection is duplicated (App + PlaceholdersViewModel) - drift here
  leaks secrets to logs → one `PlaceholderService.RefreshFromSettings`.
  `UpdateMode` is stringly-typed with 5 restatement sites and collides
  with the Intune enum of the same name → `AppUpdateMode` enum.
  `"Intune"`/`"SCCM"` literals ×40+ despite `AppPlatform` existing.
  750ms/500ms magic intervals ×8 → `UiTimings` constants.

## 7. Logging quality

- **562 log sites; only 17 use `AppLogger.Exception`** - 116 log
  `ex.Message` losing stack/type. ~20 high-value conversions identified
  (update pipeline, settings save/load incl. the silent
  returning-defaults data-loss event, key-vault ops, MSAL cache, module
  load, git commit failures).
- **Log flood**: the vault brute-force decrypt path writes 2 lines per
  vault key at Info (401 lines for a 200-key vault) into a 1MB×5
  rotation - enough to flush the session's history. Keep count + match.
- **Unlogged destructive actions**: all six tenant/site/scope-tag/
  deployment-group deletes; the `UpdateMode` policy change.
- **Four prefix conventions** (`Word:` ~330, `[Bracket]` 34, sentence ~15,
  bare ~25); UpdateService alone uses three, the intunewin pipeline four.
  Standardize on `Component:` + lint rule.

## 8. UX findings (facts from the inventory)

- **Run flow**: per-step history (`PackageProgress.Steps` with per-step
  `State`/`ErrorDetail`) is recorded all run long and **never rendered** -
  users must read raw logs to find the failed step. The run log pane has
  no filter/search/level toggles (the Logs view has all three) and no
  line cap. Status column shows raw enum tokens (`PartialSuccess`).
- **Elapsed times**: four format families; running job *steps* show
  frozen durations (never ticked, no minutes branch - a 5-minute step
  renders "312.4s"); a Tools grid `ElapsedDisplay` raises no change
  notification at all.
- **Labels**: five Save variants ("Save" and "Save Changes" are the same
  operation visible simultaneously); completion states differ ("Up to
  date" vs "Saved"); `Done` (jobs) vs `Succeeded` (run) for the same
  concept; ellipsis convention inconsistent; `Reset Defaults` vs `Reset
  All Settings` indistinguishable.
- **Badges**: three visual systems on one nav rail; the Inventory badge
  hardcodes `#1565C0`/`White` and ignores themes; job state pills
  hardcode hex; badge coverage is asymmetric across views.
- **Empty states**: three different treatments + none at all in Run/
  Scripts/Tools/Detection; jobs dialog re-implements the empty-state
  style imperatively and its filter mutates the global default
  collection view.
- **Jobs panel**: modal dialog built in code-behind; completed jobs
  capped at 200/session (good); details step-tree exists (good).

## 9. Partially implemented plans

| Plan | Missing |
|---|---|
| `performance-and-stability-investigation.md` | **All of P2** (UiStallMonitor, startup `[PERF]` timings, stylus opt-out, 750ms serializer de-risk), P3 (measured memory diet), P4 (perf release gate) - was awaiting review |
| `update-flow-and-token-polling-plan.md` | Phase E remnant: tooltip-coverage doc rows for the splash update panel |
| `Helpers/DateTimeFormats.cs:13-15` | Promises a lint rule that was never written |
| `Helpers/JsonElementExtensions.cs` | Promises a migration that never happened (§6 - delete or do) |

---

## 10. Technical plan

Ordered by consequence; each phase is releasable. Tests accompany every
behavioral change; suite stays green throughout.

### P0 - Security hotfixes (small, ship immediately)
1. **SEC-1**: `Ps(string)` quote-doubling helper, applied to all five
   PSADT replacements. Test: hostile + apostrophe company names.
2. **SEC-2**: containment check on `IconFile` (mirrors the existing
   template-path guard), falling back to the derived name + warning.
   Same guard on the read side.
3. **SEC-3**: `??=` null-coalesce pass for all collections/dictionaries
   in `SettingsRepair.Apply`. Test: settings.json with each key null.
4. **SEC-4**: 100ms regex match timeout + `RegexMatchTimeoutException`
   fallback in `Redact`.
5. **SEC-5**: HTTPS-only acceptance of the org `ReleasesUrl`; scheme
   allow-list (`http/https`) in the markdown link handler.
6. **SEC-6**: `DecryptAuthentic` in `PlaceholderSecureStore.GetValue`.
7. **SEC-10**: rename corrupt store to `.corrupt-<timestamp>` before reset.
8. BUG-1/BUG-2: route both call sites through `PlatformConfig`.
9. BUG-3: one-shot warn latch in the three `CheckForChanges` catches.
10. BUG-4 + the two stale comments + SEC-9 type coercion.

### P1 - Bounds & the deferred perf work (one release)
1. SEC-8: file-size caps (few MB) on Config.json/settings.json loads
   with clear errors; package/assignment/deployment count caps + warn.
2. Perf plan P2, all four items (stall monitor, `[PERF]` startup lines,
   stylus opt-out, serializer → PropertyChanged-debounce - the latter
   also resolves the two 750ms findings in §5).
3. Git poller: gate on active section (cheap) or FileSystemWatcher.
   Account timer: compute countdown on popup open.
4. Logging: `AppLogger.Exception` for the ~20 sites; delete the vault
   log-flood; log the six deletes + UpdateMode changes; rename
   `AddRedactionPatterns`→`SetOrgRedactionPatterns` and extract
   `OrgDefaultsSeeder.ApplyImported` + `PlaceholderService.RefreshFromSettings`.

### P2 - Dead code + global-state consolidation (one release)
Delete the dead items (§6); `PlatformConfig.WrappRoot` + subpaths + lint
rule; `AppUpdateMode` enum; `UiTimings` constants; collapse
`ShowInfoAsync`/`ShowWarningAsync`; SEC-7 installer ACL note goes to the
release checklist (installer change).

### P3 - Deduplication program (multiple releases, ordered by risk)
Sequence exactly as §4 ranks 1→7; each extraction lands with its own
visual smoke pass; defer rank 8; rank 9 last with focused review on the
event-handler symmetry. `AppPlatform` literal cleanup rides along with
rank 1/2 (same files). Target: −1,300 lines, lockstep editing eliminated.

### P4 - UX pass (one release, all items user-visible)
Render the per-step run history (expander per package row) + friendly
outcome labels; run-log filter reusing the Logs view mechanics + line
cap; one `DurationFormat` helper used by jobs/steps/tools (fixes frozen
step durations via the existing 500ms ticker); label normalization
(one Save vocabulary, one completion state, ellipsis rule); one badge
control with theme brushes; shared empty-state control applied to the
missing views; jobs-panel filter without mutating the default view.

### P5 - Comment diet (last, mechanical, reviewed in chunks)
Apply the §1 rules file-by-file (services first, then VMs, then views);
target −4,500 to −6,000 lines; each chunk builds + full suite; the
constraint-comment keep-list is the review criterion.

### Verification gates (every phase)
Build 0 warnings · full suite green · for P0 additionally: new
regression tests for each SEC finding · for P3: per-extraction visual
smoke · for P4: screenshot pass · release notes name every behavior
change.
