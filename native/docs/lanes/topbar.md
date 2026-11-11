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

## Left (exact list, in order)

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
