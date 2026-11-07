# Lane notes: native-launcher (R3-3), part 1

## What is here

- `Mantle.Core/Features/LauncherIndex/`: the index, ported from `lib/launcher-index.js`: Start Menu `.lnk`
  walk (two roots, depth 8, dedupe, noise, stand-ins through a port of `lib/pinned.js`'s `.lnk` reader), Store
  apps (`Get-StartApps` parse, AUMID check, Mantle Player stand-in, 10 min cache, 4 s first-build wait, late
  merge), PowerShell profile functions and aliases, the Settings table, Mantle's own rows, power rows from
  `powercfg /a`. `SettingsTable.g.cs` is generated from Electron (see below).
- `Mantle.Core/Features/Search/`: `search()` and its scorers, `capByGroup`, the URL row, and
  `ui/launcher/groups.js` (groups, web row, Tab jump).
- `Mantle.Core/Features/Run/`: `apps.index`, `apps.search`, `apps.launch` (`LauncherCommands`, generated
  registration) on `LauncherService`, and the launch plans. `--test` is always a dry run.
- `Mantle.App/Surfaces/Launcher/LauncherSurface.cs`: `[Surface("launcher", AtStart = true)]`, its own window
  (not the bar's pop-up framework: the launcher is centred on the primary display, not anchored to the bar).

## The spec is Electron's own output

`native/tools/parity/launcher/make-golden.cjs` runs Electron's real `lib/launcher-index.js` and
`ui/launcher/groups.js` in plain node on a scratch fixture: a two-root Start Menu with 85 shortcuts written byte
for byte, a profile, a fake `powercfg /a`, a fake Store list. HOME, APPDATA and ProgramData point at the scratch
folder, and `child_process.execFile` is stubbed, so nothing is spawned and no user data is read. It writes:

- `tests/Mantle.Core.Tests/Search/LauncherGolden.g.cs`: the fixture, Electron's `build()` rows and sources, and
  `search()` + groups for 1,220 queries (every query in the Electron tests plus prefixes, words and edge cases);
- `src/Mantle.Core/Features/LauncherIndex/SettingsTable.g.cs`: `SETTINGS`, `MANTLE_SETTINGS` and the 144 pages
  the Settings section modules claim.

`node native/tools/parity/launcher/make-golden.cjs --check` exits 1 when Electron has moved on (`GOLDEN DRIFT`).
**Asked of the coordinator:** run it in CI (it needs only node and the repo) so an Electron change to the
ranking or the table fails the native build until the golden is regenerated.

## Asked of other lanes

- **native-r3-core-4 (host):** create a `LauncherService(IndexInputs.ForWindows(), test: Host.Args.Test, log)`
  on the host thread, register `LauncherCommands.Register(bus, new LauncherCommands(service))`, and warm it with
  `service.IndexAsync()` at start (LAU-017). The surface needs `apps.search` / `apps.launch` / `apps.index` on
  the bus and the `theme` event.
- Win tap / Start button / Alt+Space toggle: the host's `toggle "launcher"`. Win+S / Win+R modes are part 2.

## Deviations (proposed)

- Settings claims come from a generated snapshot (`SettingsTable.g.cs`), not by running
  `ui/settings/sections/*.js` at run time (LAU-149, LAU-151): native has no section modules.
- Mantle Settings / Task Manager / Files / Player are not native yet, so their rows open Windows' own page or
  app and say why (Electron does the same when Mantle's is off). Revisit when those windows land.
- URL rows: Electron opens them in Quartz; native part 1 refuses live with plain words (part 2).

## Part 2 (lane native-r3-launcher-2)

- **The AOT cast bug** (`surface launcher: could not be made (at start, hidden and parked): Specified cast is not
  valid.`): the tree came from `XamlReader.Load` + `FindName` casts. In the AOT build an element handed back as a plain
  `object` is wrapped by its runtime class name, and a class the trimmed app cannot find (TextBox and ScrollViewer are
  in no compiled XAML) comes back as a bare COM object, so `(TextBox)` throws. Fix: every element is made in code with
  typed references (`LauncherSurface.Build`, rows as a `Row` record, no `FindName` per selection); brushes are read from
  Tokens.xaml per theme (`LauncherTokens`) and set on a theme change; the TextBox's own hover/focus states are turned
  plain by lightweight styling. UIA ids: `LauncherUia.cs` gives the panel (Group), `results` (List), each row (ListItem,
  ItemStatus "selected") and `hint` real peers, as Electron's roles.
- **Host wiring** (App.xaml.cs, one commented block before `StartSurfaces`): `LauncherService` + `LauncherCommands`
  (`apps.index`/`apps.search`/`apps.launch`), `IndexAsync()` at start. A `--test` run uses
  `IndexInputs.ForTestRun(MANTLE_TEST_LAUNCHER_DIR)`: only that scratch fixture (make-golden.cjs's layout, plus
  `powercfg.txt` / `store.txt` fakes), never the machine's Start Menu, profile, Store list or powercfg; unset = only the
  built-in rows. Plus one line in `OnLaunched`: the bar's `Closed` runs `Quit(...)` (the ONE shutdown): with the
  launcher parked, the bar is no longer the last window, so WinUI's last-window exit never came (UI test
  `Close_and_kill_leave_no_process_behind` caught it).
- **TestQuiet:** a `--test` launcher is `WS_EX_NOACTIVATE` and is shown with `AppWindow.Show(false)`; quiet (no
  `MANTLE_TEST_VISIBLE=1`) it is also layered alpha 0 and click-through. `MANTLE_TEST_LAUNCHER_KEYS=1` (with
  `MANTLE_TEST_VISIBLE=1`, runner only) lets a UI test give it the keyboard (activated like live, query focused).
- **Search timing:** a lone keystroke searches at once; keys within 12 ms of each other are folded into one search 12 ms
  later (Electron's queueSearch waits 12 ms on every key; a WinUI timer rounds 12 ms up to a 15.6 ms tick, which put
  native behind Electron on the first run). The log has `first frame with rows N ms after the open` (every open) and,
  in `--test`, `typed "q": n row(s) on screen N ms after the keystroke` (TextChanged to the next
  `CompositionTarget.Rendering` after the rows).
- **UI tests** `tests/Mantle.UiTests/Launcher/LauncherTests.cs` (scratch fixture: 4 bare `.lnk` files in both roots).
- **Parity:** `tools/parity/launcher/run-launcher.ps1` (+ `electron-launcher.mjs`, `uia-set.ps1` written at run time):
  golden `--check`, optional `-Ui` (launcher + surface-host UI tests), then per theme Electron then native with the
  same fixture and query (`steam`), 5 opens each, the query set letter by letter, the page grabbed, SSIM, pair image
  `native/out/parity/launcher/<theme>/launcher-pair.png` (Electron above, native below), `LAUNCHER-PARITY` /
  `LAUNCHER-LATENCY` / `LAUNCHER:` lines. A `--test` Electron launcher is a FRAMED window (title bar + menu), so its
  page (innerWidth x innerHeight) is what is grabbed.
- **make-golden.cjs:** `--fixture=<dir>` writes only the fixture; `--check` compares row ids by first appearance
  (an id hashes the row's full path, and the fixture is in a fresh temp folder each run, so the old check always
  drifted). **For the coordinator:** add `node native/tools/parity/launcher/make-golden.cjs --check` to ci.ps1's
  `unit` stage (node only, ~2 s); today it runs only inside run-launcher.ps1.

First numbers (run 42388736155, 1920x1080 at 100 %, query `steam`): SSIM light 0.8295, dark 0.5864 (not masked).
Open-to-first-frame median native 6.9 / 7.1 ms vs Electron 49.2 / 18.2 ms (light / dark); keystroke-to-rows median
native 19.9 / 15.4 ms vs Electron 17.1 / 15.2 ms (that run still had the 12 ms timer on every key; the leading-edge
change is in the next commit). Electron's open is measured from `mantle.call('launcher.show')` in its page (IPC,
main's prepare and backdrop, show); native's from `Show()` on its UI thread (the second start's pipe and the host hop
are not in it). The SSIM is confounded: a `--test` Electron launcher is a framed OPAQUE window, so its 10 px margin is
black and, in dark, its pane sits over black ("The see-through effect is off: Mantle could not picture the screen
behind this window" in its footer); native's pane is see-through over the wallpaper. A fair next step: a second pass
over a solid black desktop.

## Part 3 starts here

Known gaps from the pairs (not masked): Electron shows a **Files** group (Windows Search off on the runner: a warning
line) and **icons** (a generic file glyph for a `.lnk` with no target, a search glyph on the web row); native has
neither yet. The backdrop picture (Electron's blurred photo of what is behind) is not built: native's pane is the same
token stack over the unblurred desktop. The light drop shadow (`--lg-shadow`) is not drawn.
Then, in order: the backdrop picture and the unfold/fold, query memory (90 s), search/run/quick modes, power rows with
confirm, file rows (sidecar `search.files`), calculator, icons (sidecar resolver), keyboard-refused note, blur-closes,
profile command launch in Windows Terminal (ported, untested), URL launch through Quartz; UI tests for PageUp/PageDown,
Tab, a held Enter, scroll-into-view.
