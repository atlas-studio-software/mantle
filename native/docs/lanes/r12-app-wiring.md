# Lane r12-app-wiring (2026-11-25)

Branch `native/r12-app-wiring`, off native/stage10 438a52dc with r12-boot (d7bcc350) and r12-levitate-host (412e102e)
merged first. EM ticket t_af477e. The Mantle.App side of what those two lanes left open.

## 1. The live-shell lock in Program.cs

`Program.Main`, in the `--live` branch, right after the TwoShells refusal and before the live lines are printed:
`LiveLock.TryTake` (Safety/LiveLock.cs, `Local\Mantle.LiveShell`). A refusal is logged, said on stderr, lets the
single-instance claim and the log go and exits `ExitCodes.Failed`, like the TwoShells refusal. The holder lives in
`Host.LiveLock` for the life of the process, and Main's `finally` disposes it before `claim.Dispose()` on the same thread
(a named mutex has thread affinity). A log line names the lock, plus the "abandoned ... taken over" note when there is one.

Tests: `Boot/AppWiringTests.Program_takes_the_live_lock_...` pins the order from the source (one take, in the live branch,
after TwoShells, before LiveLines; the refusal exits Failed and disposes the claim; the release comes before the claim in
the finally). The mutex itself is covered by `Safety/LiveLockTests` (r12-boot). NOT run: a real `--live` start, so it is
not proven that a second native `--live` on another data root refuses on a desktop.

## 2. The restore scripts next to MantleNative.exe

`Mantle.App.csproj` item `MantleRestoreScript`: the repo's `tools\restore-windows.ps1`, `restore-windows.cmd` and the
three scripts restore-windows.ps1 runs from its own folder (`ramreclaim-undo.ps1`, `restore-banners.ps1`,
`restore-osd.ps1`), linked to `tools\` with CopyToOutputDirectory and CopyToPublishDirectory PreserveNewest. They are the
same files Electron ships, with no second copy.

`tools/publish.ps1` prints `PUBLISH <mode>: FAILED (tools\ next to MantleNative.exe is missing ...)` and exits 1 when
any of the five is missing from the published folder, so ci.ps1's build stage fails (native/out/bin/MantleNative).

Tests: `Boot/AppWiringTests.Every_script_restore_windows_calls_...` reads every `Join-Path $PSScriptRoot '<x>'` in
restore-windows.ps1 and requires the csproj list and publish.ps1's `$restoreSet` to hold each one, and the two lists to be
equal. `The_exe_finds_its_restore_script_...` checks `Host.RestoreScript`'s path and, when the build stage left a
published exe in native/out/bin/MantleNative, that all five files are next to it.

Still true (r12-boot.md): restore-windows.ps1 does not stop `MantleNative.exe`, so native must have quit first
(switch-back.ps1 waits for that). Not proven: that the script prints `verified:` after a native run.

## 3. The saved Dim through the new reader

`LevitatePanel.FromGlass` now reads `LevitateDim.Read(DesktopGlass.Look?.Values, theme)`. `GlassLook.Values` is the look
host's `Local()` (its saved `{ lookSync, accent, glass }`, a copy), taken in `DesktopGlass.Feed` alongside the Wash
answers. Before this it read Wash's answer.

`LevitateDim.Resolve` follows look-sync v2.1 (EM branch dca42dc, look-sync.js:53-58 SEED and CONTRACT.md:352/:378). The
seed is 0.20 in dark and light. A stored value that is a finite number in 0.20..0.90 wins, 0.30 included (no special
case). Missing, not a number (a string, null, a bool, NaN) or out of range (1.5, 0.10, -1) reads as the seed 0.20. It was
clamped before (0.95 gave 0.90), and the two existing assertions that pinned the clamp now expect 0.20. This is now the same
rule as `Wash.Alpha` and `Legibility.PanelLift`.

Tests: `Look/LevitateTests.Saved_dim_v21_...` (a theory, dark and light): missing, no glass and no look give 0.20; a stored
0.30 gives 0.30, also with dimAuto false; 1.5, 0.1, "0.45", null and true give 0.20; NaN through Resolve gives 0.20; the
range ends 0.20 and 0.90 hold. `PopupLevitateTests`: `Dim(5)` is 0.20.

NOT run: a desktop with a saved Dim, so the painted panel is not checked.
