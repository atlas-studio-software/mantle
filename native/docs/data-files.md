# Data files

Baseline: origin/master 6ca499f (2026-11-06). Inventory by lane native-plan-inv-data.

Every file, folder, registry value, scheduled task and renderer-side storage key Mantle (Electron main,
renderer pages, C# sidecar, elevated helper, tools scripts, installer) reads or writes, so the native port
can carry the user's data over. Paths are from the code; `file#function` names the writer. Anything marked
**ELECTRON-INTERNAL** sits in a Chromium store a native app cannot read as plain files.

## Folder roots

| Root | Live / dev (`electron .`, `npm run live`) | Packaged (Mantle-Setup.exe) | `--test --profile-name=<x>` |
|---|---|---|---|
| `<userData>` | `%APPDATA%\mantle` (Electron default from package.json `name: "mantle"`; main.js never calls `app.setName`) | `%APPDATA%\Mantle` (electron-builder `productName: Mantle` becomes the app name; same folder on NTFS, case-insensitive; lib/packaged-entry.js line 22 comment says `%APPDATA%\mantle`) | `%TEMP%\mantle-test-<x>` (main.js line ~441 `app.setPath('userData', path.join(os.tmpdir(), 'mantle-test-' + flags.profileName))`; lib/args.js makes `--profile-name` mandatory with `--test`, `[A-Za-z0-9][A-Za-z0-9_.-]{0,63}`, no `..`) |
| `<settings>` = `<userData>\features` | `%APPDATA%\mantle\features` | `%APPDATA%\Mantle\features` | `%TEMP%\mantle-test-<x>\features` (lib/features.js#createFeatures `settingsDir`; every feature's `ctx.settings.file` = `<settings>\<name>.json`) |
| `<stateDir>` (sidecar) | dir of `STATE_FILE` = `<userData>` (main.js passes `<userData>\shell-state.json`) | same | `<userData>` of the test profile; sidecar `--passive` uses `%TEMP%` subfolders for some records |
| `<lookSync>` | `%APPDATA%\look-sync` (lib/look-sync.js#defaultDir; env `LOOK_SYNC_DIR` overrides) | same | `LOOK_SYNC_DIR` if set, else `<userData>\look-sync` (main.js ~1091) |
| `<localMantle>` | `%LOCALAPPDATA%\Mantle` (sidecar Elevate.cs, MenuBar.cs fallback, elevated helper) | same | `MANTLE_ELEVATE_DIR` for the elevate queue in test mode; handshake dir overridable in the helper |
| `<appDir>` | the git clone (e.g. the repo folder); `node_modules\electron\dist\electron.exe` | `%LOCALAPPDATA%\Programs\Mantle` (electron-builder per-user NSIS default, `perMachine: false`; not asserted by repo code) with `resources\app\...` (`asar: false`) | the clone |
| `<helperDir>` | `%ProgramFiles%\Mantle\Elevated` (lib/features/_taskmgr-elevated.js `installDir`) | same | not installed by tests |
| first-start box | n/a | `%TEMP%\mantle-first-start` (lib/packaged-entry.js#firstStart: Chromium junk of the consent dialog only) | n/a |
| Notes | `%USERPROFILE%\Documents\Mantle Notes` (literally USERPROFILE, not `getPath('documents')`) | same | `MANTLE_NOTES_DIR` (refused under Documents) else `<settings>\notes` |

Mode notes: "dev" and "live" share `<userData>`; there is no separate dev profile. A `--quit` / `--open`
message process uses the same `<userData>` only to find the running instance (single-instance lock is keyed
by `<userData>`), and writes nothing (lib/welcome.js comment).

## Files: Mantle's own profile (`<userData>`)

| ID | Path pattern | Format | Writer | Readers | Atomic write | Versioning | Encryption | Test isolation | Electron-internal? |
|---|---|---|---|---|---|---|---|---|---|
| F-001 | `<userData>\shell-state.json` | JSON `{writtenAt, pid, units:'physical'\|'virtualised', original:{abmState, workarea:[l,t,r,b]}, changed:{autohide, appbars:[hwnd], taskbarRegion}, restored}` (sidecar/State.cs header) | sidecar/State.cs#ShellState (write at start, after each change, at restore) | tools/restore-windows.ps1 (`-StateFile` default `%APPDATA%\mantle\shell-state.json`), installer/put-windows-back.ps1 via it, lib/welcome.js (evidence) | `.tmp` then `File.Replace` (ReplaceFile), fallback copy | none (shape is the contract with restore-windows.ps1) | none | test profile dir | no |
| F-002 | `<userData>\features.json` | JSON object `{<feature>: bool}` tray/Settings feature switches | lib/features.js#saveSwitches | lib/features.js#createFeatures, lib/welcome.js (evidence), ui/unfold/pace-load.js (dev) | `.tmp` + rename; unreadable file moved to `features.json.unreadable` | none | none | test profile | no |
| F-003 | `<settings>\<feature>.json` (one per lib/features/<name>.js that saves: apppower, corners, downloads, emoji, files, gamemode, gcal, jumplist, layouts, notes, ontop, player, qstiles, ramreclaim, run, screenrec, screenshot, settings, spotify, taskmgr, timer, topbar, unfold, updates, lookaway, ...) | JSON object: declared `mod.options` values (validated by lib/features.js#validateOptions) plus feature state keys: apppower `rules`; corners `actions`; downloads `seenAt`; emoji `apps, closeAfterInsert, recents, tone`; files `bounds, pins`; gamemode `auto, boost, liftSaver`; gcal `calendars`; jumplist `pins` (Mantle's own taskbar pin overlay); layouts `layouts` (`[{name, savedAt, entries}]`, lib/features/_layouts-core.js); lookaway `delay, turn`; notes `active`; ontop `pins`; qstiles `focus`; ramreclaim `pieces`; run `history`; screenrec `audio, fps, mic, saveDir`; screenshot `autoSave, mode, saveDir`; spotify `trimAboveMB`; taskmgr `autoFree, bounds, lastAuto, ticks, tm`; timer `clock, held, last`; topbar `hidden, overGames`; unfold `when`; updates `cache` (release + ETag), `laterTag, laterUntil` | lib/features.js#settingsFor (`ctx.settings.set`) | each feature via `ctx.settings.get`; main.js (jumplist overlay via lib/features/_jumplist-pins.js#read) | `.tmp` + rename; over `SETTINGS_MAX` refused; bad file moved to `<name>.json.unreadable` (layouts copies to `layouts.json.unreadable`) | none per file; per-key legacy reads in code | none | test profile | no |
| F-004 | `<userData>\theme.json` | JSON `{mode:'system'\|'light'\|'dark'}` | lib/theme.js (save: `.tmp` + rename) | lib/theme.js, lib/welcome.js | `.tmp` + rename; bad file moved aside | none | none | test profile; `--theme=` pins a test run and never saves | no |
| F-005 | `<userData>\look-sync.json` | JSON `{lookSync, accent:{hue}, glass:{dark:{blurPx,dim,panel}, light:{...}, render}}` Mantle's local look + sync switch | lib/look-host.js (~line 96) | lib/look-host.js, lib/welcome.js | `.tmp` + rename | none | none | test profile | no |
| F-006 | `<userData>\first-run.json` | JSON `{state:'pending'}` or `{state:'done', how:'finished'\|'skipped'\|'existing-profile', at, evidence?}` | lib/welcome.js#writeFlag | lib/welcome.js#judge/readFlag | `.tmp` + rename | none | none | test never writes it unless `--first-run` | no; but `judge()` also treats `<userData>\Local Storage` as evidence of an old profile |
| F-007 | `<userData>\installed-consent.json` | JSON `{accepted:true, at, ...}` (packaged first-start warning) | lib/packaged.js#writeConsent | lib/packaged.js#readConsent (lib/packaged-entry.js) | `.tmp` + rename | none | none | packaged only; `--test` passes straight through | no |
| F-008 | `<userData>\crash.json` | JSON `{history:[ms...], last:{at, what, restarted}}` crash-loop record (3 restarts / 10 min) | main.js `uncaughtException` handler (`crashFile()`) | main.js (recoveryLine, tray, diagnostic report), lib/welcome.js | plain `writeFileSync` (no tmp) | none | none | test profile | no |
| F-009 | `<userData>\supervisor-main.json` | JSON, main's record for the supervisor (`standDown {why}` for a pid = deliberate quit; exe, args, folder to restart) | lib/supervisor.js (ONLY main writes) | sidecar/Supervisor.cs (the detached supervisor) | `.tmp` + rename (lib/supervisor.js ~179) | none | none | `--test` only with `--supervise` | no |
| F-010 | `<userData>\supervisor-state.json` | JSON, supervisor's verdict `{at, title, words, tray, log}` told once | sidecar/Supervisor.cs (ONLY it writes) | lib/supervisor.js#noticeFor | `.tmp` + `File.Replace`/`Move`, plain-write fallback (Supervisor.cs ~846) | none | none | as F-009 | no |
| F-011 | `<userData>\supervisor.lock` | lock file held share-none by a live supervisor (pid-reuse guard) | sidecar/Supervisor.cs | lib/supervisor.js (lockSaysGone) | n/a | none | none | as F-009 | no |
| F-012 | `<userData>\mantle.log`, `mantle.log.1`, `mantle.log.2` | UTF-8 text log, scrubbed of home/user/host (main.js#makeScrubber); rotates at 5 MB, keeps 2 | main.js#teeLogToFile/makeLogTee (only when `--log=<file>`; logon task and packaged copy pass `<userData>\mantle.log`) | humans, tools/osd-bright-logstats.js, diagnostic report | append; rotate by rename | none | none | test runs log where `--log=` says | no |
| F-013 | `<userData>\diagnostic-report.txt` | text, scrubbed | main.js (~8722, diagnostic report command) | the user | plain write | none | none | test profile | no |
| F-014 | `<userData>\keep-ready.json` | JSON (which windows to keep pre-warmed), trailing newline | lib/keep-ready.js | lib/keep-ready.js | `.tmp` + rename; bad file to `keep-ready.json.unreadable` | none | none | test profile | no |
| F-015 | `<userData>\media-guard.json` | JSON, protected media apps (`guard:false` per app keeps it controllable); hand-written by the user | none (user edits by hand) | lib/media-guard.js#read (once per start) | n/a | none | none | test profile | no |
| F-016 | `<userData>\notifications-seen.json` | JSON `{ids:[...]}` | lib/notifications.js | lib/notifications.js | `.tmp` + rename | none | none | test profile | no |
| F-017 | `<userData>\switcher.json` | JSON switcher (Alt+Tab / Win keys) settings | lib/switcher.js (~403) | lib/switcher.js | `.tmp` + rename | none | none | test profile | no |
| F-018 | `<userData>\notif-banners.json` | JSON record of Windows' banner settings Mantle turned off (per AUMID prior `ShowBanner`) + `pid, at` | lib/features/toasts.js (RECORD_FILE) | sidecar/ToastBanners.cs, sidecar/Supervisor.cs, tools/restore-banners.ps1 (`-RecordFile` default `%APPDATA%\mantle\notif-banners.json`), tools/restore-windows.ps1 | `.tmp` + rename | none | none | test profile; tests use an HKCU scratch key (`testBannersKey`) | no |
| F-019 | `<userData>\notif-banners-back.sidecar.json`, `.supervisor.json`, `.script.json` | JSON receipts: who put the banners back | sidecar, supervisor, restore-banners.ps1 | lib/features/_toasts-owner.js (BACK_FILES) | not verified | none | none | test profile | no |
| F-020 | `<userData>\gamemode-restore.json` | JSON record of everything game mode changed (power plan etc.) to undo after a crash | lib/features/gamemode.js (RECORD) | lib/features/gamemode.js at start | `.tmp` + rename | none | none | test profile | no |
| F-021 | `<userData>\topbar-appbar.txt` (beside the state file; fallback `%LOCALAPPDATA%\Mantle\topbar-appbar.txt`) | text `"<hwnd> <...>"` marker of the top bar's registered appbar | sidecar/MenuBar.cs#MarkerPath | sidecar/MenuBar.cs (removes a dead appbar next start), lib/welcome.js (evidence) | plain `File.WriteAllText` | none | none | test profile | no |
| F-022 | `<userData>\maxfold-windows.json` (beside state file; `--passive`: `%TEMP%\mantle-maxfold-test\maxfold-windows.json`) | JSON `{hwnd, pid, set, assumedOriginal}` window whose style MaxFold changed | sidecar/MaxFold.cs#WriteRecord | sidecar/MaxFold.cs, tools/restore-windows.ps1 | `.tmp`, delete, move | none | none | `--passive` temp dir | no |
| F-023 | `<userData>\taskbar-guard.log` (beside state file, else `%TEMP%`) | text note from TaskbarHide's guard to the next sidecar | sidecar/Program.cs#GuardNotePath users (TaskbarHide.cs) | next sidecar | append | none | none | test profile | no |
| F-024 | `<userData>\ping-tests.json` (+ `.bad.json` aside) | JSON `{version:1, tests:[...]}` saved / recent ping tests (renamable) | lib/features/_pingtest-history.js (topbar) | same | `.tmp` + rename; bad file renamed to `*.bad.json` | `version: 1` | none | test profile | no |
| F-025 | `<settings>\clipboard-pins.dat` (+ `.tmp`, `.unreadable`-style aside) | **raw DPAPI blob** (CryptProtectData, current user, flag 1 = UI_FORBIDDEN, entropy UTF-8 `"Mantle.ClipHistory.pins.v1"`) of JSON `{v:1, items:[...]}` pinned clipboard items | sidecar/ClipHistory.cs#SavePins (path handed in by lib/features/clipboard.js `pinsFile`) | sidecar/ClipHistory.cs#LoadPins | `.tmp` then `File.Replace` / `File.Move`; recovers a lone `.tmp` | `v: 1` inside | DPAPI user scope + fixed entropy: a native app on the same Windows account decrypts it directly with `ProtectedData.Unprotect(bytes, entropy, CurrentUser)` | test profile | no (Mantle's own DPAPI, not safeStorage) |
| F-026 | `<settings>\ramreclaim-journal.json` | JSON `{v:1, entries:{...}}` original HKCU DWORDs before Mantle changed them | lib/features/_ramreclaim-core.js#save (throws = no change) | same; tools/ramreclaim-undo.ps1 (`-Journal` default `%APPDATA%\mantle\features\ramreclaim-journal.json`), tools/restore-windows.ps1 | `.tmp` + rename (sync) | `v: 1` | none | test profile; test key allowlist | no |
| F-027 | `<settings>\taskmgr-history.json` | JSON app-history counters (Task Manager App history page) | lib/features/_taskmgr-history.js | same | `.tmp` + rename | not verified | none | test profile | no |
| F-028 | `<settings>\files-last.json` | JSON `{path, items, count, at}` first view of the last folder (instant open) | lib/features/files.js (LAST_FILE, ~186) | lib/features/files.js at start | `.tmp` + `fs.promises.rename` | none | none | test profile (settingsDir) | no; disposable cache |
| F-029 | `<settings>\files-thumbs\<key>.<kind>.png` | PNG thumbnail cache, pruned to `TH.keep` newest | lib/features/files.js#thumbFile | lib/features/files.js | plain write | key encodes source | none | test profile | no; disposable |
| F-030 | `<settings>\notes\` (test only) | as F-050 | lib/features/_notes-store.js | same | as F-050 | | | this IS the test fallback | no |
| F-031 | `<userData>\clips-thumbs\` + `clips-thumbs\meta.json` | PNG/JPG thumbs of game clips + JSON `{<thumbKey>: meta}` | lib/features/clips.js#metaFor | same | plain `writeFileSync` | none | none | test profile | no; disposable |
| F-032 | `<userData>\player-library.json` | JSON media library scan cache | lib/features/_player-library.js | lib/features/player.js | `.tmp` + `fs.promises.rename` | not verified | none | test profile | no; rebuildable |
| F-033 | `<userData>\player-covers\*` | extracted album covers (`cover.png` / `folder.png` style) | lib/features/_player-library.js, lib/features/player.js (~223) | player UI | plain write if missing | none | none | test profile | no; rebuildable |
| F-034 | `<userData>\player-thumbs\*` | video thumbnails | lib/features/player.js (~286) | player UI | plain write | none | none | test profile | no; rebuildable |
| F-035 | `<userData>\wallpapers\library.json` | JSON `{version:1, selected, items:[{id,title,type,file,thumb,...}], removed:[], lively}` | lib/wallpaper-lively.js#Library.save | lib/wallpaper.js, lib/wallpaper-lively.js#Library.load | `.tmp` + rename; unreadable moved aside | `version: 1` | none | test profile | no |
| F-036 | `<userData>\wallpapers\<id>\...` | copied wallpaper files (picture/video/gif) or whole copied web-wallpaper folders (`LivelyInfo.json`, `LivelyProperties.json`, html/js) | lib/wallpaper.js (~717, ~762 `copyFile` / `cp`), lib/wallpaper-lively.js (import, ~422) | wallpaper protocol `mantle-wp://<id>` (lib/wallpaper.js#protocol.handle), desktop page | copy | Lively's own formats | none | test profile | no (but see L-019 for their web storage) |
| F-037 | `<userData>\gcal\token.bin` | Electron `safeStorage.encryptString(JSON)` of Google refresh + access token | lib/features/_gcal-store.js#putSecret | `#getSecret` | `<name>.<pid>.tmp` + rename | none | **safeStorage** (see Encryption note) | test profile; selftest uses `<userData>\gcal-selftest` | **ELECTRON-INTERNAL** (needs Chromium `Local State` key) |
| F-038 | `<userData>\gcal\client.bin` | safeStorage ciphertext of `{client_id, client_secret}` from the user's Google client file | lib/features/_gcal-store.js#putSecret | `#getSecret` | as F-037 | none | **safeStorage** | as F-037 | **ELECTRON-INTERNAL** |
| F-039 | `<userData>\gcal\cache.json` | JSON `{v:1, events:[...], calendars...}` (flyout opens offline) | lib/features/_gcal-store.js | `#getCache` (requires `v===1`) | `.tmp` + rename | `v: 1` | none (plain; deleted on sign-out with token.bin) | as F-037 | no |
| F-040 | `<userData>\update-pending.json`, `update-done.json`, `update-failed.json`, `update-failed-release.json`, `update.log` | JSON update records + text log (clone self-update) | lib/updater.js (writeDurable from lib/update-watch.js for release record; plain write for pending; append for log) | lib/updater.js, lib/update-watch.js | `writeDurable`: tmp + `fsync` + rename (the only fsync in the JS code) | none | none | `--test` never checks (status off) | no |
| F-041 | `<userData>\update-watch.js`, `update-watch.json` | copy of the restart watcher script + its config | lib/updater.js (~650) | detached `node`/electron watcher | plain write | none | none | not in tests | no |
| F-042 | `<userData>\selftest.json`, `timer.json` (selftests) and `<userData>\gcal-selftest\` | selftest outputs | lib/features/_*-selftest.js | test harness | plain | none | none | test-only | no; do not migrate |
| F-043 | `%TEMP%\mantle-shot\` (FRAME_DIR) | captured frames for the screenshot overlay | lib/features/screenshot.js, sidecar/Screenshot.cs (temp, cleaned) | screenshot overlay | n/a | none | none | test uses `mantle-shot-test-out` | no; disposable |
| F-044 | Chromium profile inside `<userData>` | see the ELECTRON-INTERNAL section below | Electron | Electron | | | | test profile | **ELECTRON-INTERNAL** |

## Files: user-visible output folders

| ID | Path pattern | Format | Writer | Readers | Atomic write | Versioning | Encryption | Test isolation | Electron-internal? |
|---|---|---|---|---|---|---|---|---|---|
| F-050 | `%USERPROFILE%\Documents\Mantle Notes\notes.md` | Markdown, notes split by `<!-- mantle:note -->` marker lines; backslash escape for marker-looking lines; CRLF read as LF, BOM dropped; max 8 MB, 500 notes; must be valid UTF-8 | lib/features/_notes-store.js (via lib/features/notes.js) | same; the user by hand | `notes.md.tmp-<pid>` (O_EXCL) + rename, EPERM/EBUSY/EACCES retry ~1 s; re-stat before each rename | format = marker | none | `MANTLE_NOTES_DIR` (refused under Documents) else `<settings>\notes` | no |
| F-051 | `...\Mantle Notes\deleted.md` | Markdown append log of deleted notes (dated marker line first); never through a link | lib/features/_notes-store.js | the user | append (delete refused if append fails) | marker | none | as F-050 | no |
| F-052 | `...\Mantle Notes\notes (changed outside Mantle YYYY-MM-DD HHMMSS).md` | copy of an outside-changed notes.md before overwrite (rate-limited MAX_COPIES per window) | lib/features/_notes-store.js | the user | copy | none | none | as F-050 | no |
| F-053 | screenshots: setting `screenshot.saveDir`, else Windows' Screenshots known folder, else `Pictures\Screenshots` | PNG files | sidecar/Screenshot.cs (save), lib/features/screenshot.js#saveDirFor | the user, Files | sidecar writes; temp in `%TEMP%` | none | none | `--test`: only under `%TEMP%` (`mantle-shot-test-out`) | no |
| F-054 | screen recordings: setting `screenrec.saveDir`, else `Videos\Captures` | MP4; `<name>.part` renamed to final | sidecar/Recorder.cs (`_part` -> final `File.Move`), lib/features/screenrec.js#outDirFor | the user, Clips gallery | `.part` + move | none | none | `--test`: under `%TEMP%` only (`MANTLE_REC_TEST_DIR` / `mantle-rec-feature-test`) | no |
| F-055 | game clips folder: Windows' Game Bar captures folder (sidecar `gaming.get`), else `Videos\Captures` | MP4 clips (Game Bar records); Clips trims write a NEW file; ClipsEncode `.part` + move | sidecar/ClipsEncode.cs, lib/features/clips.js | Clips gallery | `.part` + move | none | none | `--test`: `MANTLE_CLIPS_DIR` under `%TEMP%` or empty temp folder; `MANTLE_CLIPS_FAKE=1` | no |
| F-056 | process dumps: `%TEMP%\<exe>.DMP` (`" (2)"` etc. never overwrites) | minidump | sidecar/ProcOps.cs (Task Manager "Create dump file") | the user | direct | none | none | dry-run in tests | no |
| F-057 | `%TEMP%\mantle-setup-<install\|uninstall>.log`, `%TEMP%\mantle-logon-task.xml` | text transcript; task XML (UTF-16 LE BOM) | installer/put-windows-back.ps1; tools/install-autostart.ps1 | the user / schtasks | plain | none | none | n/a | no |

## Files: shared with other apps (Helm, Quartz, Windows, Lively)

| ID | Path pattern | Format | Writer | Readers | Atomic write | Versioning | Encryption | Test isolation | Electron-internal? |
|---|---|---|---|---|---|---|---|---|---|
| F-060 | `<lookSync>\look.json` | JSON `{schema:1, rev, writer:{app,...}, look:{theme\|accent\|glass:{v, rev, by}}, desktop:{rev, by, still, ...}}` (CONTRACT in the look-sync project) | lib/look-sync.js (vendored, do not edit) for Mantle; Helm and Quartz write too | Mantle, Helm, Quartz | `look.json.<app>.<pid>.<rand>.tmp` + rename with retries | `schema: 1`; a newer schema = read-only state | none | `LOOK_SYNC_DIR` or `<userData>\look-sync` | no |
| F-061 | `<lookSync>\look.json.bad-<ts>` | damaged copy moved aside, then re-seeded | lib/look-sync.js | humans | rename | | | as F-060 | no |
| F-062 | `<lookSync>\stills\wall-<rev>.jpg` (keep 3 newest; `.tmp` cleaned) | JPEG still of the current desktop wallpaper/video frame for Helm/Quartz | lib/look-desktop.js#makeDesktopPublisher | Helm, Quartz, lib/wall-still.js, lock-screen picture path | tmp + rename (tmp unlinked on failure) | `rev` in name | none | as F-060 | no |
| F-063 | `%APPDATA%\Helm\badge.json` (override `MANTLE_HELM_BADGE_FILE`) | JSON badge Helm writes | Helm | lib/helm-badge.js (read-only) | n/a | | | env override | no |
| F-064 | `%LOCALAPPDATA%\Lively Wallpaper\...` (`Library\SaveData\wptmp\<id>\LivelyInfo.json`, `Library\wallpapers\<id>\`, `WallpaperLayout.json`, `Settings.json`, `AppRules.json`, `library.json`) | Lively's JSON(C) | Lively | lib/wallpaper-lively.js (read-only import) | n/a | Lively's | none | read-only | no |
| F-065 | `%APPDATA%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\*.lnk` | Windows' taskbar pins | Windows (Mantle never writes; own pins are an overlay in F-003 jumplist `pins`) | lib/pinned.js, lib/features/jumplist.js | n/a | | | read-only | no |
| F-066 | `%ProgramData%\...\Start Menu\Programs`, `%APPDATA%\...\Start Menu\Programs` | shortcuts | Windows | lib/launcher-index.js (in-memory index, no disk cache) | n/a | | | read-only | no |
| F-067 | `%APPDATA%\Microsoft\Windows\Recent\*.lnk` | recent items | Windows | lib/features/search.js, sidecar/Files.cs, sidecar/JumpList.cs | n/a | | | read-only | no |
| F-068 | `%LOCALAPPDATA%\Microsoft\Windows\WinX\Group1..3` | Win+X menu links | Windows | lib/quick-links.js | n/a | | | read-only | no |
| F-069 | `%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db`, `%LOCALAPPDATA%\Packages\<pfn>\LocalState\...` (toast images) | Windows notification DB (SQLite) | Windows | sidecar/Notifications.cs (read-only) | n/a | | | read-only | no |
| F-070 | `%APPDATA%\Microsoft\Windows\Network Shortcuts`, `rasphone.pbk`, `%WINDIR%\System32\WDI\LogFiles\StartupInfo`, SRUM db | Windows data | Windows | sidecar Files.cs, NetMisc.cs; elevated/StartupImpact.cs, elevated/Srum.cs (temp copy deleted) | n/a | | | read-only | no |
| F-071 | `%APPDATA%\Spotify\Spotify.exe` | exe path | Spotify | lib/features/_spotify-app.js, sidecar/SpotifyMem.cs | n/a | | | | no |
| F-072 | `%USERPROFILE%\.secrets\google` | folder the gcal "Choose Google client file" dialog opens in when it exists (else Downloads) | the user | lib/features/gcal.js (dialog default path only) | n/a | | | | no |
| F-073 | `~\browser` (Quartz) (`MANTLE_BROWSER_DIR`) | Quartz install | Quartz | lib/quartz-exe.js | n/a | | | env override | no |
| F-074 | Downloads folder (`app.getPath('downloads')`) | user files | browsers | lib/features/downloads.js, lib/features/_downloads-list.js (skips `thumbs.db`, `ehthumbs.db`) | n/a | | | | no |

## Files: elevation and helper (`%LOCALAPPDATA%\Mantle`, `%ProgramFiles%\Mantle`)

| ID | Path pattern | Format | Writer | Readers | Atomic write | Versioning | Encryption | Test isolation | Electron-internal? |
|---|---|---|---|---|---|---|---|---|---|
| F-080 | `%LOCALAPPDATA%\Mantle\elevate\<nonce32hex>.req.json` / `.res.json` / `used-nonces.txt` | JSON request/answer pairs for the one-shot UAC elevation path (per-user ACL, reparse points refused, leftovers older than `LeftoverMs` deleted) | sidecar/Elevate.cs (req; the elevated copy writes res) | sidecar/Elevate.cs | `.res.json.tmp` + move (planted-file guards) | none | none (ACL only) | `MANTLE_ELEVATE_DIR` in test mode | no; transient |
| F-081 | `%LOCALAPPDATA%\Mantle\elevated\handshake.json` | JSON `{pid, token}` single-use, user-ACL'd; deleted on read | lib/features/_taskmgr-elevated.js (~234) | sidecar/elevated/Program.cs#ReadHandshake | direct write to an opened fd (writeHandshake), no rename | none | none (ACL) | helper `_dirOverride` | no; transient |
| F-082 | `%LOCALAPPDATA%\Mantle\elevated\helper.log` | text (names/args never logged) | sidecar/elevated/Program.cs#OpenLog | the user | append | none | none | same dir | no |
| F-083 | `%ProgramFiles%\Mantle\Elevated\mantle.elevated.exe`, `version.txt` | installed admin helper + its version (expected = sidecar/elevated/VERSION) | tools/mantle-elevated-setup.ps1 (one UAC) | lib/features/_taskmgr-elevated.js#installedVersion | copy | `version.txt` vs VERSION (stale = set up again) | none | not installed by tests | no |
| F-084 | `\\.\pipe\Mantle.Elevated` | named pipe (not a file) | helper | Task Manager | | | | | no |

## Files: app folder and developer-only

| ID | Path pattern | Format | Writer | Readers | Atomic write | Versioning | Encryption | Test isolation | Electron-internal? |
|---|---|---|---|---|---|---|---|---|---|
| F-090 | `<appDir>\no-updates.local` | presence = updates opt-out | the user | lib/updater.js (OPT_OUT_FILE) | n/a | | | | no |
| F-091 | `%USERPROFILE%\.mantle-dev.json` (`MANTLE_DEV_CONFIG`) | JSON dev env | the developer | tools/dev-env.js | n/a | | | dev only | no |
| F-092 | `<appDir>\assets\backgrounds\catalog.json` + images | built-in wallpaper catalog (shipped, read-only) | repo | lib/wallpaper-presets.js | n/a | | | | no; ships with the app |
| F-093 | `<appDir>\sidecar\ping-targets.json` | shipped config | repo | sidecar ping test | n/a | | | | no; ships with the app |
| F-094 | `<appDir>\...\mantle-files.ico`, `mantle-player.ico`, `mantle-taskmgr.ico` | icons used for window/AUMID/file-association icons (paths written into registry R-005) | repo | lib/features/files.js, player.js, taskmgr.js | n/a | | | | no; the native exe must ship equivalents and rewrite R-005 paths |
| F-095 | `shots/`, `suite-out/`, `trace.json`, `report.json` etc. under a chosen out dir | test/measurement outputs | ui/*/shoot.js, tools/run-suite.js, main.js (`--screenshot=`, flyout trace) | developers | plain | | | test only | no; never migrate |

## ELECTRON-INTERNAL: Chromium stores inside `<userData>`

| ID | Path pattern | What Mantle keeps there | Native readable? |
|---|---|---|---|
| F-100 | `<userData>\Local Storage\leveldb\*` | **ELECTRON-INTERNAL**. Every `localStorage` key in the L table, origin `file://` (all Mantle pages are `loadFile`d, so they share one origin), plus each web wallpaper's own storage under origin `mantle-wp://<id>`. lib/welcome.js counts this folder as evidence of an existing profile. | No: LevelDB with Chromium's key encoding (`_file://\x00\x01<key>`, values UTF-16/latin-1 prefixed). Must be exported by the Electron app. |
| F-101 | `<userData>\Session Storage\*` | **ELECTRON-INTERNAL**. Nothing used (no `sessionStorage` in ui/). | n/a, nothing to carry |
| F-102 | `<userData>\IndexedDB\*`, `Service Worker\*`, `blob_storage\*`, `WebStorage\*`, `Shared Dictionary\*`, `SharedStorage` | **ELECTRON-INTERNAL**. Mantle's pages use none; web wallpapers (third-party html/js under `mantle-wp://`) may. | No; wallpaper state is lost (they re-init) unless WebView2 hosts them with an exported profile |
| F-103 | `<userData>\Network\Cookies`, `Network\Network Persistent State`, `TransportSecurity`, `Trust Tokens` | **ELECTRON-INTERNAL**. No Mantle feature relies on cookies (gcal uses its own tokens; Spotify revive does not read Spotify's cookies). | Cookies values are encrypted with the same `Local State` key; nothing to carry |
| F-104 | `<userData>\Cache`, `Code Cache`, `GPUCache`, `DawnGraphiteCache`, `DawnWebGPUCache`, `Crashpad` | **ELECTRON-INTERNAL**. Disposable caches and crash dumps. | not needed |
| F-105 | `<userData>\Preferences` | **ELECTRON-INTERNAL** (JSON). Spellcheck language and per-origin settings (e.g. zoom) Chromium keeps; Mantle sets no zoom it relies on. | JSON is readable, but nothing Mantle relies on lives there |
| F-106 | `<userData>\Custom Dictionary.txt` (Chromium spellcheck; `Dictionaries\*.bdic` downloads) | **ELECTRON-INTERNAL** location. Notes pad is the only page with `spellcheck: true` (lib/features/notes.js ~166); no code calls `addWordToSpellCheckerDictionary` and no "Add to dictionary" menu exists in lib/features/notes.js or ui/notes, so the file is expected empty or absent. | Plain UTF-8 word list: native can read it, but the native spellchecker (Windows ISpellChecker) keeps its own user dictionary, so it needs a one-time import |
| F-107 | `<userData>\Local State` | **ELECTRON-INTERNAL** (JSON). `os_crypt.encrypted_key` = base64(`"DPAPI"` + DPAPI blob of a random 256-bit AES key). The key for F-037/F-038. | Yes with care: see Encryption note |
| F-108 | window positions | Mantle does NOT use Chromium/Electron window-state storage; Files and Task Manager keep `bounds` in F-003. | n/a |
| F-109 | `lookaway` session partition | `session.fromPartition('lookaway')` (no `persist:`): in memory, nothing on disk. | n/a |

### Encryption note: Electron safeStorage ciphertext (F-037, F-038)

- On Windows, `safeStorage.encryptString` is Chromium OSCrypt: output = ASCII `v10` (3 bytes) + 12-byte nonce
  + AES-256-GCM ciphertext + 16-byte GCM tag. The AES key is random per profile and stored in
  `<userData>\Local State` as `os_crypt.encrypted_key` = base64 of `DPAPI` (5 ASCII bytes) + a DPAPI blob
  (CryptProtectData, current user, no entropy).
- `v11` is the Linux (libsecret) prefix and does not occur here. Raw DPAPI blobs (no prefix, starting
  `01 00 00 00 D0 8C 9D DF 01 15 D1 11 8C 7A 00 C0 4F C2 97 EB`) were Chromium's pre-v80 format and should not
  occur under Electron 44. Chrome's newer `v20` app-bound encryption is a Chrome-browser cookie feature, not
  Electron safeStorage. A migrator must check the prefix and refuse (say why) on anything but `v10`.
- A native app CAN decrypt them, same Windows account only: read `Local State`, base64-decode
  `encrypted_key`, strip `DPAPI`, `ProtectedData.Unprotect(blob, null, CurrentUser)` -> 32-byte key; then
  `AesGcm(key).Decrypt(nonce=bytes[3..15], ciphertext=bytes[15..^16], tag=last 16)`. It cannot when the
  profile was copied to another account or machine (DPAPI), or when `Local State` was deleted/reset (the key
  is gone; `getSecret` already reports "could not be decrypted").
- Safer plan: decrypt once in the migrator (or have the Electron app write a DPAPI-only export) and
  re-store under the native app's own DPAPI (ProtectedData with Mantle entropy, like F-025). Fallback that
  always works: the user signs in to Google again (client.bin needs the client file chosen again).
- F-025 (`clipboard-pins.dat`) is Mantle's own raw DPAPI with entropy `Mantle.ClipHistory.pins.v1`: no
  Chromium involved, native reads it as-is.

## Registry, scheduled tasks and other OS state

| ID | Key / value | Type / data | Writer | Readers / undo | Test isolation | Native port |
|---|---|---|---|---|---|---|
| R-001 | Scheduled task `\Mantle` (logon trigger for the user, RestartOnFailure; `<Command>` electron.exe (clone) or Mantle.exe (packaged); `<Arguments>` `. --live --log=%APPDATA%\mantle\mantle.log` or `--live "--log=..."`; WorkingDirectory = app folder) | Task Scheduler XML from tools/mantle-logon-task.xml | tools/install-autostart.ps1 (`npm run autostart`; packaged: lib/packaged.js#autostartCommand when the first-start box is ticked) | `-Uninstall` (only if it starts that exe, `-OnlyIfExe`); installer | not touched by tests | must be re-registered for the native exe; Electron's task removed (`-OnlyIfExe`) |
| R-002 | Scheduled task `Mantle Elevated Helper` (RunLevel Highest, Interactive, `mantle.elevated.exe --pipe`, 10 min limit) | Task Scheduler | tools/mantle-elevated-setup.ps1 | lib/features/_taskmgr-elevated.js#status; `-Uninstall` | not installed by tests | keep (the helper is already C#) or re-register if its path/name changes |
| R-003 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | NOT written for Mantle itself; `app.setLoginItemSettings` unused. Startup.cs only toggles other apps via StartupApproved (R-009). | none | | | native must not add a Run value either, or document the change |
| R-004 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\<AUMID>` `ShowBanner` | DWORD set 0 while Mantle draws its own banners; deleted/restored after | sidecar/ToastBanners.cs (lib/features/toasts.js drives) | record F-018; restored by sidecar, supervisor, tools/restore-banners.ps1, tools/restore-windows.ps1 | `testBannersKey` HKCU scratch key | same keys, same record file, same three restore roads |
| R-005 | Player file associations: `HKCU\Software\Classes\MantlePlayer.Video` / `.Audio` (name, icon, `shell\open\command`), `HKCU\Software\Classes\.<ext>\OpenWithProgids` `<ProgID>` (REG_NONE), `HKCU\Software\Mantle\Player\Capabilities` (+`\FileAssociations`), `HKCU\Software\RegisteredApplications` `Mantle Player` | REG_SZ/REG_NONE; command line points at the Electron exe + app folder | lib/features/_player-assoc.js (reg.exe, on user request) | Windows Default Apps (`ms-settings:defaultapps`); `reg query RegisteredApplications` | dry run in tests (lists lines, writes nothing) | one-time migration: rewrite open command + icon paths to the native exe; ProgIDs unchanged so the user's defaults survive |
| R-006 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Dsh` `IsPrelaunchEnabled`, `...\SearchSettings` `IsDynamicSearchBoxEnabled`, `...\Lock Screen` `LockScreenWidgetsEnabled` | DWORD target 0 (allowlist; HKCU + REG_DWORD only) | lib/features/_ramreclaim-core.js (reg.exe) | journal F-026; tools/ramreclaim-undo.ps1; restore-windows.ps1 | test-only allowlist key | same keys + journal; native must read the existing journal so undo still works |
| R-007 | `HKCU\Software\Microsoft\DirectX\UserGpuPreferences` `<exe path>` | REG_SZ `GpuPreference=n;` per app rule | sidecar/AppPowerGpu.cs (from apppower `rules`, F-003) | Windows; rule removal deletes the value | dry run | same; rules carried in F-003 |
| R-008 | Taskbar auto-hide (ABM_SETSTATE via SHAppBarMessage; Explorer persists it in `HKCU\...\Explorer\StuckRects3` `Settings`); work area (SPI_SETWORKAREA); taskbar window region; top-bar appbar | OS state, not a value Mantle writes directly | sidecar/Taskbar.cs, TaskbarHide.cs, MenuBar.cs | originals in F-001; tools/restore-windows.ps1; installer/put-windows-back.ps1 | `--passive` / test never changes the taskbar | identical record + restore script contract (restore-windows.ps1 must keep working with the native app) |
| R-009 | `HKCU`/`HKLM\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run`, `Run32`, `StartupFolder` | REG_BINARY enable/disable flags | sidecar/Startup.cs (Task Manager Startup tab; HKLM needs admin, not on helper allowlist) | Windows | dry run | Windows-owned; nothing to migrate |
| R-010 | Windows settings the Settings surface edits for the user: `Control Panel\Desktop` (CursorBlinkRate...), AutoplayHandlers, `Shell\USB`, `HKLM ...\USB\AutomaticSurpriseRemoval`, Bluetooth, DeviceSetup, `Windows NT\...\Windows` (legacy printer), CloudStore night light `Data`, `UserGpuPreferences`, `HKLM GraphicsDrivers HwSchMode`, Accessibility/Themes/Cursors/ColorFiltering/ScreenMagnifier/Narrator, Winlogon (dynamic lock, restart apps), VideoSettings, CDP, GameBar/GameDVR QWORDs, Personalize binary values, Fonts, CapabilityAccessManager ConsentStore, W32Time `Type`, TimeZone keys, WindowsUpdate UX settings, Internet Settings proxy, Notifications banners | various | sidecar/Sys*.cs, Dev*.cs, Display.cs, NetProxy.cs, SysCommon.cs#Write, ToastBanners.cs (via lib/features/_settings-sections/*) | Windows | `dryRun` in tests | Windows-owned; no migration; native Settings must write the same keys |
| R-011 | Read-only inputs: `Explorer\Taskband` `Favorites` (pin order), `Explorer\Advanced` (jump list prefs), `Themes\Personalize` `AppsUseLightTheme` (via nativeTheme), `Explorer\RunMRU`, `App Paths`, `Software\Microsoft\Clipboard`, `System\GameConfigStore\Children`, `Explorer\VirtualDesktops` + SessionInfo, ConsentStore `webcam`/`location`, `Classes\AppUserModelId\<aumid>`, Uninstall keys, Search CrawlScopeManager, power schemes, network class keys | read | lib/pinned.js, lib/theme.js, lib/features/_lookaway-camera.js, sidecar Run.cs, ClipHistory.cs, MenuBar.cs, Desktops.cs, Notifications.cs, SysApps.cs, Search.cs, PowerMode.cs, NetAdapters.cs | | | read the same keys |
| R-012 | Shell replacement (`Winlogon\Shell`), Explorer policies (`Policies\Explorer`, `NoTaskbar...`) | NOT written. ramreclaim only describes `HKLM\SOFTWARE\Policies\...\Windows Search DisableSearch` and `...\Dsh AllowNewsAndInterests` as advice text; its `allowed()` refuses any non-HKCU write. Explorer keeps running underneath. | none | | | keep it that way |
| R-013 | AppUserModelID `com.owensilva.mantle` (main.js `app.setAppUserModelId`; electron-builder `appId`) | identity, not a value | main.js | Windows keys taskbar grouping, jump lists, notification settings and toast history under it | test uses the same AUMID (not verified per window) | native exe must set the SAME AUMID or the user's per-app notification settings/pins split |
| R-014 | Installer: Start Menu `Mantle\Mantle.lnk`, `Mantle\Restore Windows taskbar.lnk` (-> `resources\app\tools\restore-windows.cmd`); electron-builder per-user uninstall entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\<guid>` (default behaviour, not in repo code) | shortcuts / uninstall key | installer/installer.nsh, electron-builder | Windows | CI only (installer/ci-install-test.ps1) | native installer replaces them; keep a "Restore Windows taskbar" entry |
| R-015 | Game mode changes (power plan, others per lib/features/gamemode.js) | OS settings via sidecar GameMode.cs / PowerMode.cs | lib/features/gamemode.js | record F-020 | test profile | same record so a crash undo still works across the switch |

## Renderer localStorage (all **ELECTRON-INTERNAL**, origin `file://`, in F-100)

| ID | Key | Owning surface | Value shape | Behaviour that depends on it |
|---|---|---|---|---|
| L-001 | `mantle.desktop.widgets` | Desktop right-click menu (ui/desktop/deskmenu.js) | `'on'` / `'off'` (absent = on) | Desktop widgets shown or hidden (`body.widgets-off`) across restarts |
| L-002 | `settings.cache` | Settings window (ui/settings/settings.js#cacheSave/cacheLoad) | JSON `{v:1, at, slots:{qs, net, battery, pm, gm, mantle, audio, about}}` (only ok slots, saved at most every 1500 ms and on hide) | Settings paints last-known values instantly before fresh reads; disposable |
| L-003 | `taskmgr.rail` | Task Manager (ui/taskmgr/taskmgr.js) | JSON string `"open"` / `"closed"` (default open) | nav rail expanded/collapsed |
| L-004 | `taskmgr.runHistory` | Task Manager "Run new task" | JSON string array, newest first, case-insensitive dedupe, max 26 | the dialog's history drop-down |
| L-005 | `taskmgr.tbl.<id>` (`processes`, `details`, `services`, `startup`, `users`, `apphistory`; ids per page's table) | Task Manager tables | JSON `{vis:[col ids], w:{col:px}, sort:{...}}` | shown columns, widths, sort per page |
| L-006 | `taskmgr.lastView` | Task Manager | JSON string page id | written on every page switch, never read in this baseline (dead write) |
| L-007 | `taskmgr.perf.sel` | Performance page | JSON string, default `"cpu"` | selected device |
| L-008 | `taskmgr.perf.cpuGraph` | Performance | `"overall"` / `"logical"` | CPU graph mode |
| L-009 | `taskmgr.perf.kernel` | Performance | bool, default false | Show kernel times |
| L-010 | `taskmgr.perf.gpuGraph` | Performance | `"multi"` / `"single"` | GPU graph layout |
| L-011 | `taskmgr.perf.gpuEng` | Performance | JSON `{<gpu id>: [engine name per graph slot]}` | which engine each GPU graph shows |
| L-012 | `taskmgr.perf.gsv` | Performance | bool | Graph summary view |
| L-013 | `taskmgr.perf.sumv` | Performance | bool | Summary view (device list alone) |
| L-014 | `taskmgr.perf.hideGraphs` | Performance | bool | list without sparklines |
| L-015 | `taskmgr.proc.group` | Processes page | bool, default true | Group by type |
| L-016 | `taskmgr.proc.status` | Processes | legacy one word `none`/`suspended`/`efficiency` | read only to seed L-017 (old-version migration) |
| L-017 | `taskmgr.proc.status2` | Processes | JSON `{susp:bool, eff:bool}` | Status column values shown |
| L-018 | `taskmgr.proc.res` | Processes | JSON `{mem, disk, net: "values"\|"percents"}` | resource columns as values or percents |
| L-019 | any key, origin `mantle-wp://<id>` | third-party web wallpapers (F-036) | wallpaper-defined | a web wallpaper's own saved state (Lively-compatible) |

## Carry-over plan inputs

### (a) Native app can read as-is (same paths, plain files or Mantle's own DPAPI)

- F-001 shell-state.json, F-021 topbar-appbar.txt, F-022 maxfold-windows.json, F-023 taskbar-guard.log (sidecar
  is already C#; keep the same shapes so restore-windows.ps1 keeps working).
- F-002 features.json and F-003 `features\<name>.json` (all options and state keys: layouts, pins, rules,
  history, emoji recents, timer, bounds...). Plain JSON; the native settings layer must accept the same keys
  and the `.unreadable` aside rule.
- F-004 theme.json, F-005 look-sync.json, F-006 first-run.json, F-007 installed-consent.json, F-008 crash.json,
  F-014 keep-ready.json, F-015 media-guard.json, F-016 notifications-seen.json, F-017 switcher.json,
  F-018/F-019 banner records, F-020 gamemode-restore.json, F-024 ping-tests.json, F-026 ramreclaim journal,
  F-027 taskmgr-history.json, F-035/F-036 wallpapers library + files, F-039 gcal cache.json.
- F-025 clipboard-pins.dat (DPAPI + entropy `Mantle.ClipHistory.pins.v1`, same account).
- F-050..F-055 notes, deleted.md, screenshots, recordings, clips (user folders, unchanged).
- F-060..F-062 look-sync shared folder (vendored contract; native needs a C# port of lib/look-sync.js with
  the same schema-1 rules, tmp naming and rev logic).
- F-080..F-083 elevation files (C# already).
- Disposable caches the native app may simply rebuild: F-028, F-029, F-031..F-034, F-043.

### (b) Needs a one-time migration

- R-001 logon task: re-register for the native exe (install-autostart equivalent), remove the Electron one
  only if it starts the Electron exe.
- R-005 player associations: rewrite `shell\open\command` and icon paths to the native exe (same ProgIDs so
  the user's chosen defaults stay).
- R-013 AppUserModelID: keep `com.owensilva.mantle` on the native exe (no data moves, but a different id
  orphans notification settings, R-004 record keys and taskbar grouping).
- F-037/F-038 gcal token/client: decrypt with the `Local State` key (Encryption note) once, re-store with the
  native app's DPAPI scheme; on any failure say so and ask the user to sign in again.
- F-106 Custom Dictionary.txt: Mantle never adds words to it (no API call, no menu); import it only if a
  non-empty file is found.
- F-012 mantle.log and F-040/F-041 updater records: the native updater is a different mechanism; old records
  should be archived, not interpreted (a stale `update-pending.json` must not trigger the native watcher).
- F-009/F-010/F-011 supervisor files: if the native app keeps sidecar/Supervisor.cs, the `supervisor-main.json`
  exe/args must point at the native exe; a stale record naming electron.exe must not restart Electron.
- F-094 icon files referenced by registry (R-005) and AUMID shortcuts: ship native equivalents.
- Packaged `<userData>` is `%APPDATA%\Mantle` vs clone `%APPDATA%\mantle`: same folder on NTFS; the native app
  should use `%APPDATA%\mantle` exactly so the restore scripts' defaults keep working.

### (c) Electron-internal: must be exported by the Electron app before switch-over

- L-001..L-018 (Local Storage leveldb, F-100): add an Electron command (e.g. `profile.export`) that reads
  `localStorage` in a `file://` page and writes `<userData>\export\localstorage.json` `{v:1, at, items:{key: value}}`;
  the native app imports it once. Must-carry: L-001 (desktop widgets off), L-003..L-005, L-007..L-018
  (Task Manager layout and choices). Safe to drop: L-002 (cache), L-006 (dead), L-016 (legacy, after it seeded L-017).
- L-019 web wallpaper storage (`mantle-wp://<id>` origins): export per origin only if the native app hosts web
  wallpapers in WebView2; otherwise accept the loss and say so in the switch-over notes.
- F-037/F-038 if the native migrator will not touch `Local State`: the Electron app exports the decrypted
  token/client re-protected with plain DPAPI (`ProtectedData`, CurrentUser, Mantle entropy) to
  `<userData>\gcal\token.dpapi` / `client.dpapi`, then the native app reads those.
- F-102/F-103/F-104/F-105/F-107: nothing Mantle relies on; leave in place (delete only after the user is
  done with the Electron app; `Local State` is needed until F-037/F-038 are migrated).

## Not covered

- Per-feature option key lists (names, types, defaults) inside F-003 are only summarised; the Settings/feature
  parity lanes own the full key tables (`mod.options` in each lib/features/<name>.js).
- Exact JSON shapes of F-009/F-010 supervisor files, F-014 keep-ready.json, F-017
  switcher.json, F-020 gamemode-restore.json, F-027 taskmgr-history.json, F-032 player-library.json were not
  read field by field; writers are named so the port can lift them.
- Atomicity of F-019 (banner receipts) and F-027 versioning not verified.
- The packaged install folder (`%LOCALAPPDATA%\Programs\Mantle`) and uninstall registry key are electron-builder
  defaults, not asserted by repo code.
- Taskmgr `tbl.<id>` table ids were taken from the `*.shots.js` fixtures (apphistory, details, startup, users)
  plus processes/services by inference; the exact list of table ids was not confirmed.
- Every sidecar Settings registry write (R-010) is grouped, not listed value by value; the Settings parity
  lane covers them per control.
- Clip/screenshot sidecar temp files under `%TEMP%` (ClipsEncode, Screenshot, AltTabLive, AppPowerProc, Emoji,
  GameMode, MenuBar temp guards) were noted only as guarded temp paths, not inventoried individually.
