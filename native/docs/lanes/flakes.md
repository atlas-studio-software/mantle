# Lane notes: UI test flakes (native-r8-flakes)

Four UI tests failed once or twice each on 2026-11-16, a different one per run. A merge needs one fully green
full run, so each one was traced to its cause. No sleep was lengthened, no check loosened, no limit lowered, and
no test is skipped or retried.

Branch: `native/r8-flakes` (from `native/main` 63a89535).

## 1. Apps that do not fit (the "+N" panel never took the keyboard): a product bug

Test: `StripTests.Apps_that_do_not_fit_wait_behind_plus_N_and_its_panel_acts_like_the_strip`.
Failed in runs 43830388635 and 43836510792: "bar tasks: overflow: the panel has the keyboard" was never logged.

Evidence (the app logs `strip-uitest-strip-overflow-*-native-log.txt` of both runs):

- `pop-up Apps that do not fit: opening`, `bar tasks: overflow: the panel opened with 15 app(s)`, then
  `pop-up Apps that do not fit: shown at ...`, and then nothing at all for 3 s. Neither "has the keyboard" nor
  "keys do not reach the panel" was logged, and PopupHost logged neither "took the keyboard" nor "could not take
  the keyboard".
- The UI thread was alive the whole time: the test's UIA reads of the panel (`bar.ovf`, its 15 tiles, their
  rectangles) were all answered between "shown" and the keyboard check.
- In the passing runs (for example 20261111-170203 and 20261111-171323 in native-r3-tasks/.cloudtest) the line
  "took the keyboard on its own ask (focused)" follows "shown" by 17 to 30 ms.

Cause: the panel asked for the keyboard on the open, before its window was on screen. PopupHost answered "the
pop-up is not open", and the panel then asked again on a 50 ms `DispatcherQueueTimer` held only by a local
variable. Every path through the retry logs a line, so in the two failed runs the retry never ran: the timer never
fired. A timer nothing holds can be collected before it fires; that is the likely reason, and the design (ask
before the window is on screen, then poll) was wrong either way. In real use the same thing left the arrows and
Esc reaching nothing.

Fix: the same as the Downloads shelf's (274a16a5 on `native/r7-media3`). `PopupHost.OnScreen` is raised once a
pop-up's first frame is shown, and the panel asks for the keyboard there, once, with no timer. The app menu
(`BarWindow.JumpList.cs`, `JlKeyboard`) had the same open-then-poll timer and gets the same fix.

Shared lines: `native/src/Mantle.App/Shell/Popup/PopupHost.cs` +5 (the `OnScreen` event and its raise). These are
byte for byte the lines of 274a16a5, so a later merge of `native/r7-media3` meets the same text.

## 2. Theme set switches the bar within 1 s: a test bug (the probe read the runner's taskbar)

Test: `ThemeTests.Theme_set_switches_the_bar_within_1_s_of_the_call_and_answers_the_full_view`.
Failed once (run 43836206025): "the bar never went dark within 8 s of theme.set (luminance 238.0 -> 238.0)".

Evidence:

- The app did switch: its log has "theme: Dark -> Mantle is dark", "topbar: theme dark", "backdrop: captured;
  dark blur 5 px" and "look: the host's theme is dark (Dark); the bar now shows dark".
- The light reading was 238.0, never seen in a pass. In 30 passing runs across the lanes the light pill reads
  191.0 or about 231.5 (and goes to 76.5 or 121.0).
- 238.0 is exactly the runner's own Windows taskbar: every pixel of the taskbar in a runner screenshot is
  (238, 238, 238), luminance 238.0. The bar's pill sits at y 1018 and the runner's taskbar takes y 1032..1080.
- The runner's taskbar is set to auto-hide, but an auto-hidden taskbar stays up while it holds the foreground
  (found by the notifications lane, d9b449a4 on `native/r7-media3`). So the probe sampled the taskbar from before
  the switch to the end, and the bar under it was never seen.

The flake loop shows it happening in every pass: ci.ps1's desk watcher logs "DESK ui during: taskbar shown 2x,
ends shown", each time with "foreground explorer ... [Shell_TrayWnd]" (runs 43842157127, 43843061023). The taskbar
takes the foreground when a test kills the app that had it, and a click on the bar leaves the pointer resting in
the taskbar's band, which also keeps an auto-hidden taskbar up.

Fix (test side):

- `Win32.TaskbarAutoHide(true)` (the harness's one way to get the runner's taskbar off the bar, called by
  `StripTests.Click`, `StripTests.DragTo`, `ClickOnBar` and the assembly setup) now also hands the foreground to
  the runner's desktop (Progman) when the taskbar holds it, and moves the pointer off the taskbar's band. Setting
  auto-hide alone did not drop a taskbar held up either way. Shared lines: `native/tests/Mantle.UiTests/Win32.cs`
  +11.
- `Probe` and each light read check that the app's own window is what lies under the probe (its corners and
  centre). If not, they call `TaskbarAutoHide(true)` and wait; if anything still covers the probe after 3 s the
  test fails there, naming the window. The "never went dark" message now also names whatever lies over the probe.

## 3. Popups paint the desktop still sharp (dark): a test bug (the click never reached the bar)

Test: `GlassTests.Popups_paint_the_desktop_still_sharp_lined_up_with_the_screen_after_a_move_a_park_and_in_each_kind ("dark")`.
Failed once (run 43833241748): "More did not open" right after the first click on `bar.more`.

Evidence: the log's tail has no line of any kind for that click. A click the bar's XAML hears opens More (the
test's stand-in pop-up, "opening ..."); a click in the bar's first moments that falls through to the input site
is caught by `EarlyClicks` and logged ("a click on bar.more came before the window took the pointer itself");
neither appears. So the press never reached the bar's window. The test's `ClickEl` pressed blind at the button's
centre: unlike `StripTests.Click` and `NotificationsTests.ClickOnBar` it never checked what lies at that point.
The same runner taskbar as in 2 is the window that can lie there (the More button is at the bar's right end,
inside the taskbar's band).

The note about a first sample taken one frame early after a move (worst error 29 or 31) is not this failure:
`Lined` already looks up to 12 times, 250 ms apart, and a passing light run shows exactly that (run 43827392539,
light-more-moved: error 31, then 0 on the next look).

Fix (test side): `ClickEl` checks the process under the click point first; if it is not the app, it calls
`TaskbarAutoHide(true)` as in 2 and waits up to 3 s, else fails naming the covering window.

The same cover also failed the overflow test once in the loop (run 43843061023, pass 2: the taskbar came up with
the foreground at 09:54:04.609, the test's app started at 09:54:05.269, and its log ends at the fit with no
"drag: lifted" line; the failure screenshot shows the runner's taskbar up with the pointer on its Start button;
the exact message was lost because each pass overwrites ui.trx). `DragTo` already called
`TaskbarAutoHide(true)`, which then did not drop the taskbar; it does now.

## 4. Desktop menu opens placed: a test race with a start-up stall (the stall itself is open)

Test: `DesktopSurfaceTests.Desktop_menu_opens_placed_lists_its_rows_hides_the_widgets_and_keeps_a_failure_up`.
Failed once (run 43827392539): `System.TimeoutException: UIA Timeout` (COMException 0x80131505) from
`ElementFromHandle` on the menu's window.

Evidence (`artifacts/parity/desktop/uitest-deskmenu-native-log.txt` of that run):

- `desktop: menu opened at 320,220 ...` at 08:06:41.979, the test's `FromHandle` right after it.
- `bar tasks: test: the UI thread has not run queued work for 2000 ms` at 44.031 and `the UI thread ran queued
  work 2282 ms late` at 44.322: the UI thread was held from about 42.04 to 44.32. UIA's connection timeout is
  2 s, so the first UIA read of the menu (served by that thread) timed out.
- This start-up stall is in every CI log the beat watches: 0.5 to 2.3 s, starting when the sidecar comes up
  ("sidecar ok", "state: ..."), ending just before "topbar: shown (sidecar ready)" (seen in the deskmenu, strip
  overflow and osd logs of 2026-11-16). The 11-11 `Close_and_kill_leave_no_process_behind` UIA timeout
  (run base 2407cf6c) is the same kind of first UIA read during start-up.

Cause: the test opened the menu about 1.4 s after the app started, inside that stall.

Fix (test side, `DesktopSurfaceTests.cs` +5 lines): the menu is opened once "topbar: shown (sidecar ready)" is
logged. The top bar is shown on the same UI thread after the start's queued work, so the menu is no longer read
inside the stall.

Open (not fixed here): the start-up stall itself. Something queued on the UI thread once the sidecar is up holds
it for up to 2.3 s on a CI runner; a user who right-clicks the desktop in Mantle's first 2 s would meet a frozen
menu. It is outside this lane's files (the start path in App.xaml.cs, the surfaces' state handlers); the beat in
`BarWindow.Tasks.cs` only says how long, not what. Next step: a test-only stopwatch around each UI-thread handler
of the sidecar's ready/state change, to name it.

## The flake loop

`tools/ci.ps1 -UiFilter "<dotnet test --filter>"` runs only those UI tests. ci.ps1 runs each named stage once (it
puts them in the documented order), so the loop calls it again and again after one build, each call with its own
desk reset and NATIVE-CI line:

    pwsh -NoProfile -File native/tools/ci.ps1 -Stages build && for i in 1 2 3 4 5 6; do
      pwsh -NoProfile -Command "Get-ChildItem ([IO.Path]::GetTempPath()) -Filter 'mantle-native-uitest-*' -Directory |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue";
      pwsh -NoProfile -File native/tools/ci.ps1 -Stages ui -UiFilter '<filter>'; done

Each pass must start without the last pass's test profiles (see Runs): a fresh runner never has them.

Shared lines: `native/tools/ci.ps1` +1 parameter and the filter on the `dotnet test` line.

## Runs

The loop filter: the overflow test, all ThemeTests, both glass "still sharp" runs, the desktop menu test, and the
app menu's keyboard test (`The_menu_draws_its_bottom_rows_while_it_reads_and_the_arrow_keys_move_over_every_row`,
for the JumpList change): 10 tests a pass.

- Run 43842157127 at 45eebd63: one pass (ci.ps1 ran each named stage once), 10 of 10.
- Run 43843061023 at 7383ddf2: 8 passes, pass 1 10 of 10, passes 2..8 failed. Not the fixes: the tests' profile
  folders (fixed names under %TEMP%) were left from pass 1, so the app menu test, which reads its log from the
  start, found pass 1's "the jump list arrived" ("the slow read finished at once") in passes 2..8; and in pass 2
  the runner's taskbar covered the overflow test's drag (section 3). A fresh CI runner never has those folders.
- Run 43844871485 at 7383ddf2, the profile folders removed before each pass: 8 passes, 10 of 10 each (80 of 80).
