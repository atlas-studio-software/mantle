# Lane notes: the see-through look (native/r8-glass)

Brief: the user's 2026-11-11 look, native only (look-sync CONTRACT "2026-11-11: sharp painted glass + tint override").
Ledger: docs/parity/look.md (SEE-001 .. SEE-021).

## What is where
- `Mantle.Core/Look/Wash.cs`: look-sync `washAlpha`, one to one (pure; never throws). `WashTests` replays every
  case of look-sync's test/wash.test.js with the same inputs and expected outputs.
- `Mantle.Core/Look/NativeGuard.cs` (SEE-017, SEE-020): the readability guard over the SHARP still. `Rows` works out a
  need profile (per 8 DIP strip of the whole screen height: the brightest/darkest 4 x 4 DIP glyph box and the 95th
  percentile of the strip, 4.5:1 for the secondary ink, 8-bit compositing at the tint's alpha byte) from the decoded
  still's physical pixels. `ForWindow` is washAlpha's Automatic for one native window: max(Dim, the worst strip over the
  window's band, at least 140 DIP), on .02 steps from Dim, at most max(.88, Dim); Manual and a missing profile return the
  shared answer untouched. `Entry` is the `desktop.guard.<theme>` entry in the shape Quartz and Helm read, over the top
  140 DIP. `Measure` is an independent check (composites each pixel and sorts) for the tests and the entry's contrast.
- `Mantle.Core/Look/LookSyncCore.cs#CheckValue`: the optional `dimAuto` key (look-sync's rule and words).
- `Mantle.Core/Look/LookHost.cs`: `Wash(theme)`, `DesktopStill()`, `SetDimByHandAsync` (the Dim slider: value + manual),
  `SetDimAutoAsync` (Auto); `SetGlassAsync` takes `dimAuto` and keeps it on every write. `View()` is unchanged (the
  goldens compare it with Electron's look-host).
- `Mantle.App/Shell/Glass/DesktopGlass.cs`: the shared service. Fed from the look host (App.xaml.cs, one line); it posts
  to the UI thread, coalesced per burst. The still is decoded on a pool thread ONCE per (file stamp, monitor size and
  scale), cover-fit and centred (Fant, no blur, no saturate) into one WriteableBitmap shared by every window, and the
  guard's two profiles (dark, light) are worked out on that same pool thread from those pixels. Two sizes are kept. The
  picture file has one FileSystemWatcher (150 ms one-shot settle). Once the look names a still it is decoded ahead for
  the primary monitor (SEE-021), so the first pop-up does not show its plain fill for the length of a decode.
- `Mantle.App/Shell/Glass/GlassLayer.cs`: a window's two layers (picture, tint), rounded with its shape. The picture is
  an ImageBrush (Stretch None, top-left) with a CompositeTransform: scale 1/DPI scale (one bitmap px per physical px)
  and translate (monitor origin - layer origin) / scale. A move sets the translation and re-reads the band's need (a max
  over a few numbers, no pixel work); the tint is said in the log when it changes. `At(hwnd, element)` is an origin for
  layers placed by layout (the sum of ActualOffset up the tree, so a sliding or rising pane is lined up at rest).
- Hooks: `Shell/Popup/PopupWindow.cs` (all bar pop-ups), `Surfaces/Launcher/LauncherSurface.cs` (the pane),
  `Surfaces/Osd/OsdSurface.cs` (the pill/panel, under its 1 px edge too), `Surfaces/Toasts/ToastsSurface.cs` (each card).
- `MANTLE_TEST_GLASS=electron` (test runs only): the old look, set by the Electron pair harnesses (bar, launcher, qs,
  cal, notifs, media, previews, osd). The strip pairs do not need it (that window does not have the look).

## Why this way
- The tint is a separate layer over the picture, so a theme or Dim change never touches the bitmap, and one bitmap
  serves both themes and every window.
- The guard reads the very pixels the windows paint (the decoded still, one px per screen px), so "sample at real screen
  pixels" holds at any scale: a 4 DIP box is 5 px at 125 %.
- The row percentile is exact without sorting: a pixel's luminance under the wash moves one way as the alpha grows, so
  each pixel has its own least alpha and the strip's percentile need is the percentile of those (a 102-bin histogram).
- Strips are full screen width (the brief: "per screen row"), so a window's answer is the same wherever it sits along
  the row; that is conservative for a narrow window, never thinner than needed.
- Without a still the window keeps exactly its earlier fill (the pop-up's --lg-fill-pop inside the hairline), so the
  existing UI tests (no look.json) see the earlier look.
- The wash colour is the contract's: rgb(9 10 11) in dark (LookHost.DarkBar), white in light (LookHost.LightFill).

## Measured (cloud runner, 1920x1080 at 100 %, 2026-11-16)
- NativeGuardTests: snow-peak-like Electron .78 -> native .52 (4.56:1); night-dune-like .74 -> .30 (5.43:1); white .80;
  a thin line: box .74, row .78 -> .78; a slope low on the screen: top band .30, a window over it .74, under it .30;
  light .54 over a dark picture, .30 over a bright one. A 1920x1080 still: dark 125 ms, light 101 ms (once per still).
- GlassTests (blocks fixture, Dim .40 dark / .35 light): pop-ups raised by the guard to .76 dark / .49 light, worst
  channel error 0-1 of 6 over 54-180 points; the OSD .68 (96 of 96 points, worst 1); the launcher .76 (4092 of 4092, worst 1).

## Open questions for the user
- **The per-row profile in look.json.** CONTRACT.md defines no key for it, so it stays native-internal and the shared
  value stays `desktop.guard.<theme>` (NativeGuard.Entry, the same shape). Proposed, if Quartz and Helm should take the
  worst row over their own chrome too: `desktop.guard.<theme>.rows = { "stripDip": 8, "target": 4.5, "need": [0.30, ...] }`
  (one alpha per 8 DIP strip of the primary monitor, top to bottom, the larger of the box and row measures, NOT floored
  at Dim so it does not go stale when Dim moves); a reader takes max(Dim, max of the strips its window rect covers).
- **Publishing.** Native Mantle does not write the desktop record yet (Electron Mantle does, live). When native takes
  over, its publisher (Features/Desktop) should call NativeGuard.Entry for each theme; the band becomes 140 DIP
  (`region.heightDip` 140), so Electron's own 100 DIP key reads it as another band. The stored blur can then go to 0.
- **The bottom bar.** The 2026-11-11 section and the brief name "the top bar and every native Mantle window (Settings,
  Files, Task Manager, launcher, popups that sit over the desktop)"; neither says the bottom bar. It keeps its live
  glass (BAR-034/037) until the user says. SEE-014.
- **Seeds (SEE-018).** The contract's seed (blur 0 / Dim .30) is for a MISSING look.json, and Wash.cs already falls back to
  .30. Native's own seed (LookSyncCore.Seed, LookHost's first glass) still equals Electron Mantle's vendored look-sync
  (5 / .50, 12 / .45), and that value is in the byte goldens generated from Electron's code (Fixtures/look/golden.json
  via gen-look-golden.js, the roundtrip and profile fixtures, LookHostTests, LookSyncContractTests). Switching is a
  deliberate difference from Electron in every one of those cases; it was not made tonight because the contract also
  says Electron Mantle keeps writing its old values and native's goldens are that code's output. Should native seed
  0 / .30 now (and mark those golden cases as documented differences), or at the switch from Electron?

## Left (todo)
- SEE-012 top bar (part 2); SEE-013 desktop layer and menu (part 2); SEE-015 Settings rows.
- SEE-019: counter-moving the picture during the entrance/exit motion. Not cheap: moving the picture element against
  the pane's composition Translation also moves its rounded clip (a gap of up to 8 DIP at one edge while the pane still
  shows), and animating the ImageBrush's transform is a dependent (UI-thread) animation that can lag the compositor's.
  The clean way is a CompositionSurfaceBrush with its own offset animated with the pane's, a larger change.
- The toasts (SEE-011) have the look but no pixel test.
