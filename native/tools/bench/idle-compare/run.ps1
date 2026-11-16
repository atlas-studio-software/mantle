# run.ps1 - idle CPU and memory of Electron Mantle and native Mantle, on ONE runner, in ONE job, by ONE method.
#
#   cloudtest --recipe mantle-native-public --timeout 90 -- "pwsh -NoProfile -File native/tools/bench/idle-compare/run.ps1"
#   options: -Themes light,dark  -Rounds 3  -WindowSec 45  -SettleSec 30  -Exe <native exe>  -Out <dir>
#
# NOT a CI gate and not a ci.ps1 stage: a measurement. RUNNER ONLY (refuses unless CI / GITHUB_ACTIONS is set): it
# sets the runner's wallpaper, Windows theme and taskbar auto-hide, moves the cursor and shows both apps' test windows.
#
# Per theme, Rounds alternating pairs (Electron, native, Electron, native, ...). Each app run:
#   start it in its test mode (its own temp profile, no user data) with the parity stage's desktop and fixture window;
#   wait for it to be up, settle SettleSec; then two windows of WindowSec each:
#     hidden  - bar hidden at rest, the cursor mid-screen
#     shown   - the cursor on the bottom edge (the hover reveal), then resting on the pill, bar up and idle
#   per window: the sum of QueryProcessCycleTime over all of the app's own processes (collected at the window's
#   start by walking the process tree down from the app's main PID, plus a crash supervisor that names that PID),
#   as % of one core at the runner's measured cycle rate; the charged CPU time beside it; private bytes and
#   working set (MB) of the same processes at the window's end;
#   then the app is closed by its own quit path (a second instance with --quit), else by its main PID only.
# Writes <Out>/results.json, <Out>/summary.md, <Out>/windows.txt (per process rows) and per run logs and
# screenshots. Last line: IDLE-COMPARE: ok (...) or IDLE-COMPARE: FAILED (...).
param(
  [string]$Themes = 'light,dark',
  [int]$Rounds = 3,
  [int]$WindowSec = 45,
  [int]$SettleSec = 30,
  [string]$Exe,
  [string]$Out
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
if (-not $Out) { $Out = Join-Path $root 'native\out\bench\idle-compare' }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path
function Say($m) { Write-Host ("idlecmp {0}: {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) }
$problems = [System.Collections.Generic.List[string]]::new()
function Problem($m) { $problems.Add($m); Write-Host "idlecmp PROBLEM: $m" }

if (-not $env:CI -and -not $env:GITHUB_ACTIONS) {
  Write-Host 'IDLE-COMPARE: FAILED (refused: not on a CI runner; it changes the desktop and shows test windows)'
  exit 2
}

# the helpers first, so a compile error fails in seconds instead of after the publish
Add-Type -Path (Join-Path $PSScriptRoot 'IdleCmp.cs')
Add-Type -Path (Join-Path $root 'native\tools\parity\ParityNative.cs')
Add-Type -AssemblyName System.Drawing
[void][ParityNative]::DpiAware()
function Save-Bgra([byte[]]$px, [int]$w, [int]$h, [string]$path) {
  $bmp = [System.Drawing.Bitmap]::new($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $d = $bmp.LockBits([System.Drawing.Rectangle]::new(0, 0, $w, $h), [System.Drawing.Imaging.ImageLockMode]::WriteOnly, $bmp.PixelFormat)
  for ($row = 0; $row -lt $h; $row++) { [System.Runtime.InteropServices.Marshal]::Copy($px, $row * $w * 4, [IntPtr]::Add($d.Scan0, $row * $d.Stride), $w * 4) }
  $bmp.UnlockBits($d); $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}

# ------------------------------------------------------------------ the runner
$scr = [ParityNative]::Screen(); $W = $scr[0]; $H = $scr[1]
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$os = Get-CimInstance Win32_OperatingSystem
$perMs = [IdleCmp]::CyclesPerMs($false)
$runner = [ordered]@{
  cpu = $cpu.Name.Trim(); cores = [int]$cpu.NumberOfCores; logical = [int]$cpu.NumberOfLogicalProcessors
  os = "$($os.Caption) $($os.Version)"; ramGB = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
  screen = "${W}x${H}"; dpi = [ParityNative]::PrimaryDpi(); cyclesPerMs = [math]::Round($perMs, 1)
  runId = $env:GITHUB_RUN_ID; sha = $env:GITHUB_SHA
}
Say ("runner: {0}, {1} cores / {2} logical, {3}, {4} GB; screen {5} at {6} dpi; cycle rate {7:N0} per ms (~{8:N2} GHz)" -f `
  $runner.cpu, $runner.cores, $runner.logical, $runner.os, $runner.ramGB, $runner.screen, $runner.dpi, $perMs, ($perMs / 1e6))

# ------------------------------------------------------------------ the two apps from this checkout
if (-not $Exe) { $Exe = $env:MANTLE_NATIVE_EXE }
$bin = Join-Path $root 'native\out\bin\MantleNative'
if (-not $Exe -or -not (Test-Path $Exe)) { $Exe = Join-Path $bin 'MantleNative.exe' }
if (-not (Test-Path $Exe)) {
  Say "publishing the native app (Native AOT) to $bin"
  $t0 = Get-Date
  & pwsh -NoProfile -File (Join-Path $root 'native\tools\publish.ps1') -Mode aot -Out $bin *> (Join-Path $Out 'publish.log')
  Say ("publish exit {0} in {1:N0} s" -f $LASTEXITCODE, ((Get-Date) - $t0).TotalSeconds)
}
if (-not (Test-Path $Exe)) { Write-Host "IDLE-COMPARE: FAILED (no native exe at $Exe; see $Out\publish.log)"; exit 3 }
Push-Location $root
try { $electronExe = (& node -p "require('electron')" 2>$null | Select-Object -First 1) } finally { Pop-Location }
if (-not $electronExe -or -not (Test-Path $electronExe)) { Write-Host 'IDLE-COMPARE: FAILED (no Electron: npm deps not installed in the checkout)'; exit 3 }
$electronVer = (Get-Item $electronExe).VersionInfo.ProductVersion
Say "native exe $Exe ($([math]::Round((Get-Item $Exe).Length / 1MB, 1)) MB); electron $electronExe ($electronVer)"
$runner.nativeExe = $Exe; $runner.electron = $electronVer

# ------------------------------------------------------------------ the desktop, as the parity stage sets it
$wall = Join-Path $root 'assets\backgrounds\snow-peak.jpg'
Say ("wallpaper " + [ParityNative]::SetWallpaper($wall))
$pers = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'   # the RUNNER's key (refused off CI above)
New-Item -Force -Path $pers | Out-Null
Set-ItemProperty -Path $pers -Name EnableTransparency -Value 1 -Type DWord
function Set-WinTheme([string]$t) {
  $v = if ($t -eq 'light') { 1 } else { 0 }
  Set-ItemProperty -Path $pers -Name AppsUseLightTheme -Value $v -Type DWord
  Set-ItemProperty -Path $pers -Name SystemUsesLightTheme -Value $v -Type DWord
  [ParityNative]::BroadcastTheme()
}
[ParityNative]::TaskbarAutoHide($true)
$away = @([int]($W / 2), [int]($H / 3))
[ParityNative]::ParkCursor($away[0], $away[1])
# the same running app for both strips: a plain window, top left, away from the bar (run-parity.ps1's fixture)
$fixtureCmd = "Add-Type -AssemblyName System.Windows.Forms; `$f = New-Object System.Windows.Forms.Form; `$f.Text = 'Parity fixture'; " +
  "`$f.StartPosition = 'Manual'; `$f.Location = New-Object System.Drawing.Point(40, 40); `$f.Size = New-Object System.Drawing.Size(420, 300); " +
  "[System.Windows.Forms.Application]::Run(`$f)"
$fixture = Start-Process powershell.exe -ArgumentList '-NoProfile', '-EncodedCommand', ([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($fixtureCmd))) -PassThru
Start-Sleep -Seconds 3

# ------------------------------------------------------------------ the app's processes (for MEASURING only)
# The tree under the main PID (a child counts only if it started after its parent: a reused PID is not a child),
# plus any process outside the tree whose command line names the main PID as the one it supervises.
function Get-AppProcs([int]$mainPid) {
  $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name, CreationDate, CommandLine)
  $byId = @{}; foreach ($p in $all) { $byId[[int]$p.ProcessId] = $p }
  if (-not $byId.ContainsKey($mainPid)) { return @() }
  $kids = @{}; foreach ($p in $all) { $pp = [int]$p.ParentProcessId; if (-not $kids.ContainsKey($pp)) { $kids[$pp] = [System.Collections.Generic.List[object]]::new() }; $kids[$pp].Add($p) }
  $found = [System.Collections.Generic.List[object]]::new(); $seen = @{}
  $queue = [System.Collections.Generic.Queue[object]]::new(); $queue.Enqueue($byId[$mainPid]); $seen[$mainPid] = $true
  while ($queue.Count) {
    $p = $queue.Dequeue(); $found.Add($p)
    if ($kids.ContainsKey([int]$p.ProcessId)) {
      foreach ($c in $kids[[int]$p.ProcessId]) {
        if ($seen.ContainsKey([int]$c.ProcessId)) { continue }
        if ($c.CreationDate -and $p.CreationDate -and $c.CreationDate -lt $p.CreationDate) { continue }
        $seen[[int]$c.ProcessId] = $true; $queue.Enqueue($c)
      }
    }
  }
  foreach ($p in $all) {
    if (-not $seen.ContainsKey([int]$p.ProcessId) -and $p.CommandLine -and $p.CommandLine -match "--supervise=$mainPid(\D|$)") { $seen[[int]$p.ProcessId] = $true; $found.Add($p) }
  }
  foreach ($p in $found) {
    $cl = [string]$p.CommandLine
    $role = if ([int]$p.ProcessId -eq $mainPid) { 'main' }
      elseif ($cl -match '--type=([\w-]+)') { $m = $Matches[1]; if ($cl -match '--utility-sub-type=([\w.]+)') { "$m $($Matches[1])" } else { $m } }
      elseif ($cl -match '--supervise=') { 'supervisor (outside the tree)' }
      elseif ($cl -match '\s(--[\w-]+)') { $Matches[1] } else { '' }
    [pscustomobject]@{ Pid = [int]$p.ProcessId; Name = [string]$p.Name; Role = $role }
  }
}

# bar up = a visible, uncloaked window of the app, bar-sized, whose rect reaches into the bottom 120 px of the screen
function Get-BarWin([int[]]$pids) {
  @([IdleCmp]::WindowsOf($pids) | Where-Object {
      -not $_.Cloaked -and ($_.R.Bottom - $_.R.Top) -ge 30 -and ($_.R.Bottom - $_.R.Top) -le 200 -and ($_.R.Right - $_.R.Left) -ge 300 -and
      $_.R.Top -lt $H -and $_.R.Bottom -gt ($H - 120) }) | Select-Object -First 1
}
function Describe-Wins([int[]]$pids) {
  (@([IdleCmp]::WindowsOf($pids)) | ForEach-Object { '{0} "{1}" [{2}] {3},{4},{5},{6}{7}' -f $_.Pid, $_.Title, $_.Class, $_.R.Left, $_.R.Top, $_.R.Right, $_.R.Bottom, $(if ($_.Cloaked) { ' cloaked' } else { '' }) }) -join ' | '
}
# the app's PIDs as an int array (empty, never null, when the app is gone)
function Pids([int]$mainPid) { [int[]]@(Get-AppProcs $mainPid | ForEach-Object { $_.Pid }) }
# a line in a log the app still writes (opened shared, as the UI tests read it)
function LogHas([string]$path, [string]$text) {
  try { $f = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite'); try { return ([IO.StreamReader]::new($f)).ReadToEnd().Contains($text) } finally { $f.Dispose() } } catch { return $false }
}
function Until([scriptblock]$cond, [int]$ms) { $end = (Get-Date).AddMilliseconds($ms); while ((Get-Date) -lt $end) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 200 }; return [bool](& $cond) }

$rowsLog = Join-Path $Out 'windows.txt'
Set-Content -Path $rowsLog -Value "idle-compare per-process rows (run $($env:GITHUB_RUN_ID), $($runner.cpu), cycle rate $([math]::Round($perMs,1)) per ms)" -Encoding utf8

function Measure-Window([string]$tag, [int]$mainPid, [string]$state) {
  $procs = @(Get-AppProcs $mainPid)
  if (-not $procs.Count) { Problem "$tag ${state}: the app has no processes left"; return $null }
  $sys0 = [IdleCmp]::SystemTimes()
  $bar0 = Get-BarWin ([int[]]@($procs | ForEach-Object { $_.Pid }))
  $fg = [IdleCmp]::Foreground()
  $w = [IdleCmp]::Begin([int[]]@($procs | ForEach-Object { $_.Pid }), [string[]]@($procs | ForEach-Object { $_.Name }), [string[]]@($procs | ForEach-Object { $_.Role }))
  Start-Sleep -Milliseconds ($WindowSec * 1000)
  $w = [IdleCmp]::End($w)
  $sys1 = [IdleCmp]::SystemTimes()
  $procsEnd = @(Get-AppProcs $mainPid)
  $pidsStart = [int[]]@($procs | ForEach-Object { $_.Pid }); $pidsEnd = [int[]]@($procsEnd | ForEach-Object { $_.Pid })
  $bar1 = Get-BarWin $pidsEnd
  $new = @($pidsEnd | Where-Object { $_ -notin $pidsStart })
  $sysBusy = [math]::Round(100 * ($sys1[1] - $sys0[1]) / (($sys1[0] - $sys0[0]) + ($sys1[1] - $sys0[1])), 2)
  $wantUp = $state -eq 'shown'
  $ok = ([bool]$bar0 -eq $wantUp) -and ([bool]$bar1 -eq $wantUp) -and $w.AliveAtEnd -gt 0
  $r = [ordered]@{
    tag = $tag; state = $state; wallMs = [math]::Round($w.WallMs); procs = $procs.Count; opened = $w.Opened; aliveAtEnd = $w.AliveAtEnd
    spawnedInWindow = $new.Count; cyclePct = $w.CyclePct; chargedPct = $w.ChargedPct
    cycleMs = [math]::Round($w.CycleMs, 2); privateMB = [math]::Round($w.PrivateMB, 1); workingSetMB = [math]::Round($w.WorkingSetMB, 1)
    barUpAtStart = [bool]$bar0; barUpAtEnd = [bool]$bar1; valid = $ok; systemBusyPctAllCores = $sysBusy; foreground = $fg
  }
  $line = "IDLECMP {0} {1}: {2} % of one core by cycles ({3} % charged); {4} processes ({5} opened, {6} new in the window, not counted); private {7} MB, working set {8} MB; bar up at start {9}, at end {10}; whole runner {11} % busy (all cores){12}" -f `
    $tag, $state, $r.cyclePct, $r.chargedPct, $r.procs, $r.opened, $r.spawnedInWindow, $r.privateMB, $r.workingSetMB, $r.barUpAtStart, $r.barUpAtEnd, $sysBusy, $(if ($ok) { '' } else { ' - NOT VALID (bar state or process gone)' })
  Write-Host $line
  Add-Content -Path $rowsLog -Value $line, ("  foreground {0}; windows: {1}" -f $fg, (Describe-Wins $pidsEnd)), ([IdleCmp]::Rows($w)) -Encoding utf8
  if (-not $ok) { Problem "$tag ${state}: not valid (bar up at start $($r.barUpAtStart), at end $($r.barUpAtEnd), $($w.AliveAtEnd) alive)" }
  return [pscustomobject]$r
}

# ------------------------------------------------------------------ one app run
function Run-App([string]$app, [string]$theme, [int]$round) {
  $tag = "$theme r$round $app"
  $prof = "idlecmp-$theme-$round-$app"
  $dir = Join-Path $Out "$theme-r$round-$app"; New-Item -ItemType Directory -Force -Path $dir | Out-Null
  [ParityNative]::ParkCursor($away[0], $away[1])
  $env:MANTLE_TEST_VISIBLE = '1'
  $t0 = Get-Date
  if ($app -eq 'electron') {
    # parity's Electron instance minus the CDP port and the stand-in cursor: the hover poll reads the real cursor, as
    # native's does; --no-desktop because a native --test run without MANTLE_TEST_DESKTOP keeps its desktop layer parked
    $argv = @('.', '--test', '--test-live-bar', "--profile-name=$prof", "--theme=$theme", '--no-desktop')
    $p = Start-Process -FilePath $electronExe -ArgumentList $argv -WorkingDirectory $root -PassThru `
      -RedirectStandardOutput (Join-Path $dir 'electron.out.txt') -RedirectStandardError (Join-Path $dir 'electron.err.txt')
    $quit = @('.', '--quit', '--test', "--profile-name=$prof")
    $quitExe = $electronExe
  } else {
    $ud = Join-Path ([IO.Path]::GetTempPath()) "mantle-native-$prof"; New-Item -ItemType Directory -Force -Path $ud | Out-Null
    $env:MANTLE_TEST_HOVER = '1'   # the hover bar, hidden at rest (as the native idle UI test runs it)
    $argv = @('--test', "--profile-name=$prof", "--user-data=$ud", "--theme=$theme", "--bench-out=$dir")
    $p = Start-Process -FilePath $Exe -ArgumentList $argv -PassThru
    $env:MANTLE_TEST_HOVER = $null
    $quit = @('--test', "--profile-name=$prof", "--user-data=$ud", '--quit')
    $quitExe = $Exe
  }
  $env:MANTLE_TEST_VISIBLE = $null
  Say "$tag started pid $($p.Id): $(Split-Path -Leaf $p.Path) $($argv -join ' ')"
  $res = [ordered]@{ app = $app; theme = $theme; round = $round; pid = $p.Id; windows = @() }
  try {
    $ready = if ($app -eq 'electron') { Until { $p.HasExited -or @([IdleCmp]::WindowsOf((Pids $p.Id))).Count -gt 0 } 90000 }
      else { Until { $p.HasExited -or (LogHas (Join-Path $dir 'native-log.txt') 'bar hidden: at rest') } 60000 }
    if ($p.HasExited -or -not $ready) { Problem "${tag}: not up (exited $($p.HasExited)) in time"; return $res }
    $res.readyS = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
    Start-Sleep -Seconds $SettleSec
    [ParityNative]::ParkCursor($away[0], $away[1])
    Start-Sleep -Seconds 2
    $res.windows += @(Measure-Window $tag $p.Id 'hidden' | Where-Object { $_ })
    # the hover reveal: the cursor on the bottom edge, then resting on the pill (the native idle test's point)
    [ParityNative]::ParkCursor([int]($W / 2), $H - 1)
    $up = Until { $null -ne (Get-BarWin (Pids $p.Id)) } 4000
    $rest = $H - [int][math]::Round(14 * $runner.dpi / 96.0) - 10
    [ParityNative]::ParkCursor([int]($W / 2), $rest)
    if (-not $up) { Problem "${tag}: no reveal within 4 s of the cursor on the bottom edge; windows: $(Describe-Wins (Pids $p.Id))" }
    Start-Sleep -Seconds 3
    $res.windows += @(Measure-Window $tag $p.Id 'shown' | Where-Object { $_ })
    try { Save-Bgra ([ParityNative]::Grab(0, 0, $W, $H)) $W $H (Join-Path $dir 'shown-end.png') } catch { Say "screenshot failed: $($_.Exception.Message)" }
  } finally {
    [ParityNative]::ParkCursor($away[0], $away[1])
    $measured = Pids $p.Id
    # the app's own quit path first; its main PID only if that does not end it (never by name, never the tree)
    $how = 'quit path'
    if (-not $p.HasExited) {
      $q = Start-Process -FilePath $quitExe -ArgumentList $quit -WorkingDirectory $root -PassThru -WindowStyle Hidden
      if (-not $p.WaitForExit(20000)) { $how = 'main PID (the quit path did not end it in 20 s)'; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; [void]$p.WaitForExit(10000) }
      if (-not $q.HasExited) { [void]$q.WaitForExit(5000) }
    } else { $how = 'it had exited by itself' }
    Start-Sleep -Seconds 3
    $left = @($measured | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
    Say ("$tag closed by its {0}; {1} of its {2} processes still running{3}" -f $how, $left.Count, $measured.Count, $(if ($left.Count) { ': ' + ($left -join ',') + ' (left running, not killed)' } else { '' }))
    if ($left.Count) { Problem "${tag}: $($left.Count) process(es) outlived the app's quit: $($left -join ',')" }
    $res.closedBy = $how; $res.leftRunning = $left.Count
    Start-Sleep -Seconds 5
  }
  return $res
}

# ------------------------------------------------------------------ the rounds
$all = [System.Collections.Generic.List[object]]::new()
$t00 = Get-Date
foreach ($theme in ($Themes -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
  Set-WinTheme $theme
  Start-Sleep -Seconds 3
  for ($i = 1; $i -le $Rounds; $i++) {
    foreach ($app in 'electron', 'native') { $all.Add((Run-App $app $theme $i)) }
  }
}
if (-not $fixture.HasExited) { Stop-Process -Id $fixture.Id -Force -ErrorAction SilentlyContinue }

# ------------------------------------------------------------------ the table
function Med([double[]]$v) { $s = @($v | Sort-Object); if (-not $s.Count) { return $null }; $m = [int][math]::Floor($s.Count / 2); if ($s.Count % 2) { $s[$m] } else { ($s[$m - 1] + $s[$m]) / 2 } }
function Col($rows, [string]$name) { [double[]]@($rows | ForEach-Object { $_.$name }) }
function Fmt([double[]]$v, [string]$f) { if (-not $v.Count) { return 'n/a' }; ("{0:$f} ({1:$f} to {2:$f})" -f (Med $v), ($v | Measure-Object -Minimum).Minimum, ($v | Measure-Object -Maximum).Maximum) }
$md = [System.Collections.Generic.List[string]]::new()
$md.Add("# idle-compare: run $($env:GITHUB_RUN_ID) at $($env:GITHUB_SHA)")
$md.Add('')
$md.Add("Runner: $($runner.cpu), $($runner.cores) cores / $($runner.logical) logical, $($runner.os), $($runner.ramGB) GB, $($runner.screen); cycle rate $([math]::Round($perMs / 1e6, 3)) GHz. Electron $electronVer. Windows of $WindowSec s, settle $SettleSec s, $Rounds rounds per theme. Median (min to max) over the valid windows.")
$md.Add('')
$md.Add('| theme | state | app | CPU % of one core (cycles) | CPU % charged | private MB | working set MB | processes | valid windows |')
$md.Add('|---|---|---|---|---|---|---|---|---|')
foreach ($theme in ($Themes -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
  foreach ($state in 'hidden', 'shown') {
    foreach ($app in 'electron', 'native') {
      $ws = @($all | Where-Object { $_.app -eq $app -and $_.theme -eq $theme } | ForEach-Object { $_.windows } | Where-Object { $_.state -eq $state })
      $v = @($ws | Where-Object { $_.valid })
      $md.Add(("| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} of {9} |" -f $theme, $state, $app,
          (Fmt (Col $v 'cyclePct') 'N3'), (Fmt (Col $v 'chargedPct') 'N3'), (Fmt (Col $v 'privateMB') 'N1'),
          (Fmt (Col $v 'workingSetMB') 'N1'), (Fmt (Col $v 'procs') 'N0'), $v.Count, $ws.Count))
    }
  }
}
$md.Add('')
$md.Add('Per window:')
$md.Add('')
$md.Add('| theme | round | app | state | CPU % cycles | CPU % charged | private MB | WS MB | processes | runner busy % (all cores) | valid |')
$md.Add('|---|---|---|---|---|---|---|---|---|---|---|')
foreach ($r in $all) { foreach ($x in $r.windows) { $md.Add(("| {0} | {1} | {2} | {3} | {4:N3} | {5:N3} | {6:N1} | {7:N1} | {8} | {9:N2} | {10} |" -f $r.theme, $r.round, $r.app, $x.state, $x.cyclePct, $x.chargedPct, $x.privateMB, $x.workingSetMB, $x.procs, $x.systemBusyPctAllCores, $x.valid)) } }
if ($problems.Count) { $md.Add(''); $md.Add('Problems:'); $md.Add(''); foreach ($p in $problems) { $md.Add("- $p") } }
$md | Set-Content -Path (Join-Path $Out 'summary.md') -Encoding utf8
[ordered]@{ runner = $runner; windowSec = $WindowSec; settleSec = $SettleSec; rounds = $Rounds; runs = $all; problems = $problems } |
  ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $Out 'results.json') -Encoding utf8
$md | ForEach-Object { Write-Host "SUMMARY $_" }
$nWin = @($all | ForEach-Object { $_.windows }).Count; $nValid = @($all | ForEach-Object { $_.windows } | Where-Object { $_.valid }).Count
$want = 2 * 2 * $Rounds * @($Themes -split ',' | Where-Object { $_.Trim() }).Count
$mins = [math]::Round(((Get-Date) - $t00).TotalMinutes, 1)
if ($nValid -eq $want -and -not $problems.Count) { Write-Host "IDLE-COMPARE: ok ($nValid of $want windows valid; $mins min)" }
else { Write-Host "IDLE-COMPARE: FAILED ($nValid of $want windows valid, $nWin measured; $($problems.Count) problem(s); $mins min)"; exit 1 }
