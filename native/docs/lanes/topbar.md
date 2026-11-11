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
- **Smoke:** its UI Automation probe took the process's first top-level window, which is now the top bar; it asks
  for 'Mantle bar' by name (ci.ps1, smoke probe `uia` mode).
- **Menu commands (5e0704ab):** `TopBarFeature.Commands.cs`: `about`, `settings`, `winSettings`, `taskmgr`,
  `newWindow`, each a dry run in --test (`refuseInTest`), opening and spawning through `ITopBarShell`
  (`ProcessTopBarShell` live, a fake in `TopBarCommandsTests.cs`). Deviation: a `newWindow` spawn that fails at once
  answers `ok: false` as well as the "New window: ..." note (Electron's spawn fails later, so it answers ok). Not
  ported yet: `window` and `uia*` (need TOP-308's handle guard), `power` (needs the launcher's power runner, not native
  yet). Rows: TOP-314/316/318/319 tested; TOP-317 ported (the Win+I delivered branch has no test).
- **Strip note (TOP-175, ported):** `topbar.note` is now a `TopBarNote` record. The surface draws it before the stats
  (warn glyph, cut at 60 characters, tooltip, AutomationId `topbar.note`) and clears it after 8 s. No UI test triggers
  a note yet: a `note=` key in `MANTLE_TEST_TOPBAR` would let one.

## Part 4 (lane native-r7-topbar-4, same branch): the dropdowns and their keys

- **Core:** `TopBarMenu.cs` is topbar.js's S.open / S.armed / S.edit and activate / move / stepTitle / openSub /
  refreshLevel / the keydown switch as a pure machine ("now" passed in). Each call returns a `MenuStep` (Render,
  OpenSub, OpenTitle, Armed, Call, Close, Commit) that the surface carries out. Also the geometry (`Left` flip and
  pull-in, `FitDown`, `WindowHeight`, `KeepVisible`) and the strip notes. `TopBarModel` gained `Item` / `FromJson`
  (the app's own rows: id, pos, radio, ownerDraw, lazy, cleaned recursively), `LayoutItems`, and the New window
  item's exe and args. The Notepad++ fixture carries Electron's mock W32 tree (ids 100 + pos).
- **Feature:** `CheckArgs` (TOP-308: only handles the surface was given, i.e. the app in front and its menu's owner;
  there is no UIA read natively yet, so that third handle never exists). Commands `menus`, `invoke`, `uiaInvoke`,
  `window` and `power` are added, and `menuState` refocuses the app (sidecar `focus`, live only).
  - **Deviation (safer):** in --test, `invoke`, `window` and `power` answer a dry run from Core and never call the
    sidecar. Electron passed `dryRun` to the sidecar for `window` and sent `invoke` for real. `menus` drops `init`
    (no WM_INITMENUPOPUP into a real app), and a fixture answers its own menu.
  - **Power:** Lock, Sleep, Restart and Shut down only. Shut down is always `/s /t 0`, because no fast-startup read
    is wired. Hibernate and Sign out are not here, so TOP-315 stays todo.
- **Surface:** `TopBarSurface.Menus.cs` (the class is now partial).
  - **Drawing:** the dropdowns are drawn on a Canvas over the bar. Under it sits a clear click-away layer, below the
    bar only. Each dropdown is a Border > ScrollViewer > rows. Rows are XamlReader buttons; AutomationIds are
    `topbar.menu.<level>.<row>`, the ScrollViewer is `topbar.menu.<level>`, info rows are `topbar.menu.<level>.info`
    and the name box is `topbar.menu.namebox`.
  - **Window size:** the window grows through menuState → `topbar.place` and shrinks on close. A window region left
    by an earlier crop is cleared once the window is as tall as asked.
  - **Hover:** a hover re-paints the selection in place (no rebuild). A row with a submenu opens it after 110 ms.
  - **Keys:** `PreviewKeyDown` on the root.
  - **Closing:** blur (`Window.Activated` Deactivated) closes the menu, and so does another app coming to the front.
  - **While open:** a focus push on the same app does not rebuild the bar, so the keyboard focus stays; the bar is
    redrawn on close.
  - **Cost while closed:** no element, no timer.
- **Not done:** TOP-080 (motion), TOP-028 (focus ring), the dark menu glass, `layouts.state` (no native layouts
  feature: the submenus say "Window layouts are not running"), TOP-090/091 (stat dropdown keys, ping test / app power
  routing), and the press-drag-release pick (a row button clicks on release only when pressed on itself).
- **Tests:** Core: `TopBarMenuTests.cs` (12) and `TopBarMenuCommandsTests.cs` (3). UI: `TopBar/TopBarMenuTests.cs`
  checks the Mantle menu's rows, the window growing and shrinking, About's note, Restart's second click, File >
  Recent Files > notes.txt as a dry run, and then, after a real click, Down x3 + Enter, Right and Escape.
- **Runs:**
  - 43041284554 (2372917b): build failed (`DispatcherQueueTimer` was ambiguous with Windows.System).
  - 43042131661 (ca584d3d, build,unit): 771/772 (the Chrome fixture is zoomed, so the test read "Restore size").
  - 43042815126 (ac023952): `NATIVE-CI: ok (16 ok, 0 skipped)`, unit 772/772, ui 60/60 (the menus test passed),
    bench ok, topbar 4 of 4 scored (light 0.6139, dark 0.6989).
- **Ledger:** `mark-topbar-rows.py native ui ac023952` marked 67 rows (54 tested, 13 ported). TOP rows in shell.md
  after it: 114 tested, 15 ported, 1 better (TOP-218), 252 todo.

## Part 5 (lane native-r7-topbar-5, same branch): the stat dropdowns

- **Core:** `TopBarStatMenu.cs` is renderStatMenu's rows (`StatRow`: Head, Row, Cores, Info, Sep, TaskMgr) per meter,
  askDetail / renewDetail's timing (`AsksDetail`, `RenewDue`: 5 s, 450 ms after a sample, never for ping), the hang
  from the cell's right edge (`Left`), openTaskMgr's notes (`TaskMgrNote`), statsDetail's step-aside refusal and the
  paused push (`Paused`, "stats paused"). `TopBarMenu.ForStat` opens a meter's dropdown with Ti 0, so Left / Right walk
  the titles from the first (TOP-090); Escape closes with refocus; other keys do nothing (as Electron with no app power).
- **Feature:** `statsDetail` (kind checked; refused while stepped aside and not peeking; a fixture run answers itself;
  live it asks the sidecar `topbar.stats {on, detail}`). The stats fixture now carries Electron's mock details (cores,
  top processes, commit, engines, adapters, the ping server and pick), and `stats=paused` is mock-mantle.js's paused push.
- **Surface:** `TopBarSurface.Stats.cs`. The dropdown is drawn once per shape; a sample of the same shape sets only the
  words and core bars that changed (no re-measure, no re-place, no resize). The renewal timer is made on first use and
  stopped on close, so a closed dropdown has no element and no timer. The open cell gets the pressed wash. "Open Task
  Manager" closes the dropdown, asks `taskmgr.open {view: 'apps'}` (no native Task Manager yet, so the host answers
  that the command does not exist) and then `topbar.taskmgr` (a dry run in --test); the strip says which.
  - **Deviation:** a --test dry run of Windows' Task Manager says "Task Manager: Mantle's did not open (...); dry run
    (--test): would have run ...", not "Opened Windows' Task Manager".
- **Also:** the sidecar's paused push (`{paused, reason}`) was stored as a sample, so the cells read "--"; it now shows
  "stats paused" with the reason as its tooltip and keeps the last readings (TOP-099). Dark dropdowns take Helm's menu
  metrics (TOP-188's sizes: padding 4, min width 180, rows at least 30 px with 5 / 10 padding, 12 px key text,
  `--lg-divider` separators); light dropdowns take `--hud-face` (three layers of `--lg-fill-strong`, drawn as one fill
  of the composite alpha) instead of `--lg-fill-pop`.
- **Tests:** Core `TopBarStatMenuTests.cs` (7). UI `TopBar/TopBarStatTests.cs`: CPU's rows and its detail ask, the hang
  from the cell, Open Task Manager's dry run and note, PING's rows (no ask, no Task Manager), a second press closing it,
  and Right from a real click on CPU walking to the app's menu.
- **Runs:**
  - 43052113126 (43399828, build,unit,ui,topbar): unit failed 1 of 777 (the one statsDetail feature fact; the log has
    no assertion text, so it is now three facts and the sidecar args are matched by content); ui 61/61 (the stat
    dropdown test passed), topbar 4 of 4 scored.
  - 43054136449 (7b5260b3, full): `NATIVE-CI: ok (16 ok, 0 skipped)`, unit 780/780, ui 61/61 (the idle-CPU test
    included), bench ok, topbar 4 of 4 scored (light 0.6139 / 0.6337, dark 0.7344 / 0.6989 at 100 / 150 %: the strip
    pairs are unchanged, there is no dropdown pair).
- **Ledger:** `mark-topbar-rows.py native ui 7b5260b3` marked 24 rows (19 tested, 4 ported, 1 better: TOP-102). TOP
  rows in shell.md after it: 133 tested, 19 ported, 2 better (TOP-102, TOP-218), 228 todo.

## Left (exact list, in order)

1. **Sections not started** (shell.md order):
   - **App power (TOP-111..135): blocked.** Its rows only appear when main decorates the top processes (`ctl`, and
     `stats.apppower` caps), and every control calls `apppower.set / trim / reset / remember`. That is the `apppower`
     feature (apps.md APW-001..), which is not native yet and is not this lane's file scope. Port it first, then the UI.
   - **Ping test + history (TOP-139..159):** the PING dropdown's rows are done (TOP-138); the test needs
     `topbar.pingTest` (sidecar PingTest.cs is linked), its `pingtest` pushes and the history file
     (`ping-tests.json` in the profile, lib/features/_pingtest-history.js). TOP-091 (the key order: ping name box, app
     power, the in-menu name box) waits for both.
   - Tools (the timer / notes features); Strip note is ported (needs a UI test);
   - Glass, Under colour, Glass + Strip (dark backdrop), Under colour (dark only): all need a picture of what is under
     the bar (the desktop layer's strip, `glass.topbarMenu`, sidecar `underrow` / `underwatch` / `underpaint`). Done so
     far: TOP-180 (dark with no picture is flat `--lg-fill-pop`), part of TOP-188 (its sizes, not the lip, the
     pressed row or the disabled opacity), TOP-187's face (not its `--lg-shadow`). A stat dropdown parity pair is not added: Electron's page needs a taller window and `&stat=cpu`,
     and native a fixture key that opens the dropdown at start plus a log line with its rect;
   - Top-row hints, Recovery + lifecycle, Data + IPC (statsDetail, invoke, uia, window, power left; newWindow, about,
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
