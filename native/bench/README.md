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
10.0.26100, AMD EPYC 7763, 4 vCPU, 16 GB, no GPU (WARP), screen 1920x1080 at 100 %. .NET SDK from
`native/global.json` (10.0.100, rollForward latestFeature), Windows App SDK 1.7.260224002. Electron 44.4.3.
Code: branch `native/infra-bench` at `fca3e9b` (bench) with the app at `511a8ff` (`native/infra-app`);
cloudtest snapshots `76f4d82` (full, run 42355490578) and `7cfe2f1` (modes, run 42355508247). The repeat at
`cb49904` (app at `d18ab35`): full run 42354272344, modes run 42354347643.

```
BENCH: ok (4 runs: electron light, native light, electron dark, native dark)
BENCH: ok (modes: sc-r2r works; sc works; fd-r2r works; aot did not run (exited early); aot-partial works)
```

### Electron Mantle (live) vs native (sc-r2r build), 45 s after launch

| run | procs | private MB | working set MB | private WS MB | idle CPU, bar up (% of 1 core) | idle CPU, bar hidden | own first-paint mark (ms) | pixels visible / settled (ms) |
|---|---|---|---|---|---|---|---|---|
| Electron, light | 16 | 671.7 | 1469.2 | 443.8 | 4.53 | 2.63 | first-paint 4875 (ready-to-show 1601, first show 4649) | 4813 / 5041 |
| native, light | 1 | 43.3 | 112.1 | 27.4 | 0.89 | n/a (always shown) | first frame 531 | 617 / 792 |
| Electron, dark | 16 | 665.7 | 1483.4 | 438.7 | 3.44 | 2.50 | first-paint 3988 (ready-to-show 1656, first show 3708) | 3717 / 4172 |
| native, dark | 1 | 38.8 | 107.1 | 23.8 | 0.39 | n/a (always shown) | first frame 386 | 479 / 682 |
| bar-only Electron ESTIMATE, light | 5 | 336.0 | 635.9 | 217.6 | | | | |
| bar-only Electron ESTIMATE, dark | 5 | 335.8 | 637.1 | 218.9 | | | | |

- Memory: the native bar costs about 1/15 of Electron Mantle's private bytes (43 vs 672 MB) and 1/16 of
  its private working set (27 vs 444 MB), and 1/8 of even the bar-only Electron estimate (27 vs 218 MB
  private WS). The repeat run agrees within 3 % (light: Electron 669.9 / 1461.3 / 442.6, native 44.4 /
  112.9 / 28.2).
- Idle CPU is small and noisy on a shared 4-vCPU runner: across the two full runs and the two modes runs
  native read 0.16 to 1.6 % of one core (11 samples), Electron 1.7 to 4.5 % with its bar up and 0.9 to
  2.6 % with it hidden. Native is below Electron-with-its-bar-up in all four pairs; treat sub-1 %
  differences as noise.
- First paint: Chromium paints the bar page while it is still hidden (ready-to-show about 1.6 s); the
  page's own `first-paint` (4.9 / 4.0 s, light / dark) only comes once Mantle shows the bar (4.6 / 3.7 s),
  just before Mantle logs `features: started` (5.0 / 4.2 s). Native draws its first frame at 0.53 / 0.39 s
  and is on screen about 0.1 s later.
- The Electron bar on the runner shows letter tiles where the app icons go (screenshots
  `electron-*-bar.png`); the native bar shows the real icons. Not a bench problem; noted for the parity lane.

Electron per process (private / working set / private WS MB):

| Electron process | scope | light | dark |
|---|---|---|---|
| main (also the GPU thread: `--in-process-gpu`) | tree | 201.3 / 374.1 / 137.5 | 200.4 / 369.4 / 138.0 |
| Mantle.Sidecar (sidecar) | tree | 68.6 / 114.8 / 41.5 | 68.8 / 118.0 / 42.2 |
| renderer ui/bar | tree | 38.9 / 92.7 / 28.5 | 39.5 / 94.3 / 28.7 |
| renderer ui/launcher | tree | 37.2 / 86.4 / 27.9 | 33.3 / 85.8 / 25.1 |
| renderer ui/switcher | tree | 34.9 / 90.5 / 26.5 | 29.5 / 78.2 / 20.2 |
| renderer ui/desktop | tree | 34.8 / 85.9 / 26.0 | 33.9 / 84.8 / 24.1 |
| renderer ui/topbar | tree | 33.1 / 84.3 / 23.6 | 39.6 / 95.1 / 28.0 |
| renderer ui/clipboard | tree | 30.4 / 79.9 / 20.0 | 31.2 / 82.5 / 21.0 |
| renderer ui/snap | tree | 29.8 / 73.4 / 20.8 | 27.0 / 74.2 / 18.4 |
| renderer ui/taskview | tree | 29.2 / 78.0 / 20.2 | 32.9 / 83.4 / 23.9 |
| renderer ui/screenshot | tree | 29.1 / 89.1 / 19.0 | 27.5 / 89.9 / 18.3 |
| renderer ui/unfold | tree | 29.1 / 71.2 / 19.6 | 26.1 / 75.4 / 17.5 |
| renderer ui/osd | tree | 28.6 / 72.2 / 18.8 | 29.3 / 74.4 / 19.4 |
| Mantle.Sidecar (second sidecar) | tree | 15.2 / 16.8 / 2.6 | 15.2 / 16.9 / 2.6 |
| utility: network service | tree | 12.0 / 37.5 / 7.5 | 11.9 / 38.5 / 7.4 |
| **Mantle.Sidecar `--supervise=<main pid>` (crash supervisor)** | **outside the tree** | **19.5 / 22.4 / 3.8** | **19.6 / 22.6 / 3.9** |
| sum in the tree (15 processes) | | 652.2 / 1446.8 / 440.0 | 646.1 / 1460.8 / 434.8 |
| sum, all 16 | | 671.7 / 1469.2 / 443.8 | 665.7 / 1483.4 / 438.7 |

Bar window marks (ms from launch), light / dark: created 276 / 234, dom-ready 1599 / 1551, did-finish-load
1599 / 1575, ready-to-show 1601 / 1656, 2nd rAF after load 1625 / 1639, first show 4649 / 3708, first-paint
and first-contentful-paint 4875 / 3988, frame after the first show 4701 / 3847. The probe loaded 92 / 56 ms
after launch, booted 41 / 44 ms later, and saw 12 windows.

**The probe, proven.** `probe-loaded-<pid>.json` from both themes: process type `browser`,
`require.resolve('electron')` = `node_modules\electron\index.js`, and `require('electron')` at load time =
`{"type":"string","hasApp":false,"value":"...node_modules\\electron\\dist\\electron.exe"}`. So under
`NODE_OPTIONS=--require`, Node runs the module before Electron's browser init makes `electron` the API; the
spike's probe got the npm package's exe path, found no `app`, and wrote nothing. This probe hooks
`Module._load` and booted on main.js's own first `require('electron')` (`marks.json` `bootedBy`), and
`marks.json` has the bar window's own paint marks in both themes.

### Build modes (native only, light, the same runner type)

| mode | folder MB | files | exe MB | first frame ms: median (launch 1, 2, 3) | pixels visible ms, median | private / WS / private WS MB at 45 s | idle CPU 60 s (% of 1 core) | build s | warnings (trim/AOT) | works? |
|---|---|---|---|---|---|---|---|---|---|---|
| sc-r2r (today's) | 217.7 | 495 | 0.3 | 416 (621, 378, 416) | 525 | 44.0 / 112.4 / 27.8 | 0.26 | 70 | 1 (0) | works |
| sc | 173.9 | 495 | 0.3 | 701 (786, 695, 701) | 812 | 37.4 / 115.5 / 27.3 | 1.46 | 23 | 1 (0) | works |
| fd-r2r | 50.0 | 40 | 0.2 | 513 (606, 512, 513) | 617 | 38.1 / 115.7 / 27.5 | 0.31 | 37 | 1 (0) | works, after installing the Windows App Runtime 1.7 (67.9 MB installer, 17 s) |
| aot (the app as it is) | 105.3 | 276 | 8.2 | - | - | - | - | 49 | 2 (1) | **crashes at start** (exit 0xC000027B) |
| aot-partial (+ one `partial`) | 105.6 | 276 | 8.2 | **174** (210, 174, 172) | **253** | **30.9 / 77.3 / 23.2** | 0.29 | 42 | 1 (0) | works |

- The one warning everywhere is CS0108 (`x:Name="Content"` in BarWindow.xaml hides `Window.Content`). The
  repeat run (app at `d18ab35`) gave the same picture: sc-r2r 424 ms median, 43.9 / 112.3 / 27.8 MB; sc
  669 ms; fd-r2r 495 ms; aot crashed the same way.
- **Why `aot` crashes**: `native-log.txt` of every aot launch ends in
  `UNHANDLED: System.InvalidCastException: Specified cast is not valid. at
  ABI.Microsoft.UI.Xaml.IWindow2Methods.set_SystemBackdrop ... at Mantle.App.BarWindow..ctor()`. The build
  said why: `TransparentBackdrop.cs(14,23): warning CsWinRT1028: Class 'TransparentBackdrop' implements
  WinRT interfaces but it or a parent type isn't marked partial`. Without `partial`, CsWinRT generates no
  WinRT vtable for the class, so handing it to WinUI (`SystemBackdrop = new TransparentBackdrop()`) fails
  under AOT. With `partial` (aot-partial) the warning is gone, there are no trim/AOT warnings left, and the
  bar passes every check: ready.json, pixels, real icons, the four click checks, see-through, the captured
  backdrop. Its log matches the sc-r2r log line for line.
- The expected JSON problem does not exist: the app's bench output (`ready.json`, `cells.json`) and the
  single-instance hand-off already use source-generated `JsonSerializerContext`s, and Mantle.Core reads its
  data with `JsonDocument` and is `IsAotCompatible`. The bench's old `aot-json` mode was dropped.
- fd-r2r: the folder is the smallest, but the machine must have the .NET 10 runtime and the Windows App
  Runtime 1.7 (x64) installed first, and those are serviced outside the app. The runner already had .NET;
  a user's PC may not.

### Recommendation: ship Native AOT

Memory first: AOT is the lowest on every measure (private 30.9 MB vs 37 to 44, working set 77 vs 112 to
116, private WS 23.2 vs 27.3 to 27.8). Then startup: first frame 174 ms vs 416 (sc-r2r), 513 (fd-r2r) and
701 (sc). Then size: 105.6 MB, half of today's sc-r2r (217.7 MB) and nothing to install. fd-r2r is smaller
on disk but needs two runtimes installed and kept up to date separately (the Windows App Runtime alone is a
67.9 MB installer), and starts slower. AOT also builds faster than sc-r2r (42 vs 70 s). Idle CPU does not
separate the modes (all within the runner's noise).

The cost is AOT's code rules, which the app lane must keep:

1. `native/src/Mantle.App/Native/TransparentBackdrop.cs`: `internal sealed partial class TransparentBackdrop
   : SystemBackdrop` (the only change this bench needed; proven by aot-partial).
2. Make the build fail instead of the app: in `Mantle.App.csproj` add
   `<WarningsAsErrors>$(WarningsAsErrors);CsWinRT1028;IL2026;IL2067;IL2070;IL2072;IL2075;IL2091;IL3050;IL3051</WarningsAsErrors>`
   (or all IL2xxx/IL3xxx), so the next class that implements a WinRT interface without `partial`, or a
   reflection call the trimmer cannot see, stops the build. Keep `EnableTrimAnalyzer` / `EnableAotAnalyzer`
   on (they are).
3. Rules for new code: every class that derives from a WinUI/WinRT type or implements a WinRT interface and
   is handed to WinUI is `partial`; JSON only through a `JsonSerializerContext` or `JsonDocument` (never
   `JsonSerializer.Serialize(obj)` on an unknown type); `{x:Bind}`, not `{Binding}` (or
   `[GeneratedBindableCustomProperty]` on the bound types); no `Activator`/`Type.GetType`/`Assembly.Load`.
4. Ship with `native/tools/publish.ps1 -Mode aot` (it already passes `PublishAot=true`, self-contained,
   `WindowsAppSDKSelfContained=true`), and point `native/tools/ci.ps1`'s build stage at the AOT output so
   the unit, UI, bench and parity stages test what ships.
5. Not proven by this bench, to check under AOT before switching: reading real data files (the bench's
   data root is empty, so theme.json, look.json, features.json and pins were all defaults), the
   single-instance hand-off (second launch), `--quit`, and the FlaUI UI tests.
6. Optional: rename `x:Name="Content"` in BarWindow.xaml (CS0108).
