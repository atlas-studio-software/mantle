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
  - `SnapFaces.cs`: the snap window's faces as numbers for that surface (notice spot, grow and time; picker spot;
    the five Win+Z layouts and `rest`; the picker's zone and row walk). DIP, JavaScript rounding.
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
- SNAP-058/059/070: lib/features/snap.test.js checks none of the notice spot, the grow, the time or the layouts table
  (only SNAP-067's picker spot, line 219); added here. SNAP-071 cites ui/snap/shoot.js, a screenshot script, not a
  test; the walk is added here, the digit keys are not (the digit handling was not found in ui/snap/snap.js).

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
  `Snap.cs`, unchanged), 048, 050, 052, 054..056, 058, 059, 067, 070, 071 (rules written, no surface), 060..066,
  068, 069, 072..099 (notice, picker, Snap Assist, the snap window),
  102..117 (rest, settings, tray, theme, DPI of the window, test mode, stop, Windows' own snap settings), 118..170
  (window layouts and their pop-up).
- UNF-001..111: all left (the lock/wake unfold overlay: `lib/features/_unfold-core.js`, `_unfold-windows.js`,
  `sidecar/Unfold.cs`). Not restore/maximize behaviour, despite the lane brief's gloss.

## Second window (lane r9-snap-2, 2026-11-24)

Still nothing built or run (no .NET SDK). Two new files in `Mantle.Core/Features/Snap/`, model only, no surface:

- `SnapPanel.cs`: lib/features/snap.js#picker, #pickZone, #openAssist, #candidates, #pickWindow, #giveFocus, #end,
  the pick and dismiss commands and #reveal's keyboard check. A panel is a `SnapShow` {seq, mode, rect (DIP), payload
  = snap.show's} plus a hide by seq; the surface converts physical to DIP (`toDip`) and finds the display's work area
  (`workOf`), both injected. Its own `ArrowAsync` wraps `SnapCore.ArrowAsync` and opens Snap Assist after a snap.
- `SnapPages.cs`: ui/snap/snap.js's keys (the picker's incl. the digits 1-5, Enter/Space, Esc; Snap Assist's), the
  tile grid (`Layout`, `Tile`), the row paging (`SelectTile`, `MoreHint`) and every face's words, as pure functions.
  No colour, font, film, sheen, hairline or box anywhere, so the surface can sit on the shared style layer
  (Look/Levitate) with the panel background in one place there.
- `SnapCore.cs`: `R1`, `Js`, `Truthy`, `Int`, `Notice` made `internal`; new `internal LandedAsync(label, answer)`, the
  same never-strand check and put-back as Win+arrow, plus the "Could not put <app> back on the screen" notice.
- Tests: `Mantle.Core.Tests/Features/Snap/SnapPanelTests.cs`, `SnapPagesTests.cs` (xUnit, fake sidecar, nothing touched).

**Not wired.** SnapFeature still claims only the four arrows and calls `SnapCore` directly; the surface lane swaps
in `SnapPanel` (its `ArrowAsync`, `PickerAsync` for "Win+Z", `PickAsync`/`DismissAsync` for the page,
`Off` from the enabled change, `Stop`) and adds "Win+Z" to the chords. Until then every row below stays `todo` with
its model marked written-not-run.

### Safety (r9-snap-2)

Every window a pick moves (snap.zone from the picker and from Snap Assist) goes through `SnapCore.LandedAsync`:
on its own monitor = no extra call; else every live monitor is read and `SnapSafety.Clamp` decides; a stranded window
gets one `layouts.place`; a put-back that fails is a notice. NEW: a window that had to be put back (after Win+arrow,
Win+Z or Assist) gets NO Snap Assist (its empty zones were measured for where it was meant to land) and the keyboard
goes back to it. Tests, all `SnapPanelTests`:
`Safety_a_Win_Z_zone_landing_off_every_live_monitor_is_put_back_once_and_gets_no_Snap_Assist` (monitor gone),
`Safety_a_Win_Z_put_back_that_fails_is_a_notice_never_silent`,
`Safety_multi_monitor_a_Snap_Assist_pick_on_the_first_windows_other_monitor_is_judged_there_and_left_alone`,
`Safety_multi_monitor_a_Snap_Assist_pick_hanging_off_the_other_monitor_is_fitted_back_onto_it`,
`Safety_DPI_too_little_title_bar_at_150_percent_is_put_back_but_the_same_landing_at_100_percent_is_left`,
`Safety_a_Win_arrow_snap_that_had_to_be_put_back_opens_no_Snap_Assist`,
`Safety_a_pick_that_moved_nothing_by_rect_is_not_checked_or_moved`. Restore: no new restore path was added; the
restore rule's tests stay the first window's (`SnapSafetyTests#A_restore_*`).

### Deviations (r9-snap-2)

- New notice text "Windows did not say where the window is." (title "Snap layouts are not available") when snap.info
  has no frame; snap.js would throw there. New error "Snap Assist has no empty zone left" (cannot happen in snap.js).
- No Snap Assist after a put-back (above).
- A Win+Z pick whose zone answer had no target hwnd opens Assist for the answer's hwnd (snap.js would use undefined).
- In a test run Snap Assist offers nothing (native has no test windows of its own yet) and says so in the log.
- The end-of-panel log is `#N mode: ended: why`; the drawn/shown timings and the picture count belong to the surface.
- `MoreHint` keeps snap.js's trailing " · " when only "more above" applies (ported as is, not fixed).

### PEND-012 (r9-snap-2)

- SNAP-071: the digit keys ARE in ui/snap/snap.js (keydown, `/^[1-9]$/` and at most the layout count); the first
  window's note missed them. Still no Electron test: ui/snap/shoot.js is a screenshot script. Added here.
- SNAP-062 (Win+Z again closes and gives focus back), SNAP-068, SNAP-075 (a refused Win+Z pick, "no zone N in layout
  X"), SNAP-076's click-away half, SNAP-077, SNAP-092, SNAP-093's switch-off half: lib/features/snap.test.js has no
  case (its Esc case, line 240, and the Assist pick cases, lines 168-176, are real). Added here.
- SNAP-074: snap.test.js checks the focus after an Assist pick only, not after a plain Win+Z pick; added.
- SNAP-079: the "already filled" exclusion is still only the sidecar's live `Occupied`; no pure test possible here.
- SNAP-082..085, 088, 089: cite ui/snap/shoot.js, a screenshot script, not a test; the model is tested here.

### Rows (r9-snap-2)

Model written-not-run, still `todo` (not wired / no surface), 31 rows: SNAP-060..069, 071..078, 080..085, 088..093,
110 (067 and 071 extended). Left: SNAP-001..004, 026, 036..046, 048, 050, 052, 054..056, 058, 059, 070 (as before),
079 (live path), 086, 087 (mouse and pictures: surface), 094..099 (the snap window), 102..109, 111..170;
UNF-001..111.
