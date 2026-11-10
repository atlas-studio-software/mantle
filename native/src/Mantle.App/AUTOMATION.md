# UI Automation contract of the native bar

UI tests (native/tests/Mantle.UiTests, FlaUI) find the bar by these ids. Keep them exactly; a
change here is a change to the tests.

| AutomationId | element | control type | Name | notes |
|---|---|---|---|---|
| (top-level window) | the bar's HWND | Window | `Mantle bar` (the window title) | process `MantleNative.exe` |
| `bar.window` | the XAML root (the whole window's content) | Group | `Mantle bar` | a Grid subclass with its own peer (`UiaGrid`) |
| `bar.pill` | the island | Group | `Mantle bar` | `UiaGrid` |
| `bar.start` | the Mantle mark | Button | `Start` | |
| `bar.apps` | the running-app row | Group | `Running apps` | an ItemsControl subclass with a Group peer (`UiaItemsControl`); the app buttons are its children |
| `bar.app.<n>` | one running app | Button | the app's window title (its window in front, else its first) | `<n>` is 0-based, in display order, renumbered on every change; HelpText = the exe's file name (`notepad.exe`); ItemStatus = `pinned` or `running` (a pin, or an app only running), then `; open` (it has a window: the running line), `; active` (its window is in front: the accent line), `; after divider` (the hairline before the first running-only app), `; <N> windows` (two or more: a click opens the window list), e.g. `pinned; open; active`; BoundingRectangle = its real on-screen bounds, so a click at its centre with real input hits it. Running-only apps follow the z-order (BAR-289): one moves when it is focused or minimised, so read the cells again before each click |
| `bar.winlist` | the window list: the previews' fallback when more windows than fit as 128 DIP cards (in the pop-up window `Mantle pop-up: <app> windows`) | Pane | `<app> windows` | heading, the note `N windows are too many to preview side by side in this bar, so this is the list`, one row per window |
| `bar.winlist.<n>` | one window's row | Button | the window's title (`(untitled window)` if none) | ItemStatus `active` (its window is in front: accent) or `minimized`; a click closes the list and focuses the window, or minimises the active one |
| `bar.winlist.<n>.close` | the row's X | Button | `Close this window` | closes the list and asks the window to close (a test run refuses and says so) |
| `bar.preview.<n>` | one window's preview card (hover 400 ms on a running app, or a click on an app with two or more windows; in the pop-up window `Mantle pop-up: <app> windows`) | Button | the window's title (`(untitled window)` if none) | `<n>` is 0-based, in the app's window order; ItemStatus `; `-joined: `active` (its window is in front: the accent line), `minimized`, `closing` (its X was pressed), `still open` (asked to close 4 s ago); a click closes the flyout and focuses the window, or minimises the active one |
| `bar.preview.<n>.close` | the card's X | Button | `Close this window` | asks the window to close and keeps the flyout up; a refused close (every test run) holds the card in words |
| `bar.preview.<n>.shot` | the words in the card's picture box | Text | what the box says (`asking Windows for a live picture…`, a reason, `this window has closed`, `Not closed: <why>`), or `live picture` | ItemStatus `asking`, `live; source <w>x<h>; screen <l>,<t>,<r>,<b>` (the rectangle DWM draws the window into, screen px), `fail`, `gone`, `held` |
| `bar.downloads` | Downloads | Button | `Downloads` | |
| `bar.volume` | Volume | Button | `Volume` | |
| `bar.network` | Network | Button | `Network` | |
| `bar.battery` | the battery slot | Button | `Battery` | empty and 14 px wide with no battery, as Electron's hidden battery button |
| `bar.clock` | the clock | Button | `Clock` | |
| `bar.clock.time` | the time line | Text | the time, `h:mm:ss AM` | |
| `bar.clock.date` | the date line | Text | the date (the current culture's short date) | |
| `bar.notifications` | Notifications | Button | `Notifications` | |
| `bar.more` | More | Button | `More` | |
| `bar.more.pip` | the pip on "more" | Group | `More pip` | shown while there is a problem to tell; HelpText = every problem in words, prefixed `warn: ` when one is a warning (bar.css .mpip) |

| (top-level window) | a pop-up | Window | `Mantle pop-up: <Name>` | one per kind; an instant kind stays made and is cloaked between opens (DWMWA_CLOAKED), an on-demand kind is closed |
| `popup.<key>` | a test stand-in pop-up's body (`more`, `cal`) | Text | the kind's name | only with `MANTLE_TEST_POPUPS=1` in a test run |

WinUI 3 panels (Grid, Canvas, StackPanel, Border) make no automation peer, so an AutomationId on a
plain one never reaches the tree; `Uia.cs` gives the three containers a Group peer.

## Running it for a UI test

`MantleNative.exe --test --profile-name=<x> [--user-data=<dir>] [--theme=light|dark]`, with
`MANTLE_TEST_VISIBLE=1` in the environment. Without that variable a `--test` window is drawn at
layered alpha 0, click-through and never activated (it is still in the UIA tree, but real mouse
input passes through it). A second launch with the same profile hands its arguments to the running
one and exits; `--quit` with the same profile asks it to close.

## The bar frame and the pop-ups in a test run

- A `--test` or scratch run keeps the bar **up and still** (the smoke, bench and parity stages expect it).
  `MANTLE_TEST_HOVER=1` gives it the live hover behaviour instead: the window is hidden (SW_HIDE) at rest and
  shown when the cursor reaches the bottom 4 DIP of the primary display, hidden again 180 ms after it leaves.
  Find a hidden bar by enumerating the process's top-level windows (it is not visible while hidden).
- `--dock` makes it a permanent app bar on its bottom slot. A test run's platform is passive, so nothing is
  reserved in Windows; the log says so (`dock (start): ... nothing reserved ...`).
- `MANTLE_TEST_POPUPS=1`: More opens the stand-in pop-up "more" (instant, parked, no focus) and the clock opens
  "cal" (on demand, takes the keyboard). `--test-popwatch` arms the outside watch (outside clicks and Esc
  close a pop-up); `--test-popwatch-away` also closes it when another app comes to the front. Without it a test run watches
  nothing (a live run always does).
- With `--bench-out=<dir>` the log is mirrored to `<dir>/native-log.txt`; the frame writes stable lines the
  UI tests read: `bar hidden: at rest`, `bar revealed (<why>)`, `bar hiding: <why>`, `bar hidden: window hidden,
  nothing renders`, `bar blocked: <exe> is fullscreen in front ...`, `glass: light: live ...`, `glass: stopped ...`,
  `pop-up <Name>: opening|shown at|closed (<why>), <end>` where `<end>` is `exit <ms> ms + 40 ms, then
  parked|closed` (the window plays the exit: 144 ms, or 120 ms with Windows animations off) or `parked|closed
  at once` (replaced by another pop-up, or never painted), then `pop-up <Name>: exit done, parked|closed`;
  `pop-up watch: hook|poll ...`; `bar placed (<why>): primary ... at <pct> % (dpi <dpi>); pill ...` on every
  display or DPI change (`WM_DPICHANGED <dpi>`, `WM_DISPLAYCHANGE <w>x<h>`, `... (end of a burst)`).

## Quick settings (Surfaces/Bar/QuickSettings, docs/lanes/quick-settings.md)

The pop-up opens from `bar.volume`, `bar.network` and `bar.battery`. A `--test` run reads `MANTLE_TEST_QS`
(JSON: the six parts, `powermode`, `sessions`, `wifiList`, `devices`, `btList`, `qstiles`, `project`) in place of
the sidecar, and every write or way out is a dry run (logged, never sent; nothing opens).

| AutomationId | element | control type | notes |
|---|---|---|---|
| (top-level window) `Mantle pop-up: Quick settings` | the pop-up | Window | instant: parked (cloaked) between opens |
| `qs.panel` | the panel's root | Group | Name `Quick settings` |
| `qs.tile.wifi`, `qs.tile.bluetooth`, `qs.tile.nightlight`, `qs.tile.saver` | the first four tiles | Button | HelpText = the sub line (`Off`, `Home · 5 GHz`); FullDescription = the tooltip; ItemStatus = `on`/`off` + `; dead` + `; pending` |
| `qs.tile.airplane`, `qs.tile.dnd`, `qs.tile.focus`, `qs.tile.project`, `qs.tile.cast` | the second tile row (qs-tiles.js) | Button | as the first four; Project and Cast are never `on` |
| `qs.tiles.notes`, `qs.tiles.note.<n>` | the second row's notes strip (one per refused click or dry run) | Group / Text | Name = the note (`Test run: would turn Do not disturb on (its tile)`) |
| `qs.more.wifi`, `qs.more.bluetooth`, `qs.more.sound` | the "›" buttons | Button | open the Wi-Fi, Bluetooth and sound views |
| `qs.brightness`, `qs.volume` | the sliders (0-100) | Slider | ItemStatus = the number shown; HelpText = why when dead |
| `qs.brightness.value`, `qs.volume.value` | the numbers | Text | `62%`, `—`, `unavailable` |
| `qs.mute` | the mute button | Button | Name `Mute`/`Unmute`; ItemStatus `muted`/`unmuted` |
| `qs.notes`, `qs.note.<n>` | the notes (one per dead part or refused set) | Group / Text | |
| `qs.powermode`, `qs.powermode.<efficiency/balanced/performance>`, `qs.powermode.why`, `qs.powermode.note` | the power mode row | Group / Button / Text | ItemStatus `chosen` and/or `locked` |
| `qs.power` | the footer's battery line | Text | battery.js footer words |
| `qs.windows`, `qs.gear` | the footer's two buttons | Button | |
| `qs.back`, `qs.view.title`, `qs.view.status`, `qs.view.list`, `qs.view.state`, `qs.view.why`, `qs.view.action`, `qs.view.note.<n>`, `qs.view.more` | a "›" view's frame | Button / Text / Group | |
| `qs.wifi.scan`, `qs.wifi.net.<i>`, `qs.wifi.connect`, `qs.wifi.disconnect`, `qs.wifi.auto`, `qs.wifi.password`, `qs.wifi.problem`, `qs.wifi.next`, `qs.wifi.cancel`, `qs.wifi.error`, `qs.wifi.windows` | the Wi-Fi view | | `qs.wifi.net.<i>` HelpText = the sub line |
| `qs.bt.dev.<i>`, `qs.bt.toggle`, `qs.bt.settings`, `qs.bt.error`, `qs.bt.add` | the Bluetooth view | | `qs.bt.dev.<i>` ItemStatus = its state |
| `qs.sound.devices`, `qs.sound.dev.<i>`, `qs.sound.error`, `qs.sound.mixer`, `qs.app.<i>` (+ `.value`, `.volume`, `.mute`) | the sound view | | `qs.sound.dev.<i>` ItemStatus `default` |

Log lines the tests read: `qs: the panel opened from <anchor id> (<w>x<h> DIP)`, `qs: the panel closed (<why>); its
watch is let go`, `qs: dry run (--test): <what> would ask the sidecar <verb> <args>; nothing was sent, Windows was not
changed`, `qs: dry run (--test): would open <what> (<target>); nothing was opened`, `mantle: qs.set <part>=<value> ->
ok, reads <v> (<ms> ms)` / `-> NOT DONE: <why>`, `qs: the <title> view opened` / `closed (<why>)`, `pop-up Quick
settings: resized to <h> DIP high`.
