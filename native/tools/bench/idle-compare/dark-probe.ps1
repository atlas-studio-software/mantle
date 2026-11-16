# dark-probe.ps1 - what wakes the native app in the idle UI test's DARK hidden-at-rest window (a diagnosis, not a gate).
#
#   cloudtest --recipe mantle-native-public --timeout 60 -- "pwsh -NoProfile -File native/tools/bench/idle-compare/dark-probe.ps1"
#
# RUNNER ONLY. 1) ci.ps1 -Stages build,ui (the real suite, as CI runs it), then copies the idle test's own app logs
# (%TEMP%\mantle-frame-cpu-<theme>-*\native-log.txt) out; 2) the idle test alone, -Repeat times, logs copied the same
# way; 3) the test's sequence by hand (light then dark: hover run, "bar hidden: at rest", 3 s, a 15 s window, then a
# 30 s window) with per-thread CPU cycles and the window's wall-clock start and end beside the app log's lines.
# Writes <Out>/ (native/out/bench/dark-probe). Last line: DARK-PROBE: done (...).
param([int]$Repeat = 2, [string]$Out)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
if (-not $Out) { $Out = Join-Path $root 'native\out\bench\dark-probe' }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
if (-not $env:CI -and -not $env:GITHUB_ACTIONS) { Write-Host 'DARK-PROBE: refused (not on a CI runner)'; exit 2 }
Add-Type -Path (Join-Path $PSScriptRoot 'IdleCmp.cs')
Add-Type -Path (Join-Path $root 'native\tools\parity\ParityNative.cs')
[void][ParityNative]::DpiAware()
function Say($m) { Write-Host ("darkprobe {0}: {1}" -f (Get-Date -Format 'HH:mm:ss.fff'), $m) }
$tmp = [IO.Path]::GetTempPath()
function Copy-TestLogs([string]$tag) {
  foreach ($d in @(Get-ChildItem -Path $tmp -Directory -Filter 'mantle-frame-cpu-*' -ErrorAction SilentlyContinue)) {
    $lg = Join-Path $d.FullName 'native-log.txt'
    if (Test-Path $lg) { Copy-Item $lg (Join-Path $Out "$tag-$($d.Name).txt") -Force; Say "$tag log $($d.Name): $((Get-Item $lg).Length) bytes" }
    Remove-Item -Recurse -Force $d.FullName -ErrorAction SilentlyContinue   # the test's own temp folder; the next pass makes new ones
  }
}

# 1) the real suite
& pwsh -NoProfile -File (Join-Path $root 'native\tools\ci.ps1') -Stages 'build,ui' 2>&1 | ForEach-Object { if ("$_" -match '^(STAGE|NATIVE-CI)|IDLE-CPU|Failed ') { Write-Host $_ } }
$idle = Join-Path $root 'native\out\ui\idle-cpu.txt'
if (Test-Path $idle) { Get-Content $idle | ForEach-Object { Say "suite: $_" }; Copy-Item $idle (Join-Path $Out 'suite-idle-cpu.txt') }
Copy-TestLogs 'suite'

# 2) the idle test alone
$exe = Join-Path $root 'native\out\bin\MantleNative\MantleNative.exe'
if (-not (Test-Path $exe)) { Write-Host "DARK-PROBE: FAILED (no native exe at $exe after the build stage)"; exit 3 }
$proj = @(Get-ChildItem -Path (Join-Path $root 'native\tests\Mantle.UiTests') -Filter *.csproj)[0].FullName
$env:MANTLE_NATIVE_EXE = $exe; $env:MANTLE_TEST_VISIBLE = '1'
for ($i = 1; $i -le $Repeat; $i++) {
  $rd = Join-Path $Out "alone-$i"
  & dotnet test $proj -c Release -nologo --filter 'Name=Bar_and_glass_idle_cpu_hidden_at_rest_and_shown' --results-directory $rd 2>&1 | Select-String -Pattern 'IDLE-CPU|Passed!|Failed!|Failed ' | ForEach-Object { Say "alone $i : $($_.Line.Trim())" }
  Copy-TestLogs "alone$i"
}
$env:MANTLE_TEST_VISIBLE = $null

# 3) by hand, per thread
$scr = [ParityNative]::Screen(); $W = $scr[0]; $H = $scr[1]
foreach ($theme in 'light', 'dark') {
  [ParityNative]::ParkCursor([int]($W / 2), [int]($H / 3))
  $dir = Join-Path $Out "hand-$theme"; New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $ud = Join-Path $tmp "mantle-native-darkprobe-$theme"; New-Item -ItemType Directory -Force -Path $ud | Out-Null
  $env:MANTLE_TEST_VISIBLE = '1'; $env:MANTLE_TEST_HOVER = '1'
  $p = Start-Process -FilePath $exe -ArgumentList '--test', "--profile-name=frame-cpu-$theme", "--user-data=$ud", "--theme=$theme", "--bench-out=$dir" -PassThru
  $env:MANTLE_TEST_VISIBLE = $null; $env:MANTLE_TEST_HOVER = $null
  $lg = Join-Path $dir 'native-log.txt'
  $end = (Get-Date).AddSeconds(20)
  while ((Get-Date) -lt $end) {
    $txt = try { $f = [IO.File]::Open($lg, 'Open', 'Read', 'ReadWrite'); try { ([IO.StreamReader]::new($f)).ReadToEnd() } finally { $f.Dispose() } } catch { '' }
    if ($txt.Contains('bar hidden: at rest')) { break }
    Start-Sleep -Milliseconds 100
  }
  Say "$theme pid $($p.Id): hidden at rest seen"
  Start-Sleep -Seconds 3
  foreach ($win in @(@{ n = 'A (the test''s 15 s window)'; s = 15 }, @{ n = 'B (the next 30 s)'; s = 30 })) {
    $t0 = Get-Date -Format 'HH:mm:ss.fff'
    $pw = [IdleCmp]::Begin([int[]]@($p.Id), [string[]]@('MantleNative.exe'), [string[]]@('main'))
    $tw = [IdleCmp]::ThreadsBegin($p.Id)
    Start-Sleep -Seconds $win.s
    $rows = [IdleCmp]::ThreadsEnd($tw, 12)
    $pw = [IdleCmp]::End($pw)
    $t1 = Get-Date -Format 'HH:mm:ss.fff'
    Say ("{0} window {1}: {2} to {3}: {4} % of one core by cycles, {5} % charged" -f $theme, $win.n, $t0, $t1, $pw.CyclePct, $pw.ChargedPct)
    Write-Host $rows
  }
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue   # this probe's own app, by its PID (as the UI test's Dispose)
  [void]$p.WaitForExit(10000)
  Start-Sleep -Seconds 2
  # the app's log from start to the end of window B, minus nothing: it is short
  Get-Content $lg | ForEach-Object { Write-Host "    LOG $theme | $_" }
}
Write-Host "DARK-PROBE: done (logs in $Out)"
