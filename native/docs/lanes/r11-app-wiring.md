# Lane native-r11-app-wiring: stage 10 features wired into the app

Branch `native/r11-app-wiring` from native/stage10 f53ec019, WIP 1b9fd29d (the lane agent stalled before it committed),
stage10 da467891 merged in as 330ebbfb. Owns App.xaml.cs's feature wiring (except the notes block, lane r11-notes-gate).

## What is wired (App.xaml.cs, before the feature host loads)

- Clipboard: `ClipboardFeature.Configure` gets its pins file from `ClipHost.PinsOrRunOnly(DataRoot, Gate)`: the file only
  where the ONE write gate lets native write it; a gate check that throws keeps the pins for this run, in words.
- Emoji, Task View: `Configure(clock)` only; with no surface each stays unhealthy and its chord stays Windows' own.
- Timer: `Host.Resting` = `TimerFeature.RestingFrom(() => _rest)` (unknown until the RestCoordinator exists; the rest's
  reason turned into the game's words by `TimerFeature.GameWords`, so a notice says "while VALORANT ran", not "while
  VALORANT runs ran"). The timer hears a rest start and end through `IFeatureHostAccess.OnRest`, so a done notice held for
  a game shows once it ends (TIMER-057).
- Switcher: `Host.HandBack` = `SwitcherFeature.HandBackVia(_platform)`, the `hotkey` ask on the UNSCOPED platform, 4 s
  timeout, never throws.
- Wake: the session window's resume hook calls `WakeCalls.RunAsync`: on resume from sleep `timer.wake` and `gcal.resume`,
  both started at once, a failure is one log line each; an unlock calls nothing.

Tests: `Mantle.Core.Tests/Features/AppWiringTests.cs` (WakeCalls, RestingFrom, GameWords, the timer's OnRest path,
HandBackVia, PinsOrRunOnly). App.xaml.cs itself has no unit tests.

## Open (written, not wired, not tested)

- `OcrService.Via(IPlatform)`, `OcrService.AskOnReady(IPlatform)` and `NoOverlayClipboard` (Features/Ocr/OcrService.cs):
  nothing in the app makes an `OcrService` yet. Wire with the screenshot overlay, with tests.
- `WelcomeHost.StartWithoutWindow()` (Features/Welcome/WelcomeHost.cs): nothing in the app makes a `WelcomeHost` yet.
  Wire at start (log the verdict once, record nothing), with a test that first-run.json is left as it was.
- Notes: `NotesFeature.Configure` sits above `var clock` (lane r11-notes-gate) and passes no `Clock`, so the feature makes
  its own `HostClock()` (NotesFeature.cs:102), which takes the host loop as its context because InstallAsync runs on the
  host thread. After notes-gate merges, move the notes block under `var clock` and pass `Clock: clock`.
