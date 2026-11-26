# Lane r15-quick-settings: quick settings restyled to the approved prototype

Branch native/r15-quick-launcher (shared with the launcher restyle). Spec, matched and not redesigned:
apps-r3/mantle-bar panels/quick.js + quick.css (the layout), look.css / look.js, NOTES.md; light: apps-r3/light-mantle
(light.css, look.css, NOTES.md) and apps-r3/LIGHT.md. Not built or run on the authoring machine (no .NET SDK): the cloud
build and the UI tests are the first time any of it runs. Nothing here has been seen on a screen.

## What changed
- **Islands look only.** Everything below runs when `BarGeometry.Islands` (not `MANTLE_TEST_GLASS=electron`). The Electron
  look keeps the old full layout and the Lg* tokens untouched, so the Electron pairs still compare like with like.
- **Levitated.** `BarWindow.QuickSettings.cs` opens the pop-up with `PopupKind(..., Levitate: QsLook.Islands)`: the host
  draws the island (no ring, no tint, sheen on hover). Width 360 (fixed, not content-sized).
- **Size.** The main view's own height (measured alone), at least 240; the host's `PopupLevitate.Place` caps it at the
  monitor less 80, the content scrolls inside. Every view (Wi-Fi, Bluetooth, sound, More settings) shares that box: the
  pop-up is no longer re-sized to 360 + padding when a view opens.
- **Motion.** View change = sink (opacity 0 in 200, translateY 12 + scale .955 in 300) and lift (from translateY 28 +
  scale .965: opacity 240 after 40, transform 460 after 20); reduced motion: 120 ms fades. A press scales a control to
  .985 in 160 ms. Transform and opacity only. Easing is a cubic ease-out for both (the prototype's lift has a slight
  overshoot, not copied).
- **Inks.** `QsLook.Ink` turns each Lg* name the panel uses into its LvBar* twin (Theme/Levitate.xaml, both themes) and
  the accent into `IslandsInk.Accent`; LgInk3 becomes text-2 (readable text is never text-3). The Game mode and Hot
  corners rows get the same inks through the brush function they are given (their own hover style still uses Lg tokens).
- **Numbers.** Theme/LvQuick.xaml (x:Double / Thickness, plus `LvQsOnSubInk` and `LvQsHoverOver` per theme), read by
  `QsLook.N` / `QsLook.T`, pinned by Mantle.Core.Tests `Look/QuickSpecTests.cs` with their quick.css / look.css /
  light.css lines.
- **Sparse layout** (`QsPanel.MoreSettings.cs`): main view = tiles, Brightness, Volume (mute + "›" to sound), notes, a
  hair, ONE row "More settings", footer. The row goes one level down to a view with a back arrow and the title "More
  settings" holding Power mode, Game mode, Hot corners; no footer there.
- **Offline.** `MANTLE_TEST_NET=off` (BarWindow.Network.cs) also sets `QsPanel.ForcedOffline`: the Wi-Fi tile reads a
  connected part as not connected, so it draws on, "Not connected", one bar lit and the rest at .32, the same state as
  the bar's offline network button. Real offline (no network the system knows) already agreed: both read the same
  Wi-Fi part.

## Prototype parts native lacks
- **Top bar row** (one level down): native quick settings has no top bar row, so the More settings row's sub line says
  "Power mode, game mode, hot corners" (not the prototype's four). UI test `More_settings_view_has_a_top_bar_row` is an
  Assert.Inconclusive until one is built.
- The footer's battery glyph, the slider thumb's shadow, the power mode's sliding highlight (the chosen segment is filled
  at once), the keyboard focus ring and the accent tick on rows (the panel never takes the keyboard), the tiles' 20 px
  glyphs (native's are 16 px glyphs centred in the 20 px column). The Wi-Fi list one level down still shows the fixture's
  network as connected under `MANTLE_TEST_NET=off`.
- All nine tiles exist (Wi-Fi split, Bluetooth split, Night light, Energy saver, Airplane mode, Do not disturb, Focus,
  Project, Cast).

## UIA ids (new)
`qs.more.row` (Button, Name "More settings", HelpText = its sub line), `qs.view.more-settings` (the view, Name "More
settings"), `qs.more.back` (Name "Back to quick settings"), `qs.more.title`. Every earlier id keeps working; power mode,
game mode and hot corners ids are now inside `qs.view.more-settings`, so they exist in the tree only while it is open.

## Needs a change outside this lane (not made)
The sparse layout moves Game mode and Hot corners one level down, so these UI tests outside QuickSettings/ look for them
on the main view and will fail in the islands look until they first press `qs.more.row` and wait for the log line
`qs: the More settings view opened`:
- `tests/Mantle.UiTests/Corners/CornersRowTests.cs` (after the open at line 36),
- `tests/Mantle.UiTests/GameMode/GameModeTests.cs` (after the open at line 29),
- `tests/Mantle.UiTests/GameMode/GameModeDetailsTests.cs` (after the open at line 33),
- `tests/Mantle.UiTests/Startup/EarlyClickTests.cs` (before `qs.corners.sub` at line 36).
The layout commit is the last one of the lane, so it can be held back on its own until those are moved.

## Parity (tools owner; not changed here)
The qs stage still passes as it is: `run-qs-parity.ps1` line 81 sets `MANTLE_TEST_GLASS = 'electron'`, and that face is
unchanged. It no longer shows what users see. Under plan v0.2 the restyled surface leaves the Electron compare:
1. `native/tools/parity/references.psd1`: add `qs = 'native-golden'` beside `bar` and `strip` (lines 15-16).
2. `native/tools/parity/quick-settings/run-qs-parity.ps1`: read references.psd1 as `run-parity.ps1` lines 52-70 do. For
   `native-golden`: skip `electron-qs.cjs` (lines 44-50), do not set `MANTLE_TEST_GLASS` (line 81), shoot native on the
   islands band and score each theme against `goldens/qs/<theme>.png` at the same thresholds, counted only once
   `goldens/qs/REVIEW.md` has non-empty reviewer:, run: and sha: lines; else print
   `QS-PARITY qs-<theme>: golden pending review (reference native-golden; candidate <png>)` and end with
   `QS-PARITY: FAILED (qs golden pending review: light, dark; candidates in <out>)`, as run-parity.ps1 lines 405 and 443 do.
3. `native/tools/ci.ps1` `Stage-qs` (lines 512-532): take that verdict first, as the strip stage does (line 502), and keep
   the floors `qs light` 0.89 / `qs dark` 0.84 (lines 423-424) for the golden scores. Never loosened; goldens are made
   only after a side-by-side review.

## Not verified
The build, every test, and everything visual: sizes on screen, the levitated island around the panel, the inks in both
themes, the motion, the scroll at a short screen, and whether PopupHost.Resize keeps a levitated pop-up's place.
