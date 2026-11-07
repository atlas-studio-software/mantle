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
| `bar.app.<n>` | one running app | Button | the app's window title (its window in front, else its first) | `<n>` is 0-based, in display order, renumbered on every change; HelpText = the exe's file name (`notepad.exe`); BoundingRectangle = its real on-screen bounds, so a click at its centre with real input hits it |
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
  "cal" (on demand, takes the keyboard). `MANTLE_TEST_POPWATCH=1` arms the outside watch (outside clicks and Esc
  close a pop-up); `=away` also closes it when another app comes to the front. Without it a test run watches
  nothing (a live run always does).
- With `--bench-out=<dir>` the log is mirrored to `<dir>/native-log.txt`; the frame writes stable lines the
  UI tests read: `bar hidden: at rest`, `bar revealed (<why>)`, `bar hiding: <why>`, `bar hidden: window hidden,
  nothing renders`, `bar blocked: <exe> is fullscreen in front ...`, `glass: light: live ...`, `glass: stopped ...`,
  `pop-up <Name>: opening|shown at|closed (<why>)`, `pop-up watch: hook|poll ...`.
