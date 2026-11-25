# Lane r12-crash-restart (2026-11-25)

Branch `native/r12-crash-restart`, off native/r12-app-wiring b756536e. EM ticket t_b93a40. Rows: ship.md BOOT-014,
DATA-010 and DATA-011 (still `todo` in ship.md: written here, not run on a desktop).

## The one rule (native Mantle)

Before this lane, a native main had two crash counters, as Electron does:
- **Main's own** (`Lifecycle/Crash.cs`, main.js crashPlan): 3 restarts in 10 minutes, kept in crash.json. It only ran
  for a managed exception that reached `UnhandledException`.
- **The supervisor's** (`sidecar/Supervisor.cs`): it gives up after the 3rd death inside 2 minutes, for any death.

A native crash that never reaches managed code (an access violation, a fail-fast, a kill) was counted by the second
counter only. A managed one was counted by both, against two different limits.

Now there is ONE rule for native, in ONE place: `sidecar/CrashLoop.cs`, run by the supervisor (the only process alive
after any death). It applies whenever `supervisor-main.json` says `"native": true` (Mantle.Core/Lifecycle/Supervisor.cs
always writes that).

1. **A deliberate stop is never a crash.** crash.json is not touched and nothing is started. These are checked in the
   supervisor's existing order:
   - a `standDown` in the record. Every deliberate exit writes one first: `--quit` (switch-back.ps1 uses it), the tray's
     Quit, the kill switch, Restore Windows taskbar, the one shutdown when not crashing (App.xaml.cs Quit);
   - Windows signing out or shutting down (`SM_SHUTTINGDOWN`);
   - another live main for the folder replaced it;
   - a process-tree kill. This one puts Windows back, but nothing restarts.
2. **Every other death is a crash.** It is written to `crash.json` in main.js's shape, `{history:[ms], last:{at, what,
   restarted, ...}}`. That is the same file and shape Electron uses, in the same profile (%APPDATA%\mantle), and it
   adds `by: "supervisor"`, `pid`, `codeHex`, `words`, `why`, `newPid` and `restore`.
   - `what` is main's crash note (its error's first line), or the exit-code words.
   - The plan is crashPlan one to one: crashes 1, 2 and 3 inside 10 minutes are started again; the 4th stays down.
     `CrashLoopTests.The_rule_is_main_js_crashPlan_one_to_one` checks it against `Crash.Decide`.
3. **Staying down:**
   - no restart;
   - `tools\restore-windows.ps1 -NoKill` (this supervisor is itself a `Mantle.Sidecar.exe --supervise` process, which
     the script would otherwise stop). Windows counts as back only when the script exits 0 AND prints a line starting
     `verified:`, case-sensitive and anchored, so `NOT VERIFIED - ...` never counts;
   - a log line;
   - crash.json keeps `last.restarted:false` plus `last.restore {result, verified}`, so the tray and the next start show
     "Stopped after repeated errors at hh:mm" (App.xaml.cs, `Crash.RecoveryLine`).
   - The supervisor's existing question then follows ("Start Mantle again now?"). Yes empties the history. A
     `--supervise-test` run never asks and never runs the script.
4. **A crash that cannot be written down is not started again**, because the guard could not count it (main.js does
   the same).
5. **Never while another shell is live.**
   - The supervisor asks right after counting, and again right before the start (the waits can take up to 40 s).
   - "Live" means any of:
     - another native holds `Local\Mantle.LiveShell`. The dead main's own mutex is abandoned when it dies, so
       WaitOne(0) takes it and the supervisor releases it at once;
     - Electron Mantle's Chromium `lockfile` in the folder is held (exclusive open fails with a sharing violation);
     - an installed `Mantle.exe` runs.
   - Unknown counts as held.
   - When a shell is live: nothing is started, and Windows is NOT put back. restore-windows.ps1 stops Electron's
     processes and would fight that shell's taskbar.
   - The crash is still written down, with `restarted:false` and the reason.

Under a supervisor, main's own `UnhandledException` handler no longer writes crash.json (that would count the crash
twice). It leaves only `crashNote` in the record and exits 1. Without a supervisor (`--no-supervise`, or a test run
without `--supervise`), it still writes crash.json through the write gate, as before. Nothing restarts it then.

The supervisor's own respawn limit (Core `Supervisor.Lost`: main starts a new watcher at most 3 times in 10 minutes) is
a different thing: it guards the watcher, not Mantle. It is unchanged.

## Electron

`sidecar/CrashLoop.cs` is compiled into Electron's sidecar too (csc, C# 5; a local `sidecar/build.ps1` into a scratch
folder built 132 source files, warnings only from MaxFold.cs and Recorder.cs, which were there before). Electron's
records never carry `"native"`, so its road (the 2-minute rule, its dialog, its state file) is the same code as before:
the native block sits before Electron's checks and is skipped for them. No Electron-visible change is intended.

## Tests

`Lifecycle/CrashLoopTests.cs` (Mantle.Core.Tests, which links `sidecar/CrashLoop.cs`) uses a fake clock, a fake
crash.json, a fake launcher, a fake restore script and a fake shell probe:
- the rule equals `Crash.Decide`;
- `verified:` matching;
- stand-down, sign-out, replaced and tree kill are not counted;
- 3 restarts then the 4th down, verified;
- crashes spread over more than 10 minutes keep coming back;
- main's own history counts;
- an unverified restore, and a restore that will not run;
- yes at the question;
- a write failure, and a start failure;
- Electron live, and a shell going live during the waits;
- the record is readable by `Crash.Read` / `Crash.RecoveryLine`, and the crash note is `what`.

## Not verified (needs a real desktop)

- A real native crash under a real supervisor: that the restarted MantleNative.exe comes up live, adopts the supervisor
  (MANTLE_SUPERVISOR_PID) and takes `Local\Mantle.LiveShell` after the abandoned one.
- That restore-windows.ps1 prints `verified:` after a native run.
- The probes (lockfile, mutex, Mantle.exe) against a live Electron.
- The dialog.
- That every deliberate exit really writes `standDown` before the process ends: sign-out goes through
  SM_SHUTTINGDOWN, which is only read, never simulated here.
- Electron's own `lib/supervisor.test.js` and `lib/supervisor.e2e.test.js` were not run (cloud only).
