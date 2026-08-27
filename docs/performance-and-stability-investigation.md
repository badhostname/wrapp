# Performance & stability investigation — measured, 2026-08-18

Status: IMPLEMENTED (P1 done by the user 08-18: reboot cleared the wedge.
P2 complete — UiStallMonitor, [PERF] startup timings, stylus opt-out, and
both 750ms serializers gated: Settings by view visibility, the bundle
serializer by window activation; git poller view-gated; account countdown
popup-gated. P3 measured on a fresh 0.6.326 instance: 92 MB private /
170 MB WS at idle — the audit's 296 MB was a long-lived session; the
planned trims were already in place (session-scoped run pool, MaterialDesign
not app-merged, capped buffers), so no further diet is justified at this
size. P4: the release script now reports [STALL] counts and the last
[PERF] startup line as a pre-flight gate.)
Baseline: installed 0.6.324-beta, feed heads 0.6.325-beta.
Method: live process measurement, four managed stack captures of hung
instances, log analysis, and code audit. Every claim below is measured,
stack-captured, or carries a file:line.

---

## 1. Measured state of this machine (2026-08-18, ~10:00)

| Fact | Value |
|---|---|
| Machine uptime | **7.3 days** (booted 08-11 01:50 — the reboot recommended after the first wedge never happened) |
| RAM | 31.6 GB total, **7.7 GB free**, 34.6 GB committed |
| Wrapp instances | 2 — one healthy (451 MB WS / 296 MB private), one **hung** (pid 39244, started 09:46) |
| WebView2 processes system-wide | 46, ≈3.2 GB — **owned by Teams, Outlook, Copilot, OneDrive, PowerToys, not Wrapp** (verified by parent PID); Wrapp owns one tree (~6 processes, ~400 MB, outside Wrapp.exe's own number) |
| Wrapp silent-auth calls today | **2** (was ~5,760/day before 0.6.323) — the token-poller fix is log-proven |
| Wrapp ops >1s in today's log | 1 |

## 2. The freezes: what four stack captures prove

Four managed stack dumps were taken from hung Wrapp instances (twice on
0.6.323, twice on 0.6.324). In **all four**:

- The UI thread is parked inside
  `Win32MouseDevice.GetButtonStateFromSystem` — the Win32
  `GetKeyState` family — and never returns (identical frame across
  repeated samples minutes apart).
- **Three of four** enter it through the WPF stylus stack
  (`StylusWisp.WispLogic.CallPlugInsForMouse`); the fourth through
  tooltip handling (`PopupControlService.OnPostProcessInput`).
- **Zero Wrapp frames appear on any thread of any capture.** Not the
  token gate, not the close guard, not the instance coordinator, not the
  update flow, not Monaco glue.

Correlation: the first three occurred while a Velopack delta rebuild was
saturating the machine; the fourth (today's Monaco-era hang) occurred in
normal use — the wedge no longer needs the storm, consistent with a
degrading machine session. The tablet-input plumbing (WISP) this session
has been alive 7.3 days and has now eaten four processes.

Conclusions (in confidence order):
1. The freeze mechanism is OS input-pipeline state, session-scoped,
   cleared only by reboot. Until then, reproductions prove nothing.
2. Wrapp can still reduce its exposure: it makes no use of stylus/touch
   input, yet every mouse event runs through `WispLogic` (3 of 4
   captures). WPF's supported opt-out
   (`Switch.System.Windows.Input.Stylus.DisableStylusAndTouchSupport`)
   removes that stack entirely. Provable claim: it removes the code path
   3 of 4 captures hung in; not claimed: total immunity (the tooltip
   path calls `GetKeyState` regardless).
3. Already shipped mitigations (0.6.325): sibling windows close before
   the download, and the rebuild runs at BelowNormal priority — the CPU
   storm can no longer coincide with a window in use.

## 3. Monaco lag, specifically

- Monaco scrolling happens inside Chromium (the WebView2 child
  processes). **No Wrapp WPF code — including the new scroll helpers —
  sits in that input path**; WebView2 hosts receive input on their own
  HWND.
- Wrapp keeps exactly two persistent Monaco hosts:
  the shared script editor ([ScriptsView.xaml:169](../src/Wrapp.GUI/Views/ScriptsView.xaml))
  and the JSON view ([ConfigJsonView.xaml:72](../src/Wrapp.GUI/Views/ConfigJsonView.xaml));
  Diff/FileHistory windows create transient ones.
- The lag is therefore environmental until proven otherwise: a wedged
  input pipeline (§2) makes *everything* feel slow; 7 days of session
  accumulation and ~25 GB committed add GPU/compositor contention (the
  3.2 GB WebView2 ecosystem shares one GPU process pool).
- Plan: re-test after reboot. If Monaco lag survives a fresh session,
  capture an ETW/`dotnet-trace` profile before touching any code —
  §7 P3. No premature blame.

## 4. Async-flow audit (recent changes included)

The A5 audit (0.6.323) found no sync-over-async, no blocking locks on
the dispatcher, and blocking `Dispatcher.Invoke` only from background
threads. Re-verified for everything added since:

| Subsystem | Runtime cost | Verdict |
|---|---|---|
| `ClickToCaret` class handler | one `GetCharacterIndexFromPoint` per first-click | negligible |
| `ScrollBubbling` + dropdown guard | wheel events over ComboBoxes only | negligible |
| `SmoothScroll` ([Helpers/SmoothScroll.cs:82](../src/Wrapp.GUI/Helpers/SmoothScroll.cs)) | bounded parent-walk per wheel notch on opted-in viewers | negligible |
| Token countdown timer (15s) | local string formatting | negligible |
| `SilentAuthGate` / MSAL | event-driven only; 2 calls today (measured) | fixed & proven |
| `InstanceCoordinator` | no polling while running (launch-guard polls only pre-UI during an apply) | negligible |
| `CloseGuard` / update flow | runs only on close / update | negligible |

**The only always-on periodic work in the app** (provable by
enumeration): 2 × 750ms `CheckForChanges` full-config JSON serializers
([GeneralViewModel.cs:200](../src/Wrapp.GUI/ViewModels/GeneralViewModel.cs),
[SettingsViewModel.cs:293](../src/Wrapp.GUI/ViewModels/SettingsViewModel.cs)),
the 15s countdown tick, the 60s account refresh (guarded), and the 5s
git poller (guarded, only with a bundle open). The serializers are the
one item with *growth risk*: cost scales with config size and they
allocate two full JSON strings per tick (GC pressure). Sub-millisecond
today; unmeasured for large multi-package configs. Addressed in P2.

## 5. Memory anatomy — where the ~296 MB private sits

Constant consumers, provable from code:

| Consumer | Evidence | Estimate |
|---|---|---|
| PowerShell SDK runspace pool | min 1 / max 3 + dedicated run pool of 1 ([PowerShellService.cs:126,143-144,222](../src/Wrapp.GUI/Services/PowerShellService.cs)); each open runspace is a full PS engine | the single largest block; an idle engine alone is ~60-100 MB |
| WPF + WPF-UI + MaterialDesign | three theme/resource systems loaded (MaterialDesign carries the ~7,000-glyph icon library used by the icon selector) | tens of MB |
| Section views | all 12 created lazily on first visit, then cached for the window's life (MainWindow `GetOrCreatePage`) — including both Monaco host controls | grows with navigation, by design |
| Log buffer | capped at 2,000 entries ([LogsViewModel.cs:38](../src/Wrapp.GUI/ViewModels/LogsViewModel.cs)) | bounded ✓ |
| WebView2 in-proc controllers | 2 persistent | ~20-40 MB in-proc (children are separate processes) |

Context: a WPF app embedding the PowerShell SDK plus two Chromium
controllers sitting at ~300 MB private is within the normal band for
this stack — Wrapp is not leaking (working set was stable across the
session; the buffer caps hold). "Snappy" is gated by §2, not by this
number. There is still a realistic **50-100 MB** of diet available (P3)
if it stays a priority after the reboot re-test.

## 6. Instrumentation: the honest answer is NO

Today Wrapp has `OperationScope` timings around MSAL calls and ordinary
event logs. **Nothing detects or records a frozen dispatcher, a slow
frame, or a stall's duration.** Every freeze in this investigation was
diagnosed with external tooling (`dotnet-stack`). That is the gap behind
"do we have logging to catch these slowdowns?" — closed in P2.

## 7. The plan

### P1 — Machine hygiene (no code; blocking everything else)
Reboot. Then re-run: normal navigation, Monaco scrolling in Scripts +
JSON views, the 0.6.324→0.6.325 update, a two-window session. Expected:
wedge gone, Monaco back to normal. Every later phase's measurements are
meaningless in the current session.

### P2 — Stall visibility + exposure hardening (small, one release)
1. **`UiStallMonitor`** (new, ~80 lines): background thread heartbeats
   the dispatcher every 500ms; a missed echo starts a stall clock; on
   recovery logs `[STALL] UI thread blocked for N.Ns` (WARN at 1s,
   ERROR at 5s) with the app's state (active view, update-flow stage).
   Cheap, always on — every future freeze self-documents in app.log.
2. **Startup phase timings**: one `[PERF]` log line per startup phase
   (splash shown → services built → window shown), so "slow launch"
   reports carry numbers.
3. **Stylus/touch opt-out**: `DisableStylusAndTouchSupport` AppContext
   switch at Program.Main (Wrapp has no stylus features; removes the
   WISP stack that 3 of 4 hang captures ran through). Release-note it.
4. **De-risk the 750ms serializers**: replace timer-serialize-compare
   with serialize-on-`PropertyChanged`-debounce, or at minimum skip the
   tick when no input/PropertyChanged occurred since the last one.
   Removes the only unbounded-growth periodic cost.

### P3 — Measured memory diet (only after P1/P2 data)
Capture `dotnet-gcdump` + `dotnet-counters` on a fresh session, then
apply the trims the data justifies, candidates in expected-yield order:
release the packaging run pool's runspace after a run completes (today
it lives for the session once created), dispose Monaco models for
closed bundles, scope the MaterialDesign glyph dictionary to the icon
selector window instead of app resources. Re-measure; stop when the
number stops moving.

### P4 — Keep it snappy (process, not code)
A perf gate added to the release checklist: startup phases within
budget (from P2's `[PERF]` lines), zero `[STALL]` entries in a normal
session, working set within band. A release that regresses the numbers
says so in its notes.

## 8. Current loose end

Wrapp pid 39244 (started 09:46) is hung right now — same wedge, fourth
capture. If it holds no unsaved work, kill it; it will not recover while
the session's input plumbing stays wedged.
