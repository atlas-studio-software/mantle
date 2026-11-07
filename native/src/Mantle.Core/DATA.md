# Electron Mantle's data, and what native reads

Native Mantle reads Electron Mantle's files and **never writes them**. The rule is in the API
(`Mantle.Core.Data` has no method that writes, moves or deletes) and in the tests
(`ReadOnlyTests`: no writing method, no writing call in the source, a damaged profile is
byte-for-byte unchanged after every reader and the watcher ran). Electron owns these files: it
writes them whole (tmp + rename) and moves a damaged one aside itself.

## Where

| run | data root (Electron's `userData`) | shared look folder |
|---|---|---|
| live | `%APPDATA%\mantle` | `%APPDATA%\look-sync` (or `LOOK_SYNC_DIR`) |
| `--test --profile-name=<x>` | `%TEMP%\mantle-test-<x>` (main.js) | `<root>\look-sync` (or `LOOK_SYNC_DIR`) |
| `--user-data=<dir>` (native, tests and fixtures) | `<dir>` | `<dir>\look-sync` (or `LOOK_SYNC_DIR`) |

The only file native writes under the root is its own log, `mantle-native.log` (Electron's is
`mantle.log`, written when the logon task passes `--log=`), plus `.1` / `.2` when it rotates.

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
a reader's approximation of lib/look-host.js, which also adopts and seeds; native never publishes.

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
