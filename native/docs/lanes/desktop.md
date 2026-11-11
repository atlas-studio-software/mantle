# Lane native-r7-desktop: the desktop layer, its menu, and hot corners

Branch `native/r7-desktop`. The desktop layer (ui/desktop/, lib/windows.js createDesktop, main.js attachDesktop), the
desktop right-click menu (lib/features/deskmenu.js, ui/desktop/deskmenu.js) and hot corners (lib/features/_corners.js,
lib/corner.js), ported. Rows: docs/parity/shell.md DESK-001..127 and CORN-001..108. Built by lane native-r7-desktop
(uncompiled WIP), compiled, fixed and measured by lane native-r7-desktop-2.

## What is built

- `Mantle.Core/Features/Corners/`
  - `CornerRules.cs`: `_corners.js`: the four corners, the 6 DIP zone per display (fractional-scale last pixel, negative
    origins, the bottom-edge clip row), outer corners only across monitors, the dwell state machine (250 ms at rest
    fires once, leaving re-arms, a new watch starts disarmed, held buttons / clicks in the dwell disarm, unreadable
    cursor or buttons block in words), the check at firing (`FireBlocker`: no answer, lock screen, full screen, and a
    known-games hook the game-mode lane fills).
  - `CornerClaims.cs`: the TOP-297 claim table: `CornerClaims.Live.Claims(pt, display)`, `.Active`, `.Changed`; an
    active corner's square is the corner's, the edge band and the top-bar peek keep the rest.
  - `CornerOccupants.cs`: `lib/corner.js` (what occupies the bottom-right corner; listeners hear only real changes).
  - `CornerActions.cs`: Task view (Win+Tab's own feature, no key sent), Show desktop, Launcher, Quick settings, Lock
    screen; every failure is words, never a throw; `--test`: every action is a dry run.
  - `CornerPointer.cs`: Win32 cursor and buttons (GetCursorPos, GetAsyncKeyState, EnumDisplayMonitors); a `--test`
    run uses only a made-up test pointer.
  - `CornersFeature.cs`: `[Feature("corners")]`, `corners.state` / `corners.set`, the watch, the self-test
    (`--test` + MANTLE_CORNERS_SELFTEST=1, CORN-060).
- `Mantle.Core/Features/Desktop/`: `DeskWidgets.cs` (clock, stats, Mission Control probe words and cadence),
  `DeskStrip.cs` (Needs attention rows), `DeskMenu.cs` (rows, keys, placement), `DesktopLayer.cs` (attach words,
  remake verdicts, cluster geometry, page-rect checks), `DeskFixture.cs` (MANTLE_TEST_DESKTOP), `SysStats.cs`,
  `DeskMenuFeature.cs` (`[Feature("deskmenu")]`, `features\deskmenu.json`; the right-click hook is never armed in
  `--test`).
- `Mantle.App/Surfaces/Desktop/`: `DesktopSurface.cs` (`[Surface("desktop", AtStart)]` and `[Surface("deskmenu")]`),
  `DeskPanes.cs` (the four panes, code-built, every brush from Theme/Tokens.xaml), `DeskMenuWindow.cs`.
- App hook: the "desktop wiring" block in App.xaml.cs, before StartSurfaces.

## How it is tested

- Unit (Mantle.Core.Tests): `Features/Corners/CornerRulesTests.cs`, `Features/Corners/CornerActionsTests.cs`,
  `Features/Desktop/DesktopRulesTests.cs`.
- UI (Mantle.UiTests/Desktop/DesktopSurfaceTests.cs): the layer (made, shown, never attached, placed, drawn,
  themed, parked), the menu (opens through `--test-surface=show:deskmenu`, placed, rows, hides the widgets, keeps a
  failure up), the hot corners self-test (20 corner x action dry runs, a held button and a flick never fire, a game
  stand-in refuses, the real cursor never moves).
- Screenshot pair: `tools/parity/desktop/` (ci.ps1 stage `desktop`): Electron's ui/desktop/index.html at 960x540
  over a fake bridge (the fixture's stats, Mission Control answer and strip rows) with the clock frozen, against the
  native layer with MANTLE_TEST_DESKTOP = the same fixture; the System widget, the Needs attention strip and the menu,
  light and dark, over one colour. Run 43025045944 (SSIM, no masks; floors in ci.ps1 about 0.03 under):

  | shot | light | dark | what differs |
  |---|---|---|---|
  | System widget | 0.7171 | 0.7136 | the native pane is 3 px taller (128 vs 125); dark values are not semibold |
  | Needs attention | 0.5438 | 0.4635 | each native pane above it is 2-3 px taller (clock 94 vs 91, Mission Control 85 vs 83), so the strip's room is 4 px short: the list scrolls and its last line is cut |
  | Menu | 0.7285 | 0.7055 | no drop shadow under the native panel, 2 px taller (230 vs 228); dark row text is not semibold |

## Fixes made while compiling (lane native-r7-desktop-2)

- The layer was a white sheet on screen while UI Automation read every element (so the UI tests passed over it; the
  screenshot pair caught it). What fixed it: its presenter is no longer resizable (the one setting it did not share
  with the windows that draw, the menu and the OSD). An Activate on first show was tried too and dropped: it made
  the layer the app's front window ahead of the bar (ci smoke reads MainWindowHandle) and moved the foreground (bench
  focus checks). The menu's first show is an Activate that hands the foreground straight back (PopupWindow.Show).
- The strip's room (what the other three panes leave) was worked out once, while the Mission Control tile still had
  its 132 DIP frame, so the two rows were cut short when the tile shrank to its note; it now follows their sizes.
- The layer's root Grid had only an automation id, and a panel with no name has no automation peer, so the theme
  check could not find it: it is now named "Desktop layer".
- The hot corners UI test took the self-test's first line for its last; it now waits for "SELFTEST ok|FAILED".
- The EnumDisplayMonitors callback compiled and ran under Native AOT as written (a DllImport delegate); no clash in
  the surface's names.

## Deviations from Electron

- **Test layer background:** the `--test` layer is see-through instead of Electron's #1a1c20 stand-in (DESK-015): a
  colour literal is refused by the native lint, and see-through lets a test's own backdrop show, which the screenshot
  pair uses. It is also not resizable (Electron's is), see above.
- **The strip's rows (`better:`, DESK-089):** the strip shows `ShellStateHub.Problems`, the same list the tray menu
  reads, in the strip's order and words. So it also names problems Electron's strip did not know (the taskbar watch),
  and never calls a resting Win key a fault (problems() exempted it; Electron's strip did not).
- **Quick settings from a corner:** the action layer can open quick settings (CORN-053, tested with a fake), but
  nothing wires it to the bar's panel yet, so a corner set to Quick settings answers "this build of the bar cannot open quick settings from a
  corner yet" in words. CORN-053/054 stay todo.
- **Mixed-scale monitors:** a monitor's DIP origin is its physical origin divided by its own scale. Electron uses the
  same rule for a display at 0,0 and for same-scale neighbours, but mixed-scale neighbours can gain a gap or overlap of
  a few DIP between them, so an "outer" corner test there can differ from Electron's by those pixels. Not covered by a
  test (the runner has one monitor).
- **Widgets on/off** is kept in `features\deskmenu.json` (the feature's settings), not the page's localStorage (there
  is no page).

## Round 3 (lane native-r7-desktop-3)

- **Screenshot pair** (run 43042794903, SSIM, no masks; before = run 43025045944): System 0.8572 / 0.8346 (was
  0.7171 / 0.7136), Needs attention 0.6240 / 0.6796 (was 0.5438 / 0.4635), menu 0.9526 / 0.9496 (was 0.7285 / 0.7055),
  light / dark. Floors in ci.ps1 about 0.02-0.03 under.
- **Bench fix:** a live (or bench) layer is held hidden until it is under the icons; before, a refused attach (the
  bench's passive sidecar) left a full-screen window over every app and the bench's focus checks failed (run
  43038770664). Bench ok in run 43042794903.
- **Pane heights:** each native pane was 2-3 DIP taller than Electron's. Two causes: the Border's 1 DIP hairline took
  layout room (Electron draws it inside, ::after), so the padding now gives it back; and a 13 px Segoe UI line is 18 DIP
  in WinUI (DirectWrite rounds ascent + descent up) but 17 in Chromium (each rounded), so the date and the System values
  trim it below the line (`DesktopLayer.LineTrim`, unit-tested). The System rows also carry Electron's 1 px border-top
  inside the row, and dt / dd sit on one baseline.
- **Menu:** Bahnschrift (tokens.css --ui-font; the "bolder" text was this font, in both themes), padding 4 + the 1 DIP
  border, and deskmenu.css --dm-shadow drawn by `Osd/CssShadow` (the CSS formula into a bitmap) in the 20 DIP margin. The
  --lg-lip-up 1 px highlight under the top hairline is not drawn.
- **Menu keys (DESK-118..120):** `DeskMenuKeys`, a WH_KEYBOARD_LL hook on its own thread, armed only while the menu is
  open, hands it bare Up / Down / Home / End / Enter and swallows them with their key-ups (`DeskMenu.NavVerdict`, the
  sidecar's PopWatch rule); never while a Mantle window is in front. A --test run arms it only with
  MANTLE_TEST_DESKMENU_KEYS=1 (it then swallows too, so a test's Enter never reaches the runner's desktop). The highlight
  is the row's ItemStatus plus a UIA notification.
- **DESK-124:** the helper's fallback is said in words (`DeskMenu.Fallback`); a stand-down sets the health row and the
  hook re-arms after 30 s. The 1 s ack and the replay to Windows' menu are the sidecar's (DeskClick.cs).
- **ShellWatch:** a hidden top-level window on its own thread hears WM_DISPLAYCHANGE / WM_DPICHANGED / the work-area
  change and TaskbarCreated: the layer, a child of Progman once attached, hears neither. Tests post to this window only
  (found by its title in Mantle's process); nothing is broadcast and Explorer is never restarted.
- **DESK-016:** a burst of display changes is one fit now and one after 400 ms quiet; `FitVerdict` (skip in --test,
  when put back, or with no layer; later while held hidden); live: desktop {fit:true}, then `PageFill` (content vs the
  main display at its effective DPI), one 1 px resize to make it re-read the scale, words if that does not help.
- **DESK-011..013:** `DesktopSurface.Remake.cs`. A layer window that closes while Mantle runs is made again on the
  back-off (`Verdict`, at most 5 in ten minutes), held hidden until under the icons (fitted first if a display change
  waited), closed and never shown over the apps when the tries run out (`GaveUp`). Explorer back: try now / make / wait
  / recheck (`ShellBack`, one per 3 s). Put back from the tray: `DesktopSurface.Releases()` (desktop {detach:true}) runs in
  MoreReleases, and a layer closing then is not made again.
- **CORN-053/054:** `CornersFeature.ToggleQs` (App.xaml.cs) runs `BarWindow.ToggleQuickSettingsFromCorner` on the bar's
  thread (the panel on the right cluster, like Win+A twice); `CornerQs.Within` answers "the bar did not answer within
  1500 ms" and clips a bar's reason to one line of 160 characters.
- **Shared files touched:** App.xaml.cs (the desktop wiring block: Wire gets the tray's Paused, ToggleQs; MoreReleases
  adds the desktop detach), Surfaces/Bar/QuickSettings/BarWindow.QuickSettings.cs (one method), ci.ps1 (the desktop floors).

## Not built yet (left for later rounds)

- Widget hiding under windows (DESK-091..103, except DESK-095 and DESK-102). Electron: the page reports pane rects,
  lib/features/widgethide.js hands physical-px areas to the sidecar (widgethide.watch {on, areas}), sidecar/WidgetCover.cs
  rides WinEvent hooks and pushes {covered, by}; the page fades covered panes (260 ms out, 560 ms in, no layout shift,
  no clicks); it fails open with health words. The native pane rects are already logged ("panes (physical px on screen)").
- Not run live by any test: the sidecar fit and rescale (DESK-016), the live remake attach / give-up and the Explorer-back
  try-now / make / recheck paths (DESK-011/012), the put-back detach (DESK-013), the 30 s re-arm (DESK-124), a live corner
  opening the quick settings panel (CORN-053).
- The wallpaper player and backdrop (DESK-046..072) and the Mission Control live view (WebView2, DESK-036).
- Quick settings' Hot corners row (CORN-061..085), Mantle Settings' rows (CORN-099..104).
