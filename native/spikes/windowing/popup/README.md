# windowing / popup: see-through popups, click-through, open cost

Primitives 1, 5 and 7 of the native windowing lane. One WinUI 3 app (`Popup.exe`) that opens popups
on command over a named pipe, and `check.ps1`, which drives it on a GitHub Windows runner
(Windows Server 2025, 1920x1080 at 100 %, 4 vCPU) and judges by the screen, not by the app.

```
pwsh -NoProfile -File native/spikes/windowing/popup/check.ps1        # runner only
```

Last line: `WINDOWING popup: ok (N of N checks)`. Output: `native/spikes/windowing/out/popup/`
(results.json, summary.md, the PNGs named below, log.txt, cold/<n>/cold.json).

## Results

| # | primitive | status | technique |
|---|---|---|---|
| 1 | borderless see-through popup, no frame line | **works** | the bar spike's set-up + `WM_NCCALCSIZE` returns 0 |
| 5 | click-through, hit-testing | **works** (caveat: topmost) | `SetWindowRgn` to the shape (+1 px); HTTRANSPARENT and Passthrough regions pass nothing |
| 7 | instant-open cost | **measured** | new window on demand; ONE parked window per instant popup (58-63 ms p95, ~1 MB, 0 CPU) |

### 1. No frame line: works

**Technique:** the bar spike's see-through window as is: OverlappedPresenter without border/title bar,
DWM corners off, border colour none, `WS_EX_TOOLWINDOW`, `TransparentBackdrop`,
`DwmExtendFrameIntoClientArea(-1)` and a black `WM_ERASEBKGND`. Add ONE thing: a subclass that answers
`WM_NCCALCSIZE` with 0, so the client area is the whole window and there is no non-client frame left
to draw. Also `DWMWA_TRANSITIONS_FORCEDISABLED` (no show/hide animation).

**Copy:** `PopupWindow.cs` `Configure()` (with `_hook.NcCalc = true`) and `Native.cs` `Hook` / `Proc`
(the `0x0083` case) / `ExtendFrame`. `TransparentBackdrop.cs` as is.

Numbers (run 2; stray = pixels changed by more than 8 outside the shape, 2 px AA band left out,
over the window plus a 32 px margin where a frame line or a DWM shadow would land):

| frame= | light: stray / maxDelta | dark | what it is |
|---|---|---|---|
| spike (baseline) | 3564 / 50 | 3564 / 215 | the 3 px `WS_DLGFRAME` ring (client 394x194 inside 400x200) |
| popup (style stripped to WS_POPUP only) | 49072 / 50 | 49072 / 215 | worse: the presenter puts `WS_DLGFRAME|WS_SYSMENU` straight back, and the window paints opaque (white in light, a grey slab in dark) |
| **nc** (+ `WM_NCCALCSIZE` 0) | **0 / 0** | **0 / 0** | client = window 400x200, DWM frame bounds = window |
| ncp (+ NCRENDERING_POLICY disabled) | 0 / 0 | 0 / 0 | adds nothing over nc |

- No DWM shadow: the 32 px margin is unchanged (maxDelta 0) in both themes.
- The glass is drawn exactly: light fill .45 white over the backdrop (205,210,220) = 228,230,236
  (want 228,230,236); the .10 ink hairline 185,190,201 (want 185,190,200); dark fill 23,26,29 (want
  23,25,30), white .09 hairline 59,70,89 (want 59,70,89). Max error 0.8 per channel. WinUI's Border
  draws the hairline OUTSIDE the fill (background inside the border), as the CSS does.
- Corners: an opaque test shape (radius 24) has 172 of the 272 arc-band pixels partly covered (anti-
  aliased), 0 inside pixels short of full, 0 pixels painted outside the arc. Same in both themes.
- Hide (AppWindow.Hide) then show: the desktop is clean while hidden (0 px), the re-shown popup is
  pixel-identical to the first show (0 px). Resize 400x200 -> 520x240: client follows, stray 0, glass
  still exact.
- Caveat: the window STYLE is still `0x96480000` (`WS_DLGFRAME|WS_SYSMENU` kept by the presenter).
  Harmless here (`WM_NCCALCSIZE` gives it no area, the popup is `WS_EX_NOACTIVATE`), but a later lane
  that wants plain `WS_POPUP` has to stop the presenter re-adding it (or not use OverlappedPresenter).
- Light/dark: the check flips `AppsUseLightTheme`/`SystemUsesLightTheme` and broadcasts
  `WM_SETTINGCHANGE "ImmersiveColorSet"`, then starts a fresh app per theme.
- Screenshots: `frame-<light|dark>-<spike|popup|nc|ncp>.png`, `frame-<theme>-nc-reshown.png`,
  `frame-<theme>-nc-resized.png`, `corner-<theme>.png`.

### 5. Click-through: works (window region); one caveat for every technique

A WinForms window of ANOTHER process (`fixture.ps1`, it logs every mouse message) sits under the
popup's transparent top-left corner. Real input through the queue (`SendInput`): 6 moves and a click
in the corner, a click on the shape after resting there, and one straight after arriving. Then a
plain WinUI window of the popup's OWN thread under the corner (Mantle's case: a popup over its own
bar). Each technique runs in its own process, and the check proves from the real z-order
(`EnumWindows`) that the popup is above the fixture before and after the corner click.

Run 6 (the first run with that proof):

| hit= | other process: corner moves / click | own-thread window | shape click | cost |
|---|---|---|---|---|
| none (control) | 0 / 0 (the popup takes it) | the popup takes it | popup | - |
| **region**: `SetWindowRgn` = the shape + 1 px | **6 / 1** | **passes** | popup | 0 CPU, nothing to poll |
| layered: `WS_EX_LAYERED` (alpha 255) + `WS_EX_TRANSPARENT` toggled by a 16 ms cursor poll | 6 / 1 | passes | popup | poll 16-62 ms CPU per 10 s (0.16-0.6 % of a core, runs 3-6); the shape is clickable 15 ms median, 27 ms p95 after the cursor enters |
| nchittest: `WM_NCHITTEST` -> `HTTRANSPARENT` outside the shape (top level + its same-thread children) | 0 / 0 | does NOT pass | popup | 0 CPU |
| ncregion: `InputNonClientPointerSource.SetRegionRects(Passthrough)` on the margins | 0 / 0 | does NOT pass | popup | 0 CPU |

- **Use the window region.** `SetWindowRgn` does NOT blank a WinUI 3 window (Electron's
  DirectComposition window went blank with one: main.js `PILL_RADIUS_DIP`). The glass is exact under
  it, it is still drawn after a resize (re-apply the region), and an opaque test shape keeps its
  anti-aliased corners (172 partly covered arc pixels, 0 inside pixels short, 0 outside) because the
  region is 1 px larger than the shape. Clicks and moves outside it go to whatever is below, any
  process or thread, at no cost. The poll-free answer to Electron's setIgnoreMouseEvents + HitTest.cs.
- HTTRANSPARENT passes nothing on a WinUI 3 window, not even to its own thread (its documented case).
  The XAML content lives in child windows (`DesktopChildSiteBridge`, `InputNonClientPointerSource`),
  all hooked here, and the click still lands in the popup. Do not use it.
- **Caveat (every technique, and a real Mantle risk): a popup opened while ANOTHER process holds the
  foreground does not become topmost.** `SetWindowPos(HWND_TOPMOST)` returns success and
  `WS_EX_TOPMOST` stays off (runs 4-5: every window the app opened after the fixture was clicked; the
  app re-asserted it at open, at the first frame and on demand, and it never took). It then opens
  UNDER the foreground app's windows. Runs 1-5 read that as "click-through" (the fixture was on top).
  In Mantle the bar's own click or a hotkey normally gives the process the foreground first, but a
  popup opened from a timer, a notification or the sidecar will not. The bar lane must test this
  (z-band / `SetForegroundWindow` rules / a pre-created topmost window), and every later check should
  prove its z-order as this one does.

**Copy:** `PopupWindow.cs` `ApplyRegion()` (call it after every resize), `EnsureTopmost()` (and keep
its note: it is how the problem shows up). Layered, if ever needed: `ApplyHit()` + `Poll()`.

Screenshots: `hit-<none|ncregion|nchittest|region|layered>.png`, `hit-region-resized.png`,
`hit-region-solid.png`, `corner-region.png`, `hit-samethread-<mode>.png`, `hit-screen.png`.

### 7. Instant-open cost: measured

Timing = the check's command (or `Process.Start`) to the first changed pixel at the shape's centre,
polled in a tight loop (`CheckNative.cs` `SendAndWatch` / `StartAndWatch`); the runner's frames are
~16 ms apart, so every number is quantised to that. 20 trials each, run 6 (runs 2-5 agree within
about 1 frame).

| path | first pixels median / p95 | app's own view | user rule |
|---|---|---|---|
| cold: new process -> popup | 316 / 333 ms (runs 2-7: p95 333-412 ms) | process start -> OnLaunched ~145 ms, -> first Rendering ~250 ms (run 2-5) | <= 1 s: **meets** |
| warm process, NEW Window | 79 / 110 ms (the process's first window 203 ms) | command -> Rendering ~34 ms; `new Window` ~17 ms | <= 100 ms p95: **borderline** (p95 95-126 ms over runs 2-7, met only in run 7; median ~80 ms meets) |
| parked, AppWindow.Hide / Show(false) | 47 / 63 ms | | meets |
| parked, ShowWindow SW_HIDE / SW_SHOWNOACTIVATE | 47 / 63 ms | | meets |
| parked, moved to -32000 and back | 47 / 63 ms | | meets |
| parked, DWMWA_CLOAK | 47 / 58 ms | | meets |

Memory (one process, private bytes / private working set, run 6): idle with no window ever ~21 / 10
MB; the FIRST window +12.6 / +9.7 MB (mostly XAML's one-off start-up, paid whether it is parked or
not); **each further parked window +1.0 / +0.6 MB**. Opening and closing 20 windows leaves +5.5 MB
private over one shown (managed heap 0.9 MB, so native XAML/DComp caches).

CPU: 3 parked windows over 30 s: 0 ms (runs 2-5), 16 ms in run 6 (one 15.6 ms scheduler tick, 0.05 %
of a core); idle with no window: 0 ms in 10 s; each park mode: 0 ms in 10 s. Hidden = 0 CPU holds.

**Decision:** no keep-ready process work is needed beyond the process being alive (cold is ~0.33 s,
inside 1 s, and the bar keeps the process alive anyway). A new window per open is ~80 ms median but
95-126 ms p95 over six runs (only one under 100), so it does not reliably meet the 100 ms p95 rule. For a popup that must feel instant (the launcher,
the bar's menus) keep ONE parked window per kind: 58-63 ms p95, ~1 MB each, 0 CPU. Everything else
opens a new window on demand (RAM first). Park with `DWMWA_CLOAK` (fastest, keeps place and size)
or `AppWindow.Hide`. A parked window created while the process had the foreground keeps its topmost
(see the 5 caveat): another reason to park the instant ones.

**Copy:** `PopupWindow.cs` `Park()` / `Show()`.

## What the runner cannot test

- **Scale other than 100 %**: the runner's one display is 100 %; 150 % per-monitor is not tested in
  this lane (the bar lane owns DPI). The code scales the shape by `GetDpiForWindow`, untested.
- Real hardware GPU and a real monitor's refresh rate (the runner composes in software; timings are
  quantised to its ~16 ms frames).
- A user's mouse: input is synthetic (`SendInput`), through the real input queue.
- Other windows of real apps: the fixtures are a WinForms form (another process) and a plain WinUI
  window (the popup's own thread).

## Files

- `App.xaml.cs`: the app; `--cold` (open one popup at once, write cold.json) or `--pipe=<name>`
  (commands: open / show / park / resize / close / info / events / fixture / gc / quit).
- `PopupWindow.cs`: one popup (frame=, hit=, park= techniques). `Native.cs`: Win32/DWM and the one
  subclass proc. `TransparentBackdrop.cs`: the see-through brush.
- `check.ps1`, `CheckNative.cs` (check side: pipe client, screen-change timing, input, coverage maths),
  `fixture.ps1` (the other-process window and the known-colour backdrop).

Harness changes this lane would want in `harness/` (not made here): `MinimizeOthers` (the runner's
console window sits over the test area and comes forward when focus moves: run 1 measured it instead
of the wallpaper), `SendAndWatch`/`StartAndWatch` (screen-change timing), `Move` (a mouse move through
the input queue), `IsAbove` (prove the z-order before trusting a click result: runs 1-5 were
wrong without it) and a known-colour backdrop window.
