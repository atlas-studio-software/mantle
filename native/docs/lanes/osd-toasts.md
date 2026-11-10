# Lane notes: native-osd-toasts (R3-4)

Parity rows: launch.md OSD-001..115 and TST-001..134. Electron source is the spec (read only).

## Part 1 (this branch, `native/r3-osd`)

Host-side logic and its tests only. No `[Feature]` and no `[Surface]` is registered yet, so nothing
engages in the app: the surface host (native-r3-core-4) was not on `native/main` when part 1 was written.

| Native file | Electron source | What it is |
|---|---|---|
| `Mantle.Core/Features/Toasts/BannerRecord.cs` | `_toasts-owner.js` (record, markers) | the `notif-banners.json` shape (v, owned, taken, released, pid, at) and the three put-back marker files |
| `Mantle.Core/Features/Toasts/BannerOwner.cs` | `_toasts-owner.js` | take / put back, write-ahead, stamps, released, markers, left-to-Windows list, `Mantle.ToastTest*` rule |
| `Mantle.Core/Features/Toasts/BannerRecordFile.cs` | `toasts.js` store | the record file through `SafeFile` (the one write gate); reads the markers, never writes them |
| `Mantle.Core/Features/Toasts/PlatformBannerRegistry.cs` | `toasts.js` reg | `toasts.banners` read/write through the sidecar (unchanged `sidecar/ToastBanners.cs`) |
| `Mantle.Core/Features/Toasts/BannerSafety.cs` | `_toasts-safety.js` | 8 s grace, give-up, sync put-back on leave, `restore-banners.ps1` runner and its output reader |
| `Mantle.Core/Features/Toasts/ToastsCore.cs` | `_toasts-core.js` | picker (baseline, new ids, FRESH_MS), banner fields, quiet reasons, click plan, column spot (`bannerSpot`) |
| `Mantle.Core/Features/Notifications/OwnBanners.cs` | `lib/own-banners.js` | Mantle's own banners: post / drop / act, ids, limits, running guard |
| `Mantle.Core/Features/Osd/OsdModel.cs` | `ui/osd/osd-model.js` | fader, readings, glyph paths, plain reasons, wheel, app list, place / placeLow |
| `Mantle.Core/Features/Osd/VolumeKeys.cs` | `lib/features/osd.js` (keys) | press queue (MAX_WAITING 6), dry run in --test, QS-open skip, probe and re-probe, watch settle |

### The takeover and restore-on-crash story

- **Toasts.** Mantle turns an app's Windows banner off with `ShowBanner = 0` through the sidecar. The native
  owner writes Electron's record byte-shape first (write-ahead, through the write gate), so every existing
  put-back writer works on what native took: the sidecar's own exit (`ToastBanners.RestoreForExit`), the crash
  supervisor (`Supervisor.cs`, unchanged), and `tools/restore-banners.ps1`. A record the gate refuses means
  nothing is written to Windows. In `--test` only `Mantle.ToastTest*` ids are ever taken (and a passive
  sidecar refuses anything else). Proven on a real registry by
  `Mantle.Core.Tests/Features/Toasts/BannerCrashRestoreTests.cs`: take in a scratch HKCU key, "crash", run the real
  script, every original back, the user's own 0 untouched, the marker read by the next owner; the key is
  deleted at the end.
- **OSD.** Volume keys are taken by the feature host's `hotkey` call, which is sent only in a `--live` run
  (`FeatureHost.ApplyAsync`), and live in the sidecar's hook: the sidecar dying gives the keys back. Brightness
  (parking Windows' flyout) is the sidecar's `osd.bright` with its own exit restore and `tools/restore-osd.ps1`;
  part 2 calls it.

## Requests for other lanes

- **native-r3-core-4 (surface host):** the OSD surface needs a host-side seam to reach the feature: `osd.size`,
  `osd.faded {seq}`, `osd.show` payload events, and the bar's `barRect` / pill for `placeLow`'s reach. Toasts need
  `toasts.layout`, `toasts.click`, `toasts.more`, `toasts.ready` and a corner-occupants source (bar pop-up, the
  screenshot card) for `ToastsCore.Spot`.
- **native-core:** the crash supervisor's banner put-back is the sidecar's `Supervisor.cs` (unchanged), so the
  native record must stay at `<userData>\notif-banners.json`; `RestoreBannersScript` needs the shipped path of
  `tools/restore-banners.ps1` (a fixed relative path in the install).

## Part 2 (this branch, `native/r3-osd`): the OSD feature and surface

| Native file | Electron source | What it is |
|---|---|---|
| `Mantle.Core/Features/Osd/OsdFeature.cs` | `lib/features/osd.js` | `[Feature("osd", "Volume and brightness pop-up", true, Chords = VolumeUp/VolumeDown/VolumeMute, Rest = true)]`: VolumeKeys wired (chords, the sidecar `volume` watch, the probe on every sidecar ready), events `osd.show {kind, state, expand, below, via, seq, hold}` / `osd.level` / `osd.hide {why}`, commands `osd.size`, `osd.faded {seq}` (an older seq is ignored), `osd.show`, `osd.mixer`, `osd.state`, test-only `osd.testShow` / `osd.testHide`, the 60 s backstop |
| `Mantle.App/Surfaces/Osd/OsdSurface.cs` | `ui/osd/index.html`, `osd.css`, `osd.js` | `[Surface("osd", AtStart = true)]`: one non-activating topmost window, the pane plus 18 DIP margin; the pill (glyph Button, level bar with drag and wheel, number, chevron Button), the panel ("Sound", the apps' state block), the why line, the light drop shadow (dark: the lip, no shadow), the fade (light 260 ms on --lg-ease, dark none), `WindowKinds.Raise` on every show and level, placed above the bar's pill |

- **Test road.** `--test-surface=show:osd` (a second start) calls `osd.testShow` with the JSON in `MANTLE_TEST_OSD` (the first
  instance's env; `"at": [x, y]` places the window, physical px); without it, `osd.show {via}` (the reading the feature has).
  `--test-chord=VolumeUp` drives the key road (a DRY step in `--test`).
- **Takeover gating.** The volume keys are taken from Windows only by the feature host's `hotkey` call, which a `--test` run
  never sends: `OsdFeatureTests.A_test_run_registers_..._takes_no_keys_from_Windows` (no `hotkey` call after ApplyLive in
  --test) and `A_live_run_takes_the_three_volume_keys_...` (the live rig's `hotkey` call carries the three keys; no test
  commands). The UI test also checks the log never says `features: keys ... taking VolumeUp`.
- **Screenshots.** `native/tools/parity/osd-toasts/run-osd-parity.ps1` (runner only): Electron renders its own
  `ui/osd/index.html` over the mock bridge offscreen at scale 1 (`electron-osd.cjs`); the native window is grabbed over a
  full-screen backdrop window of the same colour; SSIM per state and theme, pairs in `native/out/parity/osd/`, no masks.

### Known gaps (part 2)

- Pairs (run 42389540367, before the Button change): pill states light SSIM 0.87-0.90, dark 0.89-0.92; `vol-dead` 0.58
  (Electron 122 tall, native 105: the why line wraps differently). Not yet chased: the remaining pill difference (looked at
  `vol-62-light-pair.png`: same layout, sizes 324x82 both; differences are anti-aliasing of the glyph and text and the
  shadow's spread).
- Per-app rows are not drawn: `qs.audio.sessions` and `qs.set` have no native owner yet, so the panel shows Electron's
  "The apps can't be read" block with the bus's words, and a mute click or a drag shows "... did not take/change: ...".
- Not ported: brightness (the `osd.bright` watch that parks Windows' brightness flyout), the bar-follow glide (`osd.lift`),
  the dark see-through backdrop picture (hud-backdrop), the open panel's click-outside watch (popwatch) and the mixer's
  "seen on screen" check.
- The bar's pill rect is read from the bar window less its shadow room (BarWindow ShadowTop 56 / ShadowSide 28 DIP): a
  `barRect` source from the bar lane would replace this.
- The light drop shadow uses the light `--lg-ink` as its ink (the CSS is rgba(14,18,38,a)): no shadow token exists.

### App.xaml.cs (block `OsdWiring`)

- The bar window's `Closed` runs the ONE shutdown (`Quit("the bar window was closed", 0)`): the osd window is made at
  start, so the bar is no longer the last window and a WM_CLOSE on it left the process alive (UiTests
  `Close_and_kill_leave_no_process_behind` failed in runs 42389540367 and 42390182560 without this).

## Part 3 (branch `native/r4-osd`): OSD pairs closed, toasts feature + surface

### OSD: what the SSIM gap was (diff images of run 42767286777, `vol-62-light`)

| Cause | Fix |
|---|---|
| The hairline: CSS draws the border OVER the element's own face (background-clip: border-box); WinUI's Border draws its background inside the border, so the 10 % edge ink sat on the desktop (0x737D8C vs Electron's 0xE3E3E6) | `BackgroundSizing.OuterBorderEdge` on the pane |
| The light shadow: Composition's DropShadow BlurRadius is not CSS's blur: a hard band 0x13-0x18 darker than the desktop to the window's edge (Electron 0x02) | `Surfaces/Osd/CssShadow.cs`: the CSS box-shadow formula (shape + spread, Gaussian sigma = blur / 2, nothing under the element) into a bitmap; shared with the toast cards |
| The `unavailable` why line did not wrap: a horizontal StackPanel measures its text unbounded (one line, clipped: 105 px tall vs 122) | a two-column Grid; line-height 1.4 exact (BlockLineHeight) |
| Brightness: a fourth column of width 0 still took its 8 DIP column gap from the level | the fourth column is removed for brightness |
| Electron's shots had ClearType colour fringes: shot over an OPAQUE colour Chromium uses LCD text; Mantle's real pop-up window is transparent, where it never does | `electron-osd.cjs` passes `--disable-lcd-text` (harness only; command line and output lines unchanged) |
| Text family | `Segoe UI Variable Text` named first, as osd.css |

### Toasts

| Native file | Electron source | What it is |
|---|---|---|
| `Mantle.Core/Features/Notifications/NotificationFeed.cs` | `lib/notifications.js` (list + watch only) | the sidecar's `notifications` list and watch, READ only; every read published, a failed one with its reason |
| `Mantle.Core/Features/Toasts/ToastsFeature.cs` | `lib/features/toasts.js` | `[Feature("toasts")]`: picker on the list; owner + safety ONLY in a --live run whose host said where the record lives (`Configure`, App.xaml.cs block "toasts wiring"); quiet check; access; own banners (`ToastsFeature.Shared`); commands layout / ready / click / more / action / copy / state; test-only testShow / testOwn / testIds |
| `Mantle.Core/Features/Toasts/ToastsStatus.cs` | `_toasts-status.js` | the one status line |
| `Mantle.App/Surfaces/Toasts/ToastsSurface.cs` | `ui/toasts/*` | `[Surface("toasts")]`: window made on the first banner, closed 30 s after the last; card, stack of 3 + "+N more", in/out motion, 6 s life with hover hold, X, own banners' buttons / progress / detail / Copy, update in place, light and dark, raised, never activating |

- **Takeover gate.** `ToastsFeatureTests.A_test_run_makes_no_owner_and_never_reads_or_writes_Windows_banner_settings` and `A_live_run_takes_the_covered_apps_shows_their_new_toasts_and_puts_back_on_stop`; the UI test asserts from the log that a --test run takes nothing.
- **Test road.** `MANTLE_TEST_TOASTS` = a JSON list of steps; each `--test-surface=show:toasts` runs the next (`testShow` / `testOwn` / `testIds`; `at` = the window's bottom right in physical px; `theme`).
- **Not ported / deviations.** The glide with the bar (no bar motion source: the stack stands above the pill when the bar is up); the shaped tall column (the window is sized to the stack: Electron's own no-shape road); the FLIP slide of the cards below a leaving one; the early "toast coming" pre-warm; a click that brings the app's window forward or opens the bell (no native owner: a click asks the sidecar's `open`, a failure is said); "+N more" opens nothing yet (said); data: URL icons (file / http / ms-appx only, else the letter tile); the screenshot card and bar pop-ups as corner occupants; toast screenshot pairs (no script yet).
- **Requests.** Bar lane: a pill rect / `barRect` source and bar motion events (OSD and toast glide). Core: ship `tools/restore-banners.ps1` next to the exe (the toasts block points at `<exe dir>/tools/restore-banners.ps1`); a notification-list owner (`notifs.*`) for "+N more" and the click fallback; an app-window focus command for a click. QS: `qs.audio.sessions` / `qs.set` (OSD per-app rows).

## Part 4 starts here

1. Toast screenshot pairs in `tools/parity/osd-toasts/` (an Electron shot of `ui/toasts/index.html` over a preload `window.mantle`; the native window placed by a step's `at`).
2. The 30 s idle close and the hover hold under a UI test; the toast glide once a bar motion source exists.
3. OSD: brightness (`osd.bright`), the glide, the backdrop picture, the panel's click-outside, per-app rows.

## Part 4, round 1 (lane native-r4-osd): the blocker of run 42769441389

Fixed in this lane:

- Colour literals (TokensTests): `ToastsSurface` drew clear with `Colors.Transparent` and mixed `Lift` with `Color.FromArgb`. A default `SolidColorBrush` is clear (`Clear()`), and `Lift` now composites the two tokens' own colours.
- Toasts UI test, own card held: the runner reads as Do not disturb (`toasts.quiet` dnd 1), so Mantle's own banner was held. `toasts.testOwn` takes `dnd` / `quns` as a stand-in for Windows' quiet state from then on. The UI test sends dnd 0, quns 5, and without them the real check answers (the unit test of the hold is unchanged).
- A `--test` run never reads the notification list (no `NotificationFeed`): the runner's or the user's own notifications never come and go under a test. The unit test now asserts no `notifications` call in `--test`.
- Toasts UI test, progress line: its id sat on a bare `Grid`, which has no automation peer, so UIA never saw it. The id is now on the progress words. The stack-of-three check (it passed vacuously for the same reason) finds cards by their titles, and it also asserts the newest one is there.
- The theme-change crash (the two theme tests): every crash is `SystemBackdrop`'s own `OnDefaultSystemBackdropConfigurationChanged`, called with no target, throwing "The parameter is incorrect: target", an unhandled error that stops Mantle about 6 ms after "the bar now shows dark". These were bisected on the runner (theme.set + look.json tests, 2 runs each, the app logs counted):
  - run 42775032765: shadow off 0/4 crashed; toasts surface or feature off, still 4/4.
  - run 42776421533 was wasted (the diag skipped the UI-test build).
  - run 42777016071: a blank `WriteableBitmap` shown 0/4; the shadow pixels computed and never shown 4/4.
  - run 42777700651: no shadow plus one forced full GC 4/4; no shadow plus a 400 ms UI-thread hold 0/4.

  So a full garbage collection, then a theme change, crashes. `CssShadow` allocated about 90 bytes per pixel on the managed heap (about 9 MB per OSD size), which brought that collection into the first seconds. `CssShadow` now uses unmanaged working planes and one reused BGRA buffer. The theme tests pass (run 42778532495).
- The OSD's and the toasts' windows use `Surfaces/Osd/SeeThroughBackdrop`: TransparentBackdrop's clear brush, plus an override of `OnDefaultSystemBackdropConfigurationChanged` that never calls the base (a clear brush follows no configuration). In run 42773083145 it logged the null-target call it absorbed.

NOT fixed (another lane's file): the OSD UI test still crashes at its dark step (runs 42778532495 and 42779579180). That second run logged GC counts 0/0/0 at the theme change, so a GC is one trigger, not the only one. The stack is again the base handler of a `Native/TransparentBackdrop` (the bar's, the launcher's or a pop-up's: the OSD's own is guarded). This is a latent fault on native/main too: any full GC before a theme change crashes there as well; its tests only switch theme before the first one.

REQUEST (native-windows lane, `native/src/Mantle.App/Native/TransparentBackdrop.cs`): add

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot) { }   // a clear brush follows no configuration; the base throws on a null target

and a UI test that forces a full GC (a `--test` hook) before a theme change. Once that lands, `SeeThroughBackdrop` can go back to `TransparentBackdrop`.

Left for part 4: the full `mantle-native-public` run once the override is in; the toast screenshot pairs; the rows above.
