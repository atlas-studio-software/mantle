# Screenshot parity: Electron Mantle vs native

`run-parity.ps1` puts the same surface from both apps on the same runner screen and diffs the real
pixels. Today the surface is the bar (`-Surface bar`).

## Run it (GitHub Windows runner, public repo = free)

Commit and push your `native/*` branch, then from its worktree:

```
cloudtest --recipe mantle-core-public --timeout 30 --env MANTLE_CLOUD_RES=1920x1080 \
  --artifacts "native/out/parity/**" -- "pwsh -NoProfile -File native/tools/parity/run-parity.ps1 -Theme both"
```

The `mantle-core-public` recipe's setup installs the npm dependencies and builds the csc sidecar, which
Electron Mantle needs. The native app comes from `MANTLE_NATIVE_EXE`, `-Exe`, or
`native/out/bin/MantleNative/`; otherwise the script publishes it (self-contained) first. In the full CI
(`native/tools/ci.ps1`, stage `parity`) it reuses the build stage's exe.

It refuses to run unless `CI` / `GITHUB_ACTIONS` is set (`MANTLE_PARITY_LOCAL=1` overrides it, but only
on a machine nobody is using): it changes the wallpaper, theme and taskbar, and shows both apps' test
windows.

## What it does

1. Runner desktop: Mantle's default wallpaper (`assets/backgrounds/snow-peak.jpg`), transparency on,
   Windows light or dark, the runner's own taskbar on auto-hide, cursor parked mid-screen, and one fixture
   window ("Parity fixture", top left) so both bars have a running app.
2. Electron: `electron-bar.mjs` starts Mantle as a `--test --test-live-bar --no-desktop` instance with
   `MANTLE_TEST_VISIBLE=1` and `MANTLE_BAR_TEST_CURSOR=1`, connects over CDP, raises the bar with
   `bar.testMove {up:true}`, and reports the bar rect. After the capture it is stopped with
   `--quit --test --profile-name=parity-<theme>`, then by PID for anything left with that profile.
3. Native: `MantleNative.exe --test --profile-name=parity-<theme> --user-data=<temp> --theme=<theme>
   --bench-out=<dir>`, waiting for `ready.json` (first frame).
4. Each capture first clears the crop: any visible window of another process over it (not the app under test
   or its child processes, not the fixture, not the desktop or the runner's taskbar) is minimised without
   activation; one that stays is a problem and the capture is not scored. Then it waits until the crop stops
   moving (two grabs 300 ms apart, mean change < 0.5) and grabs the real composited screen (GDI BitBlt with
   CAPTUREBLT): the crop, then the whole screen, each a *clean* grab (below), and the crop must match the same
   rect of the whole-screen grab (mean change < 3, the clock may tick) or both are taken again (4 tries, then
   a problem line).

   **Clean grabs.** Both bars' light glass takes a picture of what is behind the bar 4 times a second, and for
   each picture the bar window is switched to `WDA_EXCLUDEFROMCAPTURE` (Electron `main.js#captureBackdrop`:
   25 ms settle plus the sidecar's shot, which ran past a second while the sidecar was busy starting; the
   native `LiveGlass` the same way). Any screen grab in that moment has no bar in it, and nothing else shows
   it: the eye sees the bar the whole time. `ParityNative.CleanGrab` reads the display affinity of the app's
   windows over the rect and grabs only after it has read 0 for 60 ms, and only keeps the grab if it still
   reads 0 right after. Without it Electron light at 100 % scored 0.7251 on two runs of native/r2-windows-2
   (runs 42368866513, 42369550297): its crop was bare wallpaper while the whole-screen grab taken right after
   showed the bar. A native/r2-core-2 run at the same time (run 42369209048, 0.9966) had the opposite pair (bar
   in the crop, none in the whole-screen shot): the same race landing the other way.
5. Crop: the pill's spec rect (min(1120, screen - 28) DIP wide, 48 tall, 14 above the bottom edge,
   centred) plus `-Margin` (24) px each side, so a pill in the wrong place shows up as a difference.
6. Diff over the unmasked pixels: SSIM on luminance (8x8 windows, step 4), the share of pixels whose largest
   channel delta is over `-PixelThreshold` (24), and the mean absolute difference per channel.

Output per theme in `native/out/parity/<theme>/`: `electron.png`, `native.png`, `diff.png` (grey = same,
red = different, blue = masked), `*-full.png` (whole screen), `*-windows.txt` (visible windows),
`electron.json`, `native-bench/ready.json`, `score.json`; all themes in `native/out/parity/parity.json`.
Lines: `PARITY bar <theme>: ssim=.. diffpx=..% meanabs=.. (ok|FAILED vs ...)` per theme, then one final
`PARITY: ok (...)` or `PARITY: FAILED (...)`.

## Masks

- **Clock slot**, always: 175 to 85 px left of the pill's right end (the same slot in both apps on the
  runner). The seconds differ between two captures taken seconds apart. When the native app exposes
  `bar.clock` through UI Automation, that rect is masked too.
- **Running-apps slot**, by default (`-MaskApps $false` compares it): the centre 440 px of the pill. On the
  runner, Electron's `--test` bar listed **no** running apps while the native bar listed the fixture and a
  console window. That is a test-mode difference on the Electron side, not a look difference (cause not
  investigated yet). Unmask it once both bars show the same set.
- The Electron page's DOM rects do not map 1:1 to screen pixels in the `--test-live-bar` window (page
  1280 CSS px wide in a 1120 DIP window), so the masks are spec-relative, not taken from the DOM.

## Thresholds

`-MinSsim 0.98` and `-MaxDiffPct 0.5`. These were set after the first comparable run (below), so that the
defects still visible in its diff images fail the gate. At the first guess (0.80 / 25 %) that run passed
even though the defects were plainly visible in the PNGs.

## Results so far

- Run 42352739396 (base app = the spike bar; only the clock masked, imprecisely): light SSIM 0.667 with
  13.2 % of pixels changed, dark 0.707 with 11.4 %. The differences:
  - the apps (none in Electron, two in native);
  - the native pill 3 px low and right, plus a 1 px frame line (both fixed since in the app);
  - the right cluster;
  - the clock text.
- Run 42355091758 (2026-11-07, native/infra 0f4532b: the contract app with the frame fix, the clock and apps
  slots masked): light SSIM 0.9692 with 0.65 % of pixels changed, dark 0.9624 with 0.77 %. The PNGs show
  both bars rendered, and the pill outline, glass and Mantle mark now coincide. What still differs:
  - the native right cluster (downloads, volume, network, bell) sits about 18 px further right than
    Electron's;
  - Electron draws an orange badge dot on "more", which native does not have;
  - an Electron status icon left of the separator falls inside the masked apps slot.
  With the thresholds above, this run would fail. That is intended: the gate fails until the cluster and
  the badge match.

## Display scales (`-Scales`, default `100,150`)

Each scale in the list sets the runner's primary display with `DisplayConfigSetDeviceInfo` (the undocumented
type -4 that SetDpi-style tools use; `ParityNative.SetScale`), re-reads the DPI, recomputes the crop and masks
(the masks scale with it), and runs both themes. 100 % writes `<Out>/<theme>/` as before; any other scale writes
`<Out>/<theme>-<pct>/` and scores as `PARITY bar <theme>-<pct>: ...`. The scale is put back to 100 % at the end.
A scale Windows refuses on the runner's display is a problem line, never a silent skip.

At 150 % the runner's own consoles (Windows Terminal: the job's PowerShell window and the console of
`electron-bar.mjs`) grow with the scale and reached over the bar's crop: both apps' 150 % captures in run
42369550297 were mostly a black console window (light-150 0.7432, dark-150 0.7669), which said nothing about
either bar. The crop clearing in step 4 minimises them and logs each one.
