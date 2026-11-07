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
| 5 | click-through, hit-testing | **works** (one caveat) | window region, or `WS_EX_LAYERED` + `WS_EX_TRANSPARENT` toggled by a poll; HTTRANSPARENT only across processes |
| 7 | instant-open cost | **measured** | keep the process; a parked window only where 100 ms p95 matters |

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

### 5. Click-through: works, with one caveat

A WinForms window of ANOTHER process (`fixture.ps1`, it logs every mouse message) sits under the
popup's transparent top-left corner. Real input through the queue (`SendInput`): 6 moves and a click
in the corner, a click on the shape after resting there, and one straight after arriving.

| hit= | other process: corner moves / click | shape click | own-thread window under the corner | cost |
|---|---|---|---|---|
| none (control) | 0 / 0 (popup took it) | popup | - | - |
| nchittest: `WM_NCHITTEST` -> `HTTRANSPARENT` outside the shape (top level + its same-thread child windows) | 6 / 1 | popup | **NO: the popup keeps the click** | 0 CPU |
| region: `SetWindowRgn` = the shape + 1 px | 6 / 1 | popup | RUN3 | 0 CPU |
| ncregion: `InputNonClientPointerSource.SetRegionRects(Passthrough)` on the 4 margins | 6 / 1 | popup | RUN3 | 0 CPU |
| layered: `WS_EX_LAYERED` (alpha 255) + `WS_EX_TRANSPARENT` toggled by a 16 ms cursor poll | 6 / 1 | popup | yes | poll: 0-31 ms CPU per 10 s (<= 0.3 % of a core); clickable 15.6 ms median, 29.3 ms p95 after the cursor enters |

- The shape renders identically under every technique (glass exact, stray 0).
- **SetWindowRgn does NOT blank a WinUI 3 window** (Electron's DirectComposition window went blank
  with a region, main.js `PILL_RADIUS_DIP`). RUN3 resize / corners.
- HTTRANSPARENT behaves the OPPOSITE of its documentation here: it passed the input to another
  process's window and NOT to a window of the popup's own thread (runs 1 and 2; that window is a
  plain WinUI window). For Mantle that is the case that matters (a popup over its own bar), so do
  not use it alone.
- Recommendation: RUN3.

**Copy:** `PopupWindow.cs` `ApplyRegion()` (region), `ApplyHit()` + `Poll()` (layered).

Screenshots: `hit-<mode>.png`, `hit-samethread-<mode>.png`, `hit-screen.png`.

### 7. Instant-open cost: measured

Timing = the check's command (or `Process.Start`) to the first changed pixel at the shape's centre,
polled in a tight loop (`CheckNative.cs` `SendAndWatch` / `StartAndWatch`); DWM frames on the runner
are ~16 ms apart, so every number is quantised to that. 20 trials each (run 2).

| path | first pixels median / p95 | app's own view | user rule |
|---|---|---|---|
| cold: new process -> popup | 364 / 381 ms | process start -> first Rendering 252 / 282 ms (start -> OnLaunched 146 ms) | <= 1 s: **meets** |
| warm process, NEW Window | 80 / 126 ms (the process's first window 217 ms) | command -> Rendering 34 / 51 ms; `new Window` 17 ms | <= 100 ms p95: **misses** (median meets) |
| parked, AppWindow.Hide/Show | 47 / 64 ms | | meets |
| parked, ShowWindow SW_HIDE / SW_SHOWNOACTIVATE | 47 / 64 ms | | meets |
| parked, moved to -32000 and back | 47 / 62 ms | | meets |
| parked, DWMWA_CLOAK | 47 / 48 ms | | meets |

Memory (one process, private bytes / private working set): idle with no window ever 21.2 / 9.9 MB;
the first window shown +11.9 / +9.1 MB (most of it XAML's one-off start-up); that window parked
33.1 / 19.0 MB; **each further parked window +1.2 / +0.8 MB**. After opening and closing 20 windows:
39.3 / 24.5 MB (+5.5 MB over one shown; managed heap 0.9 MB, so native XAML/DComp caches).

CPU: a process with 3 parked windows: 0 ms in 30 s. Idle with no window: 0 ms in 10 s. Each park mode:
0 ms in 10 s. Hidden = 0 CPU holds.

**Decision:** native needs no keep-ready PROCESS work beyond the process being alive (cold is
~0.37 s, well inside 1 s, but the bar keeps the process alive anyway). A new window per open is
80 ms median but 126 ms p95, so it misses the 100 ms p95 rule. For a popup that must feel instant
(the launcher, the bar's menus), keep ONE parked window per kind: 47-64 ms p95, ~1 MB each, 0 CPU.
Everything else opens a new window on demand (RAM first). Park with `DWMWA_CLOAK` (fastest, 48 ms
p95, the window keeps its place and size) or `AppWindow.Hide` (64 ms p95).

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
the input queue) and a known-colour backdrop window.
