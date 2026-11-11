# Lane native-r7-idle: what wakes the app while the bar rests hidden

The idle UI test (`BarFrame/HoverTests.cs` `Bar_and_glass_idle_cpu_hidden_at_rest_and_shown`) failed now and then on
"hidden at rest" (1.042 %, 1.146 %, 1.354 % against the 1.0 % limit) with the code unchanged. This lane measured the
rest window thread by thread on the CI runner, then from inside the app, and made the check read the real CPU.

## How it is measured now

- `BarFrame/ThreadCpu.cs` (test side, from outside the app): per thread over the window, the CPU time Windows charges,
  the exact CPU cycles (QueryThreadCycleTime, as ms at the runner's cycle rate), context switches (wakeups), the
  thread's name and the module of its start address. Every window goes to `idle-threads.txt`.
- `Surfaces/Bar/Frame/IdleProbe.cs` (app side, test runs only, `MANTLE_TEST_IDLE_PROBE=<delay ms>,<window ms>`): from
  the first hide at rest it counts each message the UI thread takes or is sent (by message, window class and
  parameters, with the gaps between them), lists the UI thread's windows, focus and caret, and turns on
  `Mantle.Core.Lifecycle.IdleTally`: host-thread posts and timers (`HostLoop.Delay`, `HostClock.Later`) named by their
  delegate's type, UI posts (the bar's links, the surface host, the hover poll), the bar's own timers (clock, live
  glass) and every sidecar event and call. Off, a tally hit is one volatile read.
- The test judges hidden at rest by **cycles** over **45 s** (was: charged time over 15 s). The limit is unchanged
  (1.0 % of one core); the charged number is still printed. Why: Windows charges whole 15.6 ms ticks to whichever
  thread runs at the tick, and on the runner that over- and under-counts short timer-driven wakes by up to 3.5x.

## What wakes at rest (45 s hidden at rest, light and dark, runs 43065508718 to 43071733470)

Whole process: 2075 to 2421 wakeups in 45 s, 0.38 to 0.65 % of one core by cycles. Charged time over the same
windows: 0.139 to 1.423 % (run 43070224737, light: 1.423 % charged against 0.415 % by cycles: the old check would fail).

| thread | wakeups / s | cycle ms in 45 s | what drives it |
|---|---|---|---|
| UI thread | 10.5 to 11.7 | 87 to 125 | dispatcher work: 736 to 803 wake messages (0x0060 to DcppUserAdapterWindowClass) |
| bar hover poll | 12.0 to 12.5 | 9.9 to 12.5 | `Thread.Sleep(80)` + GetCursorPos (BAR-027, by design) |
| DWM compositor (in-process) | 2.7 to 3.2 | 32 to 41 | frames committed by the visible top bar and desktop |
| Windows thread pool (5 to 7 threads) | ~15 together | ~35 to 45 | the task strip's test beat timer, sidecar line plumbing |
| mantle host | 1.1 to 1.3 | 10 to 11 | 98 to 103 sidecar lines posted |
| sidecar stdout | 1.1 | 3 to 4.5 | `topbarstats` events, about one a second |

The UI thread's dispatcher wakes, by source (from the probe):

- **The task strip's test-only UI beat: 4 a second.** `Surfaces/Bar/Tasks/BarWindow.Tasks.cs:127`
  (`if (Host.Args.Test) StartUiBeat();`) starts a 250 ms `System.Threading.Timer` (lines 140-155) that enqueues a
  ping on the UI thread. It runs while the bar is hidden, where no click can land, so every hidden-at-rest number in
  a --test run includes it (and a live run never has it). Owner: the task strip lane. The gap between wakes of this
  kind is 250 ms at the 90th percentile.
- **The top bar's stats: 1 a second.** 36 to 45 `topbarstats` sidecar events in 45 s, each posted to the top bar
  surface (`SurfaceLink ... TopBarStats`, 45 UI posts). The top bar window is visible (1920x28), so this is a shown
  surface's cost. Owner: the top bar lane (`Features/TopBar/TopBarFeature.cs:167`).
- **The desktop layer's clock: 1 a second** (`Surfaces/Desktop/DesktopSurface.cs:356-363`), while the layer is shown
  and uncovered (it is on the runner). Owner: the desktop lane.
- The bar itself: no UI-thread work at rest. Its clock ticks once a minute while hidden (BAR-417), the live glass is
  stopped (light and dark), the hover poll enqueues nothing outside the bottom band. Of the bar, tray, notifications
  and calendar, the probe counted one tray batch timer (`Features/Tray/TrayBatch`) in one 45 s window, nothing else.
- The sidecar health ping: 3 in 45 s (15 s), with its replies. Negligible next to the above; left as it is.
- The runner's timer resolution was 15.625 ms in every window (nothing in the app raises it).

Electron for comparison (bench/README.md, the earlier bench): bar hidden 0.89 to 2.63 % of one core (median about
2.0) over 60 s across its 16 processes. Native's whole process at rest here: 0.38 to 0.65 % by cycles, including the
visible top bar and desktop and the test-only beat.

## Not done here

- No waker in this lane's files was found at rest beyond the hover poll (0.02 % of one core), so no app-side fix:
  the before and after numbers above are the same code. The test-only beat (task strip lane) is the biggest single
  periodic waker; stopping it while the bar is hidden (the frame's `Hiding` / `Revealed` events) would take 4 UI
  wakes and 4 thread-pool wakes a second out of every hidden-at-rest reading.
- The hover poll could wait for the cursor to move instead of polling (EVENT_OBJECT_LOCATIONCHANGE on OBJID_CURSOR,
  or raw input as a wake-up, polling only while the cursor moves). Not attempted: it changes BAR-027's timing for
  0.02 % of a core.
