# Lane native-bar-qs (R3-2): quick settings and power mode

Branch `native/r4-qs` from native/main 960bc48. Part 1 of the lane: the pop-up, the core tiles and sliders, power mode,
and the `qs.*` commands the OSD lane waits on. Part 2 starts at "Part 2 starts here" below.

## What is built (part 1)

- `Mantle.Core/Features/QuickSettings/`
  - `QsParts.cs`: main.js QS_PARTS / qsNormalize / qsCompose / QS_WRITE / QS_LEVEL, pure.
  - `QsService.cs`: the host-thread store (qsLatest/qsStore), qs.get (cached once all six parts were read), qs.set (the
    reply is the read-back), qs.watch (the sidecar's `qswatch` lease, renewed every 20 s; a 2 s re-read when the sidecar
    cannot watch), qs.audio.sessions, qs.audio.session. Every set is one `mantle: qs.set ...` line (SetLog, a dragged
    slider summed). Publishes `"qs"` (QsSnapshot) 8 ms after a store, as main.js broadcasts.
  - `QsCommands.cs`: `[Command]` qs.get, qs.set, qs.watch, qs.audio.sessions, qs.audio.session (generated catalog).
  - `QsBattery.cs`: battery.js describe() footer and tip; `QsView.cs`: bar.js paint() tile words, bt.js tile(), the notes.
- `Mantle.Core/Features/PowerMode/PowerModeFeature.cs`: `[Feature("powermode")]`, lib/features/powermode.js: commands
  powermode.get / powermode.set / powermode.plan, the sidecar's powermode.watch on every ready, event `powermode.state`.
- `Mantle.App/Surfaces/Bar/QuickSettings/`
  - `QsPanel.cs`: the panel (bar.js buildQsPop, bar.css .qs*, powermode.css .pm*), code-built, theme tokens read per paint.
  - `BarWindow.QuickSettings.cs`: Volume, Network and Battery open it (bar.js openQs anchors), key `qs`, instant (parked
    between opens), no focus, `away`; the height is measured before each open.
- Hooks outside the lane's folders (small, commented): `BarWindow.xaml` (x:Name on the Volume and Network buttons),
  `BarWindow.xaml.cs` (one line after StartFrame), `App.xaml.cs` (block "quick settings wiring" after StartSurfaces).

## A --test run never changes the runner

- qs.set is a DRY RUN: checked exactly as a live set, logged as
  `qs: dry run (--test): <part>=<value> would ask the sidecar <verb> <args>; nothing was sent, Windows was not changed`,
  never sent; the part then reads the asked value marked `dryRun: true` (so a panel under test moves). A part that is
  not read, not ok or has no adapter is refused with the words a live sidecar would give.
- qs.audio.session: logged, never sent (a passive sidecar does not refuse the `audio` verb, nor `volume`).
- powermode.set / powermode.plan go with `dryRun: true` (the sidecar checks everything and writes nothing); a change it
  would have made answers ok:false "this is a test run, so Windows was not changed" (Electron's words).
- The panel's ways out (Energy saver, the gear, "Windows quick settings", a Bluetooth radio Mantle cannot switch) open
  nothing in a test run: `qs: dry run (--test): would open <what> (<target>); nothing was opened` and a note.
- `MANTLE_TEST_QS` (test runs only): JSON with any of volume, wifi, bluetooth, brightness, nightlight, power, `sessions`
  and `powermode`; read in place of the sidecar so every runner shows the same panel (UI tests, screenshot pairs).

## UI Automation ids (request: copy into Mantle.App/AUTOMATION.md, the core lane's file)

| AutomationId | element | notes |
|---|---|---|
| (top-level window) `Mantle pop-up: Quick settings` | the pop-up | instant: parked (cloaked) between opens |
| `qs.panel` | the panel's root (Group) | Name `Quick settings` |
| `qs.tile.wifi`, `qs.tile.bluetooth`, `qs.tile.nightlight`, `qs.tile.saver` | the tiles (Button) | HelpText = the sub line (`Off`, `Home · 5 GHz`); FullDescription = the tooltip; ItemStatus = `on`/`off` + `; dead` + `; pending` |
| `qs.brightness`, `qs.volume` | the sliders (Slider, 0-100) | ItemStatus = the number shown; HelpText = why when dead |
| `qs.brightness.value`, `qs.volume.value` | the numbers (Text) | `62%`, `—`, `unavailable` |
| `qs.mute` | the mute button | Name `Mute`/`Unmute`; ItemStatus `muted`/`unmuted` |
| `qs.notes`, `qs.note.<n>` | the notes (one per dead part or refused set) | |
| `qs.powermode`, `qs.powermode.<efficiency/balanced/performance>`, `qs.powermode.why` | the power mode row | ItemStatus `chosen` and/or `locked` |
| `qs.power` | the footer's battery line (Text) | battery.js footer words |
| `qs.windows`, `qs.gear` | the footer's two buttons | |

Log lines the tests read: `qs: the panel opened from <anchor id> (<w>x<h> DIP)`, `qs: the panel closed; its watch is let
go`, the dry-run lines above, `mantle: qs.set <part>=<value> -> ok, reads <v> (<ms> ms)` / `-> NOT DONE: <why>`.

## Requests (to other lanes)

- **Core (Shell/Popup)**: (1) a closed event or callback on PopupHost (the panel polls `Popups.OpenKey` every 250 ms
  while open to let its watch go); (2) `PopupHost.Resize(heightDip)` for an open pop-up, Electron's `bar.popover
  {height}` re-ask (the panel's height is measured once before each open; a note that arrives while it is open can be
  cut off at the bottom); (3) copy the id table above into AUTOMATION.md.
- **Core (ci.ps1)**: wire `native/tools/parity/quick-settings/run-qs-parity.ps1` as a stage (see that script's header).
- **OSD lane**: `qs.set` and `qs.audio.sessions` (and `qs.audio.session`) are registered. A refused or invalid set is
  `CommandResult.Fail(reason)`, as `OsdSurface.Set` already reads it; a success is `{ok, part, value, state, reason,
  dryRun, ms}`. Session rows carry `icon: null` with an `iconReason` (icons not read yet).

## Deviations

- qs.set's refusal is `Fail(reason)` (Electron resolved `{ok:false, reason, state}`); the refused switch's read-back
  still reaches the panel through `"qs"`.
- The power mode event is `powermode.state` (Electron `powermode`): a native feature's events must start with its name.
- A hovered tile that is on shows the hover token, not the accent (Electron keeps the accent under the pointer).

## better:

- (none claimed yet)

## Screenshot pair

`native/tools/parity/quick-settings/run-qs-parity.ps1` (+ `electron-qs.cjs`): see its header. Not run yet at handoff.

## State at the part-1 handoff (context ceiling reached)

- Commit 1d9344e (code + tests) pushed; the parity scripts and this doc in the commit after it.
- First cloud run (build,unit,ui) was still running at handoff: its result decides whether anything above compiles.
- bar.md status column NOT updated yet: mark rows only after a green run. Planned mapping (tests in brackets):
  tested: BAR-548, 560, 568, 640, 656, 657, 658, 659, 661, 667, 668, 669, 670, 671, 673, 676, 680
  [Mantle.Core.Tests/QuickSettings/QsTests.cs], BAR-746, 748 [Mantle.Core.Tests/PowerMode/PowerModeTests.cs],
  BAR-534, 567, 732, 733 [Mantle.UiTests/QuickSettings/QuickSettingsTests.cs]; ported: BAR-540, 546 (left click
  only), 553, 554, 555, 557, 558, 559, 562, 563, 564, 565, 569, 570, 571, 652, 660, 663, 672, 675, 684, 690 (no icons),
  691, 735-737, 743, 747, 749, 750. Not done: holders (677-679), restart re-take (683), net fast re-check (682).

## Part 2 starts here

1. The three "&gt;" views (ui/bar/qs-more.js): the Wi-Fi network list + scan + connect, with the password field in a
   non-activating pop-up (Q-W6); the Bluetooth device list (qs.bt.*); the sound output list (qs.audio.devices /
   qs.audio.default) and the per-app rows (qs.audio.sessions / qs.audio.session).
2. The qs-tiles row (airplane mode, do not disturb, focus, project, cast), game mode, top bar and hot corners rows.
3. The network readout's 1 s re-check while open (main.js `net {op:'watch', fast}`), the `volume` broadcast for the
   bar's speaker glyph, and the power plan list of the power mode row (powermode.plan is registered; its UI is not).
