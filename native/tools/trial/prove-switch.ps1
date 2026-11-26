<#
  native/tools/trial/prove-switch.ps1 - the early trial's way in and out (docs/trial-early.md), proven on a CI
  runner. RUNNER ONLY: it refuses unless CI or GITHUB_ACTIONS is set, because it changes logon tasks, starts native
  Mantle with --live and puts the shell back. Never run it on a real desktop.

    pwsh -NoProfile -File native/tools/trial/prove-switch.ps1 -Exe <MantleNative.exe> -Repo <repo root> [-Out <dir>]

  What stands in on the runner:
  - Electron Mantle's logon task "Mantle": a stand-in task whose action is "cmd.exe /c exit 0" (the runner has no
    Electron logon task). Electron itself is the repo clone the parity stages run (node_modules\electron); it is not
    running live, so switch-to-native's "electron . --quit" finds nothing to quit and its lockfile is not held.
  The checks, each printed as "  ok <what>" or "  FAIL <what>", then one line "SWITCH: ok (...)" or "SWITCH: FAILED (...)":
  1. before: no MantleNative.exe runs and Local\Mantle.LiveShell is not held
  2. switch-to-native.ps1 -Exe <exe> -ElectronClone <repo> -StartNative: exit 0 and its "done" line; "Mantle" is
     disabled, "Mantle Native" is enabled and runs that exe; native comes up live (MantleNative.exe runs and holds
     Local\Mantle.LiveShell) within 60 s
  3. switch-back.ps1 -Exe <exe> -StartElectron: exit 0, a "| verified:" line from restore-windows.ps1 and its "done"
     line; "Mantle" is enabled again, "Mantle Native" is disabled; no MantleNative.exe from that path runs; the lock
     is gone (or free); Electron's lockfile is not held; no Mantle.Sidecar.exe --supervise is left
  Both tasks are deleted at the end (the runner is thrown away anyway).
#>
param(
  [Parameter(Mandatory = $true)][string]$Exe,
  [Parameter(Mandatory = $true)][string]$Repo,
  [string]$Out = '',
  # the trial zip (publish.ps1 -Zip): when given, it is unpacked OUTSIDE the repo and that copy is the one switched to,
  # so the proof runs the owner's layout (no checkout's sidecar\bin to fall back on)
  [string]$Zip = ''
)
$ErrorActionPreference = 'Stop'
if (-not ($env:CI -or $env:GITHUB_ACTIONS)) { Write-Host 'SWITCH: FAILED (refused: runner only; CI and GITHUB_ACTIONS are not set, and this changes logon tasks and the shell)'; exit 2 }
if ($Zip) {
  $base = $env:RUNNER_TEMP; if (-not $base) { $base = $env:TEMP }
  $unpack = Join-Path $base 'MantleNative-trial'
  if (Test-Path -LiteralPath $unpack) { Remove-Item -Recurse -Force -LiteralPath $unpack }
  Expand-Archive -LiteralPath $Zip -DestinationPath $unpack
  $Exe = Join-Path $unpack 'MantleNative.exe'
  Write-Host "  the trial zip is unpacked to $unpack (outside the repo); its MantleNative.exe is the one switched to"
}
$Exe = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { Write-Host "SWITCH: FAILED (no exe at $Exe)"; exit 1 }
$Repo = [System.IO.Path]::GetFullPath($Repo)
if ($Out) { New-Item -ItemType Directory -Force -Path $Out | Out-Null }
$fails = [System.Collections.Generic.List[string]]::new()
$notes = [System.Collections.Generic.List[string]]::new()
function Say([string]$s) { Write-Host $s }
function Check([bool]$ok, [string]$what) { if ($ok) { Say "  ok $what" } else { Say "  FAIL $what"; $fails.Add($what) } }
function Note([string]$what) { Say "  note $what"; $notes.Add($what) }

$toolsDir = Join-Path (Split-Path -Parent $Exe) 'tools'
$toNative = Join-Path $toolsDir 'switch-to-native.ps1'
$back = Join-Path $toolsDir 'switch-back.ps1'
if (-not (Test-Path -LiteralPath $toNative) -or -not (Test-Path -LiteralPath $back)) {
  # a folder published without -Zip has no switch scripts: use the repo's copies (the same files)
  $toNative = Join-Path $Repo 'native\tools\switch-to-native.ps1'
  $back = Join-Path $Repo 'native\tools\switch-back.ps1'
  Note "the published folder has no switch scripts (built without -Zip): the repo's copies run"
} else { Say "  the switch scripts next to the exe run (the zip's layout)" }
$lockFile = Join-Path (Join-Path $env:APPDATA 'mantle') 'lockfile'

function Task([string]$name) {
  # -> $null (absent) or @{ Enabled; Command }
  $xml = & schtasks.exe /Query /TN $name /XML 2>$null
  if ($LASTEXITCODE -ne 0 -or -not $xml) { return $null }
  [xml]$x = ($xml -join "`n")
  $ns = @{ t = 'http://schemas.microsoft.com/windows/2004/02/mit/task' }
  $en = Select-Xml -Xml $x -XPath '//t:Settings/t:Enabled' -Namespace $ns
  $cmd = Select-Xml -Xml $x -XPath '//t:Actions/t:Exec/t:Command' -Namespace $ns
  return @{ Enabled = (-not $en) -or ($en.Node.InnerText -eq 'true'); Command = $(if ($cmd) { $cmd.Node.InnerText.Trim('"') } else { '' }) }
}
function LiveLock() {
  # 'absent' (no such mutex), 'free' (it exists and nobody holds it), 'held', or 'abandoned'
  $m = $null
  if (-not [System.Threading.Mutex]::TryOpenExisting('Local\Mantle.LiveShell', [ref]$m)) { return 'absent' }
  try {
    if ($m.WaitOne(0)) { $m.ReleaseMutex(); return 'free' }
    return 'held'
  } catch [System.Threading.AbandonedMutexException] { try { $m.ReleaseMutex() } catch { }; return 'abandoned' }
  finally { $m.Dispose() }
}
function NativeProcs() { @(Get-Process -Name 'MantleNative' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ($_.Path -ieq $Exe) }) }
function LockHeld([string]$file) {
  if (-not (Test-Path -LiteralPath $file)) { return $false }
  try { $s = [System.IO.File]::Open($file, 'Open', 'Read', 'None'); $s.Dispose(); return $false } catch { return $true }
}
function Run51([string]$script, [string[]]$argv, [string]$tag) {
  # the switch scripts are Windows PowerShell 5.1 scripts: run them the way the trial doc does
  $all = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script) + $argv
  # (not $out: PowerShell names are case-insensitive, and $Out is this script's output folder)
  $got = @(& powershell.exe @all 2>&1 | ForEach-Object { [string]$_ })
  $code = $LASTEXITCODE
  foreach ($l in $got) { Say "    $tag | $l" }
  if ($Out) { $got | Set-Content -Encoding utf8 (Join-Path $Out "$tag.txt") }
  return @{ Code = $code; Lines = $got }
}

function Elevated() {
  $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
  return ([System.Security.Principal.WindowsPrincipal]::new($id)).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Run51Limited([string]$script, [string[]]$argv, [string]$tag) {
  # What a normal (not "Run as administrator") terminal does: the logon task starts native with a limited token
  # (RunLevel LeastPrivilege), and native's single-instance pipe accepts only the same user's token. So when this
  # job is elevated, the script runs through a one-shot LIMITED task, like the owner's own terminal would.
  $dir = $Out; if (-not $dir) { $dir = $env:TEMP }
  $txt = Join-Path $dir "$tag.txt"; $flag = Join-Path $dir "$tag.done"; $cmd = Join-Path $dir "$tag.cmd"
  Remove-Item -Force -ErrorAction SilentlyContinue $txt, $flag
  $q = ($argv | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
  Set-Content -LiteralPath $cmd -Encoding ascii -Value @(
    '@echo off',
    ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $script + '" ' + $q + ' > "' + $txt + '" 2>&1'),
    ('echo exit %ERRORLEVEL%>> "' + $txt + '"'),
    ('echo done> "' + $flag + '"'))
  $null = & schtasks.exe /Create /TN 'MantleTrialStep' /TR ('"' + $cmd + '"') /SC ONCE /ST 23:59 /RL LIMITED /F 2>&1
  $null = & schtasks.exe /Run /TN 'MantleTrialStep' 2>&1
  for ($i = 0; $i -lt 240 -and -not (Test-Path -LiteralPath $flag); $i++) { Start-Sleep -Milliseconds 500 }
  & schtasks.exe /Delete /TN 'MantleTrialStep' /F 2>$null | Out-Null
  $got = @(); if (Test-Path -LiteralPath $txt) { $got = @(Get-Content -LiteralPath $txt) }
  $code = -1
  $last = @($got | Where-Object { $_ -match '^exit -?\d+' } | Select-Object -Last 1)
  if ($last) { $code = [int](($last[0] -replace '^exit ', '').Trim()) }
  $got = @($got | Where-Object { $_ -notmatch '^exit -?\d+' })
  foreach ($l in $got) { Say "    $tag | $l" }
  if (-not (Test-Path -LiteralPath $flag)) { Say "    $tag | (the limited task did not finish in 120 s)" }
  return @{ Code = $code; Lines = $got }
}

Say "prove-switch: exe $Exe; Electron clone $Repo; session $([System.Diagnostics.Process]::GetCurrentProcess().SessionId); user $env:USERNAME; elevated $(Elevated)"

# ---- the stand-in Electron task
& schtasks.exe /Delete /TN 'Mantle Native' /F 2>$null | Out-Null
& schtasks.exe /Delete /TN 'Mantle' /F 2>$null | Out-Null
$made = & schtasks.exe /Create /TN 'Mantle' /TR 'cmd.exe /c exit 0' /SC ONLOGON /RL LIMITED /F 2>&1
if ($LASTEXITCODE -ne 0) {
  Note ("an ONLOGON stand-in could not be made (" + ($made -join ' ') + "): a ONCE task stands in")
  $made = & schtasks.exe /Create /TN 'Mantle' /TR 'cmd.exe /c exit 0' /SC ONCE /ST 23:59 /F 2>&1
}
$t = Task 'Mantle'
Check ($null -ne $t -and $t.Enabled) "the stand-in Electron task 'Mantle' exists and is enabled before the switch"

# ---- 1 before
Check ((NativeProcs).Count -eq 0) 'before: no MantleNative.exe runs'
$l0 = LiveLock
Check ($l0 -ne 'held') "before: Local\Mantle.LiveShell is not held ($l0)"

# ---- 2 switch to native
Say 'switch-to-native:'
$r = Run51 $toNative @('-Exe', $Exe, '-ElectronClone', $Repo, '-StartNative') 'to-native'
Check ($r.Code -eq 0) "switch-to-native.ps1 exit 0 (was $($r.Code))"
Check (@($r.Lines | Where-Object { $_ -like 'switch-to-native: done:*' }).Count -gt 0) 'switch-to-native printed its done line'
Check (@($r.Lines | Where-Object { $_ -cmatch '^\s*\| verified:' }).Count -gt 0) "switch-to-native: Electron's restore-windows.ps1 printed verified:"
$te = Task 'Mantle'; $tn = Task 'Mantle Native'
Check ($null -ne $te -and -not $te.Enabled) "after switch-to-native: 'Mantle' is disabled"
Check ($null -ne $tn -and $tn.Enabled) "after switch-to-native: 'Mantle Native' is enabled"
Check ($null -ne $tn -and $tn.Command -ieq $Exe) "after switch-to-native: 'Mantle Native' runs $Exe (it runs '$(if ($tn) { $tn.Command })')"
$up = $false; $lk = 'absent'
for ($i = 0; $i -lt 120; $i++) {
  if ((NativeProcs).Count -gt 0) { $lk = LiveLock; if ($lk -eq 'held') { $up = $true; break } }
  Start-Sleep -Milliseconds 500
}
$np = NativeProcs
Check ($np.Count -gt 0) "native started through the task within 60 s ($($np.Count) process(es)$(if ($np.Count) { ', session ' + $np[0].SessionId }))"
Check $up "native holds Local\Mantle.LiveShell (it is the live shell; the lock reads $lk)"
$nlog = Join-Path (Join-Path $env:APPDATA 'mantle') 'mantle-native.log'
if (Test-Path -LiteralPath $nlog) {
  $tail = @(Get-Content -LiteralPath $nlog -Tail 400)
  foreach ($l in @($tail | Where-Object { $_ -match 'live shell: holding|two shells|mantle: |restore|refus' } | Select-Object -Last 12)) { Say "    native-log | $l" }
} else { Note "no native log at $nlog yet" }
Start-Sleep -Seconds 8   # let the live start settle (the taskbar, the app bar, the supervisor)

# ---- 3 switch back
Say 'switch-back:'
if (Elevated) {
  Note 'this job is elevated, so switch-back runs through a one-shot LIMITED task (a normal terminal, the token the logon task gives native)'
  $b = Run51Limited $back @('-Exe', $Exe, '-StartElectron') 'back'
} else { $b = Run51 $back @('-Exe', $Exe, '-StartElectron') 'back' }
if (Test-Path -LiteralPath $nlog) {
  foreach ($l in @(Get-Content -LiteralPath $nlog -Tail 400 | Where-Object { $_ -match 'single instance|quit|stand|restore|exit:' } | Select-Object -Last 14)) { Say "    native-log | $l" }
}
Check ($b.Code -eq 0) "switch-back.ps1 exit 0 (was $($b.Code))"
Check (@($b.Lines | Where-Object { $_ -cmatch '^\s*\| verified:' }).Count -gt 0) "switch-back: restore-windows.ps1 printed verified: after the native run"
Check (@($b.Lines | Where-Object { $_ -like 'switch-back: done*' }).Count -gt 0) 'switch-back printed its done line'
$te = Task 'Mantle'; $tn = Task 'Mantle Native'
Check ($null -ne $te -and $te.Enabled) "after switch-back: 'Mantle' is enabled again"
Check ($null -eq $tn -or -not $tn.Enabled) "after switch-back: 'Mantle Native' is disabled"
Check ((NativeProcs).Count -eq 0) 'after switch-back: no MantleNative.exe from that path runs'
$l1 = LiveLock
Check ($l1 -eq 'absent' -or $l1 -eq 'free') "after switch-back: nothing holds Local\Mantle.LiveShell ($l1)"
Check (-not (LockHeld $lockFile)) "after switch-back: Electron's lockfile is not held ($lockFile)"
$sup = @(Get-CimInstance Win32_Process -Filter "Name='Mantle.Sidecar.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -match '--supervise' })
Check ($sup.Count -eq 0) "after switch-back: no Mantle.Sidecar.exe --supervise is left ($($sup.Count))"

# ---- clean up (the runner is thrown away; the next stage still gets a plain desktop)
& schtasks.exe /Delete /TN 'Mantle Native' /F 2>$null | Out-Null
& schtasks.exe /Delete /TN 'Mantle' /F 2>$null | Out-Null

$n = $fails.Count
if ($n -gt 0) { Say "SWITCH: FAILED ($n check(s) failed: $($fails -join '; '))"; exit 1 }
Say ("SWITCH: ok (to native and back: tasks swapped and swapped back, native live then stopped, verified twice, lock and lockfile free" + $(if ($notes.Count) { '; notes: ' + ($notes -join '; ') } else { '' }) + ')')
exit 0
