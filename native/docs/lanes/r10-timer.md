# Lane notes: the timer and stopwatch (native/r10-timer)

Brief: TIMER-001..081 (native/docs/parity/apps.md), feature `timer`, "Timer and stopwatch (top bar)", on by default.
Electron: lib/features/_timer-core.js (rules), lib/features/timer.js (service), ui/timer/* (done pop-up), ui/topbar/*
(the bar cell and its menu).

State: written, not built and not run. This laptop has no .NET SDK and the window's rule was no dotnet, no cloudtest.
Every signature called was re-read from the tree.

## What is where

Folder `src/Mantle.Core/Features/Timer/`, namespace `Mantle.Core.Features.Timers`. The namespace is not `...Timer` on
purpose: a namespace `Mantle.Core.Features.Timer` would hide `System.Threading.Timer` from every feature next to it
(SnapFeature's `Timer? _probeTimer` and about a dozen more would then name the namespace).

- `TimerCore.cs`: the pure rules, a line-by-line port of _timer-core.js: Parse, Check, LabelFor, FormatClock (countdown
  CEIL, stopwatch FLOOR), TimeOfDay, CleanLabel, StartTimer/StartStopwatch/Pause/Resume/AddTime, LeftMs/ElapsedMs,
  NextTickMs, BarRedrawMs (+15 ms, min 20), ToJson (Electron's saved shape exactly) and Restore. No clock of its own.
- `TimerEngine.cs`: timer.js's state machine behind `ITimerHost` (wall clock, monotonic count, store write, arm one
  timeout, show/close the done notice, beep, game state). Commands, the one timeout, the monotonic deadline, wake,
  finish (idle saved BEFORE the notice), held notices during a game, restore, the tray switch, `View()` (timer.state).
  `TimerDoneNotice` is the done notice as words (title, sub line, four buttons); `ToBannerPost()` maps it onto
  Features/Notifications OwnBannerPost.
- `TimerFeature.cs`: `[Feature("timer", "Timer and stopwatch (top bar)", true)]`, no chords, no Rest. Commands
  timer.state/start/stopwatch/pause/resume/cancel/add/dismiss/again/snooze/chime/wake, event `timer.state` (replayed),
  event `timer.done` (the notice), one `HostClock.Later` for the timeout, the keys clock/last/held in
  `features\timer.json` through `ctx.Settings.UpdateAsync` (the data layer's atomic write, .unreadable rule and
  WriteGate; a refused write comes back as a failed save in its words: persist {ok:false}, health false, one log line).
  `TimerFeature.Configure(Host)` lets the app give ShowDone, CloseDone, Beep and Resting.

Tests: `tests/Mantle.Core.Tests/Features/Timer/` TimerCoreTests (pure), TimerEngineTests (a fake host, an in-memory
document, no files), TimerFeatureTests (the host rig: ManualTime, MemoryStores, FakePlatform).

## Deviations

- Done notice: nothing draws it yet. With no `Host.ShowDone` wired, a finish counts as a failed pop-up: logged,
  beeped (or logged as "no beep" while there is no shell beep), done.popup 'failed' with "no done pop-up is wired in this
  build yet" in timer.state, and `timer.done` is broadcast. Never silent, but not a pop-up either.
- As an own banner: at most 3 buttons, so Dismiss is the banner's X and the buttons are +1 min, +5 min, Restart.
- Wake: the command `timer.wake`. App.xaml.cs's resume hook (which calls gcal.resume) does not call it yet.
- Game state: `Host.Resting`; unwired = resting 'unknown', a notice shows at once (TIMER-059's own rule).
- CleanLabel never cuts half a surrogate pair (System.Text.Json refuses one).
- Electron has no laps, alarms or pomodoro; neither does this.

## Left

TIMER-002..027 (bar cell and menu; drawn through the Levitate layer when done, tabular figures), TIMER-060..064,
TIMER-068, TIMER-070..075, TIMER-077..079 (the pop-up surface, chime, Esc), TIMER-080 (Quick settings Focus tile: the
seam is `TimerEngine.StartTimer(null, ms, label)` / `Cancel()`), TIMER-081 (test hooks; beeps are counted).
