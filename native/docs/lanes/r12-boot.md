# Lane native/r12-boot: start at sign-in, one shell at a time, verified switch-back

Branch `native/r12-boot` from `438a52dc`. Nothing here was run against the real Task Scheduler, the real
shell or a real Mantle: the scripts were run only with `-DryRun`, the tests use fakes and their own mutex names.

## What is built

| Piece | Where | Tests |
|---|---|---|
| Native logon task XML (BOOT-001..005 adapted, DATA-037) | `src/Mantle.Core/Boot/LogonTask.cs` (`Xml`, `Arguments`, `Escape`, `DefaultLog`) | `tests/Mantle.Core.Tests/Boot/LogonTaskTests.cs` parses it with XDocument and checks every setting, the escaping (`A&B O'Neil` in user, exe, folder, log), name and URI |
| The `-OnlyIfExe` rule (BOOT-008), pure | `LogonTask.NormaliseCommand` / `NormaliseWanted` / `OnlyIfExe` | same file: same path, other case, quoted, `..` and `/`, other exe, relative task command, relative `-OnlyIfExe`, no Exec, no task |
| Installer (BOOT-006..008 adapted) | `tools/install-autostart.ps1` (`-Exe`, `-Uninstall`, `-OnlyIfExe`, `-TaskName`, `-User`, `-Log`, `-DryRun`, `-AssumeTask` for dry runs only) | `Boot/BootScriptTests.cs`: its `-DryRun` XML must EQUAL `LogonTask.Xml` (so the two copies cannot drift), and its `-OnlyIfExe` words must equal `LogonTask.OnlyIfExe`'s |
| The live-shell lock | `src/Mantle.Core/Safety/LiveLock.cs` (`Local\Mantle.LiveShell`, `TryTake`) | `Safety/LiveLockTests.cs`: refused from another thread in words, release then take, abandoned = taken with a log line |
| Verified switch-back | `tools/switch-back.ps1` | `BootScriptTests` (dry run: the steps in order) |
| Verified switch to native | `tools/switch-to-native.ps1` | `BootScriptTests` (dry run, clone and installed Electron) |

The XML is Electron's `tools/mantle-logon-task.xml` setting for setting: LogonTrigger for the user with
`PT15S`, InteractiveToken, LeastPrivilege, IgnoreNew, battery false/false, StartWhenAvailable, no network,
not idle-only, no wake, `PT0S`, priority 4, AllowStartOnDemand, AllowHardTerminate, RestartOnFailure
`PT1M` x3. The action is `"<MantleNative.exe>" --live "--log=<log>"` in the exe's folder. The default log is
`%APPDATA%\mantle\mantle-native.log`, not Electron's `mantle.log`: Program.cs already refuses to write
Electron's log and turns that `--log` into `mantle-native.log`, so the task says what really happens.
The XML is written UTF-16 LE with a BOM to `%TEMP%\mantle-native-logon-task.xml` and registered with
`schtasks /Create /TN "Mantle Native" /XML ... /F` (that `/F` is also what re-points a task that started an
older native exe, DATA-037).

`-Uninstall` (without `-OnlyIfExe`) only deletes the task: native has no banner or Windows-pieces journal of
its own yet, so Electron's `restore-banners.ps1` / `ramreclaim-undo.ps1` step (BOOT-007) is not copied.
`-AssumeTask` is refused without `-DryRun`.

## The task name: `Mantle Native` (URI `\Mantle Native`)

- Electron's `install-autostart.ps1`, its uninstaller's `-OnlyIfExe` road, `installer/ci-install-test.ps1`
  (`schtasks /Delete /TN Mantle`) and the welcome card all name `Mantle`. Reusing it, `schtasks /Create /F`
  would silently re-point Electron's task at the native exe, and Electron's uninstaller would then leave it
  alone ("it starts MantleNative.exe") or remove it, depending on which copy it was.
- Two tasks, one enabled at a time, is exactly what the switch scripts flip, and a rollback is
  `schtasks /Change` twice, never a re-register.
- The space follows Electron's own `Mantle Elevated Helper`. The cost: a hand-typed command must quote it
  (`schtasks /Run /TN "Mantle Native"`); every script and printed hint here does. `MantleNative` (the exe's
  name, no space) would be the fallback if quoting ever proves a problem.
- `Mantle Native` does not prefix-collide in practice: schtasks `/TN` is exact, and no Electron script
  uses a `Mantle*` wildcard.

## App wiring (Program.cs; NOT done here, Mantle.App is the other side)

Right after the TwoShells refusal block in the `args.Kind == RunKind.Live` branch (Program.cs:132-141),
before the live lines are printed:

```csharp
            var live = Mantle.Core.Safety.LiveLock.TryTake(out string? liveWhy, log.Line);
            if (live == null) { log.Line("mantle: " + liveWhy); Say(Console.Error, "mantle: " + liveWhy); claim.Dispose(); log.Dispose(); return ExitCodes.Failed; }
            Host.LiveLock = live;   // held for the life of the process; Main's thread took it
```

plus `public static IDisposable? LiveLock;` in `Host`, and `Host.LiveLock?.Dispose();` in Main's `finally`
before `claim.Dispose()` (same thread as the take: a named mutex has thread affinity; if the process just
ends, Windows releases it and the next taker logs "abandoned ... taken over", which is fine).

Why it adds something the single-instance claim does not: `InstanceKey` is per DATA ROOT, so a native
`--live --user-data=X` and a native `--live` on the profile could both go live today. The live lock is per
SESSION, whatever the data root.

## Electron note (for a later Electron job)

- Electron's `main.js` should check `Local\Mantle.LiveShell` before it goes live, through its sidecar
  (Node has no named-mutex API): `Mutex.TryOpenExisting(name, out m)` and, when it exists, `m.WaitOne(0)`
  (true or `AbandonedMutexException` = nobody live: release at once and go on; false = native is live:
  refuse with words like TwoShells.Verdict's). Electron should NOT hold the mutex itself unless native's
  TwoShells is also taught to treat it as an Electron signal; today native already sees Electron through
  its held `%APPDATA%\mantle\lockfile`, `Mantle.exe` and `shell-state.json` (SAFE-038).

## Switch scripts (real path written, NEVER run here)

`switch-back.ps1 -Exe <MantleNative.exe> [-StartElectron] [-DryRun]`:
1. `MantleNative.exe --quit` (LaunchArgs: `--quit` is a message to the running instance on the same data
   root; the live run's root is `%APPDATA%\mantle`, the default, so it reaches it). Started with
   `Start-Process -Wait` because a WinExe is not waited for by `&`.
2. wait up to 30 s for that exe to be gone; never killed (a kill leaves the cursor clip); stop if it stays.
3. `tools\restore-windows.ps1` next to the exe, `-StateFile %APPDATA%\mantle\shell-state.json`, WITHOUT
   `-NoKill` (native is gone; a leftover `Mantle.Sidecar.exe --supervise` must not restart it). Requires
   exit 0 AND a line matching `^verified:` case-sensitively (the script prints
   `verified: Windows is back the way it was`; its failure line `NOT VERIFIED - ...` must not count).
4. `schtasks /Change /TN "Mantle Native" /DISABLE` (absent = said, skipped).
5. `schtasks /Change /TN Mantle /ENABLE` (absent = stop, exit 2, with what to do).
6. `-StartElectron`: `schtasks /Run /TN Mantle`.

`switch-to-native.ps1 -Exe <MantleNative.exe> [-ElectronClone <repo> | -ElectronExe <Mantle.exe>] [-StartNative] [-DryRun]`:
Electron's own quit (`electron.exe . --quit` in the clone = `npm run quit`, or `Mantle.exe --quit`), wait
until `%APPDATA%\mantle\lockfile` is no longer held and no installed `Mantle.exe` runs, Electron's own
`restore-windows.ps1` verified the same way, disable `Mantle`, register `Mantle Native` through
`install-autostart.ps1 -Exe` (which also enables and re-points it), `-StartNative` runs it.

## Kill switch and restore: what reading and tests show

The kill switch (Ctrl+Alt+Shift+Backspace, `Keys/ShellShortcuts.cs`, wired in `Shell/Tray/ShellTray.cs`)
runs stand down the supervisor, then `ShellGuard.RestoreAsync` through the sidecar, then quit, even when the
restore fails. ShellGuard falls back to `restore-windows.ps1 -StateFile ... -NoKill` (`DirtyStart.RunScript`)
only when the sidecar cannot be asked.

Covered by tests (all with fakes):
- `Keys/Win32HotkeysTests.cs`: the accelerator parses to Ctrl+Alt+Shift+0x08; `TheKillSwitch_StandsDownFirst_ThenRestores_ThenQuits_EvenWhenTheRestoreFails_AndASecondPressWaits` and the "no supervisor" case.
- `Features/TrayTests.cs`: registration outcome (both armed / neither in a test run).
- `Safety/ShellGuardTests.cs`: SAFE002/007 (what live sets), SAFE005/010/013 (restore releases in order, verifies, marks every piece), SAFE011/012 (a test run restores nothing; a blocked sidecar runs the script), SAFE006/014/015/016, SAFE020/021/024 (supervisor stands down first).
- `Safety/SafetyTests.cs`: the one shutdown runs the restore with a time limit and prints the recovery command.
- `Args/StartArgsTests.cs`: the live start prints the `restore-windows.ps1` command.
- `tools/ci.ps1` has no stage that runs `restore-windows.ps1` or the kill switch.

FOUND: nothing in native's build or publish (`Mantle.App.csproj`, `tools/publish.ps1`) puts
`tools\restore-windows.ps1` next to `MantleNative.exe`, yet `Host.RestoreScript`, the printed recovery
command, the dirty-start restore, ShellGuard's script fallback and `switch-back.ps1` all expect it there.
A published native folder today has no script to fall back on (switch-back stops at "no file at ...";
`-RestoreScript <clone>\tools\restore-windows.ps1` points it at Electron's copy). Fix in the App lane:
copy `..\..\..\tools\restore-windows.ps1` (and what it calls: `restore-banners.ps1`, `ramreclaim-undo.ps1`,
the OSD restore) into `tools\` of the output in `Mantle.App.csproj` and check it in publish.ps1.
Also: `restore-windows.ps1` stops Electron processes, installed `Mantle.exe` and every `Mantle.Sidecar`,
but NOT `MantleNative.exe` (its matches are `electron.exe` with the repo path and `Mantle.exe` in an
install folder), so for native "put Windows back" needs native to have quit first; switch-back waits for that.

UNVERIFIED without a real desktop: that the sidecar's restore actually brings the taskbar, the work area,
the keyboard hooks and the app bar back on this machine; that the hotkey is really delivered while another
app has focus (or is elevated); that `restore-windows.ps1` prints `verified` after a native run (it has only
been proven after Electron runs); that the scheduled task registers and starts the native exe 15 s after a
real logon; that `--quit` reaches a live native started by the Task Scheduler; the real paths of both switch
scripts. None of these were run.

## (d) Crash restart (BOOT-014): next lane, not built here

Already ported before this lane: `Mantle.Core/Lifecycle/Supervisor.cs` (wired in App.xaml.cs) starts
`Mantle.Sidecar.exe --supervise` with a record naming the NATIVE exe, stands it down first on a deliberate
quit, and stops respawning the supervisor after 3 stops in 10 minutes; the sidecar's supervise mode stops
restarting main after the 3rd crash inside 2 minutes and runs `restore-windows.ps1 -NoKill`. Missing, as a
next lane: an end-to-end crash test of native under the supervisor (Electron has
`lib/supervisor.e2e.test.js`), and the same missing restore script next to the exe (above), which the
sidecar's give-up path also calls.
