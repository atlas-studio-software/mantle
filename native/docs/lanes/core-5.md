# Lane native-r3-core-5: notes (round 3)

Two look tests failed now and then on the cloud runner and passed when run again. This lane found out why. It made
the tests that raced the clock deterministic, and found one real product delay (below) in a file it does not own.
No product behaviour changed here.

## How the host hears another app's look

`Look/LookSync.cs` watches the shared folder with one `FileSystemWatcher` (no polling, like lib/look-sync.js's
`fs.watch`). An event for `look.json` starts the debounce timer (50 ms; 30 in the tests). The check then runs on the
instance's serial queue, reads the file and raises `Apply`. `LookHost` applies the theme, and `LookShell` posts the
`theme` event to the host thread. A watch that fails is re-armed after 1 s, 5 s, 30 s. `Refresh()` re-reads on focus,
resume and unlock. Three steps run on the thread pool: the watcher callback, the timer and the queue.

## What failed, and why

The suite runs its classes in parallel, and on the runner that starves the thread pool for hundreds of ms at a time.
Run 42391747730 (whole suite x30, the old tests with a timed trace) showed it:

- `LookShellTests.Theme_set_rebroadcasts_the_theme_within_1_s_...` measured **1300 ms**. In that test the command and
  the `theme` event both run on the dedicated host thread. The only pool steps are the test's own awaits: the clock
  measured when the test resumed, not when Mantle fired. `Another_apps_theme_reaches_...` (runs 42384431813 and
  42390282384: "did not come within 1000 ms", "followed after 1005 ms") timed the same way, through the same awaits.
- `LookSyncContractTests.A_rename_refused_...`: the publish answered **EPERM (after 4 tries) at +364 ms**, but the
  holder was **never released**: its `Task.Delay(8).ContinueWith` had not run. The pool had 9 threads and 6 items
  queued. The writer's retry sleeps (50/100/200 ms) were right and ran in full.
- `LookSyncContractTests.Torn_read_that_heals_...` (found by the loop, run 42392431585, 2 of 40): its
  `Task.Delay(80)` heal came after the instance's 300 ms re-read, so it timed out once and left a `.bad-` copy once.

With those test races fixed, `Another_apps_theme_...` still failed once in 40 whole-suite runs (run 42393117505,
run 37): **the event fired 1672 ms after the change**, timed on the host thread. The trace shows where the time went:

```
+906  helm publishes                          +2239 mantle sync: debounce fired
+907  event look.json.helm...tmp  (created)   +2240 check read ok; apply theme by helm
+2194 event look.json.helm...tmp  (flushed)   +2578 theme: Dark -> Mantle is dark   (theme.json saved)
+2199 event look.json             (renamed)   +2578 theme event dark on the host thread
```

Mantle's own seed in the same run took 437 ms from creating its tmp to the rename. **This is a real product delay,
outside this lane's files:** `Data/SafeFile.WriteAtomic` calls `fs.Flush(true)` (FlushFileBuffers) before every
rename. On a busy disk that costs 0.3 to 1.3 s. Electron's `writeAtomic` (lib/look-sync.js) and theme.js `save`
use `writeFileSync`, which never fsyncs. Two places pay it:

- every look.json publish by native Mantle, before Helm and Quartz can see it (1.29 s above, paid by the native
  writer that stands in for Helm);
- theme.json's save in `ThemeCore.Set`, which comes before the listeners are told (338 ms above, Mantle's own, on
  the inbound path too). The order is lib/theme.js's (save, then tell); only the save's cost differs.

The fix belongs in `Data/SafeFile.cs`: drop `Flush(true)` (the `using` still flushes the stream's buffer to the
OS), or make it opt-in for the few files where durability beats latency. The stores and look-sync already recover a
damaged file (`.unreadable`, `.bad-<time>`). Needs the owner's go; `ReadOnlyTests` keeps every write in SafeFile.
Until then this test can still fail on a loaded runner. Its message now names the step.

The rename and torn-read tests: test races only. No event was lost and no publish was lost. The retry schedule is
Electron's (50/100/200). Every native reader opens `look.json` with read, write and delete sharing, like Node, so it
never blocks another app's rename.

## The fixes (tests only, plus one internal hook)

- `LookShellTests`: `NextTheme` stamps the event on the host thread as it fires (`Seen.At`), and `Within(r, ...)`
  checks that it fired within 1000 ms of the change. This is the same 1 s promise, timed where Mantle keeps it. A
  separate 10 s bound applies only to how long the test waits to hear the event. A failure prints the timed log: each
  line carries ms, the thread id and the pool's counts.
- `A_rename_refused_...`: a thread of the test's own (not the pool) watches the writer's tmp copy. It lets go of the
  reader 20 ms after the writer closes the tmp, which is the step right before the first rename, so the release
  lands inside the first 50 ms retry delay. The test also checks the result: theme `light`, no tmp left.
- `Torn_read_...`: the file heals when the instance says it has made its first torn read. That trace line comes
  before the 300 ms wait for the re-read.
- `LookSyncOptions.Trace` (internal, tests only; null in the app): one line per step of the follow path (`event`,
  `debounce fired`, `check read <state>`, `apply`, `lost watch`).
- `native/tools/test-loop.ps1`: runs the core unit tests N times in one job (optionally filtered) and prints each
  failure's message. It ends with `TEST-LOOP: <clean> of <runs> runs clean (...)`.

## Still open (not this lane's files)

- `SingleInstanceTests.An_owner_that_died_holding_the_claim_is_taken_over_and_said` failed once in 40 whole-suite
  runs (run 42392431585: `Assert.NotNull() Failure`, line 71). Not looked into.
- The pool starvation itself comes from the suite (blocking waits in tests running in parallel). Any other test that
  times a promise through its own awaits can hit it in the same way.
