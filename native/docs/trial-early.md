# Native Mantle: the early trial (target 2026-11-29/30, after S3)

This trial has a short scope. The bar (islands look), quick settings and the launcher get the new look. Every other
panel keeps the old look, and some features stay with Windows. The owner runs every command below himself: no agent
runs any of it on the owner's desktop, and nothing here starts by itself until the switch command registers it.

## 1. Get the build

- What exists today:
  - `native/tools/ci.ps1` (build stage) runs `publish.ps1 -Mode aot`, which writes `native/out/bin/MantleNative/`.
  - Our runs give MantleNative.exe 24.1 MB, a folder of about 185 MB and 281 files, self-contained win-x64.
  - native-ci.yml never uploads that folder: its artifact `native-out` leaves the published app out.
- New in this lane: `ci.ps1 -Zip` (publish.ps1 `-Zip`) packs `native/out/bin/MantleNative-win-x64.zip`, with a
  `.sha256` file beside it. Inside the zip:
  - the published folder;
  - `Mantle.Sidecar.exe` (without it an unpacked folder has no platform exe and no crash supervisor);
  - `tools\switch-to-native.ps1`, `tools\switch-back.ps1`, `tools\install-autostart.ps1` and the `tools\` restore scripts;
  - `BUILD.txt`.

  Without `-Zip` nothing changes.
- Zip, not installer: Mantle-Setup for native (the INST rows) is still todo, so the trial uses the zip.
- Steps (the steps marked OWNER CARD need the owner's OK):
  1. stage14 = stage13 + S3, integrated and run (Mantle Engineer); reviewed (EM).
  2. OWNER CARD: push `native/stage14`.
  3. OWNER CARD: one committed run that brings the zip back:
     `cloudtest --recipe mantle-native-public --artifacts "native/out/bin/MantleNative-win-x64.zip*,native/out/logs/**" -- "pwsh -NoProfile -File native/tools/ci.ps1 -Stages build,unit,switch -Zip"`.
     The zip lands in `<worktree>\.cloudtest\<run id>\artifacts\bin\`.
  4. Check the hash: `Get-FileHash -Algorithm SHA256 MantleNative-win-x64.zip` must match the `.sha256` file.

## 2. Unpack

```powershell
$dir = "$env:LOCALAPPDATA\MantleNative\<stage14 sha>"
Expand-Archive -LiteralPath <path>\MantleNative-win-x64.zip -DestinationPath $dir
```

The logon task points at this exact folder. Keep the folder; a new build goes into a new folder.

Run sections 3 and 4 from a NORMAL PowerShell window, not "Run as administrator". The logon task starts native with
a limited token, and native's single-instance pipe answers only the same token, so an elevated `--quit` cannot reach
it. Switch-back then stops safely with "did not quit". This was found on the CI runner, whose job is elevated.

## 3. Switch to native (one command)

Electron Mantle is the clone its `Mantle` logon task starts. Its folder is the `<WorkingDirectory>` in
`schtasks /Query /TN Mantle /XML`.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$dir\tools\switch-to-native.ps1" -Exe "$dir\MantleNative.exe" -ElectronClone "<Electron clone folder>" -StartNative
```

In order, it:
1. asks Electron to quit (`electron . --quit`);
2. waits until Electron's lockfile is free (it never kills anything);
3. runs Electron's `restore-windows.ps1`, which must print `verified:`;
4. disables the `Mantle` task;
5. registers `Mantle Native` (it runs `MantleNative.exe --live` 15 s after sign-in);
6. starts it.

If any step fails, it stops and leaves the tasks as they were. Add `-DryRun` first to see every step without
changing anything.

## 4. Switch back to Electron (one command)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$dir\tools\switch-back.ps1" -Exe "$dir\MantleNative.exe" -StartElectron
```

In order, it:
1. runs `MantleNative.exe --quit`;
2. waits until native is gone (never killed);
3. runs `restore-windows.ps1`, which must print `verified:`;
4. disables `Mantle Native`;
5. enables `Mantle`;
6. starts it.

## 5. When something goes wrong

- **Emergency key: Ctrl+Alt+Shift+Backspace.** It puts Windows back, then quits native, even when the restore fails.
- **Crashes:** the supervisor (`Mantle.Sidecar.exe --supervise`) restarts native after crashes 1 to 3 within 10
  minutes. After the 4th it stays down, with Windows put back and verified. `%APPDATA%\mantle\crash.json` keeps the
  record, and the tray then says "Stopped after repeated errors at hh:mm". A clean quit (`--quit`, switch-back, the
  kill switch, signing out) never counts as a crash.
- **Recovery line (CORE-014):** a live start prints this before it touches the shell:
  `mantle: if anything goes wrong, run this and Windows comes back:` and then
  `powershell -NoProfile -ExecutionPolicy Bypass -File "$dir\tools\restore-windows.ps1"`.
  Double-click `$dir\tools\restore-windows.cmd` for the same thing. Then run switch-back (section 4).
- **Logs:** `%APPDATA%\mantle\mantle-native.log`. Native shares Electron's profile folder `%APPDATA%\mantle`.

## 6. What to judge

- **Look:** the bar over your own wallpaper, in dark and in light (Settings > Personalization > Colors). Do the
  islands frost the picture under them? Is the text readable on a bright wallpaper?
- **Motion:** auto-hide and reveal at the bottom edge, pop-ups opening and closing (quick settings, the launcher).
- **Keys:** the keys native takes are logged at start.
  - Run `Select-String "features: keys" $env:APPDATA\mantle\mantle-native.log | Select-Object -Last 1`.
  - Everything not on that line stays Windows'.
  - Volume keys are only observed: Windows still acts on them.
- **RAM** (the i3 with 11.7 GB):
  - Run `Get-Process MantleNative, Mantle.Sidecar | Select-Object Name, @{n='PrivateMB';e={[math]::Round($_.PrivateMemorySize64/1MB)}}, @{n='WorkingSetMB';e={[math]::Round($_.WorkingSet64/1MB)}}`.
  - Or use Task Manager > Details > the "Memory (private working set)" column.
- **CPU at rest:**
  - Leave the desktop alone for a minute, then run `Get-Counter '\Process(MantleNative)\% Processor Time' -SampleInterval 5 -MaxSamples 6`.
  - Divide by the number of logical cores.
- **Beside a game:** native rests while a game runs. Look for `rest` lines in the log, and check that the bar stays
  out of the way.
- **Still the old look (S4):** Downloads, the jump list, media, the notification centre with the calendar, and toasts.
  The top bar comes later (S5).
- **Still Windows':** Task View, the switcher (Alt+Tab), the clipboard (Win+V) and emoji (Win+.). Native has their
  logic but no window yet.

## 7. What has to be true by 2026-11-29, and who owns it

| item | owner |
|---|---|
| S3 (quick settings + launcher, new look) lands | Windows Desktop Engineer |
| review of S3 and of stage14 | Engineering Manager |
| stage14 integration; full wt run; `switch` stage green on the stage14 tip; the zip | Mantle Engineer |
| push of stage14, then the committed run that brings the zip back (two cards) | the owner |
| download, hash check, switch, judge | the owner |
| the bar/strip golden review | not needed for the trial |
| Electron checks `Local\Mantle.LiveShell` before going live (today only native refuses when Electron is live) | Mantle Engineer (Electron job). It doesn't block the trial when the switch scripts are used, because they disable the other task |
