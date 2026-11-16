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

## Part 6 (lane native-r7-topbar-6, same branch): the ping test, its history, and part 4's leftovers

- **Core:** `PingTestPanel.cs` is ui/topbar/pingtest.js as a pure machine: the last run and the history are pushed in
  (`OnRun`, `OnHistory`), a row press is `Act("pt-...")`, the name box's keys are `Key`, and `Rows()` returns `PtRow`s
  (Sep, Tabs, Head, Info with classes, Progress, Table with `PtCell`s, Row2, Go, NameBox, Chips, HistRow). Calls go
  through an injected `CallFn`, so the unit tests drive it with no app. `PingTestHistory.cs` is _pingtest-history.js:
  `<data root>\ping-tests.json` through `SafeFile` (the one write gate), last 50 unsaved + every saved one, a damaged file
  moved aside, a refused or failed write kept in memory with the reason. `PingTestFake.cs` plays a made-up run
  (starting, ping, down, up, done; 350 ms a step) for --test fixture runs: odd runs on "Test Wi-Fi" with a quick router,
  even runs on "Test Wi-Fi 5G" with a slow one, so two runs give two networks, amber and both verdicts. Documentation
  addresses only (192.0.2.1, 02:00:5e:...): the oss-guard refuses LAN IPs and real-looking MACs.
- **Feature:** `TopBarFeature.PingTest.cs`: `pingTest` (start / cancel / state; checked, then the sidecar's
  `topbar.pingtest`, or the fake in a fixture run), `pingTests` (list / save / rename / delete; the id must match
  `^[0-9]{1,16}-[0-9]{1,6}$`, the name `checkName`), and the sidecar's `pingtest` pushes forwarded as
  `topbar.pingtest` (without `ev`), a finished run added to the history and pushed as `topbar.pingtests`.
  `TopBarFeature.Configure(new(DataRoot, Files))` in App.xaml.cs gives it the run's data root; unconfigured (unit
  tests), every write is refused and the history says so.
  - **Nothing at rest:** the fake has no timer until a start and stops with its run; a live run asks the sidecar only on
    the user's press (and once at load for the last state and the history list, as Electron's page does).
  - **Deviation (safer):** a second damaged history file never replaces an older `ping-tests.bad.json` (Electron's
    rename would); it is kept as `ping-tests.bad-<ms>.json` and the problem line names it.
- **Surface:** `TopBarSurface.PingTest.cs` draws the rows at the end of the PING dropdown. The rows' signature joins
  the stat dropdown's shape, so a 1 s sample with the same ping rows still updates in place, and a ping push redraws.
  The name box keeps its text, caret and focus across redraws (`AfterPingRender`); `PingKey` takes keys typed in it
  before the menu keys (TOP-091). AutomationIds: `topbar.pt.tab.*`, `topbar.pt.<act verb>`, `topbar.pt.name`,
  `topbar.pt.c<row>.<col>`, `topbar.pt.h.<n>`, `topbar.pt.chip.<n>`, `topbar.pt.speed.*`; amber cells and lines carry
  ItemStatus "amber".
- **Part 4 leftovers:** `TopBarSurface.Motion.cs`.
  - TOP-080: a dropdown pops in once per menu session (`MenuLevel.Popped`; part 4 already had `Shown`, set when drawn), from `Reveal()` when it is first seen:
    light up 3 px on the bar's spring over 240 ms and a 96 ms fade, reduced motion a 120 ms fade, dark none.
    Composition animations only.
  - Press-drag-release: a press on a title or stat cell releases the button's pointer capture, so the rows hear the
    drag (hover, slide) and a release on a row picks it (`DragPick`; any release ends the drag).
  - TOP-028: the strip buttons' template now binds BorderBrush / BorderThickness; keyboard focus shows a 2 px accent
    ring (dark: 1.5 px accent, --lg-focus's outer ring; its inner dark band is not drawn).
- **Tests:** Core `PingTestPanelTests.cs` (7), `PingTestHistoryTests.cs` (7), `TopBarPingTestCommandsTests.cs` (2).
  UI `TopBar/TopBarPingTestTests.cs` (run, amber cells, closing does not cancel, history in the test profile, save by
  UI Automation, rename by the keyboard with Enter, compare, delete on a second press) and
  `TopBar/TopBarMenuPointerTests.cs` (a real pointer: press on File, slide to Edit and back, hover Recent Files opens
  its submenu, no second pop-in on a re-render, release on Open… picks it, click-away, Tab focus ring).

- **Runs:** 43063412200 (4e75cea9, build,unit,ui,topbar): unit 9 of 812 failed (an in-memory JsonValue made from an
  int throws on GetValue<double>; fixed with `PingTestPanel.Number`), ui: the ping test failed. 43065420632 and
  43066176188 (full): build failed (CS0102 `MenuLevel.Shown` already existed, then CS0246 a missing using); unit
  812/812 in the second. 43067056971 (0b1d4735, full): `NATIVE-CI: FAILED (ui failed; 15 ok, 0 skipped)`, ui 3 of 64 failed:
  the two new UI tests and part 5's `Stat_dropdowns_hang_from_their_cell...`. The log has no assertion text and no trx
  was fetched, so the causes are NOT known. Suspect first: `ArmDrag` (TopBarSurface.Motion.cs) releases the pointer
  capture on a press of a stat cell or title, which can change what a real click does (the stat test's real click on
  CPU, then ArrowRight). Try removing `ArmDrag(b)` from the stat cells first and fetch ui.trx for the messages.
- **Ledger:** NOT marked this part (no green run). `mark-topbar-rows.py` has the part 6 rows and an `upgrade` pass
  (ported -> tested for TOP-030 / TOP-071); run it only after a green full run.
- **Coordinator note (open):** part 5's stat UI test once missed "key: ArrowRight" (run 43064259180, with game mode's
  extra quick settings row); passed in 43064426559. Not looked at yet; harden with a log-line wait if a cause shows.

## Part 7 (lane native-r7-topbar-7, same branch): part 6's three UI failures (HANDOFF, not green yet)

- **Merged** origin/native/main 86ae174f (d3af033d, clean). native/main is now 259926d6 (game mode, downloads, the Helm
  badge): NOT merged yet; merge it before the next full run.
- **Commit message fix:** 0b1d4735's message has a placeholder run id; the run was 43066176188 (history not rewritten).
- **Real causes** (from run 43067056971's ui.trx, fetched with `gh run download`; cloudtest had not brought it back):
  1. Stat test, "ArrowRight never arrived" after a real click on CPU. Two things. (a) `ArmDrag` released the pressed
     button's pointer capture, which cancels the button's own press, so it never took the focus a click gives it; it now
     focuses the opener itself (Pointer state, no ring) and --test logs `drag armed: <id> (focus kept|taken|refused)`.
     Run 43072038005 passed the stat test with this. (b) The coordinator saw the same miss on main with no ArmDrag
     (3 runs). Likely race: a click (or the test's `ZOrder.Foreground`) ACTIVATES the inactive bar, and the island then
     puts its own focus back, which can be nothing (the element it had, the PING cell focused by the UIA Invoke, was
     redrawn away on close), so PreviewKeyDown on `_root` never fires. Fix (committed, NOT yet run): `KeysHomeSoon()`
     in Motion.cs runs on `_win.Activated` (not Deactivated) at Low priority, after the island's own restore: with a
     menu open and no focus inside `_root`, it focuses the ping name box, else the in-menu name box, else the open title
     or stat cell; --test logs `window activated; keys go to <id> (...)`. The stat, ping and pointer tests now wait for
     that line (a new one) before sending keys whenever the bar was not already foreground.
  2. Ping test, "Enter did not rename the test": the real click at the name box's right end hit the WinUI TextBox's
     clear (x) button and emptied it, so the name became "2". Electron's inputs have none: both top bar name boxes
     hide it (`NoClearButton`, the launcher's way), and the test checks the typed text before Enter. Passed in
     43072038005. Then "no topbar.pt.compare on the top bar": a plain Grid makes no automation peer, so the ids on the
     result / compare tables and the progress bar never reached UI Automation; they are `UiaGrid`s now (shared type in
     Uia.cs, used, not edited). Also: a name box that had the focus and is redrawn away (Enter saved, Escape left it)
     dropped the XAML focus, so later menu keys (the test's final Escape) reached nothing; `AfterPingRender(boxFocused)`
     gives the focus to the PING cell. The test keeps the app log as `uitest-topbar-pingtest-native-log.txt`.
  3. Pointer test, "the dropdown did not pop in": the hosted runner has Windows' animation effects OFF, so the app
     (correctly) did the reduced-motion fade. The old check assumed animations on; the test now reads
     SPI_GETCLIENTAREAANIMATION (what UISettings.AnimationsEnabled reports) and expects the matching line. Passed in
     43072038005.
- **Runs:** 43072038005 (643a9ac7, build,unit,ui,topbar): `NATIVE-CI: FAILED (ui failed; 3 ok, 0 skipped)`, ui 1 of
  71 (ping test: no topbar.pt.compare). 43075038269 (6c9632d7, full): `NATIVE-CI: FAILED (ui failed; 16 ok, 0
  skipped)`, ui 2 of 71: the ping test (message not read: the artifact download had not finished at the handoff) and
  `QuickSettingsViewsTests.The_three_views_open_draw_their_rows_and_every_click_is_a_dry_run` (not this lane's; it came
  with main). Every other stage ok, topbar parity 4 of 4. The last commit (KeysHomeSoon + the tests' activation waits)
  has NO run.
- **Ledger:** NOT marked (no green full run on a HEAD).
- **Next, in order:** merge origin/native/main (259926d6); read 43075038269's ui.trx for the ping test's message (if not
  already in `.cloudtest/20261111-180914-a7809b/artifacts`); run the fast loop, then `cloudtest --recipe
  mantle-native-public`; on green run `python -I native/tools/parity/topbar/mark-topbar-rows.py native ui <sha>` and
  commit the ledger and these notes. Then, if there is time: a submenu flipping left at the screen edge (TOP-072),
  losing focus closing the menu (TOP-078), an app menu that fills in late (TOP-067).

## Part 8 (lane native-r8-topbar, same branch): the stat test's race, part 6 green, the ledger

- **Merged** origin/native/main 8d4f7de4 (1bf1674d): App.xaml.cs conflicted on the wiring lines; both kept (the top
  bar's ping history, game mode).
- **The stat dropdown race** (`Stat_dropdowns_hang_from_their_cell...`, "ArrowRight never arrived" about half the runs
  on main): part 7's fix (a2a081e9: `KeysHomeSoon` on `_win.Activated`, the tests wait for "window activated; keys go
  to") had never compiled: CS0019, `(Control?)_ptBox ?? _editBox ?? <Button>` (the cast covered only the first
  operand; run 43817960648). f1ba7b0d casts each operand. Then 2 of 2 runs (build,ui) at f1ba7b0d passed the stat test,
  the pointer test and QuickSettingsViews: 43818345024 and 43819839744 (ui 78 of 79 each; the 1 was the ping test).
- **The ping test** ("the second run (a slow router) does not say the lag starts at home", runs 43075038269,
  43818345024, 43819839744). The first run's screenshot showed its verdict drawn, so the drawing was right; the test
  looked for the verdict by walking every element on the bar and reading each name live. A 1 s sample redraws rows
  while the walk reads them; a read of a row that went throws, and `Host.Until` swallows it, so the walk never
  finished (the likely cause; not proven by a trace). eed9756d: the verdict line has its own id `topbar.pt.verdict`
  (Electron's `.pt-verdict`), the test reads it by id and checks the whole start of the slow-router verdict ("... your
  own router takes 28 ms on average"), says what it read if it fails, and keeps a screenshot `topbar-pingtest-second`.
  The history row walk is retried the same way.
- **QuickSettingsViews** (part 7's full run): passed in all 4 runs of this part, with no change. Part 7's one failure
  was "the Wi-Fi view did not open" after a real click on the 22 px "›" right after the pop-up showed; nothing in this
  branch touches quick settings. Not this lane's to chase.
- **Not this lane's:** run 43821332422 (eed9756d, build,unit,ui,topbar: ui 79 of 79, topbar 4 of 4) failed unit 1 of
  929: `HelmBadgeFeatureTests.A_count_hides_when_its_writer_exits_heard_not_polled` ("a live writer's count did not
  show", from main). Its writer is `cmd /c ping -n 3` (about 2 s of life), so on a slow runner it can exit before the
  badge file is read. It passed in the full run.
- **Runs:** full 43823044229 at eed9756d: `NATIVE-CI: ok (17 ok, 0 skipped: )`, unit 929 of 929, ui 79 of 79.
- **Ledger:** `mark-topbar-rows.py native ui eed9756d` marked 26 rows (24 todo rows, and TOP-030 / TOP-071 ported ->
  tested): TOP-028, 080, 139..151, 153..159 tested; TOP-091, 152 ported. TOP rows now: 157 tested, 19 ported,
  2 better, 204 todo (of 382).
- **Top-row hints receiver (bcc4980e, TOP-362..371):** `TopRowHints.cs` is lib/toprow-hints.js's receiver (look-sync
  CONTRACT.md "Top-row hints (v1)"): the pipe, protocol v1, 8 KB lines (CRLF fine, an over-long line dropped whole and
  counted), seq per window per connection, the sender's window checked once per (app, window) through the sidecar's
  `winproc` (Quartz.exe / Helm.exe, or electron.exe under ~\browser, ~\helm or their `-*` worktrees), failed checks
  retried after 30 s, ok ones re-verified in the background, light theme keeps nothing, none / a disconnect forget,
  32 clients, 64 windows a client, each ignore reason logged at most once a minute with its count, a busy pipe said
  in words and retried every 10 s. `TopBarFeature.Hints.cs` starts it in a --live run (the live pipe) or a --test run
  (`look-sync-toprow-test-<pid>`), forwards kept hints to the sidecar's `underhint` (a refusal said once per reason)
  and resends them on a new sidecar. App.xaml.cs: `ConfigureHints(new(() => _look?.Now == "dark"))` (shared, 2 lines).
  - **Deviations (safer):** the pipe is this user's only (`PipeOptions.CurrentUserOnly`; Node's default pipe ACL lets
    only the creator, SYSTEM and admins write); a --test run refuses `LOOK_SYNC_TOPROW_PIPE` when it names the live pipe.
  - **Nothing at rest:** an overlapped connect, overlapped reads, a timer only while the pipe is busy.
  - **Not here (the glass lane's colour layer):** TOP-372..375 (see Left). Until a layer runs, the sidecar refuses
    `underhint` and the log says so once.
- **The menus test (part 4):** run 43825704178 (0e272ae0) failed it once, "the bar got no key": the real click did not
  leave the bar in the foreground, the test called `ZOrder.Foreground` and sent the keys at once. 4c360c1c gives it the
  stat test's wait for "window activated; keys go to". Passed in the 4 runs before; cause of the lost foreground not
  found. In 43827392539 the test took that path (the click left the bar in the background) and passed.
- **Full runs at 4c360c1c (hints + menus wait + main 45ba0e54):** 3 runs, each `NATIVE-CI: FAILED (ui failed; 16 ok,
  0 skipped)`, unit 952 of 952 each, every top bar UI test passed each time; the ui failures were other lanes' tests
  and differ per run: 43827392539 DesktopSurfaceTests.Desktop_menu_opens... (UIA ElementFromHandle timeout);
  43830388635 the idle CPU test (dark hidden at rest 1.666 % > 1.0) and Apps_that_do_not_fit_wait_behind_plus_N...
  (the overflow panel never took the keyboard); 43833241748 Popups_paint_the_desktop_still_sharp... ("dark").
- **Idle cost of the hints listener:** the coordinator saw "hidden again" non-zero on this branch and 0 on main. By
  the code the listener has nothing at rest (one overlapped connect wait, no timer unless the pipe is busy, reads only
  for a client, no loop that re-makes the pipe by itself). 78a9de39 proves it rather than says it: `TopRowHints.Wakes`
  counts every wake (connect, read, busy retry, check answer; a stop is not one), a --test run logs each as "hints:
  listener woke: <what>", the unit test checks 0 wakes in 1.5 s of listening, and the idle CPU test (HoverTests.cs,
  shared, 3 lines) checks the listener started once and never woke in its window. The 1 % limit is unchanged.
  Full run 43836510792 at 78a9de39: the idle test PASSED with those checks (light hidden 0.833 %, again 0.625 %; dark
  hidden 0 %, again 0 %), unit 963 of 963, ui 86 of 87: `NATIVE-CI: FAILED (ui failed; 16 ok, 0 skipped)`, the one
  failure again Apps_that_do_not_fit_wait_behind_plus_N... ("the panel never took the keyboard"; the strip lane's).
- **Ledger for the hints rows NOT marked:** no full green run on a hints HEAD. When one is green, run
  `python -I native/tools/parity/topbar/mark-topbar-rows.py native ui <sha>` (it has TOP-362..371).

## Part 9 (lane native-r8-topbar9, same branch): corners, known games, the window, the session, health

- **Merged** origin/native/stage4 (414f4f10: native/main b40021a2 + e0649c73), a fast-forward.
- **The hints rows (TOP-362..371) marked tested:** stage4 went green as native/main 414f4f10, run 43852816953
  (`NATIVE-CI: ok (17 ok, 0 skipped)`, ui 89 of 89). They were marked with part 8's script
  (`git show e0649c73:.../mark-topbar-rows.py`, then `native ui e0649c73`): 10 rows, so none of part 9's went in early.
- **TOP-297 hot corners:** the peek reads `CornerClaims.Live.Claims(pt, display)` (read only; `TopBarFeature.Corners`
  is the seam the tests swap). An ACTIVE corner's 6x6 DIP square never peeks; an Off corner takes nothing; a peek out
  stays when the cursor slides into the corner; with the corners stopped the whole edge peeks.
- **Known games RUNNING (TOP-267..272, `TopBarFeature.Games.cs`):** a `GameWatchFollower` on the host's `GameWatch`.
  Game mode drives the watch, so the bar has no timer of its own at rest; joining makes game mode's idle tick go from
  3 s to its 12 s backstop (the bar's focus events feed it). Only with nothing driving does the follower read
  `gamemode.scan {procs:true}` (3 s) every 1 s, as Electron. A game starting: Decide("game started") (hides, closes
  menus, stats off), the peek watch stops, the guard goes off, the strip is let go. While it runs nothing re-reserves
  (display changes, the 1.5 / 6 s re-checks, Explorer, re-register) and the guard stays off. A failing check keeps the
  bar aside and goes into health. The start reads the games before the reserve. `MANTLE_TOPBAR_TEST_GAME` (--test only)
  adds exe names, as Electron's. The log never names the game ("a known game", "N known games").
- **The strip races (TOP-251/252):** never reserved while a game runs; a game starting while a reserve is in flight
  lets it go again at once.
- **The window fight (TOP-227..232):** the rules are `TopBarDrift` (Core), the runner `TopBarSurface.Drift.cs`. The
  subclass hears WM_WINDOWPOSCHANGED, the check runs 250 ms later, once. Nothing runs while the window stays put.
  The surface reports the fight to the feature (`topbar.drift`); health drops it while the bar is hidden. Units are
  physical px (Electron: DIP).
- **Explorer restarted (TOP-250):** the bar's own subclass hears TaskbarCreated and calls `topbar.shell`, which runs
  ReserveStrip("Explorer restarted"); the work-area check after it registers afresh when Explorer ignored it (TOP-249).
  No timer. The UI test posts TaskbarCreated to the bar's own window only.
- **Session end (TOP-258):** App.xaml.cs (shared, 1 line) posts `TopBarFeature.SessionEnding` before the session
  end's quit on WM_ENDSESSION (ending). The strip goes now, `_ending` stops every later reserve, the game loop stops.
- **TOP-013/016 UI Automation menus (`TopBarFeature.Uia.cs`):** the feature (not the surface) reads `topbar.uia` once
  per handle for a focus snapshot with no menu and uia "try", keeps 64 handles, a failed read as an empty menu with its
  reason, and pushes the focus again with the menu.
- **Health order (TOP-380):** the window fight > the focus watch > the strip gap > the game watch > the peek watch
  (Electron's "page" first has no native equivalent).
- **TOP-381 / 382:** a new sidecar reruns Apply (unit test); stop ends the loops then watch / guard / stats off and the
  strip released, in that order (unit test). Open: the app's Quit destroys the surfaces BEFORE "put Windows back"
  (App.xaml.cs, not this lane's), so on quit the window goes before the feature lets the strip go; the restore's
  app-bar give-back runs either way.
- **n/a rows:** TOP-376..379 (no renderer page), TOP-006/007 (the window is always the display's width; no zoom guess).
- **Shared lines:** App.xaml.cs 1 line (session end). TopBarSurface.cs, non-backdrop lines only: `_want = ...` in
  Place, two cases in Proc (WM_WINDOWPOSCHANGED, TaskbarCreated), `DriftStop()` in Destroy.
- **Runs:** fast 43854706570 (build: the `Run` name clash), 43855244202 (test build errors; ui 90 of 91, the idle
  test light hidden 1.25 %), 43857502511 (unit 1017 of 1018: a JSON-escaped apostrophe in the UIA test; ui 90 of 91:
  the corners row test "This bar has no road to Mantle's commands", the corners lane's; idle passed, light hidden
  0.104 %, dark 0.937 %). The idle readings at the same code ranged 0.104..1.25 % (15 s window, 0.104 % a tick), so
  the 1.25 % was tick noise. No part 9 timer runs at rest. Full **43859548470 at 9d9a6873: `NATIVE-CI: ok (17 ok,
  0 skipped: )`**, unit 1018 of 1018, ui 91 of 91.
- **Ledger:** `mark-topbar-rows.py native ui 9d9a6873` marked 36 rows. TOP rows: 191 tested, 25 ported, 2 better,
  6 n/a, 158 todo (of 382). Part 8 ended with 157 / 19 / 2 / 0 / 204; the 10 hints rows were part of the 34 more tested.
- **Not done (budget):** TOP-003 menubar role (needs custom automation peers), Tools (TOP-211), TOP-091's UI test.

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
   - Top-row hints: the receiver is done (part 8, TOP-362..371). Left: TOP-372 (paint a hint: the colour layer),
     TOP-373 (capture verdicts -> `Mismatch` / `Confirmed`, the strip note after 10 s), TOP-374's look-change half
     (`ResendAll` in dark when the look changes; the new-sidecar half is wired), TOP-375 (the wash base). All of these
     need the colour layer under the bar, which is the glass lane's; `TopRowHints` already has the calls they need.
   - Data + IPC (statsDetail, invoke, uia, window, power left; newWindow, about, settings, ...).
2. **Parts of started sections not ported:**
   - Page and strip: TOP-003 menubar role (custom automation peers for the bar and its titles).
   - Feature + settings: TOP-211 tools hub.
   - Window + displays: TOP-220 zoom (n/a natively), TOP-221, TOP-223, TOP-226.
   - Step aside: TOP-273..280 full-screen video (media sessions, `Features/Media`), the motion (leave / arrive).
