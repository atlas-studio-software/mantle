# desktop: primitive 4, the desktop layer

**Status: works** on the runner (Windows Server 2025, build 26100), `WINDOWING desktop: ok (16 of 16 checks)`.

**Technique:** send Progman `0x052C`, make the WinUI 3 window `WS_CHILD` and `SetParent` it to
Progman, then place it in z-order straight under `SHELLDLL_DefView` (mode `progman-front`). A
window procedure wrapper answers `WM_NCCALCSIZE` (client = window), so no frame ring shows. A 1 s
recheck rejoins the desktop after an Explorer restart, or makes a new window if the old one was
destroyed. Clicks go through a low-level mouse hook, because Explorer's icon layer is on top.

Copy `DesktopLayer.cs` (`Attach`, `Recheck`, `Fit`, `Detach`, `KeepFrameless`). For clicks, copy
`MouseRoute` in `App.xaml.cs` and `DeskIcons.cs` (icon rects read from Explorer's list view).

## Numbers (run 20261107-083535-e390ca)

| claim | result |
|---|---|
| auto mode on 26100 | `progman-front`; Progman's children, top to bottom: `SHELLDLL_DefView > WinUIDesktopWin32WindowClass (ours) > WorkerW` |
| attach | 31 ms; fit 0,0 1920x1080; first frames on screen 1.1 s after launch |
| renders over the wallpaper | 99.83 % of 36,942 sampled px are the layer's colour (icons, button and taskbar excluded) |
| no frame ring | outer 3 px 99.69 % layer colour (run 2, before the `WM_NCCALCSIZE` fix: 3 px of wallpaper on the left, top and right) |
| under the icons | the probe icon's rect is 34.7 % not layer colour (icon and label drawn over us); a ring 6-14 px around it is 100 % layer colour |
| click on the layer's own button | reaches it through the mouse route (button turns yellow); hook decision max 48-57 us |
| click with no route | does NOT reach the layer: `WindowFromPoint` is Explorer's `SysListView32`, whose root is Progman (0 clicks) |
| click on an icon | stays Explorer's: the icon is selected, layer clicks 1 -> 1 |
| Win+D | the layer stays: 99.91 % layer colour, still a visible child of Progman; notepad minimised |
| Explorer restart | the process survives. Our window is DESTROYED with the old Progman (it is not orphaned); a new one is made and attached 861 ms after the new Progman appears (Explorer was back in 421 ms). Layer colour 99.91 %, icons over it again, the route still works |
| cost | 1 process; private 32.6 MB, private working set 19.4 MB; idle CPU 0.0 % of one core over 5 s |

Every mode, run once each (Notes `mode_*`):

| mode | result |
|---|---|
| `progman-front` (auto) | renders, under the icons |
| `workerw-child` | renders (inside the WorkerW, under DefView); a wallpaper app's player would cover it |
| `workerw-sibling` | does not exist on 26100: DefView stays under Progman |
| `progman` (bottom of Progman) | 0 % visible: covered by the WorkerW |
| `show-first` order (show, then attach after the first frame) | same as attach-first, so attach-first is the one to use (it never shows a full-screen top-level window) |
| **GDI baseline** (`--kind=gdi`, progman-front) | **0 % visible.** Progman has `WS_EX_NOREDIRECTIONBITMAP` (ex `0x00200080`), so a GDI-painted child has no surface to draw into. The DirectComposition content (WinUI, and Electron today) does render. So any native fallback must not be a plain GDI window |

## Caveats and findings

- **Clicks.** The icon layer covers the whole desktop, so the layer never gets mouse input
  directly. This matches the sidecar's `DeskClick.cs` finding. The spike's route takes left clicks
  inside the layer's own interactive rects, and only when they are not on an icon. A real shell
  needs the same route for every interactive part of the layer. Keyboard focus on the desktop stays
  Explorer's.
- **Explorer restart = window destroyed.** On 26100, a cross-process child is destroyed along with
  its parent. `DispatcherShutdownMode = OnExplicitShutdown` keeps the WinUI process alive, and the
  recheck builds a new `Window`. The old XAML tree is lost, so state must live outside it. No crash
  or `UNHANDLED` showed up in the app log. WinUI's `Window.Closed` did NOT fire, so a destroyed
  layer is only noticed by polling `IsWindow` (the 1 s recheck).
- **Style bits.** WinUI puts `WS_DLGFRAME | WS_SYSMENU` back after the swap. The reported style is
  still `0x54480000` even with the `WM_STYLECHANGING` filter. The ring is gone because of
  `WM_NCCALCSIZE`, not because the bits went away.
- **Shared input queue.** A child window in another process's tree attaches the two threads' input
  queues. A hung UI thread in the layer process could therefore stall Explorer's desktop input.
  Not measured here.
- The runner's console window covered part of the screen. The check minimises every window first
  (`Shell.Application.MinimizeAll`).

## What the runner cannot show

- A video wallpaper app (Lively): the "player re-inserted above us" branch of `Recheck` is not exercised.
- Multiple monitors, other scale factors (100 % only) and a resolution change after attach.
- Real (non-injected) mouse input. The clicks are `SendInput`, which the route accepts.

## Screenshots (out/desktop/)

`wallpaper.png` (before), `layer.png`, `after-button-click.png`, `after-win-d.png`,
`after-explorer-restart.png`, and `mode-<kind>-<mode>.png` for each mode run.
