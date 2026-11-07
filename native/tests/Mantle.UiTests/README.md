# Mantle.UiTests: UI automation of the real native app

MSTest + FlaUI (UI Automation 3) against the published `MantleNative.exe`, with real mouse input. These
tests check behaviour, not looks; the screenshot comparison is `native/tools/parity`.

## Run it (GitHub Windows runner, public repo = free)

```
cloudtest --committed --os windows --no-install --timeout 30 \
  --artifacts "native/out/ui/**" -- "pwsh -NoProfile -File native/tests/Mantle.UiTests/run-ui.ps1"
```

`run-ui.ps1` publishes the app if `native/out/bin/MantleNative/` has none, then runs `dotnet test` and
ends with `UITEST: ok (...)` or `UITEST: FAILED (...)`. In the full CI it is stage `ui` of
`native/tools/ci.ps1`. Output in `native/out/ui/`: `ui.trx`, `test.log` and a full-screen PNG for each
failing test.

The tests refuse to run (Inconclusive) unless `CI` / `GITHUB_ACTIONS` is set, or `MANTLE_UITEST_LOCAL=1`
on a machine nobody is using. They show the app and move the real mouse, so they must never run on the
user's screen. Each test starts the app as `--test --profile-name=uitest-<x> --user-data=<temp>
--theme=<t>` with `MANTLE_TEST_VISIBLE=1`, and kills it by PID in cleanup. The runner's taskbar is put on
auto-hide for the run, so it never covers the bar.

## Contract (from the app)

Exe from `MANTLE_NATIVE_EXE` (default `native/out/bin/MantleNative/MantleNative.exe`). Window title
`Mantle bar`. AutomationIds: `bar.window`, `bar.pill`, `bar.start`, `bar.apps`, `bar.app.<n>`
(Name = window title, HelpText = exe file name), `bar.clock`, `bar.clock.time`, `bar.clock.date`,
`bar.downloads`, `bar.volume`, `bar.network`, `bar.notifications`, `bar.more`. Panels and Borders have
no UIA peer in WinUI, so the container ids need one (a small subclass) to be findable.

## What is asserted

| test | checks |
|---|---|
| Window_has_the_contract_title_and_every_contract_id | the title, and every id above is in the UIA tree (missing ones are listed) |
| Pill_sits_where_the_spec_says | `bar.pill` bounds: width min(1120, monitor - 28) DIP, height 48, 14 above the monitor's bottom edge, centred (±2 px, DPI-aware) |
| Clock_shows_the_system_time_and_date | `bar.clock.time` parses as h:mm:ss AM/PM and matches the system clock (-2.5 s / +1.5 s), `bar.clock.date` = the short date, and the time ticks within 3 s |
| Running_app_button_focuses_minimizes_and_restores_its_window | two fixture windows each get a `bar.app.<n>` button with the right Name and HelpText. Real clicks on A: to the front, then minimized, then restored to the front. Closing A removes its button and keeps B's |
| Right_cluster_buttons_invoke_without_crashing | start, downloads, volume, network, notifications and more are enabled, support Invoke, and invoking each (then Esc) leaves the app and its window alive |
| Second_launch_with_the_same_profile_exits_and_another_profile_runs_alongside | single instance per profile |
| Close_and_kill_leave_no_process_behind | WM_CLOSE exits within 10 s, kill within 5 s, and no app process is left over |
| Dark_theme_draws_a_darker_pill_than_light | the mean luminance of empty glass in the pill is at least 30 lower in dark than in light |

## Results so far

- Run 42351787470 (base app, the spike bar): built; all 8 tests failed at the first step on the window
  title (`Mantle bar (native spike)`), which proved launch, window lookup and the failure messages.
- Run 42353609992 (the contract app, 842935b): 5 of 8 passed. Two of the failures were harness bugs, both
  fixed: the two fixtures shared one exe, and the bar groups windows by app, so they got one button; and
  System.Drawing rejects CAPTUREBLT. One was real (below).
- Run 42354085200 (2026-11-07, harness 435baf8 on app 842935b): `STAGE ui: FAILED (1 of 8 failed:
  Pill_sits_where_the_spec_says)`. 7 of 8 pass. The failure is real: UIA puts the pill at 403,1021
  (1120x48) on a 1920x1080 monitor at 100 %, 3 px right of and 3 px below the spec's 400,1018 (gap 11, not
  14). The app's own ready.json says 400,1018, so its content is inset from where the code thinks it is.
  The parity screenshots show the same shift.
- A `--test` bar on the runner also lists the runner's hosted-compute-agent console window as a running
  app. That is expected: it is a real visible window.
