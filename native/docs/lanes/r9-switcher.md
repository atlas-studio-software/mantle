# Lane native-r9-switcher: the Alt+Tab switcher and Task View, pure half

Branch `native/r9-switcher` from `40414f38` (stage 9). Rows: docs/parity/launch.md SWI-001..145 and TV-001..134.
No .NET SDK on the laptop in this window: everything below is WRITTEN, NOT RUN (not compiled, no test pass).
The held cloud command: `cloudtest --recipe mantle-native-public -- "pwsh -NoProfile -File native/tools/ci.ps1 -Stages build,unit,ui"`.

## The shape

The keys never go through Mantle: the native app links the SAME sidecar (sidecar/AltTab.cs, Hotkey.cs) that Electron
runs, so the key machine, SwitchMath (initial selection, wrap, rows), the MRU order and the activation stay the
sidecar's. This lane tests that path in-process and ports the main-process half (lib/switcher.js, ui/switcher/layout.js,
ui/taskview/layout.js, lib/features/taskview.js guards) to `Mantle.Core` as pure code.

## What is built

- `tests/Mantle.Sidecar.Tests/AltTabKeys.cs` (kept from the stopped lane, fixed): `hotkey {op:'simulate'}` and
  `{op:'order'}` through the real dispatcher (passive), scenario for scenario as lib/switcher.test.js machine(),
  order() and refusals(). Fix: the two Win-tap scenarios were missing, so the modifier-keyup count was 32 and the
  ">= 33" check would have failed. Added: SWI-025 (keys it does not handle pass while open), the kill switch Ctrl+Alt+Shift+Backspace passes (switcher open and closed,
  every switch and every chord on) and the hand-back (altTab false: Tab, Shift+Tab, arrows, Esc all pass).
- `Mantle.Core/Features/Switcher/SwitcherLayout.cs` (kept from the stopped lane; its doc pointed at a golden file
  that does not exist, now points at the tests): layout.js layout, reflow, notice, firstRow.
- `Mantle.Core/Features/Switcher/SwitcherHook.cs`: applyHook's request (`Want`) and order of checks (`Next`),
  stateWords, restWords, the stale-exe words, the tray labels and line cut. `SwitcherHookClient.cs`: applyHook against
  IPlatform (`hotkey {altTab, winKeys}`, 4 s; waits for an open Alt+Tab; a Down sidecar is not asked), Parse of the
  sidecar's Status() reply, the facts and the log line; tested on FakePlatform (`SwitcherHookClientTests`).
- `Mantle.Core/Features/Switcher/SwitcherWords.cs`: parkSpot, noticeWords, nameFor, the notice rule, cleanBoxes,
  activatedLine.
- `Mantle.Core/Features/TaskView/TaskViewLayout.cs`: ui/taskview/layout.js (layout, place, rowLen, move, firstRowFor,
  filter).
- `Mantle.Core/Features/TaskView/TaskViewGuards.cs`: the feature row and timings, current/checkHwnd/checkDesk, the desk
  command's checks in taskview.js's order, plain(), parkRect.

Tests: `Mantle.Core.Tests/Features/Switcher/{SwitcherLayoutTests,SwitcherHookTests,SwitcherWordsTests}.cs`,
`Mantle.Core.Tests/Features/TaskView/{TaskViewLayoutTests,TaskViewGuardsTests}.cs` (xunit, as the tree).

## The hand-back (safety)

`SwitcherHook.Want(settings, page, resting, featureUp)` is the only place Alt+Tab is given to Mantle: switch on AND
feature up AND page ready AND not resting. Anything else asks the sidecar for altTab false, and AltTabKeys proves a
machine with altTab false swallows nothing of an Alt+Tab. Its own tests: `Hand_back_Alt_Tab_goes_to_Windows_when_off_
unhealthy_or_resting` (Core) and `HandBack_AltTabOff_EveryAltTabKeyIsWindowsOwn` (sidecar). No key handling of this
lane sits in front of the kill switch; `KillSwitch_PassesThrough_*` prove the sidecar machine never swallows it.

## Deviations

- `Want` adds `resting` and `featureUp` terms. Electron sets page.ready false on rest and on a dead page, so no answer
  Electron gives changes; native just does not depend on that side effect.
- SwitcherLayout: a count is capped at 1e6 and an infinite work area reads as the default (JS loops on Infinity).
- TaskViewLayout: the widest tile is a whole DIP (JS keeps a fractional width when the area is under 420 DIP wide,
  only on a window under 516 DIP).
- SwitcherWords.NameFor takes the pinned name from the caller (the bar's pinned list is the bar lane's).

## PEND-012 (cited Electron test vs the row)

- SWI-010: no Electron scenario for "before main has sent the column count"; AltTabKeys adds one.
- SWI-012: the log reason "Esc" is not checked by the cited machine test.
- SWI-018: Electron counts Alt and Win keyups only; the row also says Ctrl. Ctrl keyups are checked only in the new
  kill-switch cases.
- SWI-020: the cited test covers the machine's watchdog commit only, not the 40 ms poll, the rehook or the
  other-window-in-front close.
- SWI-021 / SWI-022: "via click", the after-session pick and the "refused" log line are not covered.
- SWI-026: the bar opening its panels is not covered (machine only).
- SWI-028: no test asserts the chord ownership list.
- SWI-031: `current = -1` is not checked by op:order (it reports sel only).
- SWI-058: "rows never centred" is a page rule; layout() does not test it.
- SWI-064: the empty panel's icon and text are ui/switcher/switcher.page.test.js, not ported.
- SWI-118, SWI-119 (rule half), SWI-131 (append half), TV-007: the cited test is "none"; native tests added.
- SWI-132: applyHook's request is not unit tested in Electron (only the pendingApply words); native tests `Want`.
- TV-001: taskview.test.js drives the chord but does not assert the label or defaultOn.

## Shared files

None touched. No App/service registration, no chord table change (Win+Tab is already in ChordTable), no csproj change
(both test projects pick up new files by default glob).

## Left (ID ranges)

- SWI: 024, 029, 033..042, 044..054, 056, 057, 066, 070..087, 089..113, 121..127, 136..138, 140, 141, 143, 144;
  and the "not yet" halves noted in the status cells.
- TV: 002..006, 008..046, 049, 050, 052, 054..070, 072..079, 082..100, 102..118, 122..126, 129..134.
- Next in value: the Switcher feature itself (an IFeature that applies `Want` through the sidecar client, publishes
  StateWords, handles open/commit/cancel/activated events, the 500 ms pre-size, the stuck-overlay guard), then the
  overlay surface (Surfaces/Switcher: parked window, DWM thumbnails from CleanBoxes, notice), then Task View's feature
  and surface.

## Second lane (r9-switcher-2, 2026-11-24): the services

Still WRITTEN, NOT RUN (no SDK). Built:

- `Features/Switcher/SwitcherSessions.cs`: lib/switcher.js's session half on an `ISwitcherOverlay` seam (the surface
  implements it): onOpen (DIP layout over the monitor scale, px, centred; parked sizing, twice on a new monitor; page
  session; grid cols to the sidecar), move, switcher.painted (stale / waiting on a size mismatch / previews while
  parked, then the reveal), maybeReveal (100 ms after the key or at paint; rehearsal and test runs stop short), the
  stuck-overlay guard (Alt polled every 200 ms while shown, down after 600 ms up with no word; nothing switched),
  endVisual (previews FIRST, park only if moved, page end; once, only for THE session), commit/cancel/activated,
  pick's index mapping, onReflow (grow never shrink, column re-send, counts-only log), presize. No timers of its own:
  `Tick(nowMs, altDown)` on the QPC clock the sidecar's keyAt uses.
- `Features/Switcher/SwitcherFeature.cs`: `[Feature("switcher", "Alt+Tab window switcher", true, Rest = true)]`, no
  host chord. Asks `hotkey {altTab, winKeys}` via SwitcherHookClient at start, on page ready/down, on the tray switch,
  on settings and when the sidecar comes back; routes the sidecar's `switcher` events; commands switcher.ready,
  .down, .painted, .pick, .status, .settings, .set; one re-armed clock (40 ms while a session is up, else the 500 ms
  pre-size); `switcher.state` broadcast/replay of StateWords.
- `Features/TaskView/TaskViewFeature.cs`: `[Feature("taskview", "Task View (Win+Tab)", true, Chords = {"Win+Tab"},
  Rest = true)]` on an `ITaskViewSurface` seam: Win+Tab open / again closes, not-ready and off do nothing but a log
  line, turned off ends, dismiss reasons, activate / close / desk checked per session (TaskViewGuards) before the
  sidecar, dry runs in a test run, close refused in a test run, the 350 ms / 1.6 s close re-check and its words,
  stop/rest drops the window.

Tests: `Mantle.Core.Tests/Features/Switcher/{SwitcherSessionsTests,SwitcherFeatureTests}.cs`,
`Mantle.Core.Tests/Features/TaskView/TaskViewFeatureTests.cs` (xunit; the feature tests on the host rig of
HostTestKit.cs with FakePlatform).

### Safety (second lane)

- With NO overlay (the app today), the switcher's page is never ready, so `Want` asks altTab false: Alt+Tab stays
  Windows' own. Win+N / Win+A are asked for only when the composition root passes `Host.OnWinKey` (nothing in native
  opens the bar's panels yet; a swallowed Win+N would open nothing). So adding the feature changes no key today.
- Task View with no surface calls `Health(false)`, so the host never takes Win+Tab from Windows.
- The stop/rest hand-back: the feature's own platform refuses every call once StopOne has set Stopped, so
  StopAsync's `hotkey {altTab:false, winKeys:false}` only goes through `Host.HandBack` (the unscoped platform, given
  by the composition root). Without it a rest leaves the sidecar with altTab as last asked (it still switches on
  Alt-up, nothing drawn). Quitting is covered by ShellGuard's full put-back. OPEN ITEM for the coordinator.
- Nothing here is in front of any key; the kill switch tests in AltTabKeys are untouched.

### Deviations (second lane)

- Settings live in the feature's own store (`features\switcher.json`), not userData `switcher.json` (DataFileSpecs /
  SettingsKeys name the Electron file): an Electron user's switch is not carried over. Migration needs a shared-file
  change; not done.
- A rest hands back Win+N / Win+A too (Electron keeps them Mantle's while resting): a stopped feature hears no winkey.
- switcher.painted with a NaN viewport waits (JS lets NaN through as a match).
- The command for the switches is `switcher.set` (+ `switcher.settings` for the view); there is no
  `settings.switcher` yet (lib/features/settings.js is another lane's).
- The sidecar's `hook` event only re-publishes; it does not replace the client's last answer (the client keeps it
  private).
- No Electron setBounds fallback for a failed SetWindowPos (the surface's Place says why; it is logged).

### PEND-012 (second lane)

- SWI-044, 047, 050..052, 057, 066, 137, 140, 141, 144 cite "none" or live/page tests only: native tests added.
- SWI-045 cites the live rehearsal (switcher.live.test.js), not a unit test.
- SWI-046, 077, 086, 106, 109 cite page or live tests: they cover the page half, not main's.
- SWI-127's settings.test.js covers settings.switcher, which native does not have yet.
- TV-015: taskview.test.js has no "giveBack" check by grep; the keyboard not going back after a switch may be
  untested in Electron.

### Shared files (second lane)

None touched (no App wiring, no csproj, no chord table). The launch.md Native status cells: 36 new, and the 76 cells
of the first lane whose `/` had been turned into `| todo |` were repaired (column count now 8 on every SWI/TV row).

### Left (ID ranges, after the second lane)

- SWI: 024, 029, 033..042, 048, 049, 053..054, 056, 070..076, 078..085, 087, 090, 091, 093..105, 107, 108, 110, 112,
  113, 121, 124..126, 143; and the "not yet" halves in the status cells.
- TV: 003, 006, 008..014, 016..023, 026..028, 030, 031, 033..046, 049, 050, 052, 054, 055, 057..060, 062..070, 072..079,
  082..095, 097..100, 102..118, 122..126, 129..134.
- Next: the composition root (Configure both Hosts: overlay/surface, AltDown via GetAsyncKeyState, HandBack, OnWinKey,
  WindowCount from the bar's list), then Surfaces/Switcher on the r10-newlook style layer, then Surfaces/TaskView.
