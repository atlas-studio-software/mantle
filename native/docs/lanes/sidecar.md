# Lane native-r7-sidecar: the sidecar can never outlive the app

## The bug

The native app started Mantle.Sidecar.exe with `Process.Start` and redirected stdio. Two problems:

1. **Nothing tied the sidecar's life to the app's.** The only ways it ended were its own: stdin EOF and
   the `--parent-pid` watch in the shared `sidecar/Program.cs`. Both run the full safety restore first,
   so if that restore stalls, or the watch misses the parent, the sidecar stays.
2. **It inherited every inheritable handle of the app.** `Process.Start` with redirection calls
   CreateProcess with bInheritHandles=TRUE and no handle list. When a test host or a CI step starts the
   app with a pipe as its stdout, that pipe is inheritable in the app, so the sidecar got a copy. A sidecar
   that outlived the app kept the pipe open, and the reader never saw it end. Run 43012272746 hung for
   80 minutes that way: TestRunJob found one live Mantle.Sidecar, and no app.

Lane native-r7-topbar fixed only the test side (tests/Mantle.UiTests/TestRunJob.cs). This lane fixes
the app.

## The fix (app side only; the shared sidecar source is unchanged)

`Platform/SidecarJob.cs` and `Platform/SidecarProcess.cs`:

- **A job object owned by the app** (`SidecarJob.ForThisProcess`). It is made once and never closed
  by hand. Windows closes it when the app's process ends, however it ends: a quit, a crash, Task Manager,
  or `Process.Kill`. Its flags are libuv's, the job node puts Electron's children in, so both builds
  behave the same:
  - KILL_ON_JOB_CLOSE: the sidecar dies with the app.
  - BREAKAWAY_OK and SILENT_BREAKAWAY_OK: only the sidecar itself is in the job. What it starts is not:
    the taskbar guard (which must outlive it to put the taskbar back), and the user's own apps launched
    through it.
  - DIE_ON_UNHANDLED_EXCEPTION: a crashed sidecar ends at once instead of waiting in an error dialog, so
    the restart ladder starts on time.
- **CreateProcess with an explicit handle list** (`ContainedChild`). The child inherits exactly three
  handles: the child ends of its stdin, stdout and stderr pipes (PROC_THREAD_ATTRIBUTE_HANDLE_LIST). The
  pipes are made non-inheritable. Only the child ends are marked inheritable, just before CreateProcess,
  and they are closed right after it. The protocol needs nothing more.
- **Started suspended, put in the job, then resumed.** The sidecar never runs a single instruction
  outside the job.
- **Restarts.** Every start goes through the same launcher and the same job, so a restarted sidecar
  joins the job its first start was in.
- **One log line per start:** `[sidecar info] started the sidecar, pid N, in Mantle's own job (...)`.
  If Windows refuses the job, the line says so, with the reason and the error number. The sidecar still
  starts, and its own parent watch is then the only thing that ends it.

What a crash now means, the same as in Electron: the sidecar is ended at once, without its safety
restore. The taskbar guard (outside the job) clears the taskbar region. The state file and the next
start's dirty-start check (SAFE-017) handle the rest. A clean quit is unchanged: the app asks the
sidecar to quit and waits for it before the app exits.

## Tests

- `tests/Mantle.Core.Tests/Platform/SidecarJobTests.cs` (unit stage, in a collection that runs alone).
  The children are PING.EXE.
  - The job's flags, read back from Windows.
  - The app's job is made once, and the default launcher uses it.
  - A started child is in the job and talks on its pipes. A second start (what a restart does) joins the
    same job. Closing the job ends both.
  - Kill ends only that child.
  - Inheritance: the test holds an inheritable pipe while the child starts, then drops its own copy of
    the write end. The read ends at once, so the child has no copy. A control test shows that
    `Process.Start` with redirection does take a copy, so the check can tell.
  - Command-line quoting round-trips through CommandLineToArgvW.
- `tests/Mantle.UiTests/Sidecar/SidecarJobUiTests.cs` (ui stage). The app runs with a stand-in sidecar,
  `tests/Mantle.SidecarStub`, through MANTLE_SIDECAR_EXE (honoured only with --test). The stand-in
  never reads stdin and never watches its parent. Its first start exits with code 3, so the app restarts
  it. The test:
  1. Reads both sidecar PIDs from the log and checks that each says "in Mantle's own job".
  2. Kills the app by its own PID (`Process.Kill`, not its tree).
  3. Asserts that the restarted sidecar is gone within 5 s.

  Nothing else could have ended it. The stand-in ignores the two exits the real sidecar has, and
  TestRunJob ends its members only when the test host exits, which it has not done yet.

## The shared sidecar's own parent watch (not changed)

`sidecar/Program.cs` `WatchParent` opens the parent with `Process.GetProcessById(--parent-pid)` and waits
on it on its own thread. Reading the code, it does notice the parent dying. Two weak points are left as
they are, because the app's job now covers both:

- The watch starts only after the startup work (`WatchParent()` comes after `Lanes.Start` and the stdin
  reader). If the app dies before that and its PID is reused, the watch can follow the wrong process.
  stdin EOF still ends it in that case.
- Both of its exits run the full safety restore first. A restore that stalls keeps the process alive
  until ArmHardExit fires.

This lane could not tell which of these, if either, kept the sidecar alive in run 43012272746.
