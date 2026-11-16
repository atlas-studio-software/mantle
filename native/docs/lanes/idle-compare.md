# Lane native-r8-idlecmp: the same-machine comparison, and the test-only beat

Branch `native/r8-idlecmp` (from native/main 63a89535). The full write-up is `native/docs/bench/idle-compare.md`.

## Same-machine comparison

Electron Mantle and native Mantle (Native AOT), from the same checkout. They run on one runner in one job, each in its
test mode with the parity stage's desktop and fixture window: 3 alternating rounds per theme, 45 s windows. CPU is
the sum of exact cycles over all of the app's own processes, as % of one core. Run 43841162265, medians (range):

| | Electron | native |
|---|---|---|
| light, hidden at rest | 1.229 % (1.191 to 1.387) | 0.651 % (0.634 to 0.654) |
| light, shown idle | 6.761 % (6.749 to 7.221) | 5.330 % (5.326 to 5.349) |
| dark, hidden at rest | 1.468 % (1.446 to 1.468) | 0.626 % (0.615 to 0.648) |
| dark, shown idle | 1.508 % (1.501 to 1.522) | 0.826 % (0.820 to 0.892) |
| private MB (all windows) | 526 to 567 | 110 to 123 |
| working set MB, hidden / shown | 181 to 198 / 287 to 370 | 108 to 109 / 122 to 144 |
| processes | 12 | 2 |

Not equal between the two, and said in full in the bench doc:

- Electron's `--test` shows its launcher as a visible window (0.04 % CPU).
- Electron's test mode skips some live-only work.
- Native's test-only UI beat still runs while the bar is shown.
- Windows trims Electron's hidden renderers' working set.
- It is one 2-core runner.

## Job 1: the test-only UI beat stops while the bar is hidden

`native/src/Mantle.App/Surfaces/Bar/Tasks/BarWindow.Tasks.cs` (commit 871d30bb):

- `StartTasks`, the `if (Host.Args.Test)` block: Hiding stops the beat, Revealed starts it, a non-hover run starts it at once, and a hover run waits for the first reveal.
- `StopUiBeat(why)` is new.
- `StartUiBeat(why)` is idempotent and logs.
- The queued ping returns when its slot was already cleared, so a ping queued before a stop is not read as late.

Log lines: `bar tasks: test: UI beat started (...)`, `... stopped (...)`, `... waits for the first reveal (...)`.
No change outside `--test`.

The full run on 871d30bb was run 43836580585: `NATIVE-CI: ok (17 ok, 0 skipped: )`.

The idle test's own numbers before and after (both runs on the charged-tick method, still the test's method on
native/main; its noise on one build is 0.14 to 1.42 %):

| | hidden at rest, light | shown idle, light | hidden at rest, dark | shown idle, dark |
|---|---|---|---|---|
| before, run 43825573669 (63a89535) | 0.208 % | 6.041 % | 0.312 % | 1.353 % |
| after, run 43836580585 (871d30bb) | 0.312 % | 6.145 % | 0.208 % | 0.729 % |

One pair of charged-tick readings cannot show a cost this small. The beat was about 4 UI-thread and 4 pool-thread
wakes a second (native/docs/lanes/idle.md on native/r7-idle). The comparison above runs with the beat stopped while
the bar is hidden.

## The idle test's dark hidden-at-rest readings (a second question, same lane)

The idle UI test's dark hidden-at-rest reading went over its 1.0 % limit on the stage (7eef59af: 1.667 %, hidden
again 0.937 %) and on two branches with the see-through glass. The guess was that the glass decodes the still or
works out its readability guard (NativeGuard.Rows) inside the dark window.

The probe was `native/tools/bench/idle-compare/dark-probe.ps1`. Run 43846660374 used branch native/r8-darkidle at
8d6d6cd5, which is the stage 7eef59af plus the probe only. It ran the UI suite, the idle test alone twice, and the
test's sequence by hand with per-thread cycles. It kept the test's own app logs.

- **Not reproduced.** In the suite: dark 0.625 %, light 0.521 % hidden at rest, on the test's charged-tick method;
  88 of 88 passed. The idle test alone passed twice.
- **By hand, by cycles, window A (the test's 15 s).** Light 0.502 % and dark 0.433 %. The next 30 s: light
  0.402 %, dark 0.516 %. The per-thread profile is the same in both themes. The UI thread was 27 to 32 ms, DWM's
  in-process compositor 13 to 15, the mantle host 6 to 7, the hover poll 4 to 4.5 and the sidecar pipes about 1.5.
  No pool thread did decode-sized work.
- **The glass does no pixel work in this test.** The test's own app logs, light and dark, say:
  "glass: no desktop still: Mantle hasn't shared a desktop wallpaper yet". A --test run's desktop layer is parked,
  so nothing records a still: no decode, no guard. The dark log has nothing from the glass between start and the
  reveal.
- **The reported numbers are whole ticks.** The test reads charged CPU, which comes in whole 15.625 ms ticks.
  1.667 % of 15 s is 16 ticks, 1.354 % is 13, and 0.937 % of the 5 s "hidden again" is 3. Lane native-r7-idle
  measured that method at 0.139 to 1.423 % on one build, against 0.38 to 0.65 % by cycles. Its fix (cycles over
  45 s) is on the unmerged native/r7-idle branch.

Conclusion: no dark-only burner was found, and the glass is ruled out for this test. The high dark readings fit
the charged-tick method's scatter. One thing is not ruled out: an intermittent cause that this run did not hit. No
Shell/Glass change was made.
