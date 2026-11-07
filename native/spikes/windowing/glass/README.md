# glass: primitive 2, the island's glass

**Status: works with a caveat (the cpu mode).** Light matches Electron's real pixels. Dark matches
Electron's dark recipe, but only a reconstruction of it: the Electron dark reference shot is
contaminated (see below). The GPU routes are **blocked on the runner**: the host backdrop draws black
and acrylic draws a solid slab. They are not verified on real hardware.

**Technique (cpu mode):** Electron's own method, ported. Capture the screen behind the pill with the
window excluded from capture (`SetWindowDisplayAffinity` WDA_EXCLUDEFROMCAPTURE for ~30 ms), shrink it
by 4 (light) or 8 (dark) with a tent filter, Gaussian-blur it with transparent edges, apply
saturate(1.5) in light, and show it as a premultiplied picture exactly the size of the pill. Then
XAML draws the island's tint, its 1 px lip and its 1 px hairline, plus the light drop shadow.

Last run: cloudtest `20261107-081415-3a33da`, `WINDOWING glass: ok (15 of 15 checks)`. It ran on a
Windows Server 2025 runner (4 cores, AMD EPYC, no GPU) at 1920x1080, 100 % scale, with wallpaper snow-peak.jpg,
the Windows taskbar on auto-hide and the bottom of the screen cleared. That is the bench's setup: the
bare screen here differed from the bench run's by a mean of 0.41 (p95 1, max 2).

## How close: |RGB difference| per channel, 0-255

The comparison uses the same 1200x122 rectangle as the bench (the pill plus 40 px on each side and
60 px above it, down to the bottom edge). "Glass only" means the pill minus the boxes where Electron's
bar draws its mark, dock and right side. This spike draws no content, so the whole-pill number
includes Electron's icons and text.

| run | vs | glass only: mean / p95 / max | rim (outer 3 px) | shadow band (2-30 px above) |
|---|---|---|---|---|
| light, cpu | Electron light shot (bench run) | **0.58 / 1 / 27** | 0.79 / 2 / 27 | 0.87 / 3 / 6 |
| dark, cpu | Electron's dark recipe over this wallpaper (`ref/recipe-dark.png`) | **0.21 / 1 / 8** | - | - |
| dark, cpu | Electron dark shot as is | 23.33 / 45 / 48 | 20.04 / 42 / 48 | - |
| light, gpu (host backdrop) | Electron light shot | 21.64 / 35 / 58 (backdrop drew black) | 21.57 | 0.87 |
| dark, gpu (host backdrop) | dark recipe | 22.73 / 32 / 37 (black) | - | - |
| light, acrylic | Electron light shot | 111.65 / 132 / 133 (solid white slab) | 98.69 | 0.42 |
| dark, acrylic | dark recipe | 20.4 / 30 / 35 (solid dark slab) | - | - |

What still differs in light, cpu: almost nothing. The max of 27 is in single pixels at the pill's
anti-aliased ends. Blur strength, saturation, tint (.45 white), hairline (.10), lip (.62), corner
radius 24 and the shadow (--lg-drop-up, whose third layer draws nothing at a 48 px pill, in CSS as here)
all agree.

**Why the dark reference shot is not the target.** In dark, Electron does not blur the screen. It blurs
a 1/8-scale still of "Windows' wallpaper", which the desktop layer takes once with a screen capture
(main.js `desktopBackdrop`, `lib/wall-still.js`). In the bench run that still was taken while something
grey sat along the bottom of the screen. A fit offline puts a neutral grey (~117, 117, 115) band over
the bottom ~46-48 px, which is the Windows taskbar's place, and the shot shows taskbar-icon ghosts too.
Fed that band, the recipe reproduces the Electron dark shot to a mean of 2.9-3.9 on clean columns.
Fed the clean wallpaper, it does not (23). So the spike's "dark glass flatter than Electron's" was
mostly this grey band, not a wrong recipe. The dark target here is the recipe with the clean wallpaper:
blur(5px), no saturate, tint rgba(9,10,11,.50) (--lg-fill-bar), hairline white .09, lip white .07,
radius 18, no drop shadow, still at pitch 8, cut to the pill grown by 16 px. In light the same offline
model reproduces Electron's real shot to a mean of 0.77 (p95 1.6), so the model is trusted. Still, the
dark match is **against a reconstruction, not against fresh Electron pixels.** On the user's machine, a
chosen Mantle wallpaper makes the still 1/2 scale (sharper) instead of 1/8.

## Cost: % of one core over 30 s (app process tree; dwm.exe beside it)

The "changing" phase swaps the wallpaper every second for 30 s (29 swaps).

| run | idle: app / dwm | changing: app / dwm | follows the change? |
|---|---|---|---|
| no island (baseline) | - / 0.31 | - / 1.01 | - |
| light, cpu (4 pictures a second) | **3.38** / 1.82 | 1.97 / 2.45 | yes: 29 of 29, median 304 ms after the swap (the wallpaper itself repainted at 175 ms) |
| dark, cpu (one still per reveal, re-cut on a wallpaper change) | **0.00** / 0.00 | 2.98 / 2.82 | yes, through WM_SETTINGCHANGE: 29 of 29, median 663 ms (400 ms of that is a deliberate wait) |
| light, gpu | 0.05 / 0.00 | 0.00 / 3.95 | no (black) |
| dark, gpu | 0.00 / 0.00 | 0.00 / 3.04 | no (black) |
| light / dark, acrylic | 0.00 / 0.00 | 0.00 / 2.88 and 2.40 | no (solid fallback) |

Light, cpu: about 4 pictures a second (40 per 10 s in the app log). Each one cost 18 ms of capture, mostly waiting on DWM, and 2.2 ms of
blur. The blur runs on the 1/4-scale picture, with sigma scaled per axis, before the stretch. Offline
on the reference wallpaper, that order lands within 0.1 of 255 of stretching first and then blurring,
for about 1/16 of the work (run 2 blurred at full size: 20 ms). Electron's bench numbers for its whole
bar were 5.47 % (light, may include start-up) and 0.83 % (dark).

Like Electron, the glass is a still picture: 4 times a second in light, once per reveal in dark (plus
a re-cut when the wallpaper changes). It is not live. Windows moving behind the light island show up
within one 250 ms tick.

## GPU routes: blocked on the runner

- **Host backdrop + composition effects** (`Backdrops.cs` HostBlurBackdrop): the system backdrop
  brush is GaussianBlurEffect(HostBackdropBrush), then a CSS saturate matrix (ColorMatrixEffect), masked
  to the pill with a CompositionMaskBrush over a VisualSurface rounded rectangle. The window has
  DWMWA_USE_HOSTBACKDROPBRUSH set. It builds and connects without an error, and the mask lines up, but
  the backdrop draws **black**. So does the bare host backdrop brush with no effect and no mask (the
  `--raw` diagnostic gave identical pixels). Windows reports AdvancedEffectsEnabled True, not a remote
  session, energy saver off. The likely cause is that the runner's DWM has no GPU (Basic Render Driver)
  and so no blur-behind. This is unverified. A Transform2DEffect in the graph, used to copy Electron's
  light stretch, was refused by the effect factory with ArgumentException "The parameter is incorrect".
- **DesktopAcrylicController** told it is always active (IsInputActive true), its own tint and
  luminosity at 0, the window cut to the pill with a region (no shadow, aliased ends). The target was
  added, but it draws its solid fallback: white in light (glass-only mean 111.65 from Electron) and
  dark in dark. That is main.js's "near-opaque white slab". Even where it works it would not match:
  it has a fixed blur, noise and a luminosity layer.
- Not tried: Windows.Graphics.Capture into an in-process Win2D blur. It needs the window excluded from
  capture for good, which also hides it from every screenshot check, or toggled like the cpu mode, and
  then it is the cpu mode with the blur moved to the GPU. With the blur already at 2.2 ms, the capture
  is the cost, and WGC would not remove it.

**Pick: the cpu mode.** It is the only route that matches, and it costs about 3.4 % of one core in light
on a 4-core runner and nothing in dark at rest. Re-test the host backdrop on a machine with a GPU
before ruling it out. It would be live and nearly free in-process, but in light it cannot copy
Electron's vertical stretch: the picture of the pill shown over the pill + 14 px each side
(1148x76 from 1120x48).

## Files to copy

- `CpuGlass.cs`: `CpuGlass` (constructor arguments = the light and dark recipes), `CaptureAndRender`,
  `BlurSmallThenStretch` (light), and the full-size path in `Render` (dark).
- `App.xaml.cs`: `Tick` and `Refresh` (exclude from capture, wait 30 ms, BitBlt, include again; a
  `pause` file holds it for screenshots), `WatchWallpaper` (dark re-cut), `StripFrame` (no frame:
  WM_STYLECHANGING filter + WM_NCCALCSIZE -> 0. Run 2 showed that SetWindowLongPtr alone is undone by
  the presenter, style 0x94480000, and the client area sits 3 px in), the layer stack in `OnLaunched`,
  `AddShadow`, and `After` (every DispatcherQueueTimer kept in a field: one held only by a local was
  collected and the 4 fps loop died after ~10 s in run 2).
- Tokens: `Radius`, `Sigma`, `Sat`, `Tint`, `LipC`, `Edge` in App.xaml.cs, from
  ui/shared/glass-light.css + ui/bar/bar.css.

## Files here

- `Glass.csproj`, `App.xaml(.cs)`, `CpuGlass.cs`, `Backdrops.cs`: the app.
  `Glass.exe --out=<dir> --mode=cpu|gpu|acrylic --theme=light|dark [--sigma=N] [--fps=4] [--raw]`.
- `check.ps1`: build, bench desktop, a baseline, then each run: shot, diff, CPU idle and changing.
  Pass thresholds: the bare screen within a mean of 2 of the bench's. In the cpu mode, glass-only
  mean <= 3 against Electron (light) or the recipe (dark), the glass visibly drawn (mean >= 5 from bare
  wallpaper), and in light the glass follows >= 80 % of wallpaper swaps with a median <= 600 ms. The
  gpu and acrylic numbers are notes, not claims.
- `tools/GlassTools.cs`: the check's pixel maths (masks, stats, side-by-side).
- `ref/`: `electron-light-bar.png`, `electron-dark-bar.png`, `electron-hidden.png` (bench run
  20261106-222646-36dc9b, re-encoded, same pixels) and `recipe-light.png` / `recipe-dark.png`
  (Electron's recipe applied offline to `electron-hidden.png`).
- Artifacts per run (`out/glass/`): `native-<theme>-<mode>.png`, `sbs-<theme>-<mode>.png`
  (Electron on top, native in the middle, |difference| x4 at the bottom), `hidden.png`,
  `results.json`, `summary.md`, and `<theme>-<mode>/log.txt`.

## Harness changes this lane would want (not made)

- `Common.ps1` `Grab` returns its byte[] through the pipeline, which unrolls it into object[] (slow, and
  converted back on every call). `return , ([WinHarness]::Grab(...))` fixes it. The check has its own `G`.
- A shared "bench desktop" step (taskbar auto-hide, ClearBottom, cursor away from the bottom edge) for
  every lane that shoots the bar's place.
