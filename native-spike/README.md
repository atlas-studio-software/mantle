# native-spike: the Mantle bar in C# + WinUI 3

A prototype for one decision: should Mantle's UI move from Electron to native C#? It is not a
migration. Nothing outside this folder uses it, and it changes no Mantle file.

## What it draws

Only what the real bar (`ui/bar`) shows at rest:

- the inset pill: min(1120, screen - 28) DIP wide, 48 tall, 14 above the bottom edge, radius 24
  (light) / 18 (dark), with the three island layers (blurred backdrop picture, tint + 1 px hairline,
  1 px lip) and, in light, the drop shadow;
- the Mantle mark on the left;
- the running apps, centred on the island, with their real icons (read from the exe), the running
  line, and the accent line for the app in front. A click brings the app forward, or minimizes it
  when it is already in front;
- on the right: separator, downloads, volume, network, the clock (h:mm:ss AM over the date),
  notifications, more.

Colours, radii, spacing and type come from `ui/shared/glass-light.css` and `ui/bar/bar.css`, as the
`Lg*` resources in `MantleBar/Tokens.xaml` (light and dark).

Not built (each is migration work): flyouts and menus, pins, Store apps (AUMID), tray icons, chips,
media, battery, the real volume level and Wi-Fi bars, look.json (look-sync), the hover reveal and
its motion, drag to reorder, keyboard access.

## Build and run (Windows, .NET SDK only)

```
dotnet publish native-spike/MantleBar/MantleBar.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o native-spike/bin/MantleBar
native-spike/bin/MantleBar/MantleBar.exe --theme=light
```

## Bench

`bench/run-bench.ps1` builds the spike and measures it against the real Electron Mantle on the same
GitHub Windows runner, in one job. It runs Mantle live, so it is for a runner only. See the header
of the script for what it measures.

### Results, 2026-11-06 (spike f15fb8a, Windows Server 2025 runner, 4 vCPU, 1920x1080 at 100 %)

Memory is summed over each app's whole process tree, 45 s after launch, with the bar up.
Electron = ALL of Mantle running live (bar, launcher, desktop layer and every other surface,
plus 2 Mantle.Sidecar.exe). It does NOT include Mantle's crash supervisor, which runs outside
the tree. Native = the bar only, in one process.

| run | procs | private MB | working set MB | private WS MB | CPU, bar up (% of 1 core, 60 s) | CPU, bar hidden | pixels on screen after launch |
|---|---|---|---|---|---|---|---|
| Electron light (cold) | 15 | 706.6 | 1501.7 | 485.7 | 5.47 | 0.81 | 5056 ms |
| Native light (cold) | 1 | 44.5 | 110.6 | 29.3 | 0.05 | - | 491 ms (first frame 393) |
| Electron dark | 15 | 638.6 | 1458.4 | 432.7 | 0.83 | 1.04 | 1803 ms |
| Native dark | 1 | 41.0 | 105.8 | 25.8 | 0.05 | - | 348 ms (first frame 273) |

Electron light, per process (private MB): main 236.5, sidecars 69.8 + 15.2, network service 11.9,
11 renderers of 26-41 each. A bar-only Electron would still carry the main process, a renderer
and the sidecar: about 350-375 MB private, against 44.5 for the native bar.

The native bar passed every check in both themes: real icons, focus on click, click again to
minimize, click again to restore (through the real input queue), and see-through outside the pill.

Known gaps:
- A 1 px light frame line shows around the native window's rectangle, outside the pill: the window
  still has a thin frame. Likely fix (not tried): plain WS_POPUP style + SWP_FRAMECHANGED.
- Electron's pixel time includes its hover reveal (the cursor sits on the bottom edge from launch).
  Its own paint mark is missing: the probe never loaded in the Electron main process (unexplained).
- The Electron light CPU sample (5.47 %) may still include start-up work; dark (0.83 %) is the steadier one.
- The output folder is 217 MB (self-contained .NET 10 + Windows App SDK, ReadyToRun). Electron's own
  install size was not measured.
