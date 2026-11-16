# Idle CPU and memory: Electron Mantle vs native Mantle, same runner, same method

Lane native-r8-idlecmp, 2026-11-16. Before this, the idle numbers for the two apps came from different runners and
different methods (Windows' charged CPU ticks over 60 s for Electron in `native/bench`, the native idle UI test's
charged ticks over 15 s, then exact cycles over 45 s in lane native-r7-idle), so they could not be compared. This
measures both apps on one runner, in one job, the same way. It is a measurement, not a CI gate.

## Result (run 43841162265, branch native/r8-idlecmp at 9302a35e, Native AOT build of the same checkout)

Runner: GitHub `windows-latest` standard runner, Intel Xeon Platinum 8573C, 2 cores / 4 logical, Windows Server 2025
Datacenter 10.0.26100, 16 GB, 1920x1080 at 100 %. Cycle rate 2.293 GHz. Electron 44.4.3.
CPU is % of ONE core, summed over all of the app's own processes, by exact cycles. Median of 3 windows of 45 s,
range in brackets. Memory is read at the end of each window, summed over the same processes.

| theme | bar | Electron CPU % | native CPU % | Electron private MB | native private MB | Electron working set MB | native working set MB |
|---|---|---|---|---|---|---|---|
| light | hidden at rest | 1.229 (1.191 to 1.387) | 0.651 (0.634 to 0.654) | 539.9 (528.1 to 541.7) | 119.1 (118.6 to 119.3) | 191.0 (180.7 to 193.5) | 108.6 (108.6 to 108.7) |
| light | shown idle | 6.761 (6.749 to 7.221) | 5.330 (5.326 to 5.349) | 542.2 (532.9 to 544.5) | 122.1 (116.9 to 122.8) | 365.0 (359.3 to 369.5) | 142.5 (142.4 to 143.5) |
| dark | hidden at rest | 1.468 (1.446 to 1.468) | 0.626 (0.615 to 0.648) | 560.9 (530.9 to 567.3) | 110.1 (110.0 to 110.8) | 186.7 (185.4 to 197.7) | 108.3 (108.0 to 108.6) |
| dark | shown idle | 1.508 (1.501 to 1.522) | 0.826 (0.820 to 0.892) | 558.1 (526.0 to 560.8) | 111.3 (111.1 to 111.4) | 288.7 (287.3 to 290.4) | 123.6 (122.4 to 123.7) |

Processes: Electron 12 (main, the sidecar, the network service, 9 renderers), native 2 (the app and its sidecar), in
every window; no process started inside a window. All 24 windows valid (the bar was down for every hidden window and
up at both ends of every shown window). The whole runner was 0.55 to 2.66 % busy across all cores during the windows,
except one Electron shown window at 8.87 % (light round 2: its CPU, 6.761 %, sits inside the other two rounds' range).

Native against Electron, medians: hidden at rest about half the CPU (0.65 vs 1.23 light, 0.63 vs 1.47 dark); shown
idle 0.79x in light (5.33 vs 6.76) and 0.55x in dark (0.83 vs 1.51); private bytes about 1/4.5 to 1/5 (110 to 122 vs
540 to 561 MB); working set about 0.4 to 0.6x.

Where the cycles go, hidden at rest (dark, round 2, from `windows.txt`):

- Electron 1.468 %: main 0.686, sidecar 0.492, top bar renderer 0.182, launcher renderer 0.042, bar renderer 0.039,
  the other 7 processes 0.027 together.
- native 0.615 %: the app 0.365, its sidecar 0.250.

The sidecar is the same kind of program in both (`Mantle.Sidecar.exe --parent-pid`), so roughly 0.25 % of native's
and 0.49 % of Electron's hidden number is not the UI stack at all.

The earlier attempt, run 43840015352 (9 minutes, a different runner, AMD EPYC 7763), stopped after its first Electron
run on two script bugs (the bar test was size-based and missed Electron's 702 px tall bar window; a null Process.Path
on the native start line). Its one valid window, Electron light hidden, read 1.59 %. Fixed in 9302a35e.

## Method

`native/tools/bench/idle-compare/run.ps1` with `IdleCmp.cs`, run through
`cloudtest --recipe mantle-native-public --timeout 90 -- "pwsh -NoProfile -File native/tools/bench/idle-compare/run.ps1"`.

- **Builds.** The native app is published Native AOT (`native/tools/publish.ps1 -Mode aot`) from the checkout;
  Electron is the checkout's own `node_modules/electron` with the recipe's npm install and csc sidecar build.
- **Desktop, as the parity stage sets it.** The snow-peak wallpaper, transparency on, the Windows theme set to the
  run's theme, taskbar on auto-hide, the parity fixture window (a plain WinForms window at 40,40, 420x300) as the one
  running app, the cursor parked mid-screen (960,360).
- **Each app in its test mode, own temp profile, no user data.**
  - Electron: `electron . --test --test-live-bar --profile-name=<p> --theme=<t> --no-desktop` with
    `MANTLE_TEST_VISIBLE=1`. That is parity's Electron instance (`native/tools/parity/electron-bar.mjs`) minus its CDP
    port and its stand-in cursor, so its hover poll reads the real cursor as native's does.
  - native: `MantleNative.exe --test --profile-name=<p> --user-data=<temp> --theme=<t> --bench-out=<dir>` with
    `MANTLE_TEST_VISIBLE=1` and `MANTLE_TEST_HOVER=1`, as the native idle UI test runs it.
- **Order.** Per theme (light, then dark), 3 rounds of Electron then native: E, N, E, N, E, N.
- **Per app run.** Start; wait until it is up (Electron: its first visible window; native: its log's
  "bar hidden: at rest"); settle 30 s; park the cursor, 2 s; **hidden** window, 45 s. Then the cursor goes to the
  bottom edge (960,1079), which is the hover reveal for both apps. When the app's "Mantle bar" window is on screen
  (both apps use that title), the cursor rests on the pill at (960,1056), the native idle test's point. After 3 s
  comes the **shown** window, 45 s, and then a full-screen PNG.
- **CPU.** At a window's start the app's processes are collected by walking the process tree down from the main PID
  (a child counts only if it started after its parent), plus any process outside the tree whose command line says
  `--supervise=<main pid>`. In these runs there was none, and the walk is used to measure only. Each process is
  opened for query only, and its QueryProcessCycleTime is read at the start and at the end. A handle keeps the
  process object, so an exit mid-window still counts and a reused PID cannot be misread. The cycles are summed,
  turned into ms at the runner's cycle rate (the least interrupted of 5 spins of 40 ms against the Stopwatch, as
  ThreadCpu.cs does), and divided by the window's wall time. The charged CPU time (GetProcessTimes) is recorded
  beside it, for reference only.
- **Memory.** Private bytes (PrivateUsage, the commit charge) and working set (WorkingSetSize), summed over the same
  processes at the window's end.
- **Validity.** A hidden window needs the bar down at its start and end; a shown window needs it up at both. A
  window that fails either is reported and left out of the medians (none in run 43841162265).
- **Closing.** The app's own quit path: a second instance with `--quit` (Electron:
  `electron . --quit --test --profile-name=<p>`; native: `MantleNative.exe --test --profile-name=<p> --user-data=<d> --quit`).
  If the app is still running 20 s later, `Stop-Process` on its main PID only. Afterwards any of its measured PIDs
  still alive is reported, never killed. In the run all 12 app runs closed by their quit path with nothing left.
- **Output.** `native/out/bench/idle-compare/`: `summary.md` (the tables), `results.json`, `windows.txt` (per process
  rows, the app's visible windows and the foreground per window), each run's logs and its shown-end PNG.
  Electron's own "memory (30 s after start)" log line, kept in results.json, names each renderer's PID.

## Fairness: what is not equal

- **Electron's test mode shows its launcher.** In `--test`, the launcher is an ordinary framed window at 612,290
  (1308x815), visible the whole time. A live run keeps it hidden until it is summoned. It cost 0.04 % hidden at
  rest (round 2), and it keeps that renderer's pages in use (Electron's own line: launcher ws 87 to 92 MB at 30 s,
  visible). So Electron's working set is somewhat higher than a live Electron at rest; its CPU barely changes.
- **Electron's test mode does less than live.** Electron's other test windows are framed test shapes. Only the bar
  is the live kind (`--test-live-bar`). Some live-only work does not run in `--test`: for example, the shell
  watcher poll (`main.js` shellBack.poll returns early in test). No Electron surface has a desktop layer here
  (`--no-desktop`). The native test run keeps its desktop layer made but parked. A live user has the desktop in
  both apps, so neither number includes it.
- **Native's test-only UI beat while shown.** Since Job 1 of this lane (871d30bb), the native bar's test-only
  250 ms beat stops while the bar is hidden. It still runs while the bar is shown, so native's shown numbers carry
  it and a live run would not. Electron has no equivalent test-only timer in `main.js` (searched for timers under
  `flags.test`; its renderers were not audited).
- **The look differs while shown.** Native light refreshes its glass 4 times a second while the bar is up; dark
  holds one still picture. That is why light shown is 5.3 % for native against 0.8 % dark. Electron's light shown
  cost (6.8 %) has the same split against dark (1.5 %). The comparison is per theme, each app with its own look.
- **Working set is not "RAM in use" for Electron.** Windows trims hidden renderers. Electron's renderers report
  ws 74 to 92 MB at 30 s in its own log, and 2 to 3 MB at the end of the hidden window, so working set flatters
  Electron at rest. Private bytes is the steadier number for both.
- **Native's `--bench-out`** writes ready.json and the strip's cells.json on a layout change, as in the native idle
  test. Nothing in it is periodic.
- **One runner, one run.** A 2-core VM. The rounds agree closely (native's hidden range is 0.615 to 0.654 over 6
  windows), but it is one machine. The user's laptop (i3-1215U, Windows 11) will have different absolute numbers.

## Repeat it

```
cloudtest --recipe mantle-native-public --timeout 90 -- "pwsh -NoProfile -File native/tools/bench/idle-compare/run.ps1"
```

It takes about 30 minutes (a 2.5 minute AOT publish, then 27 minutes of rounds). Options: `-Themes light,dark`,
`-Rounds 3`, `-WindowSec 45`, `-SettleSec 30`, `-Exe <native exe>`.
