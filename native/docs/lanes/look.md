# Lane notes: the see-through look (native/r8-glass)

Brief: the user's 2026-11-11 look, native only (look-sync CONTRACT "2026-11-11: sharp painted glass + tint override").
Ledger: docs/parity/look.md (SEE-001 .. SEE-019).

## What is where
- `Mantle.Core/Look/Wash.cs`: look-sync `washAlpha`, one to one (pure; never throws). `WashTests` replays every
  case of look-sync's test/wash.test.js with the same inputs and expected outputs.
- `Mantle.Core/Look/LookSyncCore.cs#CheckValue`: the optional `dimAuto` key (look-sync's rule and words).
- `Mantle.Core/Look/LookHost.cs`: `Wash(theme)`, `DesktopStill()`, `SetDimByHandAsync` (the Dim slider: value + manual),
  `SetDimAutoAsync` (Auto); `SetGlassAsync` takes `dimAuto` and keeps it on every write. `View()` is unchanged (the
  goldens compare it with Electron's look-host).
- `Mantle.App/Shell/Glass/DesktopGlass.cs`: the shared service. Fed from the look host (App.xaml.cs, one line); it posts
  to the UI thread, coalesced per burst. The still is decoded on a pool thread ONCE per (file stamp, monitor size), cover-
  fit and centred (Fant, no blur, no saturate) into one WriteableBitmap shared by every window. Two sizes are kept.
  The picture file has one FileSystemWatcher (150 ms one-shot settle).
- `Mantle.App/Shell/Glass/GlassLayer.cs`: a window's two layers (picture, tint), rounded with its shape. The picture is
  an ImageBrush (Stretch None, top-left) with a CompositeTransform: scale 1/DPI scale (one bitmap px per physical px)
  and translate (monitor origin - layer origin) / scale. A move sets the translation only.
- Hooks: `Shell/Popup/PopupWindow.cs` (all bar pop-ups), `Surfaces/Launcher/LauncherSurface.cs` (the pane).
- `MANTLE_TEST_GLASS=electron` (test runs only): the old look, set by the Electron pair harnesses (bar, launcher, qs,
  cal, notifs, media, previews). The OSD and strip pairs do not need it (those windows do not have the look).

## Why this way
- The tint is a separate layer over the picture, so a theme or Dim change never touches the bitmap, and one bitmap
  serves both themes and every window.
- Without a still the window keeps exactly its earlier fill (the pop-up's --lg-fill-pop inside the hairline), so the
  existing UI tests (no look.json) see the earlier look.
- The wash colour is the contract's: rgb(9 10 11) in dark (LookHost.DarkBar), white in light (LookHost.LightFill).

## Open questions for the user
- **The bottom bar.** The 2026-11-11 section and the brief name "the top bar and every native Mantle window (Settings,
  Files, Task Manager, launcher, popups that sit over the desktop)"; neither says the bottom bar. It keeps its live
  glass (BAR-034/037) until the user says. SEE-014.
- **Seeds.** Native still seeds a missing look.json with Electron Mantle's values (5 / .50, 12 / .45) so the byte
  goldens against Electron's vendored look-sync hold; look-sync's new seed is 0 / .30. SEE-018.

## Left (todo)
- SEE-011 OSD and toasts; SEE-012 top bar (part 2); SEE-013 desktop layer and menu (part 2); SEE-015 Settings rows;
  SEE-017 the native guard over the sharp still; SEE-019 counter-moving the picture during the entrance/exit motion.
- The launcher (SEE-010) has the look but no pixel test yet; the real pop-up panels share the tested base.
