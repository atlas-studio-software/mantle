# Lane r13-bar-islands (S2): the bar as three islands, offline button, stuck flag

Branch `native/r13-bar-islands`, off native/r12-app-wiring b756536e. Files: BarWindow.*, Surfaces/Bar/** (Core read-only).
Spec: apps-r3/mantle-bar (NOTES.md, look.css, shell.js, barhide.js), FIXES-2.md, lab/LOOK.md, apps-r3/LIGHT.md, look-sync v2.1.

## Status (first session, 2026-11-25)
Done: the stuck flag's trigger (below). NOT done: the islands layout, the offline button, the parity reference list,
the spec tests. Nothing built or run (no cloud run yet). The plan below is what the next session builds.

## Stuck flag ("Bar may not hide", BAR-189): done here
- Native already had the words (Chips.cs, bar.js chipData ~203) and the More menu lists every chip first, but nothing in
  native ever wrote `ShellState.Bar.Ok` / `.Reason` (grep: no writer in Mantle.App or Mantle.Core), so the chip could
  never show. Electron sets it from main.js:2072 parkBar ("the bar window would not hide: ...").
- Now: `HideDone` (BarWindow.Frame.cs) raises the `bar` failure chip when the window is still visible after SW_HIDE,
  and clears it after a hide that works; `RenderPip(force)` repaints the More pip (tone "", ink-2, as the prototype).
  Chips.List skips the state-based `bar` chip while the failure one is up (never two).
- To verify: a UI test that forces a failed hide is not written (needs a way to make SW_HIDE fail on the runner).

## Islands: the plan
- Gate like S1: islands unless `DesktopGlass.ElectronLook` (MANTLE_TEST_GLASS=electron keeps today's face).
- Geometry (BarGeometry.For, islands mode): the band is screen - 2 x 12 DIP wide, 44 tall (--bar-h), 12 above the bottom
  (bar zone 68); today it is min(1120, screen - 28) x 48, 14 up. BarIntent keeps its zones on the band (the prototype keeps
  the bar while the pointer is on the islands or within 8; the band is a superset between the islands). Timings unchanged.
- XAML: wrap StartBtn, DockRow and RightCluster in three island grids (LeftIsland, DockIsland, RightIsland) with no
  background, so the electron face lays out exactly as today. Islands mode: Content padding 0; left 44 x 44; dock and
  status 44 tall, padding 0 4, gap 2; radius 12; Glass/Tint/Lip/Edge collapsed; each island gets a LevitatePanel
  (Shell/Levitate, the S1 class: frost light by size, dim, v2.1 dark film, hover-only sheen) placed with
  `LevitatePanel.Place(hwnd, island, dark)` on layout and on every geometry change; one LevitateShadow.Float bitmap
  for the three boxes in ShadowHost (all themes). Light: no film (LevitatePanel's rule until the light lift is built).
- Sizes from look.css: start button 36 (22 mark); dock cells 40 x 36, run dot 4 x 3 (text-3), focus underline 16 x 3
  accent sliding by transform 320 ease-lift; dock-sep 1 x 20, margin 0 4, hair; status glyphs 32 x 36, b-sep 1 x 20
  margin 0 4; clock padding 0 8, time 12/500, date 11 text-2. Today: cells 36 spacing 6, status 36 spacing 4.
- Dock placement (shell.js fitDock): centred on the screen; if its right edge + 12 passes the status island's left,
  it sits 12 after the M (left = 44 + 12). BarWindow.Overflow.cs's fit rule (+N) then works inside the dock island.
- The gaps between islands are part of the bar window (a click there lands on the bar while it is up, as the shadow room
  above today's pill does). A window region would clip the shadow; not done, to decide on the user's own desktop.

## Offline network button: the plan
- Today NetworkBtn is a static monitor glyph with no state. QsPanel already holds the Wi-Fi part (on, connected, ssid,
  bars) from QsService's snapshot; there is no wired state anywhere in native.
- Offline = `System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()` false (NetworkChange events), so a
  wired machine is not called offline; the words from the Wi-Fi part as net.js:54-59: "Not connected", detail
  "Wi-Fi is on but not connected to a network" / "Wi-Fi is turned off" / "No connections are available"; tooltip
  "Not connected\n<detail>\nClick: quick settings". Glyph: globe with a slash at .55 opacity (prototype ?net=off).
  Connected: the Wi-Fi glyph with bars, label "Network: <ssid>". Virtual adapters (Hyper-V, VPN) can make
  GetIsNetworkAvailable true while offline: a known limit to name in the test.

## Parity (plan v0.2): the plan
- `tools/parity/run-parity.ps1` sets MANTLE_TEST_GLASS=electron, so once the islands land it would keep comparing the
  OLD face and pass while users see the new one. It must not.
- Reference list (new, tiny): `tools/parity/references.psd1`, `@{ bar = 'native-golden'; strip = 'native-golden' }`,
  every other surface 'electron'. run-parity.ps1 reads it: for native-golden it shoots native with the islands look
  (MANTLE_TEST_GLASS unset), saves the shots as `candidate-<theme>-<scale>.png`, and compares against
  `tools/parity/goldens/bar/<theme>-<scale>.png` only when `goldens/bar/REVIEW.md` names the reviewer, run id and sha.
  With no reviewed golden it prints `PARITY bar <theme>: golden pending review (reference native-golden; candidates in
  <dir>)` and ends `PARITY: FAILED (bar golden pending review ...)`, so the stage is plainly red until a review.
  Thresholds unchanged (ssim >= 0.98, diffpx <= 0.5 %). Kept small to merge with the 150 % fix (t_5d720b).
- The `strip` stage (bar-tasks/run-strip-parity.ps1, floor 0.90 in ci.ps1) compares the bar's apps strip with
  Electron and will drop once the dock is restyled: it needs the same native-golden switch (ci.ps1 is CI's side).

## Spec tests: the plan
- Mantle.Core.Tests cannot see Mantle.App. The pure model (island count and order, sizes, radii, gaps, tokens, the
  dock-left rule, the offline words) goes in ONE new pure file. Either Core/Look/Levitate/BarIslands.cs (like S1's
  PopupLevitate; needs the Mantle Engineer or the EM's OK) with Mantle.Core.Tests/Look/BarIslandsTests.cs, or
  Surfaces/Bar/Frame/BarIslands.cs in the App with UI tests (UIA rects: 3 islands, order, 44 tall, 12 air).
- UI tests that assert today's numbers and must move to the islands numbers in the same commit: BarTests.cs:133,
  BarFrame/DisplayTests.cs:20-25 and :59-64, BarFrame/HoverTests.cs:44, :92, :131 (48 -> 44, 14 -> 12, 1120 cap -> none).

## Not verifiable without the user's own desktop
The islands over the user's wallpaper, the frost line-up and film over a bright sky, the shadow, hover reveal feel, the
gaps taking clicks, the offline glyph on a real disconnect, a real failed SW_HIDE.
