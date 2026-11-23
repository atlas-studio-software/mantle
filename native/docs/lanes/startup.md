# Lane notes: the start-up freeze and the corners row race (native-r8-startup)

Branch: `native/r8-startup` (from `native/main` with `native/r8-flakes` merged; later `native/main` 414f4f10 and the
finished `native/r7-media3` merged too).

## 1. The start-up freeze: the OSD's light shadow, drawn on the UI thread

Every CI app log showed the UI thread held for 0.5 to 2.3 s at start-up (the bar's test-only beat: "the UI thread ran
queued work 2282 ms late", run 43827392539). It began as the sidecar came up, so it looked like the sidecar's ready
handlers. It was not.

### How it was measured

`Shell/Diagnostics/StallMeter.cs` (test only: a `--test` run with `MANTLE_TEST_STALL=1`). From the bar's first frame,
for 10 s, its own thread queues one empty job on the UI thread at Normal priority (behind whatever is queued, as a
click's work would be), waits for it, rests 50 ms and queues the next. A job 100 ms or more late is a stall, logged with:

- the UI thread's own CPU time over it (GetThreadTimes): "computing" or "mostly waiting";
- the named work the UI thread was in (`Mantle.Core.Logging.UiSlice`): state handlers ("a new state (<link>)"), link
  events ("<event> (<link>)"), surface makes, surface host jobs, the strip's pushes, the host's theme, data changes, and
  every await continuation on the UI thread (a timing SynchronizationContext, installed only while the meter is on);
  with nothing named, the last named slice before it ("unnamed work after ...");
- for a wait, the thread's wait chain (Wait Chain Traversal: a lock, a COM call, a SendMessage).

The last line is "stall meter: the longest UI-thread stall in the first 10 s was N ms ...". Off in every other run;
in a meter run it ends after 10 s, so nothing runs at rest.

### What it found (CI runner, light theme)

- Run 43855935098 (c84dfccf): one stall, 755 ms, its own CPU 734 ms (computing), no named work: so XAML layout, a XAML
  event or an unnamed job, not a sidecar handler and not a wait.
- Run 43857089231 (f43bb327): the same start with each AtStart surface made on first use instead (a temporary
  test-only hook, removed since):

| made on first use | longest stall |
|---|---|
| none (as shipped) | 947 ms (CPU 859) |
| launcher | 881 ms |
| desktop | 860 ms |
| topbar | 874 ms |
| ontop, toasts | 893 ms |
| **osd** | **75 ms** |
| all six | 13 ms |
| none, `--no-backdrop` | 896 ms |

The OSD alone. It is made hidden and parked at start; its first layout raises `SizeChanged`, and `Shadow()` drew the
light `--lg-shadow` there with `CssShadow.Bitmap`: three Gaussian layers (sigma up to 28 px, 169 taps) in doubles over
the whole window, on the UI thread. Dark draws no shadow, which is why a dark start never showed it as clearly.
It lines up with the sidecar only because both happen about 0.3 s into the start.

### The fix

`OsdSurface.Shadow()` runs the blur on a pool thread (`CssShadow.Into`, pure arithmetic on its own unmanaged planes)
into one reused buffer, one at a time, the newest size winning when it lands; the UI thread only makes the bitmap
(`CssShadow.FromPixels`, one copy). The pixels are the same.

### The test

`Startup/StartupStallTests.The_UI_thread_is_never_held_over_250_ms_in_the_first_10_s` reads the meter's last line.
It never touches the app through UI Automation while the meter runs, and does not read the bar's beat.
Limit 250 ms: with the OSD out of the start the longest stall was 75 ms (and 13 ms with no surface at all), so 250 ms
leaves over 3x room for a slow runner and still fails the 600 to 2300 ms freeze. After the fix: 26 ms (run
43858590584).

### Not changed (other users of CssShadow)

- The desktop menu (`DeskMenuWindow.Shadow`) and the toast cards (`ToastsSurface.Shade`) still draw it on the UI
  thread, on first open and per card. Neither is at start, but a first right-click on the desktop pays the blur. Not
  measured here; the same `Into`/`FromPixels` pair fits both.

## 2. The corners row race: a click before the host is up

`CornersRowTests` failed once (run 43850529950): "This bar has no road to Mantle's commands", 0.3 s into the start.

Cause: the bar is shown and clickable in `OnLaunched`, before the host thread runs `HostStart`, which posts
`AttachQuickSettings` (the panel's road to qs.*, corners.*, gamemode.*) only after the surfaces' start. A click on the
speaker in that window opened the panel with no road: the Hot corners row put up its error, and the panel's other
open-time reads and its `qs.watch` were never made, until it was opened again. A user who clicks the bar in Mantle's
first moments meets the same.

Fix: the row keeps "Loading…" when it has no road yet, and `QsPanel.Attach` makes the open panel's reads (game mode,
hot corners) and its watch when the road arrives while it is open.

Test: `Startup/EarlyClickTests` holds the host thread's start 3 s (`MANTLE_TEST_HOST_LATE`, test only), clicks the
speaker in that window, checks the row waits ("Loading…"), then fills in ("2 on · push the pointer in") once the host
attaches. Passed in run 43855935098, with `CornersRowTests` and the desktop menu test.

Not checked: the bar's other pop-ups attached the same way (calendar, notifications, media, downloads, the Helm badge)
may show a "not up yet" state on a very early click; not traced here.

## 3. Asked by main: the downloads double press (run 43854350493)

- The look.json reload there is not what held the UI thread. The read and parse run on the data watcher's timer thread
  ("data: look-sync\look.json: ok" at 47.474); the UI job (`OnDataChanged`: one log line and a blur compare, no
  repaint when the blur is the same) ran 1 ms later (47.475), so the UI thread was free then. It is Mantle reacting to
  its own write, though: this run's LookShell writes look.json (rev 1 by mantle) at start, and the data watch's 2 s
  check reads it back about 2 s later as if another app had changed it. Harmless, wasted (Core/Data, not this lane).
- The guard (`DownloadsKeys.Takes`, judged at processing time with `Environment.TickCount64`) is not changed here. WinUI
  gives no time on a key event; `GetMessageTime()` during the Click is the likely press time, but a Click raised from a
  deferred job would read an older message's time and could hold back a real second press. Left for a lane that can
  test it.

## Shared lines

- `Mantle.Core/Lifecycle/SurfaceWires.cs`: `StateFeed.Subscribe(handler, who = null)` and the deliveries through UiSlice.
- `Mantle.Core/Lifecycle/SurfaceSet.cs`: a surface's Create through UiSlice.
- `Mantle.Core/Logging/StartupMarks.cs`: `MainTicks`.
- `Mantle.App/App.xaml.cs`: the meter's start, `MANTLE_TEST_HOST_LATE`, two posts through UiSlice.
- `Mantle.App/Shell/Host/SurfaceHost.cs`, `Surfaces/Bar/Tasks/BarTasksHost.cs`: posts through UiSlice.
- `Surfaces/Osd/CssShadow.cs` (+`Into`, `FromPixels`), `Surfaces/Osd/OsdSurface.cs` (`Shadow`).
- `Surfaces/Bar/QuickSettings/QsPanel.cs` (`Attach`), `Surfaces/Desktop/CornersRow.cs` (no-road line).

## Runs

- 43855935098 at c84dfccf (meter in, no fix): build ok, unit 994 of 994, UI 3 of 4 (the stall test failed by design:
  755 ms).
- 43857089231 at f43bb327 (the share table above): unit 994 of 994, UI 9 of 10 (the stall test, 947 ms).
- 43858590584 at 8d3e6d8b (the fix; native/main 414f4f10 and native/r7-media3 merged): NATIVE-CI: ok (3 ok, 0
  skipped); unit 1032 of 1032; UI 5 of 5 (the stall test, EarlyClickTests, CornersRowTests, the desktop menu test,
  the OSD test). The longest UI-thread stall in the first 10 s: 26 ms (was 755 and 947 ms), no stall of 100 ms or
  more in 177 queued jobs.
- Full run 43859364384 at 6576f9a4: NATIVE-CI: FAILED (ui failed; 16 ok, 0 skipped). UI 93 of 95. Both failures are
  the same start-up ordering, newly reachable once the freeze is gone: the top bar's start-time show ("topbar: shown
  (sidecar ready)") activates its window ("topbar: window activated; keys go to nothing") 10 to 30 ms after a pop-up
  the test opened early took the keyboard.
  - `DownloadsKeysTests.Arrows_walk_the_rows_...`: the shelf took the keyboard at 11:46:55.649, the top bar was shown
    at 55.661 and activated at 55.662, and the shelf closed "(another window took focus)" at 55.655: "the first Down
    did not reach the newest row".
  - `NotificationsTests.The_centre_takes_the_keyboard_...`: the centre took the keyboard at 11:46:04.548, the top bar
    was shown at 04.558 and activated at 04.577: "the centre did not give the keyboard back".
  Before the fix the 0.6 to 2.3 s freeze delayed the top bar's show past these tests' first pop-up. A user who opens a
  pop-up in Mantle's first second loses its keyboard the same way. The show is in the top bar lane's files
  (Surfaces/TopBar), so it is reported, not changed here: the start-time show should not activate the top bar's
  window (show it without activation, as the other surfaces are), or not while a pop-up holds the keyboard.
