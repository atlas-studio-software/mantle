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
