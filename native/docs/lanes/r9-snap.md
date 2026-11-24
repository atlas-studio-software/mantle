# Lane native-r9-snap: window snapping, the geometry and the Win+arrow move path

Round: stage 10 (native r9). Branch `native/r9-snap` from `40414f38`. Nothing here was built or run: the laptop has
no .NET SDK, so every row below is `written-not-run`, never `tested`. The held cloud command is
`cloudtest --recipe mantle-native-public -- "pwsh -NoProfile -File native/tools/ci.ps1 -Stages build,unit,ui,sidecar"`.

## The shape

- **The geometry is the sidecar's** (PLAN A4): `sidecar/Snap.cs` (SnapGeom, the Win+arrow state machine, zones,
  DPI, apps Windows scales) is linked unchanged into `Mantle.Sidecar`. It is tested in-process through the real
  dispatcher with the pure `snap.plan` verb (made-up monitors, no window touched):
  `native/tests/Mantle.Sidecar.Tests/Snap/SnapPlanTests.cs`, `SnapVerbsTests.cs` (verbs and lanes, read-only).
- **The move path is new native code** in `native/src/Mantle.Core/Features/Snap/`:
  - `SnapSafety.cs`: the never-strand rule (below), pure.
  - `SnapWords.cs`: snap.js's words (ZONE_WORDS, cap, appOf, why, namesOf, unname).
  - `SnapCore.cs`: lib/features/snap.js#arrow and #probe: snap.move {dir[,hwnd]} with 2.5 s; the refusal / error /
    does-not-fit notices; no notice for nothing to do; no app name in any log line; the --test no-target refusal;
    the start-up probe verdict. Then the safety check on every landed frame.
  - `SnapFeature.cs`: `[Feature("snap")]`, default on, rest, the four arrow chords, notices as the `snap.notice`
    event {title, text, work} for a surface that does not exist yet.
- Tests: `native/tests/Mantle.Core.Tests/Features/Snap/{SnapSafetyTests,SnapCoreTests,SnapFeatureTests}.cs` (xUnit).

## The never-strand rule (safety)

A window Mantle moves must always end with its title bar (the top 32 DIP of its visible frame, at least 64 DIP of it
across, or all of it when narrower) inside a LIVE monitor's work area, within DWM's 2 px rounding.

Two holes in the shared geometry, found while porting (sidecar/*.cs is not this lane's to change):

1. `snap.move`'s un-snap looks the remembered monitor up by id (`Snap.cs` Move, `preMon`); when that monitor is gone,
   `preMon` is null and `SnapGeom.Translate` returns the old frame untouched, so the window is put back on a monitor
   that no longer exists.
2. `Translate` also returns the frame untouched when the monitor is the same one, even if its work area shrank or its
   scale changed since (resolution change, taskbar moved), so the frame can hang off it.

The native path closes both after the fact: `SnapCore` checks the frame the sidecar reports it landed at. On the
move's own monitor = no extra call. Otherwise it reads every live monitor (`snap.monitors`, 1.5 s; if that fails, the
move's own monitor), `SnapSafety.Clamp` decides, and a stranded window gets ONE `layouts.place` {hwnd, pid, state
normal, frame} (6 s). A put-back that fails is a notice ("Could not put <app> back on the screen"), never silent.
A safe frame is never moved (a window the user left hanging off an edge stays). With no live monitor known, nothing
moves. `SnapSafety.Restore` is the restore-rect rule for a later caller (a remembered frame with the monitor id, work
area and DPI it was saved on): exact when unchanged, DPI-scaled and fitted when that monitor changed, carried onto the
nearest live monitor when it is gone.

Never touched by this lane: explorer.exe, the taskbar's show state, the kill switch. No real window was moved and no
test instance was started.

## Deviations

- **Win+Z is not claimed** (SNAP-001 says five chords). Until the picker and Snap Assist surfaces exist, Windows keeps
  Win+Z (its own layout flyout) instead of a Mantle Win+Z that would do nothing. Add "Win+Z" to the attribute when the
  picker lands.
- **New: the never-strand check** after every Win+arrow move (above); Electron has none. SNAP-028 is marked
  "better: never strands".
- **New notice** "Could not put <app> back on the screen" (only when the put-back fails).
- Notices are an event only (`snap.notice`); no surface draws them yet, so SNAP-054/055/056 stay `todo` with the words
  marked written.

## PEND-012 (cited Electron test vs the row)

- SNAP-018/019/020: test-snap.js walks only tl's cross-monitor step; tr/bl/br's cross-monitor steps were untested.
  Added here (`The_other_quarters_walk_to_the_mirrored_quarter_of_the_neighbour_monitor_and_wrap`).
- SNAP-021: test-snap.js tests only Down from a Win+Z third; Left/Right/Up added here.
- SNAP-024: no Electron test for "the monitor holding most of it"; added here.
- SNAP-025/026: the remembering itself (first snap from normal; keeping an EARLIER frame out of maximized) is
  `Snap.cs` Rec bookkeeping on the live path; test-snap.js only feeds `remembered` in. SNAP-026's main claim has no
  test anywhere (left `todo`).
- SNAP-046: test-snap.js covers the plan's "none" for a bad direction and the bad-zone words; "snap.move needs dir",
  "that screen is not connected any more" and "Windows reports no monitors" are live-path words with no pure test.
- SNAP-047: no Electron test asserts the lanes; added (read-only Lanes.For).
- SNAP-057: lib/features/snap.test.js covers it (the unnamed notice line); ported.
- SNAP-100: snap.test.js does not test the 2/5/15/30 s retry ladder or its words (only SNAP-101's older-sidecar
  case); added here as a pure verdict (`SnapCore.Probe`). The feature's timer itself is not tested.
- SNAP-055: snap.test.js has no "Could not snap this window" case; added here.
- SNAP-079: the "already filled by another window" exclusion is the live path (Execute), not in the plan test.

## The untracked folder

`native/tests/Mantle.Sidecar.Tests/Snap/` (SnapPlanTests.cs, left by a stopped lane) was KEPT: it uses the tree's
MSTest + `InProcess.Run` harness as it is at 40414f38, calls only the pure `snap.plan`, its expectations are hand
arithmetic matching sidecar/test-snap.js's pure half, and its row tags match launch.md. Converted to CRLF; two tests
added (above).

## Shared files

None edited. The chords were already in `Mantle.Core/Keys/ChordTable.cs`; the feature registers itself through the
`[Feature]` generator; the test folders are picked up by the default compile globs.

## Left (ID ranges)

- SNAP: 001 (Win+Z), 002..004 (host/hook rows, not re-tested here), 026, 036..046 (live move path and refusals in
  `Snap.cs`, unchanged), 048, 050, 052, 054..056 (surface), 058..099 (notice, picker, Snap Assist, the snap window),
  102..117 (rest, settings, tray, theme, DPI of the window, test mode, stop, Windows' own snap settings), 118..170
  (window layouts and their pop-up).
- UNF-001..111: all left (the lock/wake unfold overlay: `lib/features/_unfold-core.js`, `_unfold-windows.js`,
  `sidecar/Unfold.cs`). Not restore/maximize behaviour, despite the lane brief's gloss.
