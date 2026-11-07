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
| `aot-root.targets` | used by build mode `aot-json` only (see below); never part of the app's project |

## How to run

Everything runs through `cloudtest` from a checkout whose HEAD is pushed (the public repo runs for free in
committed mode). Each command brings back `native/out/**` (results.json, summary.md, logs, screenshots).

```
# native only, light + dark, short holds: what CI's bench stage calls (about 3-4 min of build, < 2 min of runs)
cloudtest --committed --os windows --no-install --timeout 30 --env MANTLE_CLOUD_RES=1920x1080 --artifacts "native/out/**" -- "pwsh -NoProfile -File native/bench/run-bench.ps1 -Mode smoke"

# Electron Mantle live vs native, light then dark (the recipe installs npm deps and builds the sidecar)
cloudtest --recipe mantle-core-public --timeout 45 --env MANTLE_CLOUD_RES=1920x1080 --artifacts "native/out/**" -- "pwsh -NoProfile -File native/bench/run-bench.ps1 -Mode full"

# the build-mode table (native only, light)
cloudtest --committed --os windows --no-install --timeout 60 --env MANTLE_CLOUD_RES=1920x1080 --artifacts "native/out/**" -- "pwsh -NoProfile -File native/bench/run-bench.ps1 -Mode modes"
```

Options: `-Project <csproj>` (default `native/src/Mantle.App/Mantle.App.csproj`; the exe name is the
project's `AssemblyName`, asked of MSBuild), `-Exe <path>` (skip the build in full/smoke), `-Out <dir>`
(default `native/out/bench`), `-Res WxH` (default `$env:MANTLE_CLOUD_RES`; the runner desktop starts at
1024x768), `-BuildModes sc-r2r,sc,fd-r2r,aot,aot-json`, `-Themes light,dark`.

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
  rows: main + GPU + network service + the bar's renderer + the sidecar(s) in the tree. It is an
  estimate, not a measured bar-only build.
- **CPU**: CPU time of the same set of processes over 60 s of wall time, as % of ONE core. "bar up" =
  cursor on the bottom edge (Electron's bar revealed, native's always shown); "bar hidden" (Electron
  only) = cursor away, Electron's default at rest.
- **first paint, own mark**: native = `ready.json` `firstFrameUnixMs` (the first
  `CompositionTarget.Rendering`) minus the launch time. Electron, the bar window's marks from the probe:
  `first-paint` (the page's own paint timing), `ready-to-show` (Chromium's first paint of a page not
  shown yet), `2nd rAF` (second requestAnimationFrame after did-finish-load, PERF.md's "painted"); the
  probe also records created, dom-ready, did-finish-load, first show and the frame after the first show.
- **pixels visible / settled**: from launch until the pill's rectangle on screen first differs from the
  wallpaper (mean channel difference > 6), and until 5 grabs in a row stop changing. Electron's include its
  hover reveal (the cursor sits on the bottom edge from launch).
- **works** (native): rendered (ready.json and pixels), real icons on the notepad and charmap cells, focus
  on click / click again minimizes / click again restores (through the real input queue), and see-through
  outside the pill.
- **build modes**: folder = the publish folder (MB, file count). Build s = a clean `dotnet publish` of a
  fresh copy of `native/` (own obj/bin) with a warm NuGet cache. Warnings = unique warning lines in the
  build log; (trim/AOT) = the IL2xxx / IL3xxx / CsWinRT ones. Launches 1 and 2 run until the pixels settle;
  launch 3 is the measured run (45 s, 60 s CPU, clicks). "Cold" is the first launch after publish: the
  files were just written, so they are in the OS file cache (a runner cannot drop its cache); it is not a
  cold boot.

RESULTS_PLACEHOLDER
