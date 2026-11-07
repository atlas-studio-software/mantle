<#
  native/tools/ci.ps1 - build and check the native (C# + WinUI 3) Mantle. Windows only; for a CI
  runner or cloudtest (it launches the app in the smoke, ui, bench and parity stages).

    pwsh -NoProfile -File native/tools/ci.ps1 [-Stages build,unit,smoke,ui,bench,parity,sidecar] [-Out native/out]
                                              [-BuildMode aot|sc]

  Stages (default: all, in this order):
    build    publish.ps1 -Mode <BuildMode> to <Out>/bin/MantleNative, then dotnet build of native/Mantle.Native.sln.
             BuildMode aot (Native AOT, what ships; the default) or sc (self-contained + ReadyToRun: the
             fallback if AOT breaks something; also env NATIVE_CI_BUILD_MODE=sc). Every later stage tests
             that one exe.
    unit     dotnet test tests/Mantle.Core.Tests (trx in <Out>/test)
    smoke    the published exe on this desktop: ready.json, the log, the UI Automation ids
             (src/Mantle.App/AUTOMATION.md), a live theme.json change, the single-instance hand-off,
             --quit, a quiet (--test without MANTLE_TEST_VISIBLE) window drawing nothing, the
             1 px frame-line measurement, the bar above an app window (z-order), and the five Electron
             data files read from the synthetic profile tests/Mantle.Core.Tests/Fixtures/profile.
             Needs an interactive desktop.
    ui       if tests/Mantle.UiTests/*.csproj exists: dotnet test it (MANTLE_NATIVE_EXE, MANTLE_TEST_VISIBLE=1)
    bench    if bench/run-bench.ps1 exists: -Mode smoke -Out <Out>/bench
    parity   if tools/parity/run-parity.ps1 exists: -Out <Out>/parity
    sidecar  if src/Mantle.Sidecar/*.csproj exists: its check.ps1 (3 linked build variants + its tests), else dotnet build it
  A stage whose project or script does not exist is skipped, not failed.

  The runner desktop between the GUI stages (smoke, ui, bench, parity): tools/desk/desk.ps1 -Reset runs
  before each one (leftover test apps closed, taskbar on auto-hide and out of the way, app windows
  minimized, foreground on the desktop, cursor mid-screen) and prints DESK <stage> before/reset/after
  lines; a watcher (desk.ps1 -Watch, log <Out>/desk-watch.log) logs every taskbar attention request and
  taskbar move, summarized after each GUI stage as "DESK <stage> during: ...". Only on a runner (CI set).

  Prints "STAGE <name>: ok|FAILED|skipped (<reason>)" per stage and ends with exactly one line,
  "NATIVE-CI: ok (...)" or "NATIVE-CI: FAILED (...)". Exit code 0 / 1. Each stage's full output is
  in <Out>/logs/<stage>.log.
#>
param(
  [string[]]$Stages = @('build', 'unit', 'smoke', 'ui', 'bench', 'parity', 'sidecar'),
  [string]$Out = 'native/out',
  [ValidateSet('aot', 'sc', '')] [string]$BuildMode = ''
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$native = Split-Path -Parent $PSScriptRoot
$repo = Split-Path -Parent $native
# "-Stages build,unit" through pwsh -File arrives as one string: split on commas either way
$Stages = @($Stages | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })
$known = @('build', 'unit', 'smoke', 'ui', 'bench', 'parity', 'sidecar')
$bad = @($Stages | Where-Object { $known -notcontains $_ })
if ($bad.Count -gt 0) { Write-Host "NATIVE-CI: FAILED (unknown stage(s): $($bad -join ', '); known: $($known -join ', '))"; exit 1 }
$Stages = @($known | Where-Object { $Stages -contains $_ })   # always in the documented order

if (-not [System.IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $repo $Out }
$Out = [System.IO.Path]::GetFullPath($Out)
$logs = Join-Path $Out 'logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$exe = Join-Path $Out 'bin\MantleNative\MantleNative.exe'
if (-not $BuildMode) { $BuildMode = if ($env:NATIVE_CI_BUILD_MODE -in 'aot', 'sc') { $env:NATIVE_CI_BUILD_MODE } else { 'aot' } }
$env:DOTNET_NOLOGO = '1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$results = [ordered]@{}
function Say([string]$s) { Write-Host $s }
function Short([string]$s) { ($s -replace [regex]::Escape($repo), '<repo>') }

# Run a command, its output to <logs>/<name>.log. On failure, print the error lines and the tail.
function Run([string]$name, [string]$file, [string[]]$argv) {
  $log = Join-Path $logs "$name.log"
  Say ("  > $file " + (Short ($argv -join ' ')))
  Push-Location $native   # native/global.json pins the SDK line
  try { & $file @argv *>> $log; $code = $LASTEXITCODE } finally { Pop-Location }
  if ($code -ne 0) {
    $errs = @(Select-String -Path $log -Pattern ': error |error [A-Z]+[0-9]+:|\[FAIL\]|Failed [A-Za-z]' -ErrorAction SilentlyContinue | Select-Object -First 15 | ForEach-Object { '    ' + (Short $_.Line.Trim()) })
    if ($errs.Count) { $errs | ForEach-Object { Say $_ } } else { Get-Content $log -Tail 25 | ForEach-Object { Say ('    ' + (Short $_)) } }
  }
  return $code
}

function Trx([string]$path) {
  if (-not (Test-Path $path)) { return $null }
  [xml]$x = Get-Content $path -Raw
  $c = $x.TestRun.ResultSummary.Counters
  $failed = @($x.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' } | ForEach-Object { $_.testName })
  return [pscustomobject]@{ Total = [int]$c.total; Passed = [int]$c.passed; Failed = [int]$c.failed; Names = $failed }
}

# ---------------------------------------------------------------------------------------------- build
function Stage-build {
  $bin = Join-Path $Out 'bin\MantleNative'
  $pub = Join-Path $logs "publish-$BuildMode.txt"
  & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'publish.ps1') -Mode $BuildMode -Out $bin *> $pub
  $pubCode = $LASTEXITCODE
  $pubLine = (Get-Content $pub | Where-Object { $_ -like 'PUBLISH *' } | Select-Object -Last 1)
  Say ("  " + (Short $pubLine))
  if (Test-Path "$bin.publish.log") { Move-Item -Force "$bin.publish.log" (Join-Path $logs "publish-$BuildMode.log") }
  $code = Run 'build' 'dotnet' @('build', (Join-Path $native 'Mantle.Native.sln'), '-c', 'Release', '-p:Platform=x64', '-nologo', '-clp:Summary;ErrorsOnly')
  $warn = @(Select-String -Path (Join-Path $logs 'build.log') -Pattern 'warning [A-Z]+[0-9]+' -ErrorAction SilentlyContinue).Count
  if ($pubCode -ne 0) { return "FAILED (publish ${BuildMode}: $(Short $pubLine))" }
  if ($code -ne 0) { return "FAILED (dotnet build of Mantle.Native.sln, exit $code)" }
  return "ok ($BuildMode`: published $(($pubLine -replace '^PUBLISH \w+: ok \(', '' -replace '\)$', '')); solution built, $warn warning line(s))"
}

# ---------------------------------------------------------------------------------------------- unit
function Stage-unit {
  $dir = Join-Path $Out 'test'
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $trx = Join-Path $dir 'core.trx'
  if (Test-Path $trx) { Remove-Item $trx }
  $code = Run 'unit' 'dotnet' @('test', (Join-Path $native 'tests\Mantle.Core.Tests\Mantle.Core.Tests.csproj'), '-c', 'Release', '-nologo',
    '--logger', 'trx;LogFileName=core.trx', '--results-directory', $dir)
  $t = Trx $trx
  if ($null -eq $t) { return "FAILED (no test results; dotnet test exit $code)" }
  if ($t.Failed -gt 0 -or $code -ne 0) { return "FAILED ($($t.Failed) of $($t.Total) failed: $(($t.Names | Select-Object -First 6) -join ', '))" }
  return "ok ($($t.Passed) of $($t.Total) passed)"
}

# ---------------------------------------------------------------------------------------------- smoke
$probe = @'
param([string]$Mode, [int]$TargetPid, [string]$Rect, [string]$Png, [string]$A, [string]$B, [int]$Row = -1)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -Namespace Probe -Name U32 -MemberDefinition '[DllImport("user32.dll")] public static extern bool MoveWindow(System.IntPtr h, int x, int y, int w, int hh, bool r);'
if ($Mode -eq 'place') {
  $p = Get-Process -Id $TargetPid; $h = $p.MainWindowHandle
  for ($i = 0; $i -lt 40 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 250; $p.Refresh(); $h = $p.MainWindowHandle }
  if ($h -ne [IntPtr]::Zero) { [Probe.U32]::MoveWindow($h, 20, 20, 420, 300, $true) | Out-Null; 'placed' } else { 'no window' }
  exit
}
if ($Mode -eq 'grab') {
  $r = $Rect -split ',' | ForEach-Object { [int]$_ }
  $bmp = New-Object System.Drawing.Bitmap $r[2], $r[3]
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($r[0], $r[1], 0, 0, (New-Object System.Drawing.Size $r[2], $r[3]))
  $g.Dispose(); $bmp.Save($Png, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); 'ok'
  exit
}
if ($Mode -eq 'diff') {
  # mean |a-b| over RGB: of one row (Row >= 0) or of every 3rd pixel of the whole picture
  # (not $a / $b: PowerShell names are case-blind, and the [string] params $A / $B would turn a bitmap into text)
  $ia = New-Object System.Drawing.Bitmap $A; $ib = New-Object System.Drawing.Bitmap $B
  $sum = 0.0; $n = 0
  $ys = if ($Row -ge 0) { @($Row) } else { 0..($ia.Height - 1) | Where-Object { $_ % 3 -eq 0 } }
  $step = if ($Row -ge 0) { 1 } else { 3 }
  foreach ($y in $ys) { for ($x = 0; $x -lt $ia.Width; $x += $step) {
    $p = $ia.GetPixel($x, $y); $q = $ib.GetPixel($x, $y)
    $sum += ([math]::Abs([int]$p.R - $q.R) + [math]::Abs([int]$p.G - $q.G) + [math]::Abs([int]$p.B - $q.B)) / 3.0; $n++ } }
  $ia.Dispose(); $ib.Dispose()
  [math]::Round($sum / [math]::Max($n, 1), 2)
  exit
}
if ($Mode -eq 'uia') {
  $AE = [System.Windows.Automation.AutomationElement]
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $TargetPid)
  $win = $null
  for ($i = 0; $i -lt 20 -and $null -eq $win; $i++) { $win = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond); if ($null -eq $win) { Start-Sleep -Milliseconds 250 } }
  if ($null -eq $win) { '{"error":"no top-level UI Automation element for the process"}'; exit }
  $items = @()
  foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
    $id = $e.Current.AutomationId
    if ($id -like 'bar.*') {
      $r = $e.Current.BoundingRectangle
      $items += [pscustomobject]@{ id = $id; name = $e.Current.Name; help = $e.Current.HelpText; type = $e.Current.ControlType.ProgrammaticName -replace '^ControlType\.', ''
        x = [int]$r.X; y = [int]$r.Y; w = [int]$r.Width; h = [int]$r.Height }
    }
  }
  [pscustomobject]@{ title = $win.Current.Name; items = $items } | ConvertTo-Json -Depth 4 -Compress
  exit
}
'unknown mode'
'@

function Wait-For([scriptblock]$cond, [int]$seconds) {
  $until = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $until) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 250 }
  return [bool](& $cond)
}

function Stage-smoke {
  if (-not (Test-Path $exe)) { return 'FAILED (the published exe is missing; the build stage makes it)' }
  if (-not ('Desk' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'desk\Desk.cs') }
  $dir = Join-Path $Out 'smoke'
  if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $probeFile = Join-Path $dir 'probe.ps1'
  Set-Content -Path $probeFile -Value $probe -Encoding utf8
  function P([string[]]$a) { (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $probeFile @a) -join "`n" }

  $fails = New-Object System.Collections.Generic.List[string]
  $notes = New-Object System.Collections.Generic.List[string]
  $started = New-Object System.Collections.Generic.List[System.Diagnostics.Process]
  $ud = Join-Path $dir 'profile'; $bench = Join-Path $dir 'bench'
  New-Item -ItemType Directory -Force -Path $ud, $bench | Out-Null
  # Electron's data files, synthetic (the unit tests' fixtures): the published exe reads all five at start.
  $fixtures = Join-Path $native 'tests\Mantle.Core.Tests\Fixtures\profile'
  $dataFiles = @('theme.json', 'look-sync.json', 'look.json', 'features.json', 'features\jumplist.json')
  if (Test-Path $fixtures) { Copy-Item -Recurse -Force (Join-Path $fixtures '*') $ud }
  Set-Content -Path (Join-Path $ud 'theme.json') -Value '{ "mode": "dark" }' -Encoding utf8NoBOM
  $nativeLog = Join-Path $ud 'mantle-native.log'
  $common = @('--test', '--profile-name=ci-smoke', "--user-data=`"$ud`"")
  $oldVisible = $env:MANTLE_TEST_VISIBLE
  try {
    # an app for the dock, out of the bar's way
    $np = $null
    try { $np = Start-Process notepad.exe -PassThru; $started.Add($np); $notes.Add('notepad ' + (P @('-Mode', 'place', '-TargetPid', $np.Id))) } catch { $notes.Add("notepad could not start ($($_.Exception.Message))") }
    Start-Sleep -Seconds 1

    Add-Type -AssemblyName System.Windows.Forms
    $scr = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $notes.Add("screen $($scr.Width)x$($scr.Height)")

    # ---- the visible test instance
    $env:MANTLE_TEST_VISIBLE = '1'
    $bandY = [math]::Max(0, $scr.Height - 200); $band = "0,$bandY,$($scr.Width),$($scr.Height - $bandY)"
    P @('-Mode', 'grab', '-Rect', $band, '-Png', (Join-Path $dir 'band-before.png')) | Out-Null
    $p = Start-Process -FilePath $exe -ArgumentList ($common + "--bench-out=`"$bench`"") -PassThru
    $started.Add($p)
    $readyFile = Join-Path $bench 'ready.json'
    if (-not (Wait-For { Test-Path $readyFile } 30)) {
      throw ('no ready.json within 30 s (exited: ' + $p.HasExited + $(if ($p.HasExited) { ", code $($p.ExitCode)" }) + ')')
    }
    Start-Sleep -Milliseconds 1500
    $ready = Get-Content $readyFile -Raw | ConvertFrom-Json
    $ff = $ready.firstFrameUnixMs - $ready.processStartUnixMs
    $notes.Add("first frame ${ff} ms after process start; theme $($ready.theme); $($ready.backdrop); frame: $($ready.frame)")
    if ($ready.theme -ne 'dark') { $fails.Add("theme.json said dark, the bar showed $($ready.theme)") }
    if ($ready.title -ne 'Mantle bar') { $fails.Add("window title is '$($ready.title)', not 'Mantle bar'") }
    if ($ready.quiet) { $fails.Add('MANTLE_TEST_VISIBLE=1 but the window was made quiet') }
    P @('-Mode', 'grab', '-Rect', $band, '-Png', (Join-Path $dir 'band-after.png')) | Out-Null

    # the 1 px frame line: the window's top row against the same row before launch (measured, not judged)
    $top = $ready.window.y - $bandY
    if ($top -ge 1 -and $top -lt ($scr.Height - $bandY - 1)) {
      $d0 = P @('-Mode', 'diff', '-A', (Join-Path $dir 'band-before.png'), '-B', (Join-Path $dir 'band-after.png'), '-Row', ($top - 1))
      $d1 = P @('-Mode', 'diff', '-A', (Join-Path $dir 'band-before.png'), '-B', (Join-Path $dir 'band-after.png'), '-Row', $top)
      $d2 = P @('-Mode', 'diff', '-A', (Join-Path $dir 'band-before.png'), '-B', (Join-Path $dir 'band-after.png'), '-Row', ($top + 1))
      $notes.Add("frame line: mean RGB change on the window's top row $d1 (row above $d0, row inside $d2; 0 = untouched)")
      if (($d1 -as [double]) -gt 8) { $fails.Add("frame line: the window's top row changed by $d1 (the area above the pill must be see-through)") }
    } else { $notes.Add('frame line: the top row is outside the captured band, not measured') }

    # the log
    if (-not (Test-Path $nativeLog)) { $fails.Add('no mantle-native.log in the profile') }
    else {
      $txt = Get-Content $nativeLog -Raw
      foreach ($want in @('===== mantle-native start', 'mark main', 'mark first-frame', 'data: theme.json: ok', 'single instance: claimed')) {
        if ($txt -notlike "*$want*") { $fails.Add("the log has no '$want'") }
      }
      if (Test-Path $fixtures) {
        $read = @(Get-Content $nativeLog | Where-Object { $_ -match ' data: (theme\.json|look-sync\.json|look\.json|features\.json|features\\jumplist\.json): ' })
        $notes.Add('data files: ' + (($read | ForEach-Object { ($_ -replace '^.*? data: ', '') -replace '^(.{90}).+$', '$1...' }) -join ' | '))
        foreach ($f in $dataFiles) {
          if (-not ($read | Where-Object { $_ -like "* data: ${f}: ok*" })) { $fails.Add("data file $f was not read as ok from the fixture profile ($(@($read | Where-Object { $_ -like "* data: ${f}:*" })[0]))") }
        }
      } else { $fails.Add('the fixture profile tests/Mantle.Core.Tests/Fixtures/profile is missing') }
      $unstamped = @(Get-Content $nativeLog | Where-Object { $_ -ne '' -and $_ -notmatch '^(\d{4}-\d{2}-\d{2} )?\d{2}:\d{2}:\d{2}\.\d{3} ' })
      if ($unstamped.Count) { $fails.Add("$($unstamped.Count) log line(s) without the time stamp") }
    }

    # UI Automation contract (AUTOMATION.md)
    $uiaText = P @('-Mode', 'uia', '-TargetPid', $p.Id)
    Set-Content -Path (Join-Path $dir 'uia.json') -Value $uiaText -Encoding utf8
    try { $uia = $uiaText | ConvertFrom-Json } catch { $uia = $null }
    if ($null -eq $uia -or $uia.error) { $fails.Add('UI Automation: ' + $(if ($uia) { $uia.error } else { 'probe output unreadable: ' + $uiaText.Substring(0, [math]::Min(200, $uiaText.Length)) })) }
    else {
      if ($uia.title -ne 'Mantle bar') { $fails.Add("UI Automation: the window's Name is '$($uia.title)'") }
      $ids = @($uia.items | ForEach-Object { $_.id })
      $missing = @('bar.window', 'bar.pill', 'bar.start', 'bar.apps', 'bar.clock', 'bar.clock.time', 'bar.clock.date', 'bar.downloads', 'bar.volume', 'bar.network', 'bar.notifications', 'bar.more' | Where-Object { $ids -notcontains $_ })
      if ($missing.Count) { $fails.Add('UI Automation ids missing: ' + ($missing -join ', ')) }
      $pill = @($uia.items | Where-Object { $_.id -eq 'bar.pill' })[0]
      if ($pill) {
        $notes.Add("UI Automation: bar.pill at $($pill.x),$($pill.y) $($pill.w)x$($pill.h); ready.json pill $($ready.pill.x),$($ready.pill.y) $($ready.pill.w)x$($ready.pill.h)")
        if ([math]::Abs($pill.x - $ready.pill.x) -gt 1 -or [math]::Abs($pill.y - $ready.pill.y) -gt 1) { $fails.Add("UI Automation: bar.pill is at $($pill.x),$($pill.y), the code placed it at $($ready.pill.x),$($ready.pill.y) (a window frame insets the content)") }
      }
      $apps = @($uia.items | Where-Object { $_.id -like 'bar.app.*' })
      $notes.Add("UI Automation: $($ids.Count) bar.* elements; apps: " + (($apps | ForEach-Object { "$($_.id)='$($_.name)' ($($_.help)) at $($_.x),$($_.y) $($_.w)x$($_.h)" }) -join '; '))
      if ($np -and -not $np.HasExited -and $apps.Count -eq 0) { $fails.Add('UI Automation: notepad is running but there is no bar.app.* element') }
      foreach ($a in $apps) {
        if ($a.w -le 0 -or $a.h -le 0 -or $a.x -lt $ready.window.x -or $a.y -lt $ready.window.y) { $fails.Add("UI Automation: $($a.id) bounds $($a.x),$($a.y) $($a.w)x$($a.h) are not on the bar") }
      }
      for ($i = 0; $i -lt $apps.Count; $i++) { if (-not ($apps | Where-Object { $_.id -eq "bar.app.$i" })) { $fails.Add("UI Automation: bar.app.$i missing (the ids are not 0..n-1)") } }
    }

    # z-order: the bar is above an ordinary app window (notepad, started before it)
    $barH = [Desk]::WindowOf($p.Id, 'Mantle bar')
    $npH = if ($np -and -not $np.HasExited) { [Desk]::WindowOf($np.Id, '') } else { [IntPtr]::Zero }
    if ($barH -eq [IntPtr]::Zero) { $fails.Add('z-order: no visible top-level window titled Mantle bar') }
    elseif ($npH -eq [IntPtr]::Zero) { $notes.Add('z-order: notepad has no window; not checked') }
    else {
      $above = [Desk]::IsAbove($barH, $npH)
      $notes.Add("z-order: bar above notepad $above (bar topmost $([Desk]::IsTopmost($barH)))")
      if (-not $above) { $fails.Add('z-order: the bar is below notepad') }
    }

    # Electron's theme.json changes live (tmp + rename, as lib/theme.js writes it)
    $tj = Join-Path $ud 'theme.json'
    Set-Content -Path "$tj.tmp" -Value '{ "mode": "light" }' -Encoding utf8NoBOM
    Move-Item -Force "$tj.tmp" $tj
    if (-not (Wait-For { (Get-Content $nativeLog -Raw) -like '*the bar now shows light*' } 10)) { $fails.Add('theme.json changed to light, the bar did not follow within 10 s') }

    # a second launch on the same profile hands off and exits
    $second = Start-Process -FilePath $exe -ArgumentList ($common + '--theme=dark') -PassThru
    $started.Add($second)
    if (-not $second.WaitForExit(15000)) { $fails.Add('a second launch on the same profile was still running after 15 s') }
    elseif ($second.ExitCode -ne 0) { $fails.Add("the second launch exited with $($second.ExitCode)") }
    if (-not (Wait-For { (Get-Content $nativeLog -Raw) -like '*a second launch handed off*' } 5)) { $fails.Add('the running instance never logged the hand-off') }
    if ($p.HasExited) { $fails.Add('the first instance exited when the second launched') }

    # --quit through the hand-off
    $quit = Start-Process -FilePath $exe -ArgumentList ($common + '--quit') -PassThru
    $started.Add($quit)
    $quit.WaitForExit(15000) | Out-Null
    if (-not $p.WaitForExit(10000)) { $fails.Add('--quit did not close the running instance within 10 s') }
    else {
      $qc = $p.ExitCode
      $notes.Add("--quit: closed (exit $qc$(if ($qc -ne 0) { ' = 0x{0:X8}: the app crashed on the way out (App.OnSecondLaunch: Close then Exit), not a CI problem' -f $qc }))")
    }

    # a quiet test window draws nothing (no MANTLE_TEST_VISIBLE)
    $env:MANTLE_TEST_VISIBLE = $null
    $ud2 = Join-Path $dir 'profile-quiet'; $bench2 = Join-Path $dir 'bench-quiet'
    New-Item -ItemType Directory -Force -Path $ud2, $bench2 | Out-Null
    P @('-Mode', 'grab', '-Rect', $band, '-Png', (Join-Path $dir 'quiet-before.png')) | Out-Null
    $q = Start-Process -FilePath $exe -ArgumentList @('--test', '--profile-name=ci-quiet', "--user-data=`"$ud2`"", "--bench-out=`"$bench2`"") -PassThru
    $started.Add($q)
    if (-not (Wait-For { Test-Path (Join-Path $bench2 'ready.json') } 30)) { $fails.Add('quiet: no ready.json within 30 s') }
    else {
      Start-Sleep -Milliseconds 1500
      $r2 = Get-Content (Join-Path $bench2 'ready.json') -Raw | ConvertFrom-Json
      if (-not $r2.quiet) { $fails.Add('quiet: a --test run without MANTLE_TEST_VISIBLE was not made quiet') }
      P @('-Mode', 'grab', '-Rect', $band, '-Png', (Join-Path $dir 'quiet-after.png')) | Out-Null
      $dq = P @('-Mode', 'diff', '-A', (Join-Path $dir 'quiet-before.png'), '-B', (Join-Path $dir 'quiet-after.png'))
      $dv = P @('-Mode', 'diff', '-A', (Join-Path $dir 'band-before.png'), '-B', (Join-Path $dir 'band-after.png'))
      $notes.Add("quiet: mean RGB change over the bar's band $dq (the visible bar changed it by $dv)")
      $dqn = 0.0
      if (-not [double]::TryParse(($dq -as [string]).Trim(), [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$dqn)) { $fails.Add("quiet: the pixel comparison gave no number ('$dq')") }
      elseif ($dqn -gt 1.0) { $fails.Add("quiet: the quiet window changed the screen (mean $dq)") }
    }
    & $exe '--test' '--profile-name=ci-quiet' "--user-data=$ud2" '--quit' | Out-Null
    if (-not $q.WaitForExit(10000)) { $fails.Add('quiet: --quit did not close it') }
  } finally {
    $env:MANTLE_TEST_VISIBLE = $oldVisible
    foreach ($x in $started) { try { if (-not $x.HasExited) { Stop-Process -Id $x.Id -Force -ErrorAction SilentlyContinue } } catch { } }
    $notes | ForEach-Object { Say ('    ' + (Short $_)) }
    $notes | Set-Content -Path (Join-Path $dir 'notes.txt') -Encoding utf8
    foreach ($f in @('profile\mantle-native.log', 'profile-quiet\mantle-native.log')) { $src = Join-Path $dir $f; if (Test-Path $src) { Copy-Item $src (Join-Path $logs ('smoke-' + ($f -replace '\\', '-'))) } }
  }
  if ($fails.Count) { $fails | ForEach-Object { Say ('    FAIL ' + $_) }; return "FAILED ($($fails.Count) check(s): $($fails[0]))" }
  return "ok ($BuildMode exe: ready, log, data files, UI Automation ids, z-order, live theme, hand-off, --quit, quiet window)"
}

# ---------------------------------------------------------------------------------------------- the optional stages
function Stage-ui {
  $proj = @(Get-ChildItem -Path (Join-Path $native 'tests\Mantle.UiTests') -Filter *.csproj -ErrorAction SilentlyContinue)
  if ($proj.Count -eq 0) { return 'skipped (no tests/Mantle.UiTests/*.csproj)' }
  if (-not (Test-Path $exe)) { return 'FAILED (the published exe is missing; the build stage makes it)' }
  $dir = Join-Path $Out 'ui'
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $env:MANTLE_NATIVE_EXE = $exe; $env:MANTLE_TEST_VISIBLE = '1'
  try {
    $code = Run 'ui' 'dotnet' @('test', $proj[0].FullName, '-c', 'Release', '-nologo', '--logger', 'trx;LogFileName=ui.trx', '--results-directory', $dir)
  } finally { $env:MANTLE_TEST_VISIBLE = $null }
  $t = Trx (Join-Path $dir 'ui.trx')
  if ($null -eq $t) { return "FAILED (no test results; dotnet test exit $code)" }
  if ($t.Failed -gt 0 -or $code -ne 0) { return "FAILED ($($t.Failed) of $($t.Total) failed: $(($t.Names | Select-Object -First 6) -join ', '))" }
  return "ok ($($t.Passed) of $($t.Total) passed)"
}

function Stage-script([string]$name, [string]$script, [string[]]$argv) {
  if (-not (Test-Path $script)) { return "skipped (no $(Short $script))" }
  $env:MANTLE_NATIVE_EXE = $exe
  $code = Run $name 'pwsh' (@('-NoProfile', '-File', $script) + $argv)
  $last = Get-Content (Join-Path $logs "$name.log") -Tail 1
  if ($code -ne 0) { return "FAILED (exit $code; $(Short $last))" }
  return "ok ($(Short $last))"
}

function Stage-sidecar {
  $proj = @(Get-ChildItem -Path (Join-Path $native 'src\Mantle.Sidecar') -Filter *.csproj -ErrorAction SilentlyContinue)
  if ($proj.Count -eq 0) { return 'skipped (no src/Mantle.Sidecar/*.csproj)' }
  # The sidecar lane's own check: the 3 build variants of the linked sidecar + its in-process tests,
  # ending with one SIDECAR-LINK line (output in <Out>/sidecar). Plain build if the script is gone.
  $check = Join-Path $native 'src\Mantle.Sidecar\check.ps1'
  if (Test-Path $check) {
    $code = Run 'sidecar' 'pwsh' @('-NoProfile', '-File', $check)
    $last = Get-Content (Join-Path $logs 'sidecar.log') -Tail 1
    if ($code -ne 0) { return "FAILED (exit $code; $(Short $last))" }
    return "ok ($(Short $last))"
  }
  $code = Run 'sidecar' 'dotnet' @('build', $proj[0].FullName, '-c', 'Release', '-nologo', '-clp:Summary;ErrorsOnly')
  if ($code -ne 0) { return "FAILED (dotnet build of $($proj[0].Name), exit $code)" }
  return "ok ($($proj[0].Name) built)"
}

# ---------------------------------------------------------------------------------------------- the runner desktop
# Before each GUI stage the desktop is put back (desk.ps1 -Reset); a watcher logs taskbar attention requests
# and taskbar moves for the whole run, summarized per stage. Runner only (desk.ps1 refuses elsewhere).
$guiStages = @('smoke', 'ui', 'bench', 'parity')
$deskPs = Join-Path $PSScriptRoot 'desk\desk.ps1'
$onRunner = [bool]($env:CI -or $env:GITHUB_ACTIONS)
$watchLog = Join-Path $Out 'desk-watch.log'
$watcher = $null
function Desk-Reset([string]$tag) {
  if (-not $onRunner -or -not (Test-Path $deskPs)) { return }
  $lines = @(& pwsh -NoProfile -File $deskPs -Reset -Tag $tag 2>&1 | ForEach-Object { "$_" })
  $lines | ForEach-Object { Say ('  ' + (Short $_)) }
}
function Desk-During([string]$tag, [int]$from) {
  if (-not $watcher -or -not (Test-Path $watchLog)) { return }
  $new = @(Get-Content $watchLog | Select-Object -Skip $from)
  $flash = @($new | Where-Object { $_ -match ' FLASH ' })
  $shown = @($new | Where-Object { $_ -match ' taskbar shown ' })
  $last = @($new | Where-Object { $_ -match ' taskbar (shown|hidden)' } | Select-Object -Last 1)
  $who = @($flash | ForEach-Object { ($_ -replace '^\S+ FLASH ', '') -replace " \d+ '", " '" } | Group-Object | ForEach-Object { "$($_.Name) x$($_.Count)" })
  Say ("  DESK $tag during: taskbar shown $($shown.Count)x" + $(if ($last) { ", ends $((($last[0] -split ' ')[2]).TrimEnd(';'))" } else { '' }) +
    "; $($flash.Count) attention request(s)" + $(if ($who.Count) { ': ' + ($who -join '; ') } else { '' }))
  foreach ($l in @($new | Where-Object { $_ -match ' (FLASH|taskbar shown) ' } | Select-Object -First 6)) { Say ('    ' + (Short $l)) }
}

# ---------------------------------------------------------------------------------------------- run
Say "native-ci: stages $($Stages -join ', '); build mode $BuildMode; out $(Short $Out)"
if ($onRunner -and (Test-Path $deskPs) -and @($Stages | Where-Object { $guiStages -contains $_ }).Count) {
  Remove-Item -Force -ErrorAction SilentlyContinue $watchLog
  $watcher = Start-Process pwsh -ArgumentList '-NoProfile', '-File', $deskPs, '-Watch', '-Log', $watchLog -PassThru -WindowStyle Hidden
}
$dotnetVer = (& dotnet --version 2>$null)
Say "native-ci: dotnet SDK (from native/) $(Push-Location $native; & dotnet --version 2>$null; Pop-Location) ; default $dotnetVer"
foreach ($s in $Stages) {
  $t0 = Get-Date
  Say "STAGE ${s}: running"
  $gui = $guiStages -contains $s
  if ($gui) { Desk-Reset $s }
  $watchFrom = if ($watcher -and (Test-Path $watchLog)) { @(Get-Content $watchLog).Count } else { 0 }
  try {
    $r = switch ($s) {
      'build'   { Stage-build }
      'unit'    { Stage-unit }
      'smoke'   { Stage-smoke }
      'ui'      { Stage-ui }
      'bench'   { Stage-script 'bench' (Join-Path $native 'bench\run-bench.ps1') @('-Mode', 'smoke', '-Out', (Join-Path $Out 'bench')) }
      'parity'  { Stage-script 'parity' (Join-Path $native 'tools\parity\run-parity.ps1') @('-Out', (Join-Path $Out 'parity')) }
      'sidecar' { Stage-sidecar }
    }
    $r = @($r)[-1]   # a stage's own stray output never becomes its result
  } catch {
    $r = "FAILED (the stage threw: $(Short $_.Exception.Message))"
  }
  if ($gui) { Desk-During $s $watchFrom }
  $secs = [math]::Round(((Get-Date) - $t0).TotalSeconds)
  $results[$s] = $r
  Say "STAGE ${s}: $r [${secs}s]"
}
if ($watcher -and -not $watcher.HasExited) { Stop-Process -Id $watcher.Id -Force -ErrorAction SilentlyContinue }
$failed = @($results.Keys | Where-Object { $results[$_] -like 'FAILED*' })
$okN = @($results.Keys | Where-Object { $results[$_] -like 'ok*' }).Count
$skipN = @($results.Keys | Where-Object { $results[$_] -like 'skipped*' }).Count
if ($failed.Count) { Say "NATIVE-CI: FAILED ($($failed -join ', ') failed; $okN ok, $skipN skipped)"; exit 1 }
Say "NATIVE-CI: ok ($okN ok, $skipN skipped: $((@($results.Keys | Where-Object { $results[$_] -like 'skipped*' })) -join ', '))"
exit 0
