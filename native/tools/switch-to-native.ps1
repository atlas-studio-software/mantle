# native/tools/switch-to-native.ps1 - from Electron Mantle to native Mantle, verified. The reverse of switch-back.ps1.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File native\tools\switch-to-native.ps1 -Exe <MantleNative.exe> [-ElectronClone <repo>] [-StartNative]
#   ... -DryRun                    print every step and command; change NOTHING (no process, no task, no restore)
#   ... -ElectronClone <folder>    Electron runs from a clone (npm start): quit it with its own `electron . --quit`
#                                  and restore with <clone>\tools\restore-windows.ps1
#   ... -ElectronExe <Mantle.exe>  Electron is installed (default %LOCALAPPDATA%\Programs\Mantle\Mantle.exe when no
#                                  -ElectronClone): quit with Mantle.exe --quit, restore with resources\app\tools\restore-windows.ps1
#   ... -RestoreScript / -StateFile   override those (state default: %APPDATA%\mantle\shell-state.json)
#   ... -NativeTask / -ElectronTask   default: 'Mantle Native' / 'Mantle'
#   ... -QuitTimeoutSec <n>        how long Electron gets to quit (default 30)
#
# The steps, in order; each one must succeed before the next runs:
#   1. ask Electron to put Windows back and quit, with its own command (npm run quit = electron . --quit)
#   2. wait until Electron is gone: its single-instance lock (%APPDATA%\mantle\lockfile) no longer held and no
#      installed Mantle.exe running (TwoShells.cs's signals). NEVER killed here; if it stays, this stops.
#   3. run Electron's restore-windows.ps1 (it also stops a leftover crash supervisor) and require exit 0 AND its
#      line "verified: Windows is back the way it was"; not verified = stop with the tasks as they were
#   4. disable Electron's logon task (schtasks /Change /TN Mantle /DISABLE), when there is one
#   5. register (or re-point) the native task: install-autostart.ps1 -Exe <MantleNative.exe> (schtasks /Create /F,
#      enabled); this also re-points a task that started an older native exe (DATA-037)
#   6. -StartNative: start it now (schtasks /Run /TN "Mantle Native")
#
# Windows PowerShell 5.1 on purpose (no ternary, no '&&', no '?.').
param(
  [string]$Exe = '',
  [string]$ElectronClone = '',
  [string]$ElectronExe = '',
  [string]$RestoreScript = '',
  [string]$StateFile = '',
  [string]$NativeTask = 'Mantle Native',
  [string]$ElectronTask = 'Mantle',
  [int]$QuitTimeoutSec = 30,
  [switch]$StartNative,
  [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
function J([string]$a, [string]$b) { return [System.IO.Path]::Combine($a, $b) }
function Parent([string]$p) { return [System.IO.Path]::GetDirectoryName($p) }
function Say([string]$text) { Write-Output $text }
function Fail([string]$text, [int]$code = 1) { Write-Output "switch-to-native: STOPPED: $text"; exit $code }
function Show([string]$exe, [string[]]$argv) {
  $parts = @()
  if ($exe -match '\s') { $parts += '"' + $exe + '"' } else { $parts += $exe }
  foreach ($a in $argv) { if ($a -match '\s') { $parts += '"' + $a + '"' } else { $parts += $a } }
  return ($parts -join ' ')
}
function TaskExists([string]$name) {
  $null = & schtasks.exe /Query /TN $name 2>&1
  return ($LASTEXITCODE -eq 0)
}
function Sch([string[]]$argv) {
  $out = & schtasks.exe @argv 2>&1
  if ($LASTEXITCODE -ne 0) { Fail ("schtasks failed: " + (Show 'schtasks.exe' $argv) + ": " + ($out -join ' ')) }
}
# Electron's lock held? (TwoShells.ProbeLock: an exclusive open fails while a running Electron has it open.)
function LockHeld([string]$file) {
  if (-not (Test-Path -LiteralPath $file)) { return $false }
  try { $s = [System.IO.File]::Open($file, 'Open', 'Read', 'None'); $s.Dispose(); return $false } catch { return $true }
}

if (-not $Exe) {
  $guess = J (Parent $PSScriptRoot) 'MantleNative.exe'
  if (Test-Path -LiteralPath $guess -PathType Leaf) { $Exe = $guess } else { Fail "-Exe <path to MantleNative.exe> is required" }
}
$Exe = [System.IO.Path]::GetFullPath($Exe)
$profileDir = J $env:APPDATA 'mantle'
if (-not $StateFile) { $StateFile = J $profileDir 'shell-state.json' }
if ($ElectronClone) {
  $ElectronClone = [System.IO.Path]::GetFullPath($ElectronClone)
  $quitExe = J $ElectronClone 'node_modules\electron\dist\electron.exe'
  $quitArgs = @('.', '--quit')
  $quitIn = $ElectronClone
  if (-not $RestoreScript) { $RestoreScript = J $ElectronClone 'tools\restore-windows.ps1' }
  $installedExe = ''
} else {
  if (-not $ElectronExe) { $ElectronExe = J $env:LOCALAPPDATA 'Programs\Mantle\Mantle.exe' }
  $ElectronExe = [System.IO.Path]::GetFullPath($ElectronExe)
  $quitExe = $ElectronExe
  $quitArgs = @('--quit')
  $quitIn = Parent $ElectronExe
  if (-not $RestoreScript) { $RestoreScript = J $quitIn 'resources\app\tools\restore-windows.ps1' }
  $installedExe = $ElectronExe
}
$installer = J $PSScriptRoot 'install-autostart.ps1'
if ($DryRun) { Say "switch-to-native: DRY RUN, nothing is changed" }
foreach ($need in @($Exe, $quitExe, $RestoreScript, $installer)) {
  if (-not (Test-Path -LiteralPath $need -PathType Leaf)) {
    if ($DryRun) { Say "  note: no file at $need; the real run stops here" } else { Fail "no file at $need" }
  }
}

# 1
Say "step 1: ask Electron Mantle to put Windows back and quit, with its own command"
if ($DryRun) { Say ("  would run: " + (Show $quitExe $quitArgs) + "   (in $quitIn, and wait for it to answer)") }
else {
  $p = Start-Process -FilePath $quitExe -ArgumentList $quitArgs -WorkingDirectory $quitIn -Wait -PassThru -WindowStyle Hidden
  Say "  asked Electron Mantle to quit (exit $($p.ExitCode))"
}

# 2
$lock = J $profileDir 'lockfile'
Say "step 2: wait up to $QuitTimeoutSec s until Electron is gone: $lock not held and no installed Mantle.exe running (never killed here)"
if ($DryRun) { Say "  would wait, then stop here if it still runs" }
else {
  $deadline = (Get-Date).AddSeconds($QuitTimeoutSec)
  do {
    $held = LockHeld $lock
    $left = @()
    if ($installedExe) { $left = @(Get-Process -Name 'Mantle' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ($_.Path -ieq $installedExe) }) }
    if (-not $held -and $left.Count -eq 0) { break }
    Start-Sleep -Milliseconds 500
  } while ((Get-Date) -lt $deadline)
  if ($held -or $left.Count -gt 0) { Fail "Electron Mantle did not quit in $QuitTimeoutSec s (lock held: $held; Mantle.exe pids: $(($left | ForEach-Object { $_.Id }) -join ', ')), so nothing else was changed. Quit it from its tray menu or press Ctrl+Alt+Shift+Backspace, then run this again." }
  Say "  Electron Mantle is not running"
}

# 3
$restoreArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $RestoreScript, '-StateFile', $StateFile)
Say "step 3: put Windows back and require 'verified'"
if ($DryRun) { Say ("  would run: " + (Show 'powershell.exe' $restoreArgs)); Say "  and require exit 0 and a line starting 'verified:'" }
else {
  $out = @(& powershell.exe @restoreArgs 2>&1 | ForEach-Object { [string]$_ })
  $code = $LASTEXITCODE
  foreach ($l in $out) { Say "  | $l" }
  $verified = @($out | Where-Object { $_ -cmatch '^verified:' }).Count -gt 0
  if ($code -ne 0 -or -not $verified) { Fail "restore-windows.ps1 did not verify Windows is back (exit $code), so the logon tasks were left as they are. Follow its 'Try, in order' lines above." }
}

# 4
Say "step 4: disable Electron's logon task '$ElectronTask'"
$argv = @('/Change', '/TN', $ElectronTask, '/DISABLE')
if ($DryRun) { Say ("  would run: " + (Show 'schtasks.exe' $argv) + "   (when the task exists; none is said and skipped)") }
elseif (-not (TaskExists $ElectronTask)) { Say "  no '$ElectronTask' logon task; nothing to disable" }
else { Sch $argv; Say "  disabled '$ElectronTask'" }

# 5
$instArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $installer, '-Exe', $Exe, '-TaskName', $NativeTask)
Say "step 5: register (or re-point) the native logon task '$NativeTask' for $Exe"
if ($DryRun) { Say ("  would run: " + (Show 'powershell.exe' $instArgs)) }
else {
  $out = @(& powershell.exe @instArgs 2>&1 | ForEach-Object { [string]$_ })
  $code = $LASTEXITCODE
  foreach ($l in $out) { Say "  | $l" }
  if ($code -ne 0) { Fail "Windows is back and Electron will not start at sign-in, but the native task could not be registered (see above)." 2 }
}

# 6
if ($StartNative) {
  Say "step 6: start the native task now"
  $argv = @('/Run', '/TN', $NativeTask)
  if ($DryRun) { Say ("  would run: " + (Show 'schtasks.exe' $argv)) }
  else { Sch $argv; Say "  started it through the Task Scheduler (it is not a child of this terminal)" }
} else { Say "step 6: skipped (no -StartNative): native Mantle starts at the next sign-in, or run: schtasks /Run /TN `"$NativeTask`"" }

if ($DryRun) { Say "switch-to-native: dry run done; nothing was changed" } else { Say "switch-to-native: done: Windows verified back, '$ElectronTask' disabled, '$NativeTask' registered" }
exit 0
