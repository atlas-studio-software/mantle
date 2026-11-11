# Lane native-r7-media: media, game mode, downloads, badges

Branch `native/r7-media` from native/main 3c8d76d. Part 1 of the lane: the media back end, the now-playing chip, the
card and its source list. Game mode, downloads, the badges, quick links and the Spotify section are NOT started (see
"Left" below).

## What is built (part 1)

- `Mantle.Core/Features/Media/`
  - `MediaPick.cs`: lib/media-pick.js (the sticky pick, 60 s grace, automatic and the protected-only rule), pure.
  - `MediaGuard.cs`: lib/media-guard.js (media-guard.json, `--media-guard`, `--media-controls`), pure but for the read.
  - `MediaService.cs`: main.js mediaRead / mediaPush / mediaPollArm / mediaPickArm and the three commands' bodies. A host
    service, not a feature (`media` is a reserved prefix, and Electron's media is main.js's, not a tray feature).
  - `MediaCommands.cs`: `[Command]` media.get, media.select, media.control, and media.card (native only, below).
  - `MediaView.cs`: bar.js mediaApp / mediaLine / mediaStatus / mediaCan / fmtMs / mediaPos and the source list, pure.
- `Mantle.App/Surfaces/Bar/Media/`
  - `BarWindow.Media.cs`: the chip (`#media`, `#mchip`, `#mtog`) built in code and put first in the right island,
    renderMedia / paintMediaChip / mediaDo / openMedia, the 1 s progress tick, the "media" and "mediactl" chips.
  - `MediaCard.cs`: buildMediaPop / mediaPaint (bar.css .mcard .mc-*); `MediaPics.cs`: MEDIA_SVG and data-URL pictures.
- Hooks outside the lane's folders (small, commented): `BarWindow.xaml.cs` (one line after StartQuickSettings),
  `App.xaml.cs` (block "media wiring" after the quick settings block).

## A --test run never changes the runner

- media.control is a DRY RUN: checked exactly as a live press (action, AUMID, the guard), then logged as
  `media: dry run (--test): <action> for <aumid> would ask the sidecar media {...}; nothing was sent`, never sent.
- `MANTLE_TEST_MEDIA` (test runs only): JSON `{sessions:[full sidecar sessions], events?, eventsReason?, fail?}` read
  in place of the sidecar, so every runner draws the same chip and card. A dry-run play/pause/toggle moves the made-up
  session (playing/status) so a test sees the chip follow, as qs.set's dry run does. `fail` makes the read fail.
- Nothing in this lane starts, stops or talks to a real Spotify or Helm.

## UI Automation ids

| AutomationId | element | notes |
|---|---|---|
| `bar.media` | the chip's group | Collapsed (absent) when nothing is shown |
| `bar.media.chip` | the art button | Name = "title · artist"; HelpText = the tooltip; ItemStatus `playing`/`paused` |
| `bar.media.toggle` | play/pause | Name = its tooltip; ItemStatus `pause`/`play` + `; off`; absent under the guard |
| (window) `Mantle pop-up: Now playing` | the card's pop-up | key `media`, instant, no focus |
| `media.card` | the card | |
| `media.card.art` | artwork box | HelpText `art`/`app`/`none` |
| `media.card.title`, `.artist`, `.app` | the head lines | Name = the text |
| `media.card.prog`, `.now`, `.dur`, `.progwhy` | progress | |
| `media.card.previous`, `.toggle`, `.next` | transport | Name = tooltip; ItemStatus `on`/`off` (+ `; busy`) + `; <glyph>` |
| `media.card.lock`, `.lockhead`, `.lockhow` | the guard's lock box | the --flag's hyphens are non-breaking |
| `media.card.note` | the refusal note | absent when empty |
| `media.card.src`, `.srcwhy`, `.src.<aumid lower-case>`, `.src.auto` | source list | ItemStatus `on`/`off` (+ `; gone`) |
| `media.card.foot` | "Updates every 10 s instead of live: ..." | absent when live |

Log lines the tests read: `media: the chip shows <app> (<playing|paused>[, controls off])`, `media: the chip is hidden
(no session shown)`, `media: the card opened (...)`, `media: the card closed (<why>)`, the dry-run line above,
`mantle: protected media apps: <apps>, rule <on|off> (<by>)`, `mantle: media pick dropped: <reason>`.

## Deviations

- `media.card {open}` is a native-only command: main.js read its own `barPopover.key` to let timeline-only changes
  through while the card is open; native's pop-ups live on the UI thread, so the bar tells the host.
- media.control's refusal is `CommandResult.Fail(reason)` (Electron resolved `{ok:false, reason}`); the bar reads it the
  same way (the note / the warn chip).
- Mantle Player's icon is not drawn yet (the app's letter stands in); Electron used Mantle's own player icon.
- BAR-907 (the chip drops its title when the icons do not fit): the chip's title has been hidden for good since
  2026-09-24 (bar.css `.media .mtxt { max-width: 0 }`), so native draws picture + play/pause only and has nothing to drop.

## better:

- (none claimed yet)

## Left (for the next part of the lane)

- Spotify: the idle Spotify chip and card (BAR-915..918), the card's Spotify section, queue, library, search, More,
  launch (BAR-927, BAR-965..1009). Needs the Spotify link (lib/features/spotify.js, spotify-app) first; tests must use a
  fixture link, never the real Spotify.
- Game mode (68 rows, lib/features/gamemode.js, _gamemode-plan.js, ui/bar/gamemode.js) and its QS row; the rest rows
  (BAR-717..729) are built by core-6's RestCoordinator and only need their coverage confirmed and marked.
- Downloads (41 + 1), Helm badge (11), Quick links (9), Badges (4).
- The screenshot pair script `native/tools/parity/media/` (media widget + game mode, light and dark) and its ci.ps1 stage.

## State at handoff (part 1)

- bar.md: 24 rows `tested:` (unit tests, green in cloud runs: unit 638/638); 44 rows `ported:412781b`. The 22 rows
  the UI tests cover (Mantle.UiTests/Media/MediaTests.cs) are still `ported:` because no UI run of the final tip has
  been read yet: the last full run (GitHub run 43004490866) lost contact on an API rate limit. Earlier UI runs passed
  the chip, toggle dry run, card, transport and note checks; the one remaining failure was the source-row pick (the
  tests now click again when the card refuses a click because the list just moved, and the card logs
  `media: the card picked <label> (<aumid>)`). Re-run, then flip those rows to `tested:` with the test names.
- Unit flakes seen in one run (not this lane's): LookShellTests.Another_apps_theme..., BannerCrashRestoreTests.
- previews failed once with light SSIM 0.8196 (known Electron reference flake; this lane does not touch previews).

## Part 2 (lane native-r7-media-2)

### What is built

- `Mantle.Core/Features/GameMode/GameModePlan.cs`: lib/features/_gamemode-plan.js, the PURE half of game mode, whole:
  detectGame, step (auto on/off, AWAY_MS, suppress), runningGames (javaw memory, own session, test stand-ins), gameLabel,
  restStep, restIsGame, plan (the never-touch list, the game's tree, sound, elevated, Windows' own, pending), names,
  leftByWhy, trimCalls, trimPlan, cpuBase, biggestApp, bytes, appCount, boostLine. Tests: GameMode/GameModePlanTests.
- `Mantle.Core/Features/Downloads/DownloadsList.cs`: lib/features/_downloads-list.js (shelf, skipReason, arrival, fsWhy,
  JARGON, the plain read over a real folder) and ui/bar/downloads.js's pure words (ago, size, meta, kindOf, shown,
  sentence). Tests: Downloads/DownloadsListTests (the plain read runs on a temp folder, never the user's Downloads).
- `Mantle.Core/Features/Badges/HelmBadge.cs`: lib/helm-badge.js's pure half (parse, matches, describe, cleanSources,
  defaultSources with MANTLE_HELM_BADGE_FILE) plus the disc's text and ink. Tests never read the real Helm file.
- `Mantle.Core/Features/QuickLinks/WinXLinks.cs`: lib/quick-links.js (rows, letters, submenu, byKey, byId).
- `tools/parity/media/`: the now-playing card pair (electron-media.cjs + run-media-parity.ps1) and ci.ps1 stage
  `media` (the block marked "stage media"); `mark-rows.cjs` marks bar.md rows from a JSON map.

### Ledger rules used

Rows whose rule is ported and unit-tested but which no running feature uses yet (game mode, Downloads, the Helm badge,
quick links: no service, no surface) stay `todo` with a note naming the class and test, so the ledger never says native
does something it does not. Only rows a passing test covers end to end are `tested:`.

### Results

- Run 43012883434 (build, unit, ui, media): `NATIVE-CI: ok (4 ok, 0 skipped)`; unit 667/667, ui 49/49 (the three
  MediaTests among them), media light 0.5596 / dark 0.6158.
- Fixed on the way: the card's source list read its place before layout at paint, so the first click after a paint
  was refused ("the list moved 0 ms ago"); it now reads it after each layout pass too (MediaCard.cs).
- bar.md: 21 media rows flipped `ported:` -> `tested:` (MediaTests); the rest rows BAR-717..721, 723, 724, 727..729
  `tested:` against core-6's RestCoordinatorTests / ShellRestPartsTests / FeatureHostTests / RestTests. Not marked:
  BAR-722 (a slow or throwing onRest handler: no test), BAR-725/726 (the edge and hover poll during a game rest: bar
  shell). 43 rows `todo` with the class and test of their ported rule (game mode 27, Downloads 8, Helm badge 6, quick links 2).
- The pair (tools/parity/media): the card's layout, art box, head lines, app line and transport match; the gap is
  text rasterisation, and the progress reason's size (11.5 -> 12.5 px, line-height 1.45 as bar.css after that run).
  The dark toggle's sunk fill reads a little darker natively. After the size fix (run 43014608940): light 0.7717, dark
  0.7190; floors light 0.74, dark 0.69.

### Left (part 3)

- Game mode as a feature: the service (lib/features/gamemode.js: the scan tick, power and Energy saver steps, easing
  and restore through the helper, the restore record and crash put-back, the boost's timing, Lively, the rest
  hand-off to RestCoordinator) and the quick-settings tile (ui/bar/gamemode.js). Every Windows change a dry run in
  --test; MANTLE_GAMEMODE_TEST_GAME stand-ins. Then the game mode half of the screenshot pair.
- Downloads: the feature (folder watch, seenAt, downloads.* commands, the token rows, dry-run open/reveal) and the
  shelf pop-up; DownloadsList is its pure half.
- Helm badge: the file watch and the disc on bar cells (HelmBadge is its pure half; tests point
  MANTLE_HELM_BADGE_FILE at a fixture, never the real Helm). Quick links: Win+X mode in the launcher (WinXLinks).
- Spotify (unchanged from part 1).

## Part 3 (lane native-r7-media3)

### What is built

- `Mantle.Core/Features/GameMode/GameModeFeature.cs`: [Feature("gamemode")], lib/features/gamemode.js around the pure
  GameModePlan: the scan tick (3 s idle, 5 s on, 5 s resting, 12 s backstop; one at a time), the power and Energy saver
  steps, easing through gamemode.apply (chunks of 40), the 30 s replan, the boost (baseline at on, one trim through
  gamemode.trimbg a minute in, never twice per game; the mid-game switch puts the eased apps back), wallpaper hold and
  Lively, the restore record (`<data root>\gamemode-restore.json`, through the ONE write gate), crash put-back at the
  first ready, what could not be put back kept and tried every 30 s (20 tries), the shared game watch it drives
  (GameWatch.Drive / Whole / Publish), and the rest hand-off (IFeatureHostServices.RestAsync -> RestCoordinator).
  Commands gamemode.state / set / auto / liftSaver / boost; the event `gamemode.state` (replayed). Options auto,
  liftSaver, boost. `GameModeJson.cs`: the scan reply read into the plan's records, applyItem / restoreItem / recordEntry.
- `GameModeView.cs`: ui/bar/gamemode.js view() (pure). `Mantle.App/Surfaces/Bar/GameMode/GameModeRow.cs`: the row in
  quick settings (before the notes, as bar.js attaches it); hook: four lines in QsPanel.cs (marked "game mode").
- Wiring: App.xaml.cs "game mode wiring" (one Configure line: data root, own pid, the write gate).
- The pair: tools/parity/media now also shoots the Game mode row (head + switch line) from Electron's mock (?gm=off)
  and native; ci.ps1 stage media gates on 4 of 4 and the `gamemode light/dark` floors.

### A --test run never changes Windows

- Every write (gamemode.apply / power / saver / trimbg, Lively's command) is logged as
  `gamemode: dry run (--test): would ask the sidecar <verb> {...}; nothing was sent` and never sent; reads are reads.
- Deviation: Electron skipped the crash put-back in a test run. Native runs it as a DRY RUN against the TEST profile's
  record (refused outright if the data root is %APPDATA%\mantle) and drops that record, so a test proves it:
  `gamemode: test run: the restore record was read from the test profile and dropped; nothing was sent`.
- MANTLE_GAMEMODE_TEST_GAME (test only): stand-in exe names counted as known games running (the rest end to end).
- The wallpaper hold is not wired natively yet (the desktop layer is another lane's), so the Wallpaper row says
  "this version of Mantle cannot pause its wallpaper for games" (blocked), never a silent skip.

### UI Automation ids (the row)

`qs.gamemode` (group), `qs.gamemode.sub`, `qs.gamemode.switch` (Name "Game mode: Turn on|Turn off", ItemStatus
`on|off[; pending][; dead]`), `qs.gamemode.more` (ItemStatus expanded/collapsed), `qs.gamemode.why` (ItemStatus `warn`),
`qs.gamemode.list`, `qs.gamemode.row.<key>` (plan rows `qs.gamemode.row.plan.<label-with-dashes>`; ItemStatus = status),
`qs.gamemode.last`, `qs.gamemode.opts`, `qs.gamemode.opt.auto|liftSaver|boost`.
Log: `qs: game mode row: switched on|off (...)`, `qs: game mode row: details open|closed`, `gamemode: on (<how>[, DRY RUN]): ...`,
`gamemode: off: <why>`, `gamemode: restore (<why>): <text>`.

### Results (part 3)

- Run 43037040342 (build, unit, ui, media; the unit project did not build there): media 4 of 4, the Game mode row's
  pair light 0.7076 / dark 0.6493 (floors 0.68 / 0.62); the card pair unchanged (0.7717 / 0.7190).
- Run on d329b870 (build, unit, ui): NATIVE-CI ok (3 ok, 0 skipped); unit 731/731 (GameModeFeatureTests, 11),
  ui 58/58 (GameModeTests, 2).
- bar.md: 17 game mode rows tested (BAR-751, 758, 761, 762, 768, 769, 770, 775, 782, 783, 794, 795, 797, 799, 801,
  805, 807). The other game mode rows stay todo: the service and row now do them, but no test checks that row yet
  (for example BAR-781 the 30 s replan, BAR-786 the waiting line, BAR-808 the double click, BAR-812/813 every Details
  row, BAR-815 Escape, BAR-818 reduced motion).
- The pair's gap: text rasterisation, the native track and knob drawn as Borders (no knob shadow), the dark switch fill.

### Left (part 4)

- Downloads (feature + shelf pop-up; DownloadsList is its pure half), the Helm badge (file watch + the disc on bar
  cells), quick links (Win+X mode in the launcher, WinXLinks), Spotify: not started in part 3.
- Game mode: the wallpaper hold (needs the desktop layer's hold), tests for the rows above, Escape in Details.

## Part 4 (lane native-r7-media4)

### What is built

- `Mantle.Core/Features/Downloads/DownloadsFeature.cs`: [Feature("downloads")] (lib/features/downloads.js). The folder is
  read in-process (DownloadsList.ListFolder with attributes: Hidden/System left out) off the host thread; ONE
  FileSystemWatcher (700 ms quiet, 4 s cap, partial-file changes ignored); the 2-minute retry only while the folder is
  missing; seenAt (first run = now, a future value reset, an unsaveable one noted); commands downloads.list / seen / open /
  reveal / folder / drag by opaque tokens; the event `downloads.state` (replayed); Rest = true. Logs counts only.
- `Mantle.App/Surfaces/Bar/Downloads/`: BarWindow.Downloads.cs (the button bar.downloads: hidden off, muted bad/resting,
  the dot, the warn chip "downloads off"; open re-reads then marks seen; the keyboard ask; arrows / Home / End / Esc) and
  DownloadsShelf.cs (the panel). Hooks: `x:Name="DownloadsBtn"` (BarWindow.xaml), `StartDownloads()` (BarWindow.xaml.cs),
  one AttachDownloads line in App.xaml.cs's media wiring.
- `Mantle.Core/Features/Badges/HelmBadgeFeature.cs`: [Feature("helmbadge")] (lib/helm-badge.js feature + createWatcher).
  Event-driven only: a folder watch (tmp + rename), 120 ms settle, the writer's exit HEARD (Process.Exited) instead of
  Electron's 30 s pid re-check, a missing folder waited for by a watch on its parent (the 30 s retry only when the
  parent is missing too). `Mantle.App/Surfaces/Bar/Badges/BarWindow.HelmBadge.cs`: CellVm's badge half (partial) and
  the paint over the strip cells. Hooks in the strip's files: the disc (two Borders, accent / warn) in the cell template
  (BarWindow.xaml), `BadgeLine` in CellVm.Tip and `BadgeStatus` in its status, `PaintHelmBadges()` after SyncCells
  (BarWindow.Tasks.cs, three lines); one AttachHelmBadge line in App.xaml.cs.
- The qs pair (tools/parity/quick-settings): part1 removes Electron's .gmrow, so MANTLE_TEST_QS_NO_GAMEMODE=1 (test only,
  set by run-qs-parity.ps1 in part1) leaves the Game mode row out natively too (QsPanel.cs, the game mode lines).

### A --test run

- Downloads: open / reveal / folder are dry runs (`downloads: dry run (--test): would ...; nothing was opened`); the
  folder is MANTLE_DOWNLOADS_DIR only. Without it a test run reads NO folder (never the real Downloads) and the foot says
  "Test run: MANTLE_DOWNLOADS_DIR is not set, ..." (a deviation from Electron, which read the real one).
- Helm badge: only MANTLE_HELM_BADGE_FILE or the test profile's own `sources` setting; without them no file is read.
- MANTLE_TEST_DL_KEYS=1 (with MANTLE_TEST_VISIBLE=1, a CI runner only) lets a test shelf take the keyboard.

### UI Automation ids

Downloads: `bar.downloads` (Name = the tooltip; ItemStatus reading|ok|new|off|resting), `bar.downloads.dot`, `dl.panel`,
`dl.title`, `dl.sub`, `dl.list` (HelpText = why the arrows do not reach it), `dl.row.<n>` (Name = "name, new, kind,
size · age"; HelpText = the tooltip; ItemStatus fresh / busy), `dl.reveal.<n>`, `dl.empty` (ItemStatus reading|bad|
resting|empty), `dl.empty.big`, `dl.empty.sub`, `dl.path`, `dl.next`, `dl.note`, `dl.detail`, `dl.folder`.
Log: `downloads: N items, M new[, K downloading] [why]`, `downloads: the shelf opened|closed (...)`,
`downloads: the shelf shows N rows (...)`, `downloads: <verb> was a dry run|done`.
Helm badge: a cell's ItemStatus ends "; badge N[ warn]"; log `helmbadge: source N: count C`, `helmbadge: bar.app.N shows ...`.

### better:

- BAR-900 / BAR-901: the folder is read in-process with its attributes from start: no helper-down "simpler list" where
  hidden files could show, and the first read is not held for the helper or 10 s.
- BAR-1032: the Helm badge has no 30 s timers: the writer's exit is heard and a missing folder is waited for by a watch,
  so nothing runs while the file does not change.

### Results (part 4)

- Run 43063262898 on 0c3596f7 (build, unit, ui): NATIVE-CI ok (3 ok); unit 816/816 (DownloadsFeatureTests 9,
  HelmBadgeFeatureTests 7), ui 66/66 (DownloadsTests 2, HelmBadgeTests 1; the idle-CPU test among them).
- bar.md: 56 rows marked (Downloads 42, Helm badge 14).

### Left (part 5)

- Quick links (Win+X mode in the launcher; WinXLinks is its pure half): not started (needs the launcher's mode and the
  run lane's exec; BAR-1037..1045 stay todo).
- Game mode leftovers: UI tests for BAR-781, 786, 808, 812/813, 815, 818; Escape in Details; the wallpaper hold.
- Helm badge: the "+N" overflow's sum and tiles (BAR-257, BAR-1030); the keep-on-top pin moving top-left when a count
  shows (Electron's .cell.hb-on .ontop-badge).
- Downloads: keyboard walk, double-click guard, drag-out and the live open / reveal are ported but untested.

## Part 5 (lane native-r7-media5)

### What is built

- Quick links (Win+X), BAR-1037..1045:
  - `Mantle.Core/Features/QuickLinks/QuickMenu.cs` (pure): the rows in Windows' groups (Tools / Open / Power and desktop),
    the Shut down or sign out submenu, the underlined-letter keys, ask-first arming (the first press arms for 5 s, the
    same letter or Enter does it, nothing confirms within 250 ms), the once-per-decision guard (in flight or within
    500 ms), the hint keys and the placeholder "Quick links: press a letter".
  - `Mantle.Core/Features/QuickLinks/QuickLinksFeature.cs`: [Feature("quicklinks", Chords Win+X, Rest)] and
    `QuickLinkPlan` (pure): what each row id runs. Settings rows go through LauncherLaunch (Windows Settings, with its
    "Mantle Settings is not running" words); Task Manager / File Explorer open Windows' own (Mantle's are not in the
    native build); the system tools by ShellExecute; Terminal is wt.exe or Windows PowerShell with Electron's words,
    (Admin) with Verb runas; Desktop is the sidecar's hotcorners.desktop. `quicklinks.power {action, confirmed}`:
    shutdown.exe /l, /h, /r /t 0, /s /t 0, and SetSuspendState(FALSE, FALSE, FALSE) for Sleep; ask-first rows refuse
    without confirmed.
  - `Mantle.App/Surfaces/Launcher/LauncherSurface.Quick.cs`: the mode drawn in the launcher Mantle already has (no
    new window). Win+X over Quick links closes it; over the plain launcher it switches. Hiding returns to apps mode.
    Search opens the launcher's search. Run says it is not in the native build yet (no Win+R lane natively).
  - The launcher's main file (`LauncherSurface.cs`) gets `partial` and twelve one-line hooks: Create, Show, Hide,
    Destroy, RunSearch, Apply, FillRow, Select, RenderHint, OnKey and Activate.
  - No timers at rest: the 5 s arm timer runs only while a row is armed, the 1.4 s hide only after a live action.
- Game mode:
  - BAR-815 ported: Esc inside the open Details closes it, gives Details the focus back and is handled before the
    pop-up's own Esc, so quick settings stays open.
  - BAR-781: the replan interval is `Host.ReplanMs` (default 30000). A test run reads MANTLE_TEST_GAMEMODE_REPLAN_MS
    (at least 500) and logs each replan.
  - BAR-808: `GameModeView.SwitchTakes`: a second click within 500 ms is the same decision. A dry run answers in
    milliseconds, so the in-flight flag alone let a double click switch twice.
  - Shared file `QsPanel.cs`, one line: a test panel passes `_room.TakeKeys` to the row, so a test can press Esc in
    Details (MANTLE_TEST_QS_KEYS=1).
- Downloads: `Mantle.Core/Features/Downloads/DownloadsKeys.cs` (pure): the Up/Down/Home/End walk (it was inline in the
  shelf) and the one-press guard (in flight, or the same row's same action within 500 ms). In a test run the shelf logs
  `downloads: key <K>: row N of M`.

### Deviations

- Shut down runs `shutdown /s /t 0` without Electron's `/hybrid`-when-Fast-Startup probe (a full shut down).
- Quick links rows use the launcher's letter tile, not their own icons.
- Run (Win+R) from Quick links says it is not in the native build yet.
- Mantle's Task Manager and Files are not in the native build, so those rows always open Windows' own app.

### Left (part 6)

- Quick links: the live runs (UAC, shutdown.exe, sleep, the desktop toggle) were never run: every run here is --test.
- Downloads: drag-out and the live open / reveal (a test run never opens a file); the "Reading your Downloads folder…"
  state shows only for the few ms of a read, so it is not caught in a UI test.
- Game mode: the wallpaper hold. BAR-818: native draws the switch, knob and chevron with no transitions, so reduced
  motion has nothing to turn off.
