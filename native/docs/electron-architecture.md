# Mantle today: the Electron architecture, as a map for the native team

Baseline: origin/master 6ca499f (2026-11-06). Facts are cited as `file#function` (all paths repo-relative). This is a map of what exists, not a design. Written by lane native-plan-inv-core; the per-behaviour contract is `docs/parity/core.md`.

## 1. Process model

Mantle is one Electron main process (`main.js`, ~9.8k lines) plus renderer processes (one per BrowserWindow, `sandbox:true`, `contextIsolation:true`, shared `preload.js`; `lib/windows.js#baseWebPreferences`) plus one C# helper family built from the same exe, `sidecar/bin/Mantle.Sidecar.exe`. Electron flags: `in-process-gpu` (saves ~50 MB, a GPU crash takes Mantle down) and `disable-features=CalculateNativeWinOcclusion` (windows that are "covered" keep painting) (`main.js` flags section ~443-460). The app sets its own AppUserModelId at start (main.js#app.setAppUserModelId).

### 1.1 Processes and who starts them

| Process | Command line | Started by | Dies when |
|---|---|---|---|
| main | `Mantle.exe` (packaged) or `electron . --live` | logon task `Mantle` (tools/mantle-logon-task.xml), Start menu, the supervisor, or "Restart Mantle" (`main.js#restartPlan`) | quit paths in section 8 |
| sidecar (main one) | `Mantle.Sidecar.exe --state-file=<userData>\shell-state.json [--passive]` (`--passive` in `--test`) | `lib/sidecar.js#Sidecar.start`, called first thing in `app.whenReady` (`main.js` ~9616); `windowsHide:true` | its stdin hits EOF or its parent dies: it first runs its own restore (sidecar lane) |
| supervisor | `Mantle.Sidecar.exe --supervise=<main pid> --supervise-detach` then a detached copy | `lib/supervisor.js#makeSupervisor` (`main.js` ~665-700, started right after the lock is won); live always unless `--no-supervise`, test only with `--supervise` | main stands it down (`supervisor.standDown(why)`), or after giving up |
| taskbar guard | `Mantle.Sidecar.exe --taskbar-guard=<sidecar pid>` | the sidecar itself (`sidecar/TaskbarHide.cs` line ~721), at tbhide time | the sidecar dies: the guard clears the empty region on Explorer's taskbar |
| restart helper | hidden `powershell.exe` waiting for the old pid, then runs the `Mantle` scheduled task | `main.js#arrangeRestart` (only for task-started live Mantle) | after relaunching |
| restore script | `powershell -File tools/restore-windows.ps1 -StateFile ... -NoKill` | `main.js#dirtyStartGuard`, `#restoreWithScript`, the supervisor give-up, `tools/restore-windows.cmd` (Start menu) | script end |

### 1.2 Windows (renderers)

Page = `ui/<dir>/index.html` unless noted. "Kept" = created at start and held; "on demand" = created at first use then kept or unloaded by `lib/features/_idle-unload.js` (10 min).

| Window | Page | Size / flags | Lifetime |
|---|---|---|---|
| bar | ui/bar | live: display width x `BAR_H`=48 DIP, frameless transparent, `focusable:false`, skipTaskbar, always-on-top `screen-saver`, placed by sidecar SetWindowPos; test: framed 1280 wide (`lib/windows.js#createBar`) | kept, created hidden at ready; never `hide()`-den (parked/shaped) |
| launcher | ui/launcher | 680x460 centred, frameless transparent, level `floating` (`#createLauncher`) | kept; destroyed while a game runs, re-made on wake (`main.js#restNow/#wakeNow`) |
| desktop layer | ui/desktop | primary display bounds, transparent, non-focusable, attached under the icons by the sidecar (`#createDesktop`) | kept, created held-hidden; re-made after Explorer restarts (`main.js#remakeDesktop`) |
| Alt+Tab overlay | ui/switcher | 400x240 resized per list, transparent, parked off screen (`lib/switcher.js` ~272) | kept; destroyed while resting |
| Wallpapers | ui/wallpapers | frameless, centred (`lib/wallpaper.js` ~905) | on demand |
| Welcome | ui/welcome | transparent, centred (`lib/welcome.js` ~170) | first start / Settings > About |
| look toast | ui/toasts-like small page | `main.js#lookToastShow` (~1140) | on demand |
| feature windows | ui/<feature> | ~22 more in `lib/features/*.js` (grep `new BrowserWindow`); pop-ups transparent skip-taskbar at `screen-saver`/`pop-up-menu`; app windows (Settings, Task Manager, Files, Clips, Player) frameless with the page's own title bar | per feature: kept warm (osd, clipboard, snap, taskview, screenshot overlay), on demand, or keep-ready opt-in (`lib/keep-ready.js`: Settings/Task Manager/Files default OFF) |

Window policy facts: `lib/raise.js#up` re-raises every pop-up on every show (live only); `lib/test-quiet.js` makes every `--test` window invisible (section 10); `lib/foreign-parent.js#guardWindow` is attached to every window by `app.on('browser-window-created')` (`main.js` line 41).

## 2. Startup sequence (in order)

1. `lib/test-quiet.js#install` (first line; no-op live). `require('electron')`, then lib requires (sidecar, state, plain-error, windows, features, ...). `main.js` 1-130.
2. `lib/args.js#parseArgs(argv)`: a bad flag prints `mantle: <reason>` and `app.exit(2)`. Extra flags read directly from argv: `--dock --pill --flush --no-edgeclip --no-tbhide --os-morph --test-tbhide --test-live-bar --test-trace= --media-guard --media-controls --log=` (`main.js` 140-440).
3. `--log=<path>` tee installed over stdout/stderr (`#teeLogToFile`, `#makeLogTee`, `#makeScrubber`).
4. `--test`: `app.setPath('userData', %TEMP%\mantle-test-<profile>)`. Set AUMID, Electron switches. `STATE_FILE = <userData>\shell-state.json`. `lib/welcome.js#judge` reads the profile BEFORE anything writes.
5. `--quit` / `--open=` branch: ask the lock, print, `app.exit(0|1|3|4)`; no window, no sidecar (`main.js` ~480-560, `#findOtherMantle` runs a <=15 s PowerShell query).
6. `--live` gate: neither `--live` nor `--test` prints the warning and `app.exit(2)`. Live prints the recovery line + state-file path; the 3 s console countdown only with a TTY (`lib/start-wake.js#countdownVerdict`).
7. `app.requestSingleInstanceLock()`; losers `app.exit(0)`.
8. `new ShellState(MODE, edge)`; `dirtyStartGuard()` (live: restore if `shell-state.json` says `restored:false`, 30 s timeout).
9. `makeSupervisor(...).start()` then `new Sidecar({...})` (not yet started).
10. Module-level wiring: `broadcast`, theme/look hosts, COMMANDS table (~line 5420-6090), `ipcMain.handle('mantle:call'|'mantle:replay')`, `ipcMain.on('mantle:theme-now')`, `sidecar.on(...)` handlers, `web-contents-created` guard.
11. `app.whenReady` (`main.js` ~9616): `sidecar.start()` FIRST (its ~1 s start overlaps the rest; calls made meanwhile wait up to 8 s for its `ready` line) -> theme -> look sync -> bar window -> welcome host -> 30 s/30 min memory log + 60 s trim -> launcher window -> desktop layer (held hidden) -> wallpaper -> look desktop -> notifications -> switcher -> `startFeatures()` -> test baseline -> crash-recovery notice -> `registerShortcuts()` -> `createTray()` -> apps index refresh -> display/power listeners -> hover watch (live: armed after the bar's first placement) -> pin watch + first pinned push -> screenshot/trace/flyout entry points.
12. On the sidecar's `ready` line: `applyMode` -> live `applyLive` (protocol ping with 2/5/15 s retries; desktop attach; `taskbar autohide`; bottom-strip placement + re-places at 300 ms/1.5 s/6 s; traywatch; edge clip; `tbhide` with a 1 s retry ladder; Win-key hook; switcher; features' chords) -> glass -> tray poll. A second and later `ready` re-reads all readouts after 2.5 s.
13. No timings are printed for the whole boot; individual log lines carry them: "taskbar hiding in force ... (N s after the sidecar was ready)", "window loaded: <page> in N ms" (test only), memory line at 30 s.

## 3. Command-line flags and exit codes

| Flag | Meaning | Source |
|---|---|---|
| `--live` | required to change the Windows shell; excludes `--test`, `--quit`, `--open` | args.js |
| `--test --profile-name=<x>` | harmless instance: temp userData, passive sidecar, invisible windows; name = `[A-Za-z0-9][A-Za-z0-9_.-]{0,63}` | args.js |
| `--quit` | ask the running Mantle to put Windows back and quit | main.js |
| `--open=wallpapers\|player` (+ `--play=<abs path>` x64) | ask the running Mantle to open a window | args.js#openRequest |
| `--screenshot=<dir>`, `--flyout=<name>`, `--theme=`, `--first-run`, `--bar-edge=top\|bottom`, `--no-desktop`, `--no-acrylic` | test/QA (first four require `--test`) | args.js |
| `--supervise`, `--no-supervise` | crash supervisor opt-in (test) / opt-out (live) | supervisor.js#wanted |
| `--dock`, `--pill`, `--flush`, `--no-edgeclip`, `--no-tbhide`, `--os-morph`, `--media-guard`, `--media-controls`, `--log=<path>` | main.js direct flags | main.js |
| `--test-*` | test probes (second-instance messages: `--test-click= --test-memory= --test-report --test-tray --test-crash --test-unload --test-chord= --test-key=`) | main.js |

Exit codes: 0 normal/quit delivered; 1 `--quit` found nothing, or crash exit; 2 bad flag or missing `--live`; 3 `--open` with no running instance; 4 `--open` refused. Env overrides (test only): `MANTLE_SIDECAR_EXE`, `MANTLE_TEST_SIDECAR_HEALTH`, `MANTLE_TEST_PAGE_HANG_MS`, `MANTLE_FEATURES_DIR`, `MANTLE_IDLE_UNLOAD_MS`, `MANTLE_TEST_VISIBLE=1`, `MANTLE_NO_LAUNCHER=1`.

## 4. Preload API and command surface

`preload.js` exposes exactly `window.mantle = { call(name, args), on(event, cb) -> unsubscribe, pathForFile(file) }`. Channels: `mantle:call` (invoke), `mantle:event` (main -> page), `mantle:replay` (invoke, current value on subscribe), `mantle:theme-now` (sendSync before first paint: theme + `--lg-*` look vars; stamps `<html data-theme>`).

Command dispatch: `ipcMain.handle('mantle:call')` (`main.js` ~6241) looks `name` up in the `COMMANDS` object (own properties only), runs it, and answers `{ok:true,result}` or `{ok:false,error}`; results/errors go through `lib/plain-error.js#toPage`. `callerVerdict` answers only frames whose URL is a file under `<root>/ui` (`#isMantlePage`); `guardContents` blocks navigation away and new windows.

Size of the surface: ~64 literal keys in the `COMMANDS` table in main.js (state, theme, look, win, bar, launcher, qs, media, battery, tray, shell, diag, sys, open ...), plus commands registered at runtime by `lib/wallpaper.js`, `lib/notifications.js`, `lib/switcher.js`, `lib/welcome.js`, and ~320 `ctx.command(...)` registrations across `lib/features/*.js` (named `<feature>.<verb>`). About 280 distinct command names are called from `ui/` pages (counted by `call('...')`: settings ~80, bar ~58, topbar ~33, player ~17, launcher ~12, desktop ~10, toasts ~6, others 1-4). Push events sent by `broadcast(name, payload)` include `state`, `theme`, `look`, `windows`, `bar.pinned`, `bar.reveal`/`bar.hide`, `bar.winkey`, `launcher:visible`, `tray`, `media`, `battery`, `volume`, `net`, `qs`, `notifications`, `mantle.rest`, `wallpaper*`, `btdevices`, and feature-specific ones.

## 5. Main <-> sidecar

`lib/sidecar.js#Sidecar` (970 lines). One child process, UTF-8 JSON lines over stdin/stdout.
- Out: `{"id":n,"cmd":"windows","ttl":ms,...args}`. In: `{"id":n,"ok":true,"result":...}` | `{"id":n,"ok":false,"error":"plain words"[,"reason":"expired"]}` | events `{"ev":"ready|windows|hotkey|taskbar|restore|log|tray|edgeclip|tbhide|switcher|qs|volume|net|media|battery|popwatch|appchanged|btdevices|...", ...}`.
- Call timeout default 8 s (`CALL_TIMEOUT_MS`); `ttl` makes the sidecar answer `expired` instead of doing work for a caller that gave up; late answers are recognised (`abandoned` map, 500) and reported; max line 4 MB (`MAX_LINE_CHARS`); calls made while starting wait for the ready line (`READY_WAIT_MS` 8 s).
- Lanes (sidecar side, `sidecar/Lanes.cs`): `ui` (default, fast commands, hooks), `capture`, `slow`, `audio`, `qs`, `focus`, `scan`, `notif`; the verbs themselves are inventoried by the sidecar lane.
- Restart: on exit, up to `MAX_RESTARTS`=3 attempts with delay 3 s doubled (3, 6, 12 s); the budget resets only after 60 s up (`STABLE_MS`); then `gaveUp` and `retry()` (tray "Reconnect Mantle to Windows", `shell.reconnect`).
- Liveness: a `ping` on the ui lane every 15 s, 3 s limit, 3 misses end the process so the restart path runs (`HEALTH_MS/HEALTH_TIMEOUT_MS/HEALTH_MISSES`).
- stderr is rate-limited to 20 lines per 10 s, 300 chars each. Status goes to State via `sidecar.on('status')` (`ok|starting|down` + reason).
- `Sidecar.isOldExeError` / `protocolAge()`: an exe older than the protocol is refused with "run npm run build:sidecar".

## 6. The status object (`lib/state.js#ShellState`)

One deep-frozen snapshot, rebuilt copy-on-write; `get()` returns the same object until a piece changes; `set(piece, patch)` emits `change` only if something moved. Pieces (all carry `reason`): `mode`, `edge`, `sidecar`, `taskbar` (`hidden` mirrors `autohide`), `workarea`, `desktop`, `hotkey`, `tray` (`status/reason` + `watch/watchReason`), `bar`, `edgeclip`, `tbhide`, `backdrop{bar,launcher,desktop}`, `glass`, `shortcuts`, plus `switcher`, `features{<name>}` and `notifications` added by their owners. `problems()` lists unhealthy pieces for the strip/tray. `main.js` `state.on('change')` (~1292) broadcasts `state` to every window, requests a batched tray rebuild and prints the problem list line.

## 7. Logging

- `--log=<path>` (logon task passes it): tee of stdout+stderr; local-time stamps; 5 MB rotation keeping `.1`/`.2`; scrubbed of home folder/user/PC name (`main.js#makeLogTee`, `#makeScrubber`). Terminal copy is raw.
- `lib/call-log.js`: repeated failures are one line + count; slider sets summarised; args written as lengths. `main.js#logSafe` hides caller words in reasons.
- `lib/plain-error.js`: raw codes -> plain words before a page sees them; codes go to the log once.
- Diagnostic report (tray): last 15 stderr lines + health + versions (`main.js#buildReport`).
- Every sidecar log event arrives as `[sidecar <level>] ...`.

## 8. Crash and exit paths, and what puts Windows back

| Path | What runs | Windows restored by |
|---|---|---|
| Tray Quit / `shell.quit` / `--quit` / `Ctrl+Alt+Shift+Backspace` | `shutdown(why)`: supervisor stand-down, stop polls, `globalShortcut.unregisterAll`, `restoreShell` (edge clip, tbhide off, app bar, taskbar flag + read-back, features' `stop()`, hotkey off, desktop detach), sidecar stop, `app.exit(code)`; 12 s hard stop | main via sidecar; fallback `restoreWithScript` (sync PowerShell) if the sidecar is blocked |
| `before-quit` / `will-quit` / `window-all-closed` | cancelled and routed to `shutdown` | same |
| Windows sign-out / restart / shutdown | `query-session-end` then `session-end` on any window -> `shutdown` (`#onSessionEnd`) | same, then sidecar exit restore, guard, next-start record |
| `uncaughtException` | `crash.json` (<=3 restarts / 10 min), `shutdown(..., {exitCode:1, crash:true})`; supervisor (or `arrangeRestart`) brings Mantle back | same + supervisor |
| `unhandledRejection` | logged once per distinct message; not fatal | n/a |
| Renderer crash / hang | page reloaded after 1/5/15 s (3 in 2 min), hang >10 s -> forced crash then reload; past the cap a "stopped" page | n/a |
| Native crash / kill -9 / End process tree / power loss | main gone: sidecar sees stdin EOF -> own restore; guard clears the taskbar region; supervisor (detached, out of the tree) restarts Mantle; 3 crashes in 2 min -> gives up, runs `restore-windows.ps1`, shows a dialog | sidecar + guard + supervisor; next live start `dirtyStartGuard` runs the script if `restored:false` |
| Sidecar death | `sidecar.on('died')`: state says restored or NOT with the recovery command; auto restart (section 5) | sidecar's own exit path; `markUnrestored` |
| Explorer dies / restarts | windows guarded (WM_NCDESTROY fix), `TaskbarCreated` -> `shellRestarted`: desktop layer re-attached/re-made (back-off 1.5-20 s, max 5 remakes / 10 min), top bar retakes its strip | n/a (nothing of Explorer's was hidden by window; taskbar region cleared by sidecar/guard) |
| Nothing running | user runs `tools/restore-windows.cmd` (or `.ps1`, `-RestartExplorer` last resort) | script: taskbar flag from the state file, taskbar region, cursor clip, banners (`restore-banners.ps1`), OSD host (`restore-osd.ps1`), RAM-reclaim values (`ramreclaim-undo.ps1`), max-fold animation, marks `restored:true` |

## 9. Game / rest mode

`lib/features/gamemode.js` (the single process-list reader, `lib/features/_game-watch.js` hub; top bar follows) decides "a known game runs" or "game mode on" and calls `host.rest(true, why)` -> `main.js#restMantle/#restNow`: features with `rest:true` are stopped via `lib/features.js#restAll` (windows destroyed, chords back to Windows, commands answer "resting while ..."), the launcher window is destroyed (Win key = Windows Start, state `hotkey.resting`), the switcher overlay and wallpaper unload, `mantle.rest` is broadcast, then `gamemode.trim` hands idle pages back; the hover poll pauses (`#gameQuiet`). `rest:false` features (clips, files, player) stay. `wakeNow` re-installs everything with the same `install()` as start-up. `lib/features/_idle-unload.js` separately unloads rarely used hidden windows after 10 min.

## 10. Test mode isolation

`--test --profile-name=<x>`: own userData in `%TEMP%` (also keys the single-instance lock); passive sidecar (no shell change; `restoreShell` only stops features); no `globalShortcut`, no hook chords (`features.fireChord` / `--test-chord` instead); no media session; tray is a stand-in; all windows invisible, click-through, non-activating (`lib/test-quiet.js`), unless `MANTLE_TEST_VISIBLE=1`; diagnostic report goes to a file not the clipboard; `tools/kill-test.ps1 -Match <profile>` stops exactly one test instance by the whole `--profile-name=` flag; `tools/toback.ps1` pushes a visible one to the back; `tools/run-suite.js` runs the whole suite serially and judges by printed totals.

## 11. What a native rewrite must replace

| Electron facility | Used for | Files |
|---|---|---|
| `BrowserWindow` (+ `win.setAlwaysOnTop`, `moveTop`, `hookWindowMessage`, `setIgnoreMouseEvents`, `capturePage`, `getNativeWindowHandle`) | all ~30 windows | lib/windows.js, main.js, lib/features/*, lib/switcher.js, lib/wallpaper.js, lib/raise.js, lib/foreign-parent.js |
| `ipcMain` / `contextBridge` / preload | `window.mantle.call/on` | main.js, preload.js |
| `globalShortcut` | `Alt+Space`, kill switch | main.js#registerShortcuts |
| `Tray`, `Menu`, balloon, `nativeImage` | tray icon + menu | main.js#createTray, lib/tray-icon.js |
| `app.requestSingleInstanceLock` / `second-instance` | single instance, `--quit`, `--open` | main.js |
| `app.relaunch`, `app.exit`, `before-quit` | restart/quit | main.js |
| `powerMonitor` (`suspend/resume/unlock-screen`), `screen` (`display-*`), `nativeTheme` | wake, display change, light/dark | main.js whenReady |
| `clipboard`, `shell.openExternal/openPath`, `dialog.showErrorBox` | report, open, errors | main.js, lib/open-target.js |
| `session`/protocol (`mantle-wp://`), `webUtils.getPathForFile` | wallpaper video, drag-drop | lib/wallpaper.js, preload.js |
| `app.getAppMetrics`, `webContents.forcefullyCrashRenderer` | memory log, hang recovery | main.js |
| koffi user32/dwmapi calls | raise check, AllowSetForegroundWindow, foreground, async keys | lib/raise.js, lib/fg-grant.js, lib/foreign-parent.js |
| child_process (PowerShell, schtasks, tasklist) | process lookup, restart, restore | main.js |

## Not verified here

Exact per-surface window sizes of feature pop-ups beyond the table; the full list of `COMMANDS` names (counted, not listed); sidecar verbs (other lane); no run was made (read-only inventory).
