# native/spikes/windowing: the window primitives native Mantle needs

Small standalone WinUI 3 apps, one per group of primitives, each with an automated check that runs
on a GitHub Windows runner and leaves screenshots and numbers. Nothing outside this folder uses
them. Each folder's README has the full numbers, the runs and the code to copy; this page is the index.

All runs: GitHub Windows Server 2025 runner (build 26100), 4 cores (AMD EPYC), 16 GB, one display at
1920x1080 and 100 % unless a check changes it, **no GPU** (DWM and the compositor render in software,
frames ~16 ms apart). The repo is public, so every run billed 0 minutes.

## Results

| # | primitive | status | technique (one line) | key numbers | code to copy |
|---|---|---|---|---|---|
| 1 | borderless see-through popup, no frame line | **works** | the bar spike's see-through set-up plus a subclass answering `WM_NCCALCSIZE` with 0 (client = window); `WM_STYLECHANGING` keeps the frame bits off | 0 changed pixels outside the shape at tol 8, light and dark (the spike's set-up left a 3 px ring: 3564 px, `smoke/`) | `popup/PopupWindow.cs` `Configure()`, `popup/Native.cs` `Hook`; `bar/BarWindow.cs` `SubProc` |
| 2 | glass that matches Electron's | **works with caveat** | Electron's own method: capture behind the pill with the window excluded from capture, shrink 4x (light) / 8x (dark), Gaussian blur, saturate, then XAML tint + lip + hairline + shadow | light vs the Electron shot: mean 0.58 / p95 1 (0-255); dark vs Electron's recipe rebuilt offline: 0.21 / 1; CPU light 3.38 % app + 1.82 % dwm while up and still (4 pictures a second), dark 0.00 % | `glass/CpuGlass.cs`; `glass/App.xaml.cs` `Tick`, `Refresh`, `WatchWallpaper`, `StripFrame`, `AddShadow`, `After` |
| 3 | bar placement | **works** (animation: caveat) | `HWND_TOPMOST` + `NOACTIVATE` with a WinEvent/60 ms re-raise; taskbar on OS auto-hide only (`ABM_SETSTATE`, state saved and restored); dock = real app bar; hover = background-thread cursor poll; fullscreen block like `foregroundBlocksReveal` | hidden at rest 0.195 % of a core; dock reserves 48 px (work area 1080 -> 1032); compositor reveal 37 frames in 559 ms, p95 16.26 ms, 0 gaps | `bar/BarWindow.cs` `Compute`, `StartPoll`, `Reveal`, `AnimateComp`, `Morph`, `ArmWatch`/`Raise`; `bar/Shell.cs` `Taskbar`, `AppBar`, `ShellState`, `Foreground.Blocks` |
| 4 | desktop layer under the icons | **works** (input: caveat) | `0x052C` to Progman, the WinUI window `WS_CHILD` of Progman straight under `SHELLDLL_DefView` (progman-front); 1 s `IsWindow` recheck; clicks through a low-level mouse hook | attach 31 ms; survives Win+D; rebuilt 861 ms after an Explorer restart; 19.4 MB private WS, 0.0 % CPU | `desktop/DesktopLayer.cs` `Attach`, `Recheck`, `Fit`, `Detach`, `KeepFrameless`; `desktop/App.xaml.cs` `MouseRoute`; `desktop/DeskIcons.cs` |
| 5 | click-through and hit-testing | **works** (caveat: topmost, see Risks) | `SetWindowRgn` to the shape + 1 px: clicks and moves outside go to any process or thread below, the glass and AA corners stay intact (it does not blank WinUI, unlike Electron's DComp window) | 6/6 moves + 1/1 click passed to another process, own-thread window passes too, 0 CPU; `HTTRANSPARENT` and `SetRegionRects(Passthrough)` pass nothing | `popup/PopupWindow.cs` `ApplyRegion()` (re-apply after every resize) |
| 6 | multi-monitor + DPI | **works with caveat** | per-monitor v2; `WM_DPICHANGED` (after WinUI's own handling) and `WM_DISPLAYCHANGE` -> 400 ms burst -> re-place; geometry re-read on every reveal; second monitor = Amyuni IddCx virtual display on the runner | 100/125/150 %: pill 1120x48 / 1400x60 / 1680x72, crisp (0 soft edge px); plug, primary switch, unplug all re-place; mixed DPI 125 % beside 100 % | `bar/BarWindow.cs` `SubProc`, `DisplayChanged`, `Replace`, `Compute`, `Reveal`; checks: `bar/probe/BarProbe.cs` `DpiSetPercent`, `SetPrimary` |
| 7 | instant-open cost | **measured** | first changed pixel on screen, 20 trials per path | cold process 316 / 333 ms (median / p95); new window in a running process 79 / 110 ms (p95 95-126 over six runs); parked window reshown 47 / 58-63 ms; each parked window +1.0 MB private, 0 CPU | `popup/PopupWindow.cs` `Park()` / `Show()` |
| 8 | global shortcuts | **works** (injected keys only) | `WH_KEYBOARD_LL` on its own thread with its own message loop, the sidecar's key machine, VK 0xFF partner key, raw-input re-arm; handlers posted to the UI `DispatcherQueue` | Win tap 0.67 ms median / 0.95 p95 to the UI handler; Alt+Space 0.30 / 0.50; Start, Task View and the system menu stay shut; survives a 2 s UI block; 22.2 MB, 0.3 % CPU | `hotkeys/KeyHook.cs` (`Start`, `Proc`, `Step`, `OnRaw`, `Install`) |

**Decision for 7:** no keep-ready process is needed (cold is ~0.33 s and the bar keeps the process
alive anyway). Keep ONE parked window per popup kind that must feel instant (launcher, bar menus):
~1 MB each, 0 CPU, inside the 100 ms p95 rule. Everything else opens a new window on demand.

## Final runs (each lane's own branch; the merge into native/windowing was not re-run)

| check | summary line | run | tested commit |
|---|---|---|---|
| `smoke/check.ps1` | `WINDOWING smoke: ok (3 of 3 checks)` | 20261107-072342-a2c76c | aab5c87 |
| `popup/check.ps1` | `WINDOWING popup: ok (41 of 41 checks)` | 20261107-083907-a0f323 | c9fe89e |
| `glass/check.ps1` | `WINDOWING glass: ok (15 of 15 checks)` | 20261107-081415-3a33da | 71be004 |
| `bar/check.ps1` | `WINDOWING bar: ok (44 of 44 checks)` | 20261107-080537-c72da0 | 05704a3 |
| `bar/check-dpi.ps1` | `WINDOWING bar-dpi: ok (18 of 18 checks)` | 20261107-081907-4c1995 | 59fb2ae |
| `desktop/check.ps1` | `WINDOWING desktop: ok (16 of 16 checks)` | 20261107-083535-e390ca | e5ae2d8 |
| `hotkeys/check.ps1` | `WINDOWING hotkeys: ok (20 of 20 checks)` | 20261107-084007-227bdf | e5ad10f |

Since those commits, only the lane READMEs changed, with one exception: `bar/probe/BarProbe.cs`
changed for the DPI check after `bar/check.ps1`'s last run, and `check.ps1` was not run again on it.
The folders are disjoint and harness/ is unchanged, so the merge itself changes no code path.

## Risks for the full port (most serious first)

1. **Topmost is refused to a window created while ANOTHER process holds the foreground.**
   `SetWindowPos(HWND_TOPMOST)` returns success, but `WS_EX_TOPMOST` stays off, so the popup
   opens under the foreground app (popup lane, runs 4-5; runs 1-5 read it as false click-through
   passes). Reproduced, not explained, not fixed. A window created while the process had the
   foreground stays topmost (parked windows, and the bar, which the check starts in front). In
   Mantle, a popup opened from a timer, a notification or a background event will hit this. Round
   2 must test it (z-bands, the foreground rules, a pre-created topmost window) and every check
   should prove its z-order from `EnumWindows`, as `popup/check.ps1` does.
2. **No GPU anywhere.** The runner composes in software. Two things are unverified on real
   hardware: the GPU glass routes, which drew black (host backdrop) and a solid slab (acrylic)
   here, and the cost of the compositor animation, which used more app CPU than SetWindowPos here.
   All timings are quantised to the runner's ~16 ms frames.
3. **Light glass refresh must follow visibility.** The 4-pictures-a-second refresh (3.38 % + 1.82 %
   dwm) runs whenever the glass window is up; the glass spike has no hidden state and never stops
   it. The bar's 0.195 % hidden figure was measured without glass. Near-zero idle depends on the
   port starting the refresh on reveal and stopping it on hide; no combined bar + glass run has
   measured that. (Option: light could take dark's still-per-reveal route, 0 % while up, at the
   cost of not following windows that move behind the bar.)
4. **Dark glass is matched to a reconstruction.** The Electron dark reference shot was taken with
   a grey band in its wallpaper still, so dark is matched to Electron's recipe applied offline,
   not to fresh Electron pixels.
5. **Desktop layer input and lifetime.** The icon layer covers the whole desktop, so every
   interactive part of the layer needs the mouse-hook route. An Explorer restart destroys the
   window and its XAML tree (no `Window.Closed`), so state must live outside it. The child
   window shares an input queue with Explorer, so a hung layer UI thread could stall the desktop
   (not measured). The video-wallpaper branch of `Recheck` is not exercised.
6. **Hotkeys were tested with injected keys only** (`--accept-injected`). Not tested: real
   hardware keys, a real Windows hook drop (26100 never dropped it, so re-arm is proven only by a
   simulated drop), an elevated window in front, sleep/resume and the lock screen. On the Server
   SKU a Win tap opens Search, not Start.
7. **DPI coverage.** 150 % was tested on the single display only, and mixed DPI only at 125 %
   beside 100 % (Windows allows only up to 125 % on the 1024x768 virtual display). Not tested: a
   real second monitor, dragging across mixed DPI, and popup / desktop / hotkeys at any scale
   other than 100 %.
8. **A new window per open misses the 100 ms p95 rule** (95-126 ms p95 over six runs). Hence the
   parked window for each instant surface.
9. One bar sub-check, the reveal fill colour in `bar/check.ps1`, reads `ready.json`'s colours
   without decoding the base64, yet it passed. Treat that one sub-check as unproven; the reveal
   itself is proven by the screen trace and the screenshots.
10. **Size.** Every spike app ships its own self-contained .NET + Windows App SDK (~217 MB a
    folder). The real port should be one process, or share one runtime.

## Harness changes the lanes asked for (not made)

- `Grab` should return `, [WinHarness]::Grab(...)` so PowerShell keeps the `byte[]` (glass).
- A shared "bench desktop" step: minimise every window, taskbar on auto-hide, clear the bottom of
  the screen, a known-colour backdrop (glass, popup, desktop).
- Helpers now copied per lane, to move into `Native.cs`: z-order proof (`IsAbove`), `SendAndWatch`
  / `StartAndWatch` (input -> first changed pixel), window `Move`, fixture windows, `ForceForeground`,
  `RootAt` (popup, bar).
- A desktop-duplication frame sampler for animation pacing (bar).

## Layout

- `Directory.Build.props`: the shared build (WinUI 3, unpackaged, self-contained; the same settings
  as native-spike/MantleBar). Each app's csproj only names itself. It and `Directory.Packages.props`
  stop MSBuild's walk up the tree, so a later native/ build file does not change these spikes.
- `harness/Common.ps1`: dot-sourced by every check: runner set-up (1920x1080, wallpaper,
  transparency on), `Build-Spike`, screen grabs, memory and CPU of a process tree, `Check` /
  `Note`, and `Finish-Check`, whose last line is the one to judge a run by:
  `WINDOWING <lane>: ok (N of N checks)` or `WINDOWING <lane>: FAILED (...)`.
- `harness/Native.cs`: Win32 helpers for the checks (grab, stray-pixel count, screen size, process tree).
- `smoke/`: the harness smoke run, and the spike's see-through window as is (the 3 px ring baseline).
- `popup/` (1, 5, 7), `glass/` (2), `bar/` (3, 6), `desktop/` (4), `hotkeys/` (8).

## Running a check (runner only)

```
pwsh -NoProfile -File native/spikes/windowing/<folder>/check.ps1
```

The checks change the runner's screen size, scale, displays, wallpaper, theme and taskbar, and move
the cursor and send keys. Never run them on a desktop someone is using. On a public repo they run
free in cloudtest's committed mode:
`cloudtest --committed --os windows --no-install --timeout 30 --artifacts "native/spikes/windowing/out/**" -- pwsh -NoProfile -File native/spikes/windowing/<folder>/check.ps1`.
Output: `native/spikes/windowing/out/<lane>/` (results.json, summary.md, screenshots, app logs).
