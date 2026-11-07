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

## Part 2 starts here

Window previews (DWM thumbnails, BAR-221..234, 308..314), the window list for action `list`, drag to reorder
(BAR-239..254) with the overlay writes, the jump list / app menu (BAR-207..220, 315..332), overflow "+N"
(BAR-235..238), badges (Helm count BAR-255..257, keep on top BAR-258, 333..347), Mantle apps standing in for
Windows' (BAR-291..296), the top bar hide rows (348..351), the parity strip screenshots
(`native/tools/parity/bar-tasks/`), and PEND-011.
