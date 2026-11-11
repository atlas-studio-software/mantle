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

## State at handoff (context ceiling)

- **Run 42997383359 (cb079414):** `NATIVE-CI: FAILED (build, unit failed)`. Cause: CS0029 x3, lambda parameters
  named `_`. Fixed in 1414fb69.
- **Run 42997875488 (1414fb69):** `NATIVE-CI: FAILED (unit failed; 1 ok)`. The build is ok, and 5 of 656 unit tests
  failed:
  - **Three of the live tests** (`A_live_run_reserves...`, `Live_a_work_area...`, `Live_hidden_by_hand...`): the
    likely cause is that they omit `test: false` in `T(...)`, so they run as --test.
  - **`TopBarPureTests.Stat_cells_fixed_decimals_and_reasons`:** a value mismatch; read the assert message.
  - **`TokensTests.No_colour_literal_in_Mantle_App_outside_Tokens_xaml`:** TopBarSurface uses
    `Microsoft.UI.Colors.Transparent`. Replace it with `new SolidColorBrush()` (clear) or a token.
- **Ledger:** no parity rows are marked yet. Rows are marked only after a green run.

## Left (exact list, in order)

1. **Make the build and unit run green.** First compile on the runner; likely spots:
   - `Typography.SetNumeralAlignment` and `FontNumeralAlignment` in TopBarSurface;
   - the async lambdas returning `ValueTask<CommandResult>` in TopBarFeature.
2. **UI tests** in `tests/Mantle.UiTests/TopBar/TopBarSurfaceTests.cs`, copying OsdSurfaceTests' Launch / Second and
   waiting with Host.LogContains. Run with `MANTLE_TEST_TOPBAR=app=npp&stats=ok` and `MANTLE_TEST_VISIBLE=1`:
   - parked then shown: log `surface topbar: made`, then `shown (`;
   - title `Mantle top bar`, not topmost in --test;
   - geometry at 100 % and 150 % with FrameProbe.SetScale: window at the primary's top-left, full width,
     round(28 x scale) tall, and a `placed at ... (display: scaleFactor)` line after the change;
   - light and dark via `--theme`;
   - each control by AutomationId (`topbar.title.mantle`, `topbar.title.app`, `topbar.title.m0`, `topbar.more`,
     `topbar.stat.cpu|mem|gpu|net|ping`, `topbar.tool.timer|notes`), clicked, each with its `... pressed:` log line;
   - Win+Alt+T through `--test-chord=Win+Alt+T`: hidden, then shown;
   - the peek through `topbar.testPeek`.
3. **Pairs:** add `tools/parity/topbar/run-topbar-parity.ps1` + `electron-topbar.cjs`:
   - Electron shoots `ui/topbar/index.html?app=npp&stats=ok[&dark=1]` over a solid colour, with the mock body
     gradient replaced by that colour;
   - native runs over a backdrop window of the same colour;
   - light and dark, 100 % and 150 %; one `TOPBAR-PARITY <tag>: ssim=` line each, then a summary.
4. **CI:** add ONE `Stage-topbar` block at the end of the ci.ps1 stage list, marked `# lane native-r7-topbar`, with its
   floor.
5. **Ledger:** mark the TOP / BAR rows the green tests cover. The Core tests name their rows: TOP-018..025, 201, 203-205,
   222, 233-238, 241-249, 253, 259-262, 265, 266, 282, 283, 286, 287, 290-303, BAR-348..350.
6. **Sections not started** (shell.md order):
   - Menus (TOP-031..), Keyboard;
   - Stats dropdowns, App power, Ping, Tools (the timer / notes features), Strip note;
   - Glass, Under colour, Glass + Strip (dark backdrop), Under colour (dark only);
   - Top-row hints, Recovery + lifecycle, Data + IPC (statsDetail, invoke, uia, window, newWindow, power, about,
     settings, ...).
7. **Parts of started sections not ported:**
   - Page and strip: TOP-003 menubar role, TOP-006/007 visible width, TOP-010..017 focus details (uia cache, title
     menuChanged re-render, the PIN_FOCUS test hook).
   - Feature + settings: TOP-211 tools hub.
   - Window + displays: TOP-220 zoom (n/a natively), TOP-224 tall window for dropdowns (the place half is done),
     TOP-225 refocus, TOP-227..232 drift put-back, TOP-250 Explorer restart (onShellRestart: no native host hook found),
     TOP-252 game start mid-reserve, TOP-258 session end.
   - Step aside: TOP-267..272 known games RUNNING (GameWatch follower), TOP-273..280 full-screen video (media sessions),
     the motion (leave / arrive).
   - Peek: TOP-297 the hot corners claim (corners not native yet).
