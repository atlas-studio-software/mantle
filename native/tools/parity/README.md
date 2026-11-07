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
4. Each capture waits until the crop stops moving (two grabs 300 ms apart, mean change < 0.5), then grabs
   the real composited screen (GDI BitBlt with CAPTUREBLT).
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

## Thresholds (provisional)

`-MinSsim 0.80` and `-MaxDiffPct 25`. They are a first guess, not calibrated. Tighten them as the native
bar converges.

## Results so far

Run 42352739396 (2026-11-07, base app = the spike bar, before the masks above; only the clock was
masked, imprecisely): light SSIM 0.667, 13.2 % of pixels changed; dark SSIM 0.707, 11.4 %. Seen in the
PNGs: the pill shape, glass and right cluster are close. The differences were these:
- the apps (none in Electron, two in native);
- the native pill sits about 2 px lower, and the native window shows a 1 px light line along its bottom
  edge (the known frame-line defect);
- the native right cluster sits about 20 px further right (the separator and icons);
- the clock text.
