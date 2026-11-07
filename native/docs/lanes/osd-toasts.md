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

## Part 3 starts here

1. Chase the pill SSIM (the glyph stroke and text rendering; the shadow) and the dead state's why-line height.
2. `[Feature("toasts", ...)]`: the owner + safety + picker on the notification list, quiet check, own banners on the page,
   status line, `toasts.state`, the takeover only in a `--live` sole-shell run; then `[Surface("toasts")]` (card, stack,
   timing, "+N more", cornerSpots with bar-follow) and its pairs in `native/tools/parity/osd-toasts/`.
3. Brightness (osd.bright watch + flyout verdicts) and the per-app rows once a `qs.audio.sessions` / `qs.set` owner exists.
