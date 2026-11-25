# native/tools/switch-back.ps1 - from native Mantle back to Electron Mantle, verified.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File native\tools\switch-back.ps1 -Exe <MantleNative.exe> [-StartElectron]
#   ... -DryRun                    print every step and command; change NOTHING (no process, no task, no restore)
#   ... -RestoreScript <path>      default: tools\restore-windows.ps1 next to the exe (Program.cs's RestoreScript)
#   ... -StateFile <path>          default: %APPDATA%\mantle\shell-state.json (a live run's data root)
#   ... -NativeTask / -ElectronTask   default: 'Mantle Native' / 'Mantle'
#   ... -QuitTimeoutSec <n>        how long native gets to quit (default 30)
#
# The steps, in order; each one must succeed before the next runs:
#   1. ask native to put Windows back and quit: MantleNative.exe --quit (LaunchArgs: a message to the running
#      instance on the same data root, never an instance itself; exit 1 = nothing was running, which is fine)
#   2. wait until no MantleNative.exe from that path runs. It is NEVER killed here: a kill leaves the cursor
#      clip in force. If it does not go, this stops and says so, with nothing else changed.
#   3. run restore-windows.ps1 (without -NoKill: native is gone, and a leftover Mantle.Sidecar.exe supervisor
#      must not start it again) and require exit 0 AND its line "verified: Windows is back the way it was"
#      (case-sensitive at the start of a line, so its failure line "NOT VERIFIED - ..." never counts).
#      Not verified = stop: the logon tasks stay as they were, so nothing starts the wrong shell at sign-in.
#   4. disable the native logon task (schtasks /Change /TN "Mantle Native" /DISABLE), when there is one
#   5. enable Electron's (schtasks /Change /TN Mantle /ENABLE); no such task = stop with what to do
#   6. -StartElectron: start Electron's task now (schtasks /Run /TN Mantle; it is not a child of this terminal)
#
# Windows PowerShell 5.1 on purpose (no ternary, no '&&', no '?.').
param(
  [string]$Exe = '',
  [string]$RestoreScript = '',
  [string]$StateFile = '',
  [string]$NativeTask = 'Mantle Native',
  [string]$ElectronTask = 'Mantle',
  [int]$QuitTimeoutSec = 30,
  [switch]$StartElectron,
  [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
function J([string]$a, [string]$b) { return [System.IO.Path]::Combine($a, $b) }
function Parent([string]$p) { return [System.IO.Path]::GetDirectoryName($p) }
function Say([string]$text) { Write-Output $text }
function Fail([string]$text, [int]$code = 1) { Write-Output "switch-back: STOPPED: $text"; exit $code }
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

if (-not $Exe) {
  $guess = J (Parent $PSScriptRoot) 'MantleNative.exe'
  if (Test-Path -LiteralPath $guess -PathType Leaf) { $Exe = $guess } else { Fail "-Exe <path to MantleNative.exe> is required" }
}
$Exe = [System.IO.Path]::GetFullPath($Exe)
if (-not $RestoreScript) { $RestoreScript = J (J (Parent $Exe) 'tools') 'restore-windows.ps1' }
if (-not $StateFile) { $StateFile = J (J $env:APPDATA 'mantle') 'shell-state.json' }
if ($DryRun) { Say "switch-back: DRY RUN, nothing is changed" }
foreach ($need in @($Exe, $RestoreScript)) {
  if (-not (Test-Path -LiteralPath $need -PathType Leaf)) {
    if ($DryRun) { Say "  note: no file at $need; the real run stops here" } else { Fail "no file at $need" }
  }
}

# 1
Say "step 1: ask native Mantle to put Windows back and quit"
if ($DryRun) { Say ("  would run: " + (Show $Exe @('--quit')) + "   (and wait for it to answer)") }
else {
  $p = Start-Process -FilePath $Exe -ArgumentList '--quit' -Wait -PassThru -WindowStyle Hidden
  if ($p.ExitCode -eq 0) { Say "  asked the running native Mantle to quit" } else { Say "  no native Mantle answered (exit $($p.ExitCode)): nothing to quit" }
}

# 2
Say "step 2: wait up to $QuitTimeoutSec s until no MantleNative.exe from $Exe runs (never killed here)"
if ($DryRun) { Say "  would wait, then stop here if it still runs" }
else {
  $deadline = (Get-Date).AddSeconds($QuitTimeoutSec)
  do {
    $left = @(Get-Process -Name 'MantleNative' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ($_.Path -ieq $Exe) })
    if ($left.Count -eq 0) { break }
    Start-Sleep -Milliseconds 500
  } while ((Get-Date) -lt $deadline)
  if ($left.Count -gt 0) { Fail "native Mantle (pid $(($left | ForEach-Object { $_.Id }) -join ', ')) did not quit in $QuitTimeoutSec s, so nothing else was changed. Quit it from its tray menu or press Ctrl+Alt+Shift+Backspace, then run this again." }
  Say "  native Mantle is not running"
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
Say "step 4: disable the native logon task '$NativeTask'"
$argv = @('/Change', '/TN', $NativeTask, '/DISABLE')
if ($DryRun) { Say ("  would run: " + (Show 'schtasks.exe' $argv) + "   (when the task exists; none is said and skipped)") }
elseif (-not (TaskExists $NativeTask)) { Say "  no '$NativeTask' logon task; nothing to disable" }
else { Sch $argv; Say "  disabled '$NativeTask'" }

# 5
Say "step 5: enable Electron's logon task '$ElectronTask'"
$argv = @('/Change', '/TN', $ElectronTask, '/ENABLE')
if ($DryRun) { Say ("  would run: " + (Show 'schtasks.exe' $argv) + "   (when the task exists; none stops here)") }
elseif (-not (TaskExists $ElectronTask)) { Fail "Windows is back and native will not start at sign-in, but there is no '$ElectronTask' logon task to enable: run npm run autostart in the Electron clone (or reinstall Mantle)." 2 }
else { Sch $argv; Say "  enabled '$ElectronTask'" }

# 6
if ($StartElectron) {
  Say "step 6: start Electron's task now"
  $argv = @('/Run', '/TN', $ElectronTask)
  if ($DryRun) { Say ("  would run: " + (Show 'schtasks.exe' $argv)) }
  else { Sch $argv; Say "  started it through the Task Scheduler (it is not a child of this terminal)" }
} else { Say "step 6: skipped (no -StartElectron): Electron Mantle starts at the next sign-in, or run: schtasks /Run /TN $ElectronTask" }

if ($DryRun) { Say "switch-back: dry run done; nothing was changed" } else { Say "switch-back: done: Windows verified back, '$NativeTask' disabled, '$ElectronTask' enabled" }
exit 0
