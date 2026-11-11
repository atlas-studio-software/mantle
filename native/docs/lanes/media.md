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
