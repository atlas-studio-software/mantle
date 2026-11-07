# Electron Mantle's data, and what native reads

Native Mantle reads Electron Mantle's files, and writes them **only through one gate**. Until the
switch-over it must never change the user's real Electron data, so every write in `Mantle.Core.Data`
and `Mantle.Core.Look` goes through `Data/SafeFile.cs`, and SafeFile asks `Data/WriteGate.cs` first:

- a test or scratch profile (`--test`, or `--user-data=` that is not the user's own folder): written;
- the user's own profile (`%APPDATA%\mantle`) or shared look folder (`%APPDATA%\look-sync`): written
  ONLY when the run is `--live` AND native is the only shell (Electron Mantle is not running). The gate
  fails closed: when nothing has said Electron is gone, it assumes it is running;
- any path outside those two folders: always refused.

A refused write changes nothing on disk and answers in plain words ("this run is not --live, so the
user's own look.json is left exactly as Electron Mantle wrote it"); the change lives in memory for this
run only, and the caller says so (a store write answers `{ok: false, reason}`, look sync shows an
`error` status with the words). `WriteGate.Describe()` is the start-up log line.

SafeFile has exactly four writing methods, each gated: `WriteAtomic` (tmp + rename, the rename retried
when busy), `MoveAside` (the `.unreadable` rule, look.json's `.bad-<time>` copies), `Remove` (a tmp or
still this code made) and `EnsureFolder` (the look-sync folder before it is watched).
`ReadOnlyTests` proves the path: no writing call (`File.Write*`, `File.Move`, `Directory.Create*`,
`FileMode.Create`, streams for writing, ...) appears in any other file of `Data/` or `Look/`; every SafeFile
method does nothing when the gate says no; a scratch gate refuses anything outside its folder; the
user's own profile is written only by a live run that is the only shell.

Who writes what, all through SafeFile:

| file | native writer | format (byte-for-byte Electron's) |
|---|---|---|
| any `Data/DataFileSpecs.cs` file (`theme.json`, `features.json`, `features\<name>.json`, `keep-ready.json`, ...) | `JsonStore` (`Stores.Open`), unknown keys kept | the spec's indent, trailing newline and tmp name |
| `<root>\look-sync.json` | `Look/LookHost.cs` (lib/look-host.js save), through its store | `{lookSync, accent, glass}`, 2-space indent, no newline |
| `<look folder>\look.json` | `Look/LookSync.cs` (lib/look-sync.js), Mantle publishes it for Helm and Quartz | `JSON.stringify(doc, null, 2) + "
"`, tmp `look.json.<app>.<pid>.<hex>.tmp`, rename retried after 50/100/200 ms |
| `<look folder>\look.json.bad-<ISO time>` | LookSync, a damaged file moved aside | the old bytes, unchanged |

The look.json bytes are proved against the JavaScript module itself: `tests/Mantle.Core.Tests/Look/gen-look-golden.js`
runs lib/look-sync.js, lib/look-host.js, lib/look-guard.js and ui/shared/glass-adapt.js and writes
`Fixtures/look/golden.json`; `LookGoldenTests` replays every case (twelve sync scenarios, each step's file
bytes, events and status) and compares. Re-run the generator after any change to those modules.

## Where

| run | data root (Electron's `userData`) | shared look folder |
|---|---|---|
| live | `%APPDATA%\mantle` | `%APPDATA%\look-sync` (or `LOOK_SYNC_DIR`) |
| `--test --profile-name=<x>` | `%TEMP%\mantle-test-<x>` (main.js) | `<root>\look-sync` (or `LOOK_SYNC_DIR`) |
| `--user-data=<dir>` (native, tests and fixtures) | `<dir>` | `<dir>\look-sync` (or `LOOK_SYNC_DIR`) |

Besides the gated writes above, native writes its own log under the root, `mantle-native.log` (Electron's is
`mantle.log`, written when the logon task passes `--log=`), plus `.1` / `.2` when it rotates. The log is native's
own file, never Electron's, so it is not behind the gate.

## What native reads (typed readers in `Data/Readers.cs`)

| file | Electron writer | schema (what Electron writes) | native reads | on a bad file |
|---|---|---|---|---|
| `<root>\theme.json` | lib/theme.js | `{ "mode": "system" \| "light" \| "dark" }`, at most 64 KB | `mode` -> the bar's theme (unless `--theme=` pins it) | corrupt / too large / unreadable: Match Windows, reason logged |
| `<root>\look-sync.json` | lib/look-host.js | `{ lookSync: bool, accent: { hue: null \| 0..360 }, glass: { dark: { blurPx 0..64, dim .20..90, panel -.30..15 }, light: {...}, render: "picture" \| "seeThrough" } }` | all three; each field checked like look-sync's `checkValue`, a bad one skipped with its reason | defaults = look-sync's SEED (sync on, own accent, dark blur 5 / dim .50, light blur 12 / dim .45) |
| `<look folder>\look.json` | lib/look-sync.js (vendored; Helm and Quartz write it too) | `{ schema: 1, rev, writer: { app, instance, pid, at }, look: { theme \| accent \| glass: { v, rev, by } }, desktop?: { rev, ok, id, type, title, file, still, reason, stillSize } }` | rev, writer app/at, theme, accent, glass, desktop record; parse rules of look-sync's `parse()` (schema missing = corrupt, schema > 1 = newer: nothing used) | nothing used, reason logged |
| `<root>\features.json` | lib/features.js | `{ "<feature>": true \| false, ... }` | every boolean switch (`FeatureSwitches.Get(name)`, null = never flipped) | not an object: no switches, reason logged |
| `<root>\features\jumplist.json` | lib/features/jumplist.js via _jumplist-pins.js | `{ pins: { hide: [{ id, name, exe, aumid }], add: [{ id, name, exe, aumid, shortcut }], order: [pin id] } }` plus the feature's other settings | `pins` only; entries without an id skipped, order de-duplicated | empty overlay, reason logged |

The bar uses `theme.json` (its theme) and the effective glass (`MantleData.EffectiveGlass`: the shared
look's glass when `lookSync` is on and look.json has a usable one, else look-sync.json's) for the
backdrop blur. The rest is read and logged now so the next surfaces can use it. `EffectiveGlass` is
a reader's approximation of lib/look-host.js; the full port (adopt, seed, publish) is `Look/LookHost.cs` + `Look/LookSync.cs`.

Every read is logged as `data: <file>: <state> (<reason>)[; using the defaults]: <value>` with
`state` = ok, missing, corrupt, unreadable, toolarge or newer; fields that were present but not used
follow as `; not used: ...`. A missing file is said too (a fresh profile has none).

## Watching

`MantleData.StartWatching()` puts a FileSystemWatcher on the root, `<root>\features` and the look
folder (each that exists), and checks the five files' stamps (last write + length) every 2 s. The
poll attaches watchers to folders that appear later and catches anything a watcher drops (buffer
overflow, folder deleted). A changed stamp re-reads that file and raises `Changed` once.

## Everything else Electron keeps under the root (not read by native)

Found by grepping main.js and lib/ for `getPath('userData')` and the file-name constants. Native
has no reader for these; none is needed by the bar.

| file | writer | what it is |
|---|---|---|
| `shell-state.json` | main.js + the C# sidecar | what Windows looked like before Mantle changed it (tools/restore-windows.ps1 reads it) |
| `features\<feature>.json`, `features\<feature>\...` | lib/features.js (each feature's `ctx.settings`) | per-feature settings (e.g. `features\settings.json` holds the Settings sections) |
| `features\files-last.json`, `features\gamemode-restore.json`, `features\player-library.json`, ... | lib/features/* | feature state |
| `switcher.json` | lib/switcher.js | Alt+Tab switcher settings |
| `notifications-seen.json` | lib/notifications.js | which notifications were seen |
| `notif-banners.json` | lib/features/toasts.js | banner record |
| `keep-ready.json` | lib/keep-ready.js | keep-ready state |
| `media-guard.json` | lib/media-guard.js | protected media apps (user-edited) |
| `crash.json` | main.js | the last crash, for the next start |
| `diagnostic-report.txt` | main.js | a report the user asked for |
| `first-run.json` | lib/welcome.js | whether the welcome was shown |
| `installed-consent.json` | lib/packaged.js | the installed build's consent |
| `supervisor-main.json`, `supervisor-state.json` | lib/supervisor.js | crash supervisor state |
| `update-pending.json`, `update-done.json`, `update-failed.json`, `update-failed-release.json`, `update.log`, `update-watch.json` | lib/updater.js | updater state |
| `mantle.log` (+ `.1`, `.2`) | main.js `--log=` tee | Electron's log |
| `Local Storage\`, other Chromium folders | Electron itself | browser storage |

None of the user's own files or values were copied to make this table or the test fixtures: the
fixtures in `native/tests/Mantle.Core.Tests/Fixtures/profile` are synthetic, built from the schemas
and defaults in Electron's code.
