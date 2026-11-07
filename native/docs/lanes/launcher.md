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

## Part 2 starts here

1. Merge the host, add `tests/Mantle.UiTests/Launcher/`: open, type, rows, arrows/PageUp/Home/End/Tab,
   Enter on a scratch `.cmd` fixture (the unit test `ALiveLaunchOpensTheFileTheRowPointsAt` already proves the
   live ShellExecute path on the runner), Esc.
2. Screenshot pairs (light/dark, 100/150 %) and latency vs Electron: a standalone script under
   `native/tools/parity/launcher/` (Electron: `--test` with `MANTLE_TEST_VISIBLE=1` over CDP, the same query
   typed in both). Not done in part 1: the surface cannot be shown before the host lands.
3. Then: the backdrop picture and the unfold/fold, query memory (90 s), search/run/quick modes, power rows with
   confirm, file rows (sidecar `search.files`), calculator, icons (sidecar resolver), keyboard-refused note,
   blur-closes, profile command launch in Windows Terminal (ported, untested), URL launch through Quartz.
