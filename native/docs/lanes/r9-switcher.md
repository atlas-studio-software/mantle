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
  ">= 33" check would have failed. Added: the kill switch Ctrl+Alt+Shift+Backspace passes (switcher open and closed,
  every switch and every chord on) and the hand-back (altTab false: Tab, Shift+Tab, arrows, Esc all pass).
- `Mantle.Core/Features/Switcher/SwitcherLayout.cs` (kept from the stopped lane; its doc pointed at a golden file
  that does not exist, now points at the tests): layout.js layout, reflow, notice, firstRow.
- `Mantle.Core/Features/Switcher/SwitcherHook.cs`: applyHook's request (`Want`) and order of checks (`Next`),
  stateWords, restWords, the stale-exe words, the tray labels and line cut.
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

- SWI: 024, 025, 029, 033..042, 044..054, 056, 057, 066, 070..087, 089..113, 121..127, 136..138, 140, 141, 143, 144;
  and the "not yet" halves noted in the status cells.
- TV: 002..006, 008..046, 049, 050, 052, 054..070, 072..079, 082..100, 102..118, 122..126, 129..134.
- Next in value: the Switcher feature itself (an IFeature that applies `Want` through the sidecar client, publishes
  StateWords, handles open/commit/cancel/activated events, the 500 ms pre-size, the stuck-overlay guard), then the
  overlay surface (Surfaces/Switcher: parked window, DWM thumbnails from CleanBoxes, notice), then Task View's feature
  and surface.
