# Update flow, close pipeline & token-polling overhaul - technical plan

Status: Phases A (A5 closed: no further offenders found), B, C, and D
IMPLEMENTED. E largely absorbed into C/D (mandatory machinery, forced
dialogs, and the jobs-panel download path were deleted as they were
replaced); remaining E items: CHANGELOG at release time, tooltip-coverage
doc row for the splash update panel.
Implementation deviations from the plan text:
- D1 step order (revised after field validation): the sibling wait gates
  the DOWNLOAD - open work is first priority, and the delta rebuild's CPU
  burst must never run while other windows are in use (it wedged a sibling
  window's input pipeline during 0.6.324 validation). The updater also
  drops to BelowNormal priority for the download/rebuild stage, and
  re-checks for newly launched windows once more before the apply.
- Startup-path failure fails OPEN back to the bundle cards; only the
  handoff path (no cards left) shows a terminal error.
- Hardened `PlaceholderSecureStore.LoadEnvelopes` along the way: a
  transient IO failure during a mutation used to persist an EMPTY map
  (silent wipe of all secure values); mutations now retry then abort.
Baseline: 0.6.322-beta (`176a9ea`)
Note (B2, implemented form): the update-apply guard is a version-stamped
marker FILE, not the planned kernel mutex - a mutex dies with its owning
process, and Velopack applies AFTER the process exits, so a mutex would
vanish exactly when the guard matters. The marker resolves three ways:
claimed by the relaunched version, deleted on abort, or expired by
staleness (2 min, fail-open) after a failed apply.

Three families of gripes, one coherent redesign:

1. **"Wrapp is Not Responding so often"** - the tenant token poller runs
   MSAL work on the dispatcher, forever, with no re-entrancy guard and no
   timeout.
2. **The dirty-state / force-close structure** - duplicated gates, three
   coordination flags, a mandatory variant bolted on for update
   enforcement, and five releases of patches on top of it.
3. **The update flow** - interactive UI during downloads, hard enforcement
   that collides with live work, percent progress that parks at 70%, no
   guard against old-build instances launching mid-update, no coordination
   with sibling windows.

Governing policy shift (this revision): **updates are never enforced
mid-session.** In-session, an available update only lights the existing
action-required indicator; hard enforcement happens exactly once - at a
fresh launch with **no sibling instances** running. That single decision is
what lets the close pipeline collapse to one cancellable flow.

---

## 0. Symptom → root cause → phase

| Gripe | Root cause | Fixed in |
|---|---|---|
| Frequent "Not Responding" | 15s `DispatcherTimer` calls MSAL silent auth on the UI thread for tenants that can never succeed silently (`ui-required`), ~5,760×/day/tenant; worst observed stall 30.3s on T001 | A |
| Freeze on wake / network change | 76 silent calls parked in-flight (one for ~20h across sleep) with no timeout or cancellation; continuations flush onto the dispatcher at once | A |
| Close pipeline fragility (recurring dialog/data-loss bugs) | Four barrier checks re-entering `Close()`, settings gate duplicated, mandatory/ordinary fork ×3, three coordination flags | C |
| Mandatory-close prompts at all | Updates could fire over live work, so enforcement was bolted onto the close pipeline | C (policy) |
| UI froze opening login popup during update download | `DownloadUpdatesAsync` awaited on the dispatcher; delta reconstruction runs minutes of CPU in its synchronous stretches | D |
| "Stuck at 70%" | Velopack reports one coarse percent through the whole delta-rebuild stage | D |
| Old-build instance launchable during update | Instance mutex is advisory-only (`App.xaml.cs:73`); nothing guards the apply window | B |
| Sibling windows lose the save opportunity when an update closes the app | No cross-instance coordination | B + D |

## 1. Diagnosis evidence (from `app.pid41212.log`, Aug 14–17)

- Every operation >1s in 71h of runtime - all 31 - is
  `MsalAuth.TryAcquireTokenSilentForTenant` on **T001** (the dispatcher).
  Worst: 30.3s, 22.3s, 15.3s, 6.9s, plus a band of 3–4s stalls.
- Timer tick is `async void` with no guard
  (`RunViewModel.ConnectionStatus.cs:376`): 348 started, 272 completed,
  **76 never completed**; in-flight depth sat at ~76 for three days.
- One call completed after **1196m31s** - parked across the machine's
  sleep window; MSAL stack shows `StatusCode: 0`, empty body (network
  hang, no timeout, no CancellationToken).
- Ruled out: cross-process cache contention (0 mutex-timeout warnings,
  single instance) and the update download (Aug-14 freeze only).

---

## Phase A - Stop the freezes (token pipeline)

Smallest shippable phase; kills the daily freezes independently of B–E.

### A1. Split the token timer into its two real jobs
`RunViewModel.ConnectionStatus.cs` - `StartTokenTimer()` (line 372).

- **Countdown ticker** (keep as `DispatcherTimer`, 15s): only
  `ConnectionChecker.UpdateTokenCountdown` over snapshots of both
  connection lists. Pure local string/date work - dispatcher-safe.
- **Silent re-check** becomes event-driven, not periodic (A3). The tick's
  `foreach (disconnected) TryAcquireTokenSilent...` loop is deleted.

### A2. Per-tenant silent-retry state machine
New pure type (testable like `IconPromptDecision`): `SilentAuthGate` with
per-tenant state `{ Untried, Retryable, UiRequired }`:

- `ui-required` → `UiRequired`: **no further silent attempts** until an
  unlock trigger. Card shows "Sign-in required"; the existing
  click-to-sign-in affordance is the only exit.
- Transient failure (network / timeout) → `Retryable` with exponential
  backoff (30s → 5m cap), not a fixed 15s hammer.
- Unlock triggers (all event-driven): interactive sign-in success
  (`SignInTenantAsync`, line 427, or any acquisition raising
  `TokenAcquired`, A3); `NetworkChange.NetworkAvailabilityChanged` (up);
  `SystemEvents.PowerModeChanged == Resume`; tenant list changed; a run
  being started (pre-run refresh, A4).

### A3. Harden `MsalAuthService` for every caller
`MsalAuthService.cs:337`, `MsalAuthService.Cache.cs`.

- `TryAcquireTokenSilentForTenantAsync(tenantId, CancellationToken)` -
  thread the token into `ExecuteAsync(ct)` / `GetAccountsAsync`; default
  linked timeout **10s**. Nothing may park for hours again.
- Wrap the acquisition body in `Task.Run(...)` so the synchronous prefix -
  DPAPI cache decrypt, `Mutex.WaitOne` (up to 5s, `Cache.cs:114`), broker
  interop - never executes on the dispatcher. Interactive flows untouched
  (WAM needs the window thread).
- New event `TokenAcquired(tenantId, result)` raised on *any* successful
  acquisition (silent or interactive, including
  `InventoryViewModel.cs:244` / `AppInventoryService.cs:761`). This
  replaces the poller as the mechanism that promotes RunView cards.

### A4. Correct the UI consumers of poll outcomes
The poller's outcome is load-bearing - the run pipeline filters deploy
targets on `State == ConnectionState.Connected` (`RunViewModel.cs:442,
504, 695, 1047`; click-guard at 127). Losing the poll must not silently
shrink deployment targets:

- RunViewModel subscribes to `TokenAcquired` → applies
  `ConnectionChecker.ApplyTokenStatus` on the dispatcher. A sign-in
  anywhere in the app lights the card up.
- **Pre-run refresh**: one bounded (10s, off-dispatcher) silent check for
  enabled-but-Disconnected tenants at run start, so eligibility is decided
  on fresh state. Tenants still `UiRequired` are reported by the existing
  pre-flight audit ("skipped: sign-in required") instead of vanishing.
- Countdown expiry (flips a card to "Token expired") is local - unchanged.
- `AccountViewModel` 60s auto-refresh (line 575): keep (renewing a cached
  near-expiry token is the silent call that legitimately succeeds) with
  the same guard + timeout, via the hardened service.

### A5. Other UI-thread blockers - CLOSED, no further offenders
Audited: sync-over-async (`.Result`/`.Wait()`/`GetAwaiter().GetResult()` -
none in the GUI), blocking `Dispatcher.Invoke` (7 sites, all marshaling
FROM background threads - they block the caller, never the dispatcher),
the 750ms `CheckForChanges` serializers (few-KB JSON, sub-ms - wasteful
but harmless), the 5s git poller (already guarded by `_pollRunning`),
sync `Process.WaitForExit()` (none), sync file reads in view-models (two,
both small local files). Verdict: the MSAL poller fixed in A1–A4 was the
app's entire "Not Responding" story.

---

## Phase B - Instance safety (independent)

### B1. Instance registry
`InstanceRegistry`: hold `%LOCALAPPDATA%\Wrapp\Locks\instances\<pid>.lock`
open exclusively (same family as `BundleLockService`). Enumerate = try-open
each; openable ⇒ stale ⇒ delete. Truthful live-PID list for any process.
The advisory mutex (`App.xaml.cs:73`) stays for logging only.

### B2. Launch guard during apply
Named mutex `Local\Wrapp.UpdateInProgress` + marker file (target version),
held from "Applying" until Velopack restarts. `App.OnStartup` checks first:
held → small themed wait window ("Wrapp is updating to vX…"), continue into
the new binary when released; 2-minute timeout → message + exit. Closes the
"launch the old build mid-swap" hole. Abandoned-mutex handling required so
a crashed updater can't wedge future launches.

### B3. Cross-instance close request (save-safe)
Each instance hosts `EventWaitHandle Local\Wrapp.CloseRequest.<pid>` with a
background waiter that marshals to the dispatcher and runs the **one close
pipeline** (Phase C) with the update-flavored context line. Every window
keeps its save prompts and its right to refuse.

---

## Phase C - One close pipeline + soft enforcement

### C1. Review findings: what exists today (`MainWindow.xaml.cs`)
- `Closing` (line 125): four sequential barriers - active jobs, transfer,
  bundle dirty, settings dirty - each `e.Cancel` + fire-and-forget handler;
  satisfied handlers call `Close()` again, re-entering the chain (a close
  can traverse `Closing` up to four times).
- The settings gate is implemented twice (`HandleSettingsCloseAsync`:598
  and inline in `FinalizeCloseAsync`:657).
- The mandatory/ordinary fork is repeated three times (:602, :624, :661).
- Three coordination flags: `_closeMandatory` (one-way, never clears),
  `_closePending`, `_closeConfirmed`.
- Two prompt variants + a validation-refused recovery dialog
  (`MandatorySaveChoiceAsync`:542, `SaveChoiceAsync`:582).
- External entry `UpdateService.MandatoryCloseHandler = CloseMandatory`
  (:181) exists solely so updates can strip Cancel from the prompts.

### C2. Policy change that enables the collapse
**No mid-session enforcement.** In-session update availability surfaces
through the existing action-required indicator (gates framework:
`GateService` advisory gates → `MainViewModel.RefreshPendingActions`, the
same status-bar chip `UpdateFeedApprovalGate` already uses) via a new
advisory `UpdatePendingGate` - "Wrapp vX is ready to install". Clicking it
starts the *user-chosen* update flow. With staying-open always a legal
outcome, the mandatory prompt variant, the recovery dialog, and
`_closeMandatory` have no reason to exist.

### C3. The one method
New `CloseGuard` (plain class, MainWindow-owned; prompts injected so the
decision table is unit-testable - extends the extract-shared-code pattern
from the table/textbox work):

```
enum CloseReason { UserClose, UpdateHandoff, SiblingCloseRequest }
Task<bool> RunAsync(CloseReason reason)   // true = proceed to close
```

One ordered walk, each step cancellable, each shown at most once per
attempt:
1. **Active jobs** - confirm cancel-all (existing red-bar/revert behavior)
   or abort the close.
2. **Transfer in progress** - info + abort (transfers are never abandoned).
3. **Dirty scopes**, iterated over a declared list - `(label, isDirty,
   saveAsync)` for bundle then settings - one standard
   Save / Don't save / Cancel prompt each, with the `stillDirty()`
   re-check: a validation-refused save keeps the window open (the
   view-model has already surfaced why). No recovery dialog needed -
   Cancel always exists.
4. Temp-workspace cleanup on the discard path (today duplicated at :163
   and :639).

`Closing` shrinks to: `if (_closeConfirmed) return; e.Cancel = true;` +
single guarded kickoff of `RunAsync(UserClose)` → on true, set
`_closeConfirmed`, `Close()`. One traversal, one place. `UpdateHandoff`
and `SiblingCloseRequest` run the same method with one context line
prepended ("An update is waiting on this window."); no other divergence.

### C4. Deletions in this phase
`MandatorySaveChoiceAsync` + recovery dialog, `SaveChoiceAsync` (absorbed),
`HandleCloseAsync`/`HandleSettingsCloseAsync`/`FinalizeCloseAsync`/
`HandleActiveJobsCloseAsync`/`HandleTransferCloseAsync` (absorbed),
`CloseMandatory`, `_closeMandatory`, `UpdateService.MandatoryCloseHandler`,
`CloseMainWindow(mandatory)`, `ForceUpdateOrCloseAsync`'s forced-close arm.
Interim behavior until Phase D lands: Auto mode at startup also just lights
the indicator (soft everywhere for one release - acceptable, explicit).
**Keep**: the `stillDirty()` data-safety semantics, jobs/transfer barrier
behavior, temp-workspace cleanup.

### C5. Enforcement matrix (the new policy, complete)

| Situation | Behavior |
|---|---|
| Fresh launch, update pending, **no siblings**, Auto mode | Splash enforces (Phase D): Update now / Close Wrapp - work hasn't started, nothing to save |
| Fresh launch, update pending, **siblings running** | No enforcement - proceed into session, indicator lit; updating one window while old-build siblings run achieves nothing |
| Fresh launch, NotifyOnly | Splash offers with Later; indicator lit if deferred |
| In-session, any mode | Indicator only - never a dialog over live work |
| User opts in (indicator / Settings) | `CloseGuard.RunAsync(UpdateHandoff)` in this window → close requests to siblings (each runs its own CloseGuard, Cancel intact) → apply at count 0 |

---

## Phase D - Update flow at the splash level

### D1. Update mode on the splash
`SplashWindow`/`SplashViewModel` gain an update mode (cards collapse, same
shell/logo/theme):

- Step list with glyphs: **Checking feed → Waiting for other Wrapp windows
  → Downloading → Rebuilding package → Applying → Restarting**.
- Info block: `AppInfo.VersionDisplay` → target version, package size,
  feed-manifest hash (`TargetFullRelease` SHA256/SHA1 - verify exact
  Velopack 1.2 property names at implementation).
- Progress: real percent only while it moves; stalled >5s (today's
  watchdog heuristic, `UpdateService.cs:400`) → **indeterminate** bar,
  step flips to "Rebuilding package". No more lying 70%.
- All Velopack calls inside `Task.Run`; progress callbacks throttled
  (≤10 UI posts/sec) before marshalling.

### D2. Startup reordering
`App.StartupCoreAsync` (`App.xaml.cs:195`): as the splash appears, fire a
fast **check-only** feed query (8s timeout, `Task.Run`) racing the card
pick. Update found before the user commits **and no siblings** (B1 query)
**and Auto mode** → `LockCards()`, transition to update mode; MainWindow
never built. All other combinations → C5 matrix (session proceeds,
indicator lit). Feed-not-yet-approved no-ops (`IsFeedTrusted` false) and
catches up next launch after the approval gate runs.

### D3. Handoff from a running session
`UpdateFlowController` (new), used by the indicator/Settings opt-in path:

1. `CloseGuard.RunAsync(UpdateHandoff)` - ordinary gates, Cancel aborts
   the update.
2. `ShutdownMode = OnExplicitShutdown` → MainWindow closes → dispose the
   composition root (`MsalAuth`, `Ps`) behind the update splash's
   "Preparing…" → download → `ScheduleApplyOnExit` (existing
   single-watcher latch, `UpdateService.cs:231`) → `Shutdown()` → Velopack
   applies and relaunches.
3. Prerequisite: `MainWindow.Closed` today calls `Environment.Exit(0)`
   (`App.xaml.cs:297–305`, exists to outrun runspace-disposal hangs).
   The handler consults `UpdateFlowController.IsHandoffActive`: normal
   closes keep today's exact behavior; only the handoff path takes the
   orderly branch. Riskiest change in the plan; default path stays
   byte-identical.

### D4. Sibling coordination at apply time
Update splash refuses to enter Applying while `InstanceRegistry` reports
other live PIDs: live count + [Ask them to close] → B3 requests, each
window gets its CloseGuard prompts. Apply proceeds only at count 0; the
user can always abort back to a normal launch with the package still
staged.

### D5. Settings page
"Check for updates" stays (check-only + "Install now" entry into D3). The
in-session download / jobs-panel path (`DownloadWithJobAsync`,
`SettingsViewModel.cs:952`) goes.

---

## Phase E - Final deletions and docs

- `UpdateService`: `OfferUpdateAsync`, `DownloadWithJobAsync` + stall
  watchdog, `InstallDownloadedAndClose` (superseded by the controller),
  remaining startup-dialog arms of `StartupCheckAsync`.
- Help topics (Settings→Updates, About), CHANGELOG,
  `docs/help-tooltip-coverage.md` rows for the splash update panel and the
  action-required entry.

## Policy decisions made explicit

1. Updates are offered mid-session only through the action-required
   indicator - never a dialog over live work, never a forced close.
2. Hard enforcement exists in exactly one place: a fresh, sibling-free
   launch in Auto mode, at the splash, before any work exists. Trade-off:
   a long-lived session (or a multi-window user) runs old code until the
   stars align; fleet convergence still happens because every window
   eventually relaunches alone.
3. During the update flow the app is exclusively an updater (blocking
   splash). NotifyOnly remains the mode for fleets updated via
   Intune/SCCM supersedence.
4. Sibling windows are *asked* to close through their normal CloseGuard -
   a sibling's **Cancel blocks the apply step by design**. Mitigations:
   the wait is visible and attributable (live count); the updater can
   always abort with the package still staged; unanswered requests re-fire
   after a delay and the blocking window shows a persistent "an update is
   waiting on this window" chip (reminder pressure, not force); the
   refusing window hits the splash gate on its own next lone launch, so
   Cancel defers - it cannot dodge.

## Risks & validation

- **Lifecycle handoff (D3)** - isolated behind `UpdateFlowController`;
  validate normal close, update close, crash-during-download (no stuck
  `UpdateInProgress` mutex).
- **CloseGuard regression risk (C3)** - the current pipeline encodes
  hard-won behavior (jobs red-bar revert, transfer non-abandonment,
  validation-refused saves, temp-workspace cleanup). Port it behind unit
  tests on the decision table before deleting the old handlers; manual
  pass over every close entry (X button, Alt-F4, indicator flow, sibling
  request, gate-declined shutdown).
- **Velopack apply with siblings running** - validate on this machine:
  two instances, apply from one, confirm refusal + close request +
  successful apply at count 0.
- **MSAL off-thread** - verify broker *silent* calls succeed from a worker
  thread and `TokenAcquired` marshals correctly.
- **Feed on slow/offline UNC** - the 8s splash check must never delay
  launch beyond its timeout.

## Test plan

Pure-logic units (xUnit, existing patterns): `SilentAuthGate` transitions
and backoff; `CloseGuard` decision table (reasons × jobs/transfer/dirty ×
save-refused × Cancel); instance-count → enforce/notify policy (C5 matrix
as a pure function); step-tracker mapping from synthetic progress
sequences; launch-guard timeout. Integration smoke per the validation
list. Suite stays green (904 + new).

## Suggested execution order

A (freeze fixes - ship alone, daily pain) → B (instance safety) → C (one
close pipeline + soft policy) → D (splash flow) → E (deletions). Each
phase is a releasable increment; C is the largest single-release risk and
gets the test-first treatment.
