# native/bench: native Mantle against Electron Mantle, and the native build modes

`run-bench.ps1` builds the native app (`native/src/Mantle.App`) and measures it on a GitHub Windows
runner; in `full` mode it also runs the real Electron Mantle **live** on the same runner, in the same
job, and measures that. It is for a runner only: it runs Mantle live (which changes the shell), moves
the cursor, clicks, changes the wallpaper, theme and screen size, and (`modes`) installs the Windows App
Runtime. Never run it on a work machine.

| file | what it is |
|---|---|
| `run-bench.ps1` | the bench. Last line: `BENCH: ok (...)` or `BENCH: FAILED (...)` |
| `BenchNative.cs` | Win32 helpers (screen grabs, clicks, process tree, screen size), compiled with `Add-Type` |
| `electron-probe.js` | loaded into Electron Mantle's main process (`NODE_OPTIONS=--require`) for per-window marks and `app.getAppMetrics()` |

## How to run

Everything runs through `cloudtest` from a checkout whose HEAD is pushed (the public repo runs for free in
committed mode). Each command brings back `native/out/bench/**` only (results.json, summary.md, logs,
screenshots): never `native/out/**`, which would also pull the published app folders (~220 MB each).

```
# native only, light + dark, short holds: what CI's bench stage calls (native/tools/ci.ps1 passes its own
# build in MANTLE_NATIVE_EXE, so there it does not build again)
cloudtest --committed --os windows --no-install --timeout 30 --env MANTLE_CLOUD_RES=1920x1080 --artifacts "native/out/bench/**" -- "pwsh -NoProfile -File native/bench/run-bench.ps1 -Mode smoke"

# Electron Mantle live vs native, light then dark (the recipe installs npm deps and builds the sidecar)
cloudtest --recipe mantle-core-public --timeout 45 --env MANTLE_CLOUD_RES=1920x1080 --artifacts "native/out/bench/**" -- "pwsh -NoProfile -File native/bench/run-bench.ps1 -Mode full"

# the build-mode table (native only, light)
cloudtest --committed --os windows --no-install --timeout 60 --env MANTLE_CLOUD_RES=1920x1080 --artifacts "native/out/bench/**" -- "pwsh -NoProfile -File native/bench/run-bench.ps1 -Mode modes"
```

Options: `-Project <csproj>` (default `native/src/Mantle.App/Mantle.App.csproj`; the exe name is the
project's `AssemblyName`, asked of MSBuild), `-Exe <path>` (skip the build in full/smoke), `-Out <dir>`
(default `native/out/bench`), `-Res WxH` (default `$env:MANTLE_CLOUD_RES`; the runner desktop starts at
1024x768), `-BuildModes sc-r2r,sc,fd-r2r,aot,aot-partial`, `-Themes light,dark`.

## What each number means

- **private MB**: private bytes (commit charge) of every process of the app, summed.
  **working set MB**: resident pages including shared DLL pages, summed (so shared pages count once per
  process). **private WS MB**: resident pages that are the process's own (perf counter
  `WorkingSetPrivate`), summed: the closest to "RAM this app costs". Read 45 s after launch, bar up.
- **Which processes.** Electron = ALL of Mantle: the main process's whole tree (GPU, utility, every
  renderer: bar, launcher, desktop layer and the other surfaces, the sidecars) **plus** every other
  process running Mantle's own `electron.exe` or `Mantle.Sidecar.exe` outside the tree. That is the crash
  supervisor (`Mantle.Sidecar.exe --supervise=<main pid>`, started detached): it gets its own row and its
  own subtotal ("outside the tree"). Native = the native exe's whole tree (today one process).
- **bar-only Electron ESTIMATE**: what a bar-only Electron app would still carry, read off the same run's
  rows: main (Mantle passes `--in-process-gpu`, so there is no GPU process: the GPU thread is in main) +
  network service + the bar's renderer + the sidecar(s) in the tree. It is an estimate, not a measured
  bar-only build, and a high one: main still carries the JavaScript of every feature.
- **CPU**: CPU time of the same set of processes over 60 s of wall time, as % of ONE core. "bar up" =
  cursor on the bottom edge (Electron's bar revealed, native's always shown); "bar hidden" (Electron
  only) = cursor away, Electron's default at rest. Before every CPU window the bench clicks charmap's
  caption, so the same window (one with no blinking caret) is in front for both apps; the cursor then rests
  inside charmap (or on the bottom edge for Electron's bar up). results.json has the foreground and a note.
- **first paint, own mark**: native = `ready.json` `firstFrameUnixMs` (the first
  `CompositionTarget.Rendering`) minus the launch time. Electron, the bar window's marks from the probe:
  `first-paint` (the page's own paint timing), `ready-to-show` (Chromium's first paint of a page not
  shown yet), `2nd rAF` (second requestAnimationFrame after did-finish-load, PERF.md's "painted"); the
  probe also records created, dom-ready, did-finish-load, first show and the frame after the first show.
- **pixels visible / settled**: from launch until the pill's rectangle on screen first differs from the
  wallpaper (mean channel difference > 6), and until 5 grabs in a row stop changing. Electron's include its
  hover reveal (the cursor sits on the bottom edge from launch).
- **works** (native): rendered (ready.json and pixels), real icons on the notepad and charmap cells, focus
  on click / click again minimizes / click again restores (through the real input queue, starting with
  whichever of the two is not in front, since a click on the front window's cell minimizes it), and
  see-through outside the pill.
- **build modes**: folder = the publish folder (MB, file count). Build s = a clean `dotnet publish` of a
  fresh copy of `native/` (own obj/bin) with a warm NuGet cache and cold build tools
  (`dotnet build-server shutdown` before each). Warnings = unique warning lines in the
  build log; (trim/AOT) = the IL2xxx / IL3xxx / CsWinRT ones. Launches 1 and 2 run until the pixels settle;
  launch 3 is the measured run (45 s, 60 s CPU, clicks). "Cold" is the first launch after publish: the
  files were just written, so they are in the OS file cache (a runner cannot drop its cache); it is not a
  cold boot. `aot-partial` = `aot` plus the one source change the AOT build asks for: every class the
  CsWinRT1028 warning names gets `partial` (today only `TransparentBackdrop`). The bench patches its temp
  copy of `native/`; the repo is never touched.

## Results (2026-11-07)

**Runner.** GitHub-hosted `windows-2025` (image win25-vs2026 20261103.298.1): Windows Server 2025 Datacenter
10.0.26100, 4 vCPU (AMD EPYC 9V74 in the runs below; other runs drew an EPYC 7763 or 9V45, so compare
numbers within one run), 16 GB, no GPU (WARP), screen 1920x1080 at 100 %, hover-to-activate off. .NET SDK
from `native/global.json` (10.0.100, rollForward latestFeature), Windows App SDK 1.7.260224002, Electron
44.4.3. App: `native/infra-app` at `511a8ff`.

| run | bench commit | cloudtest snapshot | Actions run | result |
|---|---|---|---|---|
| full | `cf37e9c` | `c81f689` | 42358360469 | `BENCH: ok (4 runs: electron light, native light, electron dark, native dark)` |
| modes | `c37afcc` (same native code path as `cf37e9c`) | `5265b67` | 42357655845 | `BENCH: ok (modes: sc-r2r works; sc works; fd-r2r works; aot did not run (exited early); aot-partial works)` |
| smoke, 1024x768 | `c37afcc` | | 42357357316 | `BENCH: ok (2 runs: native light, native dark)`, 89 s build, then about 80 s of runs |
| repeats | `fca3e9b` (full 42355490578, modes 42355508247), `cb49904` with the app at `d18ab35` (full 42354272344, modes 42354347643), `c37afcc` (full 42357377716: its Electron CPU windows had the cursor at 0,0, a bench bug fixed in `cf37e9c`; CPU excluded) | | | all `BENCH: ok` |

### Electron Mantle (live) vs native (sc-r2r build), 45 s after launch

| run | procs | private MB | working set MB | private WS MB | idle CPU, bar up (% of 1 core) | idle CPU, bar hidden | own first-paint mark (ms) | pixels visible / settled (ms) |
|---|---|---|---|---|---|---|---|---|
| Electron, light | 16 | 716.9 | 1522.3 | 480.9 | 6.82 | 1.98 | first-paint 4971 (ready-to-show 1298, first show 4859) | 4952 / 5214 |
| native, light | 1 | 42.9 | 112.3 | 27.6 | 0.16 | n/a (always shown) | first frame 453 | 538 / 698 |
| Electron, dark | 16 | 699.6 | 1520.9 | 471.4 | 0.83 | 0.89 | first-paint 3659 (ready-to-show 1157, first show 3155) | 3406 / 3778 |
| native, dark | 1 | 39.0 | 106.9 | 23.5 | 0.39 | n/a (always shown) | first frame 323 | 411 / 616 |
| bar-only Electron ESTIMATE, light | 5 | 372.0 | 669.7 | 249.6 | | | | |
| bar-only Electron ESTIMATE, dark | 5 | 375.7 | 673.7 | 253.3 | | | | |

- Memory: native costs about 1/17 of Electron Mantle's private bytes (43 vs 717 MB) and private working
  set (28 vs 481 MB), and 1/9 of even the bar-only Electron estimate (28 vs 250 MB private WS). Across
  the four full runs, light: Electron 670 to 717 MB private, 443 to 481 MB private WS (its main process
  read 198 to 238 MB); native 42.9 to 44.4 MB private, 27.4 to 28.2 MB private WS.
- Idle CPU is small and noisy on a shared 4-vCPU runner (the same window in front, charmap, every time).
  Native over 19 samples (full and modes runs): 0.10 to 1.61 % of one core, median 0.26. Electron over
  6 samples: bar up 0.83 to 6.82 % (median about 2.8), bar hidden 0.89 to 2.63 % (median about 2.0).
  Native is below Electron-with-its-bar-up in all 6 same-run pairs.
- First paint: Chromium paints the bar page while it is still hidden (ready-to-show 1.2 to 1.3 s); the
  page's own `first-paint` (5.0 / 3.7 s, light / dark) only comes once Mantle shows the bar (4.9 / 3.2 s),
  just before Mantle logs `features: started` (5.2 / 3.8 s). Native draws its first frame at 0.45 / 0.32 s
  and is on screen about 0.1 s later.
- The Electron bar on the runner shows letter tiles where the app icons go (`electron-*-bar.png`); the
  native bar shows the real icons. Not a bench problem; noted for the parity lane.

Electron per process, light / dark (private / working set / private WS MB):

| Electron process | scope | light | dark |
|---|---|---|---|
| main (also the GPU thread: `--in-process-gpu`) | tree | 238.4 / 406.8 / 169.9 | 238.8 / 402.8 / 170.0 |
| Mantle.Sidecar (sidecar) | tree | 68.5 / 115.0 / 41.6 | 68.8 / 117.9 / 42.1 |
| renderer ui/bar | tree | 38.0 / 93.3 / 28.2 | 41.1 / 97.3 / 31.3 |
| renderer ui/launcher | tree | 37.6 / 91.5 / 28.3 | 31.7 / 79.3 / 22.6 |
| renderer ui/desktop | tree | 36.2 / 86.8 / 26.4 | 34.3 / 81.7 / 25.0 |
| renderer ui/taskview | tree | 33.4 / 82.8 / 23.4 | 31.3 / 83.8 / 22.5 |
| renderer ui/topbar | tree | 33.1 / 83.0 / 23.3 | 36.0 / 93.9 / 26.4 |
| renderer ui/switcher | tree | 33.1 / 88.6 / 24.5 | 28.8 / 80.6 / 20.4 |
| renderer ui/clipboard | tree | 31.2 / 79.9 / 19.9 | 29.6 / 82.7 / 20.3 |
| renderer ui/osd | tree | 31.0 / 74.5 / 21.2 | 27.0 / 75.9 / 18.9 |
| renderer ui/snap | tree | 30.4 / 75.8 / 21.8 | 26.7 / 76.1 / 18.6 |
| renderer ui/unfold | tree | 30.3 / 77.7 / 19.7 | 31.2 / 79.6 / 21.1 |
| renderer ui/screenshot | tree | 29.1 / 89.6 / 19.0 | 27.7 / 91.0 / 18.4 |
| Mantle.Sidecar (second sidecar) | tree | 15.2 / 16.8 / 2.5 | 15.1 / 16.8 / 2.5 |
| utility: network service | tree | 11.9 / 37.8 / 7.4 | 11.9 / 38.9 / 7.4 |
| **Mantle.Sidecar `--supervise=<main pid>` (crash supervisor)** | **outside the tree** | **19.5 / 22.4 / 3.8** | **19.6 / 22.6 / 3.9** |
| sum in the tree (15 processes) | | 697.4 / 1499.9 / 477.1 | 680.0 / 1498.3 / 467.5 |
| sum, all 16 | | 716.9 / 1522.3 / 480.9 | 699.6 / 1520.9 / 471.4 |

Bar window marks (ms from launch), light / dark: created 239 / 181, dom-ready 1247 / 1067, did-finish-load
1254 / 1070, 2nd rAF after load 1280 / 1170, ready-to-show 1298 / 1157, first show 4859 / 3155, first-paint
and first-contentful-paint 4971 / 3659, frame after the first show 4912 / 3430. The probe loaded 77 / 49 ms
after launch, booted about 30 ms later, and saw all 12 windows.

**The probe, proven.** `probe-loaded-<pid>.json`, every full run, both themes: process type `browser`,
`require.resolve('electron')` = `node_modules\electron\index.js`, and `require('electron')` at load time =
`{"type":"string","hasApp":false,"value":"...node_modules\\electron\\dist\\electron.exe"}`. Under
`NODE_OPTIONS=--require`, Node runs the module before Electron's browser init makes `electron` the API, so
the spike's probe got the npm package's exe path, found no `app`, and wrote nothing. This probe hooks
`Module._load` and boots on main.js's own first `require('electron')` (`marks.json` `bootedBy`); `marks.json`
has the bar window's own paint marks in both themes.

### Build modes (native only, light)

| mode | folder MB | files | exe MB | first frame ms: median (launch 1, 2, 3) | pixels visible ms, median | private / WS / private WS MB at 45 s | idle CPU 60 s (% of 1 core) | build s | warnings (trim/AOT) | works? |
|---|---|---|---|---|---|---|---|---|---|---|
| sc-r2r (today's) | 217.7 | 495 | 0.3 | 314 (1456, 305, 314) | 409 | 43.7 / 112.6 / 28.0 | 0.13 | 61 | 1 (0) | works |
| sc | 173.9 | 495 | 0.3 | 588 (955, 588, 564) | 696 | 36.3 / 115.3 / 26.9 | 0.10 | 18 | 1 (0) | works |
| fd-r2r | 50.0 | 40 | 0.2 | 406 (477, 406, 406) | 491 | 38.8 / 116.5 / 28.0 | 0.13 | 27 | 1 (0) | works, after installing the Windows App Runtime 1.7 (67.9 MB installer, 14 s) |
| aot (the app as it is) | 105.3 | 276 | 8.2 | - | - | - | - | 38 | 2 (1) | **crashes at start** (exit 0xC000027B) |
| aot-partial (+ one `partial`) | 105.6 | 276 | 8.2 | **141** (301, 141, 140) | **238** | **31.0 / 77.1 / 22.9** | 0.18 | 32 | 1 (0) | works |

- The repeats agree. At `fca3e9b` (EPYC 7763): sc-r2r 416 ms median, 44.0 / 112.4 / 27.8 MB; sc 701 ms;
  fd-r2r 513 ms; aot-partial 174 ms, 30.9 / 77.3 / 23.2 MB; aot crashed. At `cb49904` (app `d18ab35`):
  sc-r2r 424, sc 669, fd-r2r 495 ms; aot crashed the same way.
- The one warning everywhere is CS0108 (`x:Name="Content"` in BarWindow.xaml hides `Window.Content`).
- **Why `aot` crashes**: `native-log.txt` of every aot launch ends in
  `UNHANDLED: System.InvalidCastException: Specified cast is not valid. at
  ABI.Microsoft.UI.Xaml.IWindow2Methods.set_SystemBackdrop ... at Mantle.App.BarWindow..ctor()`. The build
  said why: `TransparentBackdrop.cs(14,23): warning CsWinRT1028: Class 'TransparentBackdrop' implements
  WinRT interfaces but it or a parent type isn't marked partial`. Without `partial`, CsWinRT generates no
  WinRT vtable for the class, so handing it to WinUI (`SystemBackdrop = new TransparentBackdrop()`) fails
  under AOT. With `partial` (aot-partial) the warning is gone, no trim/AOT warning is left, and the bar
  passes every check: ready.json, pixels, real icons, the four click checks, see-through, the captured
  backdrop. Its log matches the sc-r2r log line for line.
- The expected JSON problem does not exist: the app's bench output (`ready.json`, `cells.json`) and the
  single-instance hand-off already use source-generated `JsonSerializerContext`s, and Mantle.Core reads its
  data with `JsonDocument` and is `IsAotCompatible`. The bench's old `aot-json` mode was dropped.
- fd-r2r: the folder is the smallest, but the machine must have the .NET 10 runtime and the Windows App
  Runtime 1.7 (x64) installed first, and both are serviced outside the app. The runner already had .NET;
  a user's PC may not.

### Recommendation: ship Native AOT

Memory first: AOT is the lowest on every measure (private 31 MB vs 36 to 44, working set 77 vs 112 to
117, private WS 22.9 vs 26.9 to 28.0). Then startup: first frame 141 ms vs 314 (sc-r2r), 406 (fd-r2r) and
588 (sc), the same order in every repeat. Then size: 105.6 MB, half of today's sc-r2r (217.7 MB), with
nothing to install. fd-r2r is smaller on disk but needs two runtimes installed and kept up to date
separately (the Windows App Runtime alone is a 67.9 MB installer) and starts slower. AOT also builds
faster than sc-r2r (32 to 42 s vs 61 to 70 s). Idle CPU does not separate the modes (all within noise).

The cost is AOT's code rules. What the app lane must change:

1. `native/src/Mantle.App/Native/TransparentBackdrop.cs`: `internal sealed partial class TransparentBackdrop
   : SystemBackdrop`. The only change this bench needed (proven by aot-partial).
2. Make the build fail instead of the app: in `Mantle.App.csproj`,
   `<WarningsAsErrors>$(WarningsAsErrors);CsWinRT1028;IL2026;IL2067;IL2070;IL2072;IL2075;IL2091;IL3050;IL3051</WarningsAsErrors>`,
   so the next class that implements a WinRT interface without `partial`, or a reflection call the trimmer
   cannot follow, stops the build. Keep `EnableTrimAnalyzer` / `EnableAotAnalyzer` on (they are).
3. Rules for new code: a class that derives from a WinUI/WinRT type or implements a WinRT interface and is
   handed to WinUI is `partial`; JSON only through a `JsonSerializerContext` or `JsonDocument`; `{x:Bind}`,
   not `{Binding}` (or `[GeneratedBindableCustomProperty]` on bound types); no `Activator`, `Type.GetType`
   or `Assembly.Load`.
4. Ship with `native/tools/publish.ps1 -Mode aot` (it already passes `PublishAot=true`, self-contained,
   `WindowsAppSDKSelfContained=true`), and point `native/tools/ci.ps1`'s build stage at the AOT output so
   the unit, UI, bench and parity stages test what ships.
5. Check under AOT before switching (this bench does not cover them): reading real data files (the bench's
   data root is empty, so theme.json, look.json, features.json and pins all fell back to defaults), the
   single-instance hand-off (second launch), `--quit`, and the FlaUI UI tests.
6. Optional: rename `x:Name="Content"` in BarWindow.xaml (CS0108).

### Known bench limits

- At 1024x768 (smoke without `MANTLE_CLOUD_RES`), the click on charmap's caption does not bring it to the
  front (the foreground stays on the window before; results.json `focusNote` says so), so smoke's CPU
  window there is not on the same foreground. At 1920x1080 it always worked. The click checks no longer
  depend on it.
- "Cold" launch 1 is the first launch after publish with the files in the OS cache, not a cold boot.
