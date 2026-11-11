# Lane notes: native-r7-topbar (the top bar)

Parity rows: shell.md TOP-001..382, bar.md BAR-348..351 (Top bar hide), BAR-583..588 (the QS Top bar row: the bar
lane's surface; it reads this feature's `topbar.state`). Electron source is the spec (read only). No PEND rows were in
this area.

## Part 1 (branch `native/r7-topbar`, commit cb079414)

| Native file | Electron source | What it is |
|---|---|---|
| `Mantle.Core/Features/TopBar/TopBarModel.cs` | `ui/topbar/model.js` | app name, app menu, Mantle menu, titles, fitTitles, stat formats and cells, ping cell, notes button |
| `Mantle.Core/Features/TopBar/TopBarRules.cs` | `lib/features/topbar.js` (pure parts) | BAR_DIP, the title, reserveSize, stripGap, workAreaCheck, reRegister, lockScreen, asideKind, the strip / log words |
| `Mantle.Core/Features/TopBar/TopBarDisplay.cs` | `lib/features/_topbar-display.js` | displayVerdict and makeBurst |
| `Mantle.Core/Features/TopBar/TopBarPeek.cs` | `lib/features/_topbar-peek.js` | inBand, blocker, startState, step |
| `Mantle.Core/Features/TopBar/TopBarScreen.cs` | (topbar.js `screen` reads) | the primary display, cursor and buttons, READ only (in process, no sidecar) |
| `Mantle.Core/Features/TopBar/TopBarFixtures.cs` | `ui/topbar/mock-mantle.js` | --test only: the made-up apps (npp, chrome, explorer, desktop) and stats (ok, nogpu, paused) |
| `Mantle.Core/Features/TopBar/TopBarFeature.cs` | `lib/features/topbar.js` (install) | `[Feature("topbar", "Top menu bar", true, Chords = Win+Alt+T, Rest = true)]`, options `hidden` / `overGames`, the run (ping, watch, decide, reserve, re-checks at 1.5 / 6 s, guard, stats, show, peek, health), hidden by hand, step aside (lock, full screen, the sidecar's game verdict), display bursts, the peek poll |
| `Mantle.App/Surfaces/TopBar/TopBarSurface.cs` | `ui/topbar/index.html`, `topbar.css`, `topbar.js` | `[Surface("topbar", AtStart = true)]`: the window and the strip |

### How the halves talk

- **Feature to surface:** `topbar.visible {Show, Why, Kind, Peek, Seq}`, `topbar.place {Height, Why}`,
  `topbar.focus {Json, MenuJson, Fixture}`, `topbar.stats {Json, Paused}`, `topbar.close`, `topbar.peek`, `topbar.note`.
  `topbar.state` (replayed) is the QS row's (TOP-288).
- **Surface to feature:** `topbar.placed {hwnd, scale, x, y, w, h}`, sent on every placement and again on every feature
  push until one is answered, so a feature installed after the surface still learns the hwnd. Also `topbar.display {kind,
  changed}` on WM_DISPLAYCHANGE / WM_DPICHANGED / SPI_SETWORKAREA, and `topbar.shown {on}`.
- **Commands:** `hello`, `hidden {on, via}`, `state`, `status`, `overGames {on}`, `menuState {open, height}`. Test-only:
  `testFixture {app, stats}` and `testPeek {x, y}` (a fake cursor in DIP; `{real:true}` puts the real one back).

### Windows state

- **App bar:** `topbar.reserve` / `topbar.release` are sent ONLY in a `--live` run. A `--test` run logs
  `app bar <why>: dry run (--test): would have reserved N px (28 DIP at Sx) ...` and never calls either. Unit test:
  `A_test_run_shows_the_bar_and_never_asks_for_the_app_bar`.
- **Drag guard:** `topbar.guard` is asked in both modes, as Electron does. A `--test` sidecar is passive and only
  measures drags.
- **Hidden by hand:** written to the run's own `features\topbar.json`.
- **Window:** a `--test` window is never topmost. A quiet one (no MANTLE_TEST_VISIBLE) is alpha 0, click-through, and
  sent to the back after every show.

### Deviations (proposed)

- **A visible --test window is not sent to the back** (TOP-222 keeps the quiet case). The UI tests and pairs grab it over
  a backdrop window of another process, as the OSD pairs do.
- **Health ignores "the window is not made yet".** Electron's `pageOk` would flag the feature DOWN for the moment between
  install and the window. That would hand Win+Alt+T back to Windows on every start.
- **`better:` candidate, TOP-213/218.** A WS_POPUP with no thick frame answers HTCLIENT everywhere and takes an exact
  28 DIP height. If Windows keeps it taller, a window region crops it (logged). Electron needed a shape around Chromium's
  38-40 DIP floor. To be confirmed by the UI geometry test before marking.

## Part 2 (lane native-r7-topbar-2, same branch)

### Fixes

- **The five unit failures (run 42997875488):** the three live tests now pass `test: false`; `TopBarModel.Num` reads an
  int JSON number (a built `JsonValue` of 23 does not `TryGetValue<double>`, so the ping cell said `--`); the surface's
  clear brushes are `ClearBrush()` (`new SolidColorBrush()`, all-zero ARGB) instead of `Colors.Transparent`. Run
  42998830982 (154f63d3): `NATIVE-CI: ok (2 ok, 0 skipped)`, unit 656 of 656.
- **The surface was never made on the runner** ("Specified cast is not valid", found by logging the frames of a failed
  make: `MakeButton`). The strip buttons are now loaded from XAML with their template inline (a `ControlTemplate` cast
  from `XamlReader.Load` is the likely AOT cast failure: no other surface casts one), and each themed step is guarded
  and logged once if it throws. Also: glyph paths go through a XAML `Path` (not `XamlBindingHelper.ConvertValue`), and
  tabular figures are set on a `Run` (Typography on a TextBlock is not promised to work), with any refusal logged.

### Tests and pairs

- **UI tests:** `tests/Mantle.UiTests/TopBar/TopBarSurfaceTests.cs`, three tests, each its own --test profile with
  `MANTLE_TEST_TOPBAR=app=npp&stats=ok`: (1) parked then shown, title, not topmost, toolwindow, work area untouched,
  geometry at the current scale, every control by AutomationId pressed with its log line, light then dark through
  `--test-theme=dark` (face luminance); (2) started `--theme=dark`, Win+Alt+T (`--test-chord`) hides with the dry-run
  line and the hidden flag in the run's own topbar.json, the REAL cursor resting on the top edge peeks it and leaving
  hides it, Win+Alt+T shows it again; (3) 100 % -> 150 % -> 100 % with `FrameProbe.SetScale`: same window, at the
  top-left, full width, round(28 x scale) px tall.
- **Deviation from the brief: the peek is driven by the real cursor, not `topbar.testPeek`.** No second-start road
  reaches a feature command (`--test-surface` only calls `surface.*`), and adding one means editing App.xaml.cs /
  Requests.cs outside this lane. The real cursor is the stronger test; `testPeek` stays for the Core tests.
- **Pairs:** `tools/parity/topbar/run-topbar-parity.ps1` + `electron-topbar.cjs`, light and dark at 100 % and 150 %.
  Electron draws `ui/topbar/index.html?app=npp&stats=ok` in a window that is never shown (an OFFSCREEN window paints at
  scale 1 whatever `force-device-scale-factor` says: its 150 % shots came back 1280x28), theme set by the same file as
  preload before the first paint, page over the backdrop colour, no `?flat=` (the dark glass is not native yet, so the
  dark pair compares Electron's `--lg-fill-pop` fallback with native's fill). Native over a full-screen backdrop window.
- **CI:** stage `topbar` at the end of ci.ps1's list (block marked `# lane native-r7-topbar`), gated on
  `TOPBAR-PARITY summary: 4 scored of 4` and a floor per theme (the lowest of its two scales).

## State at handoff 2 (context ceiling)

- **Runs:** 42998830982 (154f63d3) `NATIVE-CI: ok (2 ok)`, unit 656/656. 43011515526 (312921cb, build,topbar): the strip
  made at last, 4 of 4 scored but the native half was the bare backdrop (the strip was BEHIND it: handing the foreground
  back raised the backdrop; fixed in 8cc89c1d) and Electron's 150 % page was 853 CSS px wide (forced scale on a 150 %
  display; fixed in 8cc89c1d). 43012272746 (8cc89c1d, build,unit,ui,topbar): build ok, unit 656/656, ui 47 of 49 passed
  (the strip test and the scale test PASSED), then the ui stage never ended (job killed at 80 min; topbar not reached).
- **UI failures in 43012272746:** (1) `Win_Alt_T_...`: hide, dry run and "peek: watching" all fine, but the real cursor on
  the edge never peeked, with no reason logged. The next commit logs `peek: not now: <why>` on each change and, in --test,
  `peek: first look: the cursor at X,Y DIP, the edge band ...`, so the next run says why. (2)
  `Bar_and_glass_idle_cpu_hidden_at_rest_and_shown` (light, hidden at rest 1.354 % > 1 %): the visible --test strip
  rebuilt its whole right side on every 1 s stats sample. The next commit updates the cells' words in place.
- **The ui stage hang:** the trx was complete (49 executed), then `dotnet test` never returned; the job's cleanup killed
  an orphan `Mantle.Sidecar`. The first ui run (cf88e147, strip never made, feature running) also passed 30 min. Likely
  a sidecar that outlives its killed app holds the inherited stdout pipe; every UI test app now runs the topbar
  feature (topbar.watch / guard / stats in the sidecar). Sidecar exit is not this lane's code: report it to the
  coordinator / sidecar lane. Until then run `-Stages build,unit,ui` with `--timeout 80` and read `native/out/ui/ui.trx`
  from the run's artifacts (`gh run download <id> -n cloudtest-artifacts`) if the stage hangs.
- **Pairs not scored yet** with both fixes; floors are placeholders (0.50) in ci.ps1's topbar block.
- **Ledger:** not marked (no full green run). Marker ready: `python -I native/tools/parity/topbar/mark-topbar-rows.py native [ui]` (Core rows; `ui` adds the UI ones). The rows and their test ids are also in the Core and UI
  test names; TOP-218 is the `better:` (exact 28 DIP, no shape) once the scale test is green in a green run.

## Part 3 (lane native-r7-topbar-3, same branch)

- **Electron's default:** `defaultOn: true` (lib/features/topbar.js), so the native feature stays on in every run.
- **The ui stage hang:** `native/tests/Mantle.UiTests/TestRunJob.cs` puts the test host in a kill-on-close job object
  before the first test, so everything it starts (each app and its Mantle.Sidecar) ends with it. ci.ps1's
  `& dotnet ... *>> $log` waited on a pipe that a surviving sidecar held through handle inheritance. At exit it writes
  `leftover-processes.txt` to the UI out folder. Run 43026242243: `Mantle.Sidecar pid 8204, started 13:13:17.752 UTC`
  was still alive at the host's exit with no app. It was started during `TopmostTests.Bar_and_popups_started_while_
  another_app_is_in_front_sit_above_it` (13:13:16-19, whose app is killed by Dispose), so a sidecar can outlive its
  killed app by over a minute. That is sidecar lifetime code (sidecar/Program.cs: the parent watch and stdin EOF),
  not this lane's. Reported to the coordinator. Run 43028973569: none left.
- **The peek:** the watch starts disarmed (TOP-293) and the test had put the cursor on the edge before the first poll.
  The test now waits for `peek: first look:` first.
- **The pairs:** the first strip of each scale was BELOW the full-screen backdrop (its first Activate hands the
  foreground back, which raises the backdrop). The script now logs the z-order and puts the backdrop right behind the
  strip. At 150 % the runner's console covered the strip's lower 14 px; on the runner it is minimised (ClearRect).
  Scores (run 43030945612): light 0.6139 / 0.6337, dark 0.7344 / 0.6989 at 100 / 150 %. Floors: light 0.59, dark 0.67.
  Known differences: at 150 % Electron cuts the app name ("Notepad...") where native fits "Notepad++", and native's
  titles drift about 0.5 px per title to the right.
- **Ledger:** 59 rows marked by `mark-topbar-rows.py native ui` after run 43028973569 (unit 656/656, ui 49/49).

## Left (exact list, in order)

1. **Sections not started** (shell.md order):
   - Menus (TOP-031..), Keyboard;
   - Stats dropdowns, App power, Ping, Tools (the timer / notes features), Strip note;
   - Glass, Under colour, Glass + Strip (dark backdrop), Under colour (dark only);
   - Top-row hints, Recovery + lifecycle, Data + IPC (statsDetail, invoke, uia, window, newWindow, power, about,
     settings, ...).
2. **Parts of started sections not ported:**
   - Page and strip: TOP-003 menubar role, TOP-006/007 visible width, TOP-010..017 focus details (uia cache, title
     menuChanged re-render, the PIN_FOCUS test hook).
   - Feature + settings: TOP-211 tools hub.
   - Window + displays: TOP-220 zoom (n/a natively), TOP-224 tall window for dropdowns (the place half is done),
     TOP-225 refocus, TOP-227..232 drift put-back, TOP-250 Explorer restart (onShellRestart: no native host hook found),
     TOP-252 game start mid-reserve, TOP-258 session end.
   - Step aside: TOP-267..272 known games RUNNING (GameWatch follower), TOP-273..280 full-screen video (media sessions),
     the motion (leave / arrive).
   - Peek: TOP-297 the hot corners claim (corners not native yet).
