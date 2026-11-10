# Lane notes: native-bar-tasks (R3-1)

## Part 1 (this branch, native/r3-tasks)

Data side, `Mantle.Core/Features/`:

- `Pinned/PinnedReader.cs`, `LnkFile.cs`: lib/pinned.js ported (folder list, Taskband Favorites order through the
  sidecar `pins` or the byte search, sidecar `lnk` else the built-in MS-SHLLINK reader, `app` once per Store pin,
  every reason and note in Electron's words). Read only.
- `Pinned/PinOverlay.cs`: the read side of lib/features/_jumplist-pins.js (`features\jumplist.json` "pins": hide, add,
  order; norm, byOrder, sameApp) and the session's running order (`RunningOrder`). Writing it (pin, unpin, undo, a
  dragged order) is part 2.
- `WindowList/RunningWindows.cs`: withoutMantle (plus native's own MantleNative.exe shell and pop-up windows),
  groupRunning (AUMID first, exe second), nameForExe.
- `WindowList/TaskStrip.cs`: buildPinned (pins claim their group, running-only apps after), `TaskOpen` (bar.open,
  bar.window, the test dry run).
- `WindowList/TaskStripService.cs`: the host side on the platform: the `windows` push (one `windows` call if none
  arrived), pushes at most once a second with reasons merged, the 5 min clean pins cache, the pins folder watch
  (500 ms debounce, 30 s re-arm), `appchanged`, the "incomplete / complete again" log lines.
- `Icons/IconCache.cs`: lib/icon-fresh.js and main.js#iconFor (sidecar `icon` / `app`, an unreachable sidecar keeps
  the old picture).

Strip, `Mantle.App/Surfaces/Bar/Tasks/`:

- `BarTasksHost.cs`: UI <-> host bridge; `BarTaskActions` (live: sidecar focus/minimize/close/launch with the
  foreground grant; test and scratch: focus and minimise in-process on the UI thread, launch = dry run).
- `BarWindow.Tasks.cs`: `CellVm` and the strip (icon from the sidecar's PNG data URL or the letter tile, the 6x3 /
  16x3 accent running line, the 1x22 hairline before the first running-only app, tooltip, middle-click = fresh,
  chips "Some apps still loading" / "Apps not shown" / "Open windows not updating" / "App did not open"), cells.json.
- Wiring: App.xaml.cs HostStart calls `_window?.Tasks?.AttachOnHost(_platform, loop)` right after the platform is made
  (one commented block; the bar is made in OnLaunched before StartHost). The stand-in `TasksWiring.cs` is gone.

Test runs never read the user's pins: `MANTLE_TEST_PINS_DIR` (a scratch TaskBar folder) and
`MANTLE_TEST_TASKBAND_KEY` (a scratch key under HKCU holding a `Favorites` REG_BINARY). Without them a test or
scratch run shows no pins and says why in the strip's reason. Electron's `--test` reads the real pins; this is a
deliberate deviation for safety.

## Part 1, getting it green (native-r3-tasks-2)

- Build: `BarTasksHost` is partial (IDisposable is WinRT's IClosable: CsWinRT1028); the sidecar `lnk` paths go in as
  `JsonNode` (JsonArray.Add<T> is not trim-safe: IL2026/IL3050).
- `TaskStripService.BuildOnceAsync` no longer keeps a build that finished synchronously as "the one running" (it
  answered every later build with the first list, so the strip never changed again; the unit push test found it).
- Active marks: every `windows` push re-marks the active window at once (bar.js renderWindows: `TaskStripBuilder
  .MarkActive`, option `Marks`), between the once-a-second rebuilds; a click decides focus or minimise from the same
  marks. Native also takes each window's minimised flag from the push (Electron keeps the old one until the rebuild).
- Running-only order (BAR-289), as Electron: the sidecar's EnumWindows order, i.e. the z-order, so an app moves to the
  front of the running-only apps when focused and to the back when minimised; a dragged order (`RunningOrder`) wins.
  Part 1 kept first-seen order instead (a deviation); native-r3-tasks-3 removed it (`FirstSeenOrder` is gone). A
  click on the same spot twice can land on another app, as in Electron; the bench re-reads `cells.json` (settled: 1.5 s
  after its last click, two reads 400 ms apart agree) before each click instead of keeping the first positions.
- Cells: UI Automation `ItemStatus` = kind ("pinned" / "running") + "; open" + "; active" + "; after divider", the
  same fields that draw the running line and the hairline (AUTOMATION.md row requested). Middle-click is heard with
  handledEventsToo (`Cell_Loaded`). cells.json (bench) is written after layout and after an icon lands.
- Tests: `Mantle.UiTests/BarTasks/StripTests.cs` (a scratch TaskBar folder of real .lnk files made by WScript.Shell,
  powershell/pwsh fixture windows closed by PID; pins in order, open/active marks and the line in real pixels, the
  divider, a closed app leaving; click = dry run naming the .lnk, focus, minimise, restore; middle-click = "another
  copy of"). Unit: grouping as main.js keys it, first-seen order, marks between rebuilds.
- Strip parity: `tools/parity/bar-tasks/run-strip-parity.ps1` (see its header; run through the recipe's command
  override with `-Build`). Scratch %APPDATA% pins (Alpha Shell = powershell, the parity fixture's app; Beta Map;
  Gamma Reg), the runner's Taskband Favorites value off for the run, run-parity.ps1 with `-MaskApps $false`, then
  the 440 DIP apps slot cropped out of both captures: `native/out/parity/bar-tasks/<theme>/strip-pair.png`.

## Round 3, part 3 (native-r3-tasks-3)

- Running-only order follows the z-order (above). `BarTests.Running_app_button_focuses_minimizes_and_restores_its_window`
  failed 4 of 4 full UI rounds on it (its third click hit the spot A had before it was minimised and moved): it now
  finds the button again, once settled, before each click.
- Wiring swapped (above): no `MantleApp.AttachBarTasks`, no `TasksWiring.cs`.
- Window list (`Tasks/BarWindow.WindowList.cs`; bar.js#flyList): a click on an app with two or more windows (bar.open
  action `list`) opens the pop-up `winlist:<id>` ("Mantle pop-up: <app> windows") over the icon through the bar's
  PopupHost: heading, a note, one row per window (active: accent ink and the 3x14 bar; minimised: quiet ink;
  tooltip "(minimized)"), each with its X. A row: list closes, bar.window toggle/focus. The X: list closes,
  bar.window close (a test run refuses it, "Window did not switch" chip as bar.js ACT_LABEL.window). The same icon
  again closes it. DEVIATION until the previews exist: Electron opens the preview cards here and keeps the list for
  too many windows; the native note says "live previews are not in the native bar yet, so this is the list".
  ItemStatus gains "; N windows" (two or more). `BarTasksHost.Window` is the bar.window bridge.
- The click flake (main run 42384431813, `Clicks_launch_...` "no dry-run line"): see "The click flake" below.
- Test-only diagnostics (run with `--test` only): `bar tasks: test: press|click on bar.app.<n> (<title>)`,
  `bar tasks: test: strip now [...]` on every order change, and a UI-thread ping every 250 ms that logs
  `the UI thread ran queued work <n> ms late` (>= 500 ms) or `has not run queued work for <n> ms` (>= 2 s). A failed
  strip test keeps the app's whole log in `native/out/ui/strip-<profile>-<time>-native-log.txt` and its "no dry-run"
  message says where the click landed. `tools/parity/bar-tasks/repeat-ui.ps1` repeats a test filter N times and keeps
  the strip tests' app logs per round.

### The click flake

- What the failing log shows: the click came about 2 s after the app started. The log after it held one line:
  `data: look-sync\look.json: ok: rev 1 ...` (the 2 s data poll). Each time that poll fires, the UI thread logs
  `data changed: look-sync\look.json: ok` (App.xaml.cs, a queued UI job) within 0-2 ms (every passing log, and the
  bench log of that same run). In the failing run that line never came in the 6 s the test waited, and neither did a
  click line. So the bar's UI thread ran no queued work for at least 6 s, starting right after the test's last UI
  Automation read (which the UI thread answered). The click was not lost by a moved cell, and it was not the first-moments
  gap (no `bar: input:` line; strip cells ARE plain Buttons, so EarlyClicks would have passed such a click on).
- Not the cause: a strip rebuild under the pointer (the pinned cells do not move; the passing logs show the press and
  the click on the same cell 1 ms apart), the look.json publish (its UI handler only re-renders when the blur
  changes, and its line never ran), z-order moves (pins never move).
- Not found: what held the UI thread. The stall did not happen again in 16 runs of the click test with the test-only
  UI-thread ping on (cloud runs 42386880533: StripTests x6; 42387408410: whole UI suite x4; 42388736092: the CI ui
  stage plus the whole UI suite x5): 16 of 16 passed, 0 late-UI lines. No app fix was made for it, because none is
  proven. The next time it happens, the failure message and the kept app log say where the click landed and how long
  the UI thread was held (`the UI thread has not run queued work for <n> ms`).
- Side finding: in a repeat run made after the parity stage, `Theme_set_switches_the_bar_within_1_s...` fails every
  time ("never went dark ... luminance 25.2 -> 25.2"): the bar is already dark then, so parity leaves the desk in a
  state that test does not reset. It passes inside ci.ps1 (ui runs before parity). Not this lane's test.

## Round 4 (native-r4-tasks)

- BAR-289 is `better:` (the user's decision): running-only apps keep their first-seen place for the session
  (`WindowList/TaskStrip.cs#FirstSeenOrder`, back in `TaskStripService`), so an icon never moves when its window is
  focused or minimised; a dragged order (`RunningOrder`) still wins. Electron follows the z-order. The bench's
  cells.json re-read stays (harmless). `BarTests.Running_app_button_focuses_minimizes_and_restores_its_window` now
  also checks that A's button does not move on focus or minimise.
- Live previews (`Tasks/BarWindow.Previews.cs`, `Tasks/Thumbs.cs`, `Mantle.Core/Features/WindowList/Previews.cs`):
  bar.js THE WINDOW FLYOUT and lib/thumbs.js. Three ways in: resting 400 ms on a running app's icon (closes 350 ms
  after the pointer leaves icon and flyout; moving across running icons swaps at once; a pin with no windows starts
  the close), a click when bar.open answers `list`, and `MANTLE_TEST_FLYOUT=<name or exe>` in a `--test` run. One
  card per window (icon, title with "Minimized · " / "Still open · ", the X, the 16x3 accent line on the active one),
  each box with a DWM thumbnail registered onto the PREVIEW POP-UP'S OWN HWND (a top-level window of this process;
  found from the body's XamlRoot island, else by its title). Pictures land after the pop-up's 320 ms entrance (the
  pop-up rises 8 px and DWM pictures cannot ride it; Electron's flyout only fades), letterboxed, whole window incl.
  frame, opacity 255. Every box says why it has no picture (cloaked, closed, 0x0, HRESULT words). A refused close
  holds the card in words ("Not closed: ...", chip "Window did not close"); a close that worked but left the window
  says "Still open · " after 4 s. A push with the same windows updates the cards in place; different windows rebuild
  it in place (600 ms after a window closed, so its "this window has closed" card is seen); none left closes it.
  Too many windows for 128 DIP cards: the window list (`BarWindow.WindowList.cs`) with
  "N windows are too many to preview side by side in this bar, so this is the list".
- Pictures come off on every road out: the flyout polls the pop-up host every 40 ms while open (the host has no
  close event) and unregisters the moment it is not the open pop-up; the pop-up window dying drops any left.
- UI tests: `BarTasks/StripTests.Previews.cs` (fixture forms in ONE powershell.exe with solid colours, so the live
  picture is checked in the real screen pixels; real pointer moves through SendInput, because a SetCursorPos jump
  gives WinUI no pointer enter/exit). The old window-list test is replaced by the preview test and the too-many test.
- Preview parity: `tools/parity/bar-tasks/run-preview-parity.ps1` (see its header).

### Requests for other lanes (not done here: not this lane's files)

- Popups (Shell/Popup): a `Closing` event on PopupHost (the flyout polls `OpenKey` every 40 ms instead), and a
  fade-only entrance for the preview kind (Electron `.pop.fly`: no rise), so the pictures could land during the fade.
- ci.ps1 (native-r4-core): run-preview-parity.ps1 is not wired into ci.ps1.

## Part 2 starts here

Previews are done except BAR-233 (re-place on a bar width change or the island's move) and BAR-313 (the Alt+Tab
switcher's own set: `Thumbs` is per window, so it only needs the switcher); drag to reorder
(BAR-239..254) with the overlay writes, the jump list / app menu (BAR-207..220, 315..332), overflow "+N"
(BAR-235..238), badges (Helm count BAR-255..257, keep on top BAR-258, 333..347), Mantle apps standing in for
Windows' (BAR-291..296), the top bar hide rows (348..351), the parity strip screenshots
(`native/tools/parity/bar-tasks/`), and PEND-011.
