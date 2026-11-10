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

- **Core (Shell/Popup)**: (1) and (2), the closed event and Resize, were done in part 2 (this lane, with leave to
  edit PopupHost); (3) copy the id tables into AUTOMATION.md is still open.
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

- Run 42778790583 (1d9344e): `NATIVE-CI: FAILED (unit, ui failed; 1 ok)`: the token lint (Colors.Transparent in
  QsPanel) and an InvalidCastException on the first open: the AOT build cannot cast XamlReader's result to
  Shapes.Path (it can to UIElement, as OsdSurface does). Fixed in 83b9283.
- Run 42780689327 (83b9283): unit `ok (556 of 556)`; ui `FAILED (1 of 35)`: only the launcher's
  `Typing_shows_the_fixture_rows_and_the_arrows_Enter_and_Esc_work` ("Down on the last row did not wrap", LAU-048),
  not in this lane's files; both QuickSettings UI tests and the bar/rest tests passed. Whether it is a flake or caused
  by this branch is NOT established.
- Run 42781742692 (8ff3d81, the full recipe): `NATIVE-CI: ok (10 ok, 0 skipped)`; ui 35 of 35 (the launcher test
  above passed, so it was a flake there); unit 556 of 556; bar parity unchanged (light 0.9966, dark 0.9916).
- bar.md: 23 rows `tested:` and 30 rows `ported:83b9283` (53 rows of the 221). Not done: holders (677-679), restart
  re-take (683), net fast re-check (682), and everything listed in part 2.
- The screenshot pair has not been run yet.

## Part 2 (branch `native/r5-qs2`, from native/main b6a318e)

Built:

- **The three "›" views** (`QsPanel.More.cs`, ui/bar/qs-more.js + bt.js): the Wi-Fi and Bluetooth tiles are split (the
  tile switches the radio, its "›" always opens the list); a "›" after the mute button opens the sound view. A view
  replaces the main one in the same pop-up, which is re-asked for 384 DIP (`PopupHost.Resize`); the back arrow, or the
  close, stops its one read loop (Wi-Fi 4 s, sound 1.5 s with devices every third tick, Bluetooth watch renewed every
  30 s or a 3 s re-read without it).
  - Wi-Fi: rows (signal, lock, name, sub), row bodies (Disconnect, Connect, Connect automatically, the password form,
    Windows' own list), the whole-view states, the last-attempt note, a scan on open.
  - Bluetooth: rows from bt.js describe(), the battery, Connect/Disconnect or the reason, "Add device", the states.
  - Sound: the output devices (check from the read-back, "Calls", tooltips), one row per app (level slider through a
    one-in-flight sender, mute), the states.
- **Core:** `QsMoreView.cs` (all the words, pure) and `QsService.More.cs` with the commands `qs.wifi.list / scan /
  connect / disconnect`, `qs.audio.devices / default`, `qs.bt.list / watch / connect / disconnect` (always
  `Success({ok, reason, ...})`, so the bus's failure log never holds a connect's arguments). The sidecar's `btdevices`
  pushes are relayed as the event `qs.btdevices`. A failed Bluetooth toggle never names a device (BAR-694).
- **The password field (Q-W6).** The pop-up stays no-focus. Connect on a network that needs a password asks
  `PopupHost.TakeKeyboard("qs")`: WS_EX_NOACTIVATE comes off, the window in front is remembered, the pop-up is raised.
  Cancel, a sent password, leaving the view or the close give it back (`GiveBackKeyboard`: NOACTIVATE back on, focus
  to the remembered window if the pop-up still has it). Another window taking focus raises `KeyboardLost`: the box
  closes with "The keyboard went to another window, so the password box closed." (When that window is Mantle's own,
  a press on the bar, the remembered window still gets the keyboard back on the close.) The password is read once on Next or
  Enter, the field cleared in the same step, sent in one call; the service logs its LENGTH only.
- **Watch holders (BAR-677..679)** (`qs.watch {on, who}`) and the **re-take after a sidecar restart (BAR-683)**.
- **PopupHost** (item 4): the `Closed(key, why)` event (the 250 ms poll is gone) and `Resize(key, heightDip)`; the
  main view re-asks its height when its notes change (a late note is no longer cut off).

A --test run: every write above is a dry run, logged as `qs: dry run (--test): <what> would ask the sidecar <verb> <op>
<args, names and ids as their length>; nothing was sent, Windows was not changed`, answered ok:false "this is a test run,
so Windows was not changed" (shown under the row). The keyboard is refused with Electron's words unless
`MANTLE_TEST_QS_KEYS=1` (with MANTLE_TEST_VISIBLE=1, a CI runner only). `MANTLE_TEST_QS` may carry `wifiList`,
`devices` and `btList` (and part 1's `sessions`).

New ids: `qs.more.wifi`, `qs.more.bluetooth`, `qs.more.sound`; `qs.back`, `qs.view.title`, `qs.view.status`,
`qs.view.list`, `qs.view.state` / `qs.view.why` / `qs.view.action`, `qs.view.note.<n>`, `qs.view.more`;
`qs.wifi.scan`, `qs.wifi.net.<i>` (HelpText = the sub line), `qs.wifi.connect`, `qs.wifi.disconnect`, `qs.wifi.auto`,
`qs.wifi.password`, `qs.wifi.problem`, `qs.wifi.next`, `qs.wifi.cancel`, `qs.wifi.error`, `qs.wifi.windows`;
`qs.bt.dev.<i>` (ItemStatus = state), `qs.bt.toggle`, `qs.bt.settings`, `qs.bt.error`, `qs.bt.add`;
`qs.sound.devices`, `qs.sound.dev.<i>` (ItemStatus `default`), `qs.sound.error`, `qs.sound.mixer`,
`qs.app.<i>` with `.value`, `.volume`, `.mute`. Log lines: `qs: the <title> view opened` / `closed (<why>)`,
`qs: the password field has the keyboard (...)` / `did not open: <why>`, `pop-up Quick settings: resized to <h> DIP
high`, `took the keyboard on its own ask`, `gave the keyboard back (<why>)`; the close line is now `qs: the panel closed
(<why>); its watch is let go`.

Deviations (part 2):

- The views' writes answer ok:false + dryRun in a test run (Electron's passive sidecar refused them); per-app rows draw
  a letter, not the app's icon (icons are not read yet; the letter's tooltip says so).
- Bluetooth kind glyphs are path approximations of bt.js's rects and circles (square corners).

Left (not done in part 2):

- The qs-tiles row (airplane mode, do not disturb, focus, project, cast), game mode, top bar and hot corners rows.
- BAR-682, the 1 s network re-check while open: the native bar has no `net` readout watch yet to speed up.
- The `volume` broadcast for the bar's speaker glyph, the power plan list, right-click to open.
- The screenshot pair (part 1's script) and an AUTOMATION.md copy of the ids.

## Part 3 (lane native-r5-qs3, same branch, origin/native/stage = native/main 64f4eb3 merged)

The three red full runs of part 2 were not this branch's:

- 42792161352 `Theme_set_switches_the_bar...` ran before any quick-settings test (09:43:40; the first qs test 09:43:55),
  its log says the host went dark and only the pixels did not; the same "the bar never went dark" failure hit runs
  42769441389 and 42771856358 on toasts-lane snapshots without any PopupHost change.
- 42794684579 unit `LookShellTests.Another_apps_theme...` (1236 ms): Mantle.Core.Tests, no quick-settings code on its
  path; it failed the same way on native/main runs 42384431813 and 42390282384.
- 42797283215 `ClusterTests.Clock_time_box...`: a UIA timeout in `ElementFromHandle` at launch, the third UI test of the
  run, before any quick-settings code ran (nothing of this branch runs at start beyond two event subscriptions).
- The merged branch's full run 42800384215: unit 595/595, ui 45/45; it failed only `previews` (light 0.8196), whose
  ELECTRON reference crop has a black band over the flyout's top (the native crop is right).

The pair (Electron's part1 shot now keeps the "›" buttons native draws):

- Width: the main view is sized to its content as Electron's `.pop` is (max-content of its rows, min 268, max 360 with
  the split tiles; `PopupHost.Resize(key, h, w)`); a view keeps the panel's width (qs-more.js pins it, BAR-609).
- Chromium's line boxes (`Type`: font size + line-height, BlockLineHeight, whole pixels); dark uses Helm's type scale
  (13, tile sub 12), dead tiles `--lg-card`, the dark pm switch is Helm's `.seg` (field + hairline, 12/500, current
  `--lg-press` with a hairline).
- Sliders: Chromium's range look on the Slider's named parts (8 px track, accent fill to the thumb's centre, 16 px solid
  thumb). Deviation: Chromium's own track colours are not tokens; native draws them from `--lg-fill0`/`--lg-edge-out`
  and `--lg-ink-3`/`--lg-ink-2` at a fraction (`Alpha`).
- Power mode: `PowerModeView` (Mantle.Core/Features/PowerMode), a pure port of powermode.js view() (modes half): "When
  plugged in" / "When on battery", absent `settable` is settable, a refusal is the row's note (`qs.powermode.note`).
- Battery footer: battery.js's time left (`secondsLeft` / `minutesLeft`). The fixture was right; native ignored the
  field. A live sidecar's qs `power` part still carries no time (Electron words the `battery` reading; not wired).

Left: the qs-tiles row (airplane mode, do not disturb, focus, project, cast), game mode, top bar, hot corners, the
power plan list and toggle (BAR-740), the `battery` reading for the footer, AUTOMATION.md.
