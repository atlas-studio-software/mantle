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

## Part 2 starts here

1. Merge `origin/native/main` once the surface host lands.
2. `[Feature("osd", "Volume and brightness pop-up", true, Chords = VolumeUp/VolumeDown/VolumeMute, Rest = true)]`
   wiring `VolumeKeys` + brightness (`osd.bright` watch, flyout verdicts) + commands (`osd.size`, `osd.faded`,
   `osd.show`, `osd.mixer`, `osd.state`, test-only `testShow`/`testHide`).
3. `[Surface("osd")]`: the pill (glyph, slider, number, chevron), panel, fade, raise on every show via
   `WindowKinds.Raise`; UI tests and the screenshot pairs under `native/tools/parity/osd-toasts/`.
4. `[Feature("toasts", ...)]`: the owner + safety + picker on the notification list, quiet check, own banners
   on the page, status line, `toasts.state`; then `[Surface("toasts")]` (card, stack, timing, "+N more").
