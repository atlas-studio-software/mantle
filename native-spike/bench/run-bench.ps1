# run-bench.ps1 - build the native bar spike and measure it against the real Electron Mantle bar,
# on the same GitHub Windows runner, in the same job. RUNNER ONLY: it runs Mantle LIVE (which
# changes the runner's shell), moves the cursor, clicks, and changes the wallpaper and theme.
#
#   pwsh -NoProfile -File native-spike/bench/run-bench.ps1 [-Mode full|smoke] [-Out <dir>]
#
# full : build; then for light, then dark: Electron Mantle live, then the native bar.
# smoke: build; the native bar only, light and dark (no npm/Electron needed).
# Per run: time to first paint (the app's own mark + pixels on screen), RAM summed over the whole
# process tree (private bytes, working set, private working set) after 45 s, CPU over 60 s with the
# bar up, screenshots. Electron also gets a 60 s CPU sample with its bar hidden (its default at rest)
# and a per-process breakdown from inside its main process (electron-probe.js).
# Summary: results.json and summary.md in -Out; the last line says BENCH: ok or BENCH: FAILED.
param(
  [ValidateSet('full', 'smoke')] [string]$Mode = 'full',
  [string]$Out
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$ws = (Get-Location).Path
$spike = Join-Path $ws 'native-spike'
if (-not $Out) { $Out = Join-Path $spike 'out' }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$problems = [System.Collections.Generic.List[string]]::new()
function Say($m) { Write-Host ("bench {0}: {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) }
function Problem($m) { $problems.Add($m); Write-Host "bench PROBLEM: $m" }
function NowMs { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }

Add-Type -Path (Join-Path $spike 'bench\BenchNative.cs')
Add-Type -AssemblyName System.Drawing

# ------------------------------------------------------------------ the runner
$os = Get-CimInstance Win32_OperatingSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
Add-Type -AssemblyName System.Windows.Forms
$scr = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$runner = [ordered]@{
  os = "$($os.Caption) $($os.Version)"; cpu = $cpu.Name; cores = [Environment]::ProcessorCount
  ramGB = [Math]::Round($os.TotalVisibleMemorySize / 1MB, 1); screen = "$($scr.Width)x$($scr.Height)"
}
Say ("runner: " + ($runner.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value)" }) -join '; ')

# The same picture behind both bars: Mantle's default wallpaper.
$wall = Join-Path $ws 'assets\backgrounds\snow-peak.jpg'
Say ("wallpaper: " + [BenchNative]::SetWallpaper($wall))
# Transparency effects on (a server image may ship with them off).
$pers = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
New-Item -Force -Path $pers | Out-Null
Set-ItemProperty -Path $pers -Name EnableTransparency -Value 1 -Type DWord
function Set-WinTheme([string]$t) {
  $v = if ($t -eq 'light') { 1 } else { 0 }
  Set-ItemProperty -Path $pers -Name AppsUseLightTheme -Value $v -Type DWord
  Set-ItemProperty -Path $pers -Name SystemUsesLightTheme -Value $v -Type DWord
}

# ------------------------------------------------------------------ build the native bar
$sdks = @(& dotnet --list-sdks 2>$null)
Say ("dotnet SDKs: " + ($sdks -join ' | '))
$tfm = if ($sdks -match '^10\.') { 'net10.0' } elseif ($sdks -match '^9\.') { 'net9.0' } else { 'net8.0' }
$bin = Join-Path $spike 'bin\MantleBar'
$proj = Join-Path $spike 'MantleBar\MantleBar.csproj'
$t0 = Get-Date
& dotnet publish $proj -c Release -r win-x64 --self-contained true "-p:SpikeTfm=$tfm" -p:PublishReadyToRun=true -o $bin -nologo -clp:NoSummary 2>&1 |
  Tee-Object -FilePath (Join-Path $Out 'build.log') | Select-Object -Last 25 | ForEach-Object { Write-Host "  $_" }
$buildOk = $LASTEXITCODE -eq 0
$exe = Join-Path $bin 'MantleBar.exe'
$buildS = [Math]::Round(((Get-Date) - $t0).TotalSeconds)
if (-not $buildOk -or -not (Test-Path $exe)) {
  Problem "dotnet publish failed (exit $LASTEXITCODE, $tfm, ${buildS}s): see build.log"
  Write-Host "BENCH: FAILED ($($problems -join '; '))"
  exit 1
}
$binMB = [Math]::Round(((Get-ChildItem -Recurse -File $bin | Measure-Object Length -Sum).Sum) / 1MB, 1)
Say "built $tfm in ${buildS}s: $exe ($binMB MB folder, self-contained .NET + Windows App SDK, ReadyToRun)"

# ------------------------------------------------------------------ fixtures: the same running apps for both bars
[BenchNative]::TaskbarAutoHide($true)
$fixtures = @()
$fixtures += Start-Process notepad.exe -PassThru
$fixtures += Start-Process charmap.exe -PassThru
$fixtures += Start-Process cmd.exe -ArgumentList '/k', 'title Console' -PassThru
Start-Process explorer.exe -ArgumentList 'C:\Windows' | Out-Null
Start-Sleep -Seconds 4
Say ("fixtures: " + (($fixtures | ForEach-Object { "$($_.ProcessName) $($_.Id)" }) -join ', ') + ', explorer folder window')
$notepadPid = ($fixtures | Where-Object ProcessName -eq 'notepad').Id
$charmapPid = ($fixtures | Where-Object ProcessName -eq 'charmap').Id

# The pill's rectangle (main.js barGeometry: min(1120, screen - 28) wide, 48 tall, 14 above the bottom), 100 % scale on the runner.
$pillW = [Math]::Min(1120, $scr.Width - 28)
$pill = @{ x = [int](($scr.Width - $pillW) / 2); y = $scr.Height - 14 - 48; w = $pillW; h = 48 }
$shot = @{ x = $pill.x - 40; y = $pill.y - 60; w = $pill.w + 80; h = $scr.Height - ($pill.y - 60) }
$park = @{ x = [int]($scr.Width / 2); y = $scr.Height - 1 }        # the bottom edge: Mantle's hover band reveals the bar
$away = @{ x = [int]($scr.Width / 2); y = [int]($scr.Height / 3) }

function Save-Shot([string]$name, $r) {
  $bmp = [System.Drawing.Bitmap]::new($r.w, $r.h)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($r.x, $r.y, 0, 0, [System.Drawing.Size]::new($r.w, $r.h))
  $p = Join-Path $Out $name
  $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
  return $p
}

# Time from launch until the pill's pixels change on screen (and until they stop changing).
function Watch-Pixels([long]$launchMs, [byte[]]$base, [int]$timeoutS, $proc) {
  $visible = $null; $settled = $null; $prev = $null; $same = 0
  $end = (Get-Date).AddSeconds($timeoutS)
  while ((Get-Date) -lt $end) {
    if ($proc -and $proc.HasExited) { break }
    $g = [BenchNative]::Grab($pill.x, $pill.y, $pill.w, $pill.h)
    $t = NowMs
    if ($null -eq $visible -and [BenchNative]::Diff($g, $base) -gt 6) { $visible = $t - $launchMs }
    if ($null -ne $visible) {
      if ($null -ne $prev -and [BenchNative]::Diff($g, $prev) -lt 0.2) { $same++ } else { $same = 0 }
      if ($same -ge 5) { $settled = $t - $launchMs; break }
    }
    $prev = $g
    Start-Sleep -Milliseconds 15
  }
  return @{ visibleMs = $visible; settledMs = $settled }
}

function Get-Mem([int]$root) {
  $pids = [BenchNative]::Tree($root)
  $raw = @{}
  Get-CimInstance Win32_PerfRawData_PerfProc_Process | ForEach-Object { $raw[[int]$_.IDProcess] = [long]$_.WorkingSetPrivate }
  $cmd = @{}
  Get-CimInstance Win32_Process | Where-Object { $pids -contains [int]$_.ProcessId } | ForEach-Object { $cmd[[int]$_.ProcessId] = [string]$_.CommandLine }
  $rows = foreach ($p in $pids) {
    try { $pr = [System.Diagnostics.Process]::GetProcessById($p) } catch { continue }
    $type = if ($cmd[$p] -match '--type=([a-z-]+)') { $Matches[1] } else { 'main' }
    if ($cmd[$p] -match '--utility-sub-type=([A-Za-z.]+)') { $type += ':' + $Matches[1] }
    [pscustomobject]@{ pid = $p; name = $pr.ProcessName; type = $type
      privateMB = [Math]::Round($pr.PrivateMemorySize64 / 1MB, 1); wsMB = [Math]::Round($pr.WorkingSet64 / 1MB, 1)
      privWsMB = [Math]::Round(($raw[$p]) / 1MB, 1) }
  }
  $rows = @($rows)
  return [ordered]@{
    processes = $rows.Count
    privateMB = [Math]::Round(($rows | Measure-Object privateMB -Sum).Sum, 1)
    wsMB = [Math]::Round(($rows | Measure-Object wsMB -Sum).Sum, 1)
    privWsMB = [Math]::Round(($rows | Measure-Object privWsMB -Sum).Sum, 1)
    rows = $rows
  }
}

function Get-Cpu([int]$root, [int]$seconds) {
  $start = @{}
  foreach ($p in [BenchNative]::Tree($root)) { try { $start[$p] = [System.Diagnostics.Process]::GetProcessById($p).TotalProcessorTime.TotalMilliseconds } catch { } }
  $sw = [Diagnostics.Stopwatch]::StartNew()
  Start-Sleep -Seconds $seconds
  $wall = $sw.Elapsed.TotalMilliseconds
  $used = 0.0
  foreach ($p in [BenchNative]::Tree($root)) {
    try { $now = [System.Diagnostics.Process]::GetProcessById($p).TotalProcessorTime.TotalMilliseconds } catch { continue }
    $used += $now - $(if ($start.ContainsKey($p)) { $start[$p] } else { 0 })
  }
  return [ordered]@{ seconds = [Math]::Round($wall / 1000, 1); cpuMs = [Math]::Round($used); pctOfOneCore = [Math]::Round(100 * $used / $wall, 3) }
}

function Stop-Tree([int]$root) {
  foreach ($p in ([BenchNative]::Tree($root) | Sort-Object -Descending)) { try { Stop-Process -Id $p -Force -ErrorAction Stop } catch { } }
}

$results = [ordered]@{ runner = $runner; mode = $Mode; build = [ordered]@{ tfm = $tfm; seconds = $buildS; folderMB = $binMB }; runs = @() }

# ------------------------------------------------------------------ one Electron Mantle run (live, on the runner)
$electronExe = Join-Path $ws 'node_modules\electron\dist\electron.exe'
$profileDir = Join-Path $env:APPDATA 'mantle'
function Run-Electron([string]$theme) {
  $tag = "electron-$theme"
  Say "=== $tag"
  Set-WinTheme $theme
  # theme.json is Mantle's saved appearance; any file of its in the profile also tells lib/welcome.js
  # this is not a first start, so the live run shows no welcome tour.
  New-Item -ItemType Directory -Force -Path $profileDir | Out-Null
  Set-Content -Path (Join-Path $profileDir 'theme.json') -Value ('{ "mode": "' + $theme + '" }') -Encoding utf8
  $probe = Join-Path $Out "probe-$theme"
  New-Item -ItemType Directory -Force -Path $probe | Out-Null
  [BenchNative]::ClearBottom($scr.Width, $scr.Height, 320) | Out-Null
  [BenchNative]::SetCursorPos($park.x, $park.y) | Out-Null
  Start-Sleep -Milliseconds 800
  $base = [BenchNative]::Grab($pill.x, $pill.y, $pill.w, $pill.h)
  $log = Join-Path $Out "$tag.log"
  # forward slashes, no quotes: Node reads backslashes inside a quoted NODE_OPTIONS value as escapes
  $env:NODE_OPTIONS = '--require=' + ((Join-Path $spike 'bench\electron-probe.js') -replace '\\', '/')
  $env:SPIKE_PROBE_OUT = $probe
  $launch = NowMs
  $proc = Start-Process -FilePath $electronExe -ArgumentList '.', '--live', "--log=$log" -WorkingDirectory $ws -PassThru `
    -RedirectStandardOutput (Join-Path $Out "$tag.stdout.txt") -RedirectStandardError (Join-Path $Out "$tag.stderr.txt")
  Remove-Item Env:NODE_OPTIONS, Env:SPIKE_PROBE_OUT
  $px = Watch-Pixels $launch $base 60 $proc
  # wait for Mantle to say it is up (installer/ci-install-test.ps1 waits for the same line)
  $upMs = $null
  $end = (Get-Date).AddSeconds(120)
  while ((Get-Date) -lt $end -and $null -eq $upMs -and -not $proc.HasExited) {
    if ((Test-Path $log) -and (Select-String -Path $log -Pattern 'features: (started|none started)' -Quiet)) { $upMs = (NowMs) - $launch }
    else { Start-Sleep -Milliseconds 250 }
  }
  if ($proc.HasExited) {
    Problem "$tag exited early (exit $($proc.ExitCode))"
    foreach ($f in "$tag.stdout.txt", "$tag.stderr.txt") { $fp = Join-Path $Out $f; if (Test-Path $fp) { Get-Content $fp | Select-Object -Last 15 | ForEach-Object { Write-Host "  ${f}: $_" } } }
    return $null
  }
  if ($null -eq $upMs) { Problem "$tag never logged 'features: started' in 120 s" }
  $wait = 45000 - ((NowMs) - $launch); if ($wait -gt 0) { Start-Sleep -Milliseconds $wait }
  [BenchNative]::SetCursorPos($park.x, $park.y) | Out-Null
  Start-Sleep -Seconds 2
  $shotBar = Save-Shot "$tag-bar.png" $shot
  $shotFull = Save-Shot "$tag-full.png" @{ x = 0; y = 0; w = $scr.Width; h = $scr.Height }
  Set-Content -Path (Join-Path $probe 'dump-up.req') -Value 'x'
  $mem = Get-Mem $proc.Id
  $cpuUp = Get-Cpu $proc.Id 60
  [BenchNative]::SetCursorPos($away.x, $away.y) | Out-Null
  Start-Sleep -Seconds 6
  $shotHidden = Save-Shot "$tag-hidden.png" $shot
  $cpuHidden = Get-Cpu $proc.Id 60
  Set-Content -Path (Join-Path $probe 'dump-hidden.req') -Value 'x'
  $memHidden = Get-Mem $proc.Id
  Start-Sleep -Seconds 2
  $marks = $null; $mp = Join-Path $probe 'marks.json'
  if (Test-Path $mp) { $marks = Get-Content $mp -Raw | ConvertFrom-Json }
  $bar = if ($marks) { $marks.windows | Where-Object { $_.url -match 'ui/bar/index\.html' } | Select-Object -First 1 } else { $null }
  $selfMs = if ($bar -and $bar.paintedUnixMs) { [long]$bar.paintedUnixMs - $launch } else { $null }
  $rtsMs = if ($bar -and $bar.readyToShowUnixMs) { [long]$bar.readyToShowUnixMs - $launch } else { $null }
  if (-not $bar) { Problem "$tag probe saw no bar window (marks.json $(if (Test-Path $mp) { 'present' } else { 'missing' }))" }
  # quit the way the user does (Windows comes back), then make sure the whole tree is gone
  $q = Start-Process -FilePath $electronExe -ArgumentList '.', '--quit' -WorkingDirectory $ws -PassThru -NoNewWindow
  if (-not $q.WaitForExit(30000)) { Say '  quit: --quit did not return in 30 s'; try { $q.Kill() } catch { } }
  $proc.WaitForExit(20000) | Out-Null
  Say "  quit: main $(if ($proc.HasExited) { 'exited' } else { 'still running (killed)' })"
  Stop-Tree $proc.Id
  Get-Process -Name electron, Mantle.Sidecar -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
  Start-Sleep -Seconds 3
  [BenchNative]::TaskbarAutoHide($true)
  return [ordered]@{
    app = 'electron'; theme = $theme; what = 'all of Mantle, live: main + GPU + utility + every renderer (bar, launcher, desktop layer, other surfaces) + Mantle.Sidecar.exe'
    firstPaint = [ordered]@{ barPaintedMs = $selfMs; barReadyToShowMs = $rtsMs; pixelsVisibleMs = $px.visibleMs; pixelsSettledMs = $px.settledMs; mantleUpMs = $upMs }
    memUp = $mem; cpuUp = $cpuUp; cpuHidden = $cpuHidden; memHidden = $memHidden
    shots = @((Split-Path -Leaf $shotBar), (Split-Path -Leaf $shotFull), (Split-Path -Leaf $shotHidden))
  }
}

# ------------------------------------------------------------------ one native bar run
function Run-Native([string]$theme) {
  $tag = "native-$theme"
  Say "=== $tag"
  Set-WinTheme $theme
  $dir = Join-Path $Out $tag
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  [BenchNative]::ClearBottom($scr.Width, $scr.Height, 320) | Out-Null
  # not the bottom edge: that un-hides Windows' own auto-hide taskbar, which would land in the captured backdrop
  [BenchNative]::SetCursorPos($away.x, $away.y) | Out-Null
  Start-Sleep -Milliseconds 800
  $base = [BenchNative]::Grab($pill.x, $pill.y, $pill.w, $pill.h)
  $cornerAt = @{ x = $pill.x - 20; y = $pill.y - 50 }   # inside the window's shadow room, outside the pill and its shadow
  $cornerBase = [BenchNative]::Grab($cornerAt.x, $cornerAt.y, 16, 16)
  $launch = NowMs
  $proc = Start-Process -FilePath $exe -ArgumentList "--theme=$theme", "--bench-out=$dir" -PassThru
  $px = Watch-Pixels $launch $base 60 $proc
  $ready = $null; $end = (Get-Date).AddSeconds(30)
  while ((Get-Date) -lt $end -and -not $ready) { if (Test-Path (Join-Path $dir 'ready.json')) { $ready = Get-Content (Join-Path $dir 'ready.json') -Raw | ConvertFrom-Json } else { Start-Sleep -Milliseconds 200 } }
  if ($proc.HasExited) {
    Problem "$tag exited early (exit $($proc.ExitCode))"
    if (Test-Path (Join-Path $dir 'native-log.txt')) { Get-Content (Join-Path $dir 'native-log.txt') | Select-Object -Last 20 | ForEach-Object { Write-Host "  $_" } }
    return $null
  }
  if (-not $ready) { Problem "$tag wrote no ready.json in 30 s" }
  $selfMs = if ($ready) { [long]$ready.firstFrameUnixMs - $launch } else { $null }
  $wait = 45000 - ((NowMs) - $launch); if ($Mode -eq 'smoke') { $wait = 12000 - ((NowMs) - $launch) }
  if ($wait -gt 0) { Start-Sleep -Milliseconds $wait }
  Start-Sleep -Seconds 2
  $shotBar = Save-Shot "$tag-bar.png" $shot
  $shotFull = Save-Shot "$tag-full.png" @{ x = 0; y = 0; w = $scr.Width; h = $scr.Height }
  $cornerDiff = [Math]::Round([BenchNative]::Diff([BenchNative]::Grab($cornerAt.x, $cornerAt.y, 16, 16), $cornerBase), 2)
  if ($cornerDiff -gt 3) { Problem "$tag window is not see-through outside the pill (corner differs by $cornerDiff)" }
  $mem = Get-Mem $proc.Id
  $cpuUp = Get-Cpu $proc.Id $(if ($Mode -eq 'smoke') { 15 } else { 60 })

  # Focus on click, through the real input queue: notepad, then charmap, then charmap again (minimize), then again (restore).
  $focus = [ordered]@{}
  $cellsPath = Join-Path $dir 'cells.json'
  $cells = if (Test-Path $cellsPath) { (Get-Content $cellsPath -Raw | ConvertFrom-Json).cells } else { @() }
  $focus.cells = @($cells | ForEach-Object { "$($_.name)$(if ($_.hasIcon) { '' } else { ' (no icon)' })" })
  function CellOf([string]$name) { $cells | Where-Object { $_.name -ieq $name } | Select-Object -First 1 }
  function ClickCell($c) { [BenchNative]::Click([int]($c.x + $c.w / 2), [int]($c.y + $c.h / 2)) | Out-Null; Start-Sleep -Milliseconds 700 }
  $np = CellOf 'notepad'; $cm = CellOf 'charmap'
  if ($np -and $cm) {
    ClickCell $np; $focus.notepadFront = ([BenchNative]::ForegroundPid() -eq $notepadPid)
    ClickCell $cm; $focus.charmapFront = ([BenchNative]::ForegroundPid() -eq $charmapPid)
    ClickCell $cm; $focus.charmapMinimized = [BenchNative]::AnyIconic($charmapPid)
    ClickCell $cm; $focus.charmapRestored = (-not [BenchNative]::AnyIconic($charmapPid)) -and ([BenchNative]::ForegroundPid() -eq $charmapPid)
    $shotFocus = Save-Shot "$tag-after-clicks.png" $shot
    foreach ($k in 'notepadFront', 'charmapFront', 'charmapMinimized', 'charmapRestored') { if (-not $focus[$k]) { Problem "$tag focus check $k failed" } }
  } else { Problem "$tag dock had no notepad/charmap cell (cells: $($focus.cells -join ', '))"; $shotFocus = $null }
  [BenchNative]::SetCursorPos($away.x, $away.y) | Out-Null
  Start-Sleep -Seconds 2
  $memAfter = Get-Mem $proc.Id
  Stop-Tree $proc.Id
  Start-Sleep -Seconds 2
  return [ordered]@{
    app = 'native'; theme = $theme; what = 'the native bar: one process (UI + window list + icons in-process)'
    firstPaint = [ordered]@{ firstFrameMs = $selfMs; pixelsVisibleMs = $px.visibleMs; pixelsSettledMs = $px.settledMs
      mainMs = if ($ready) { [long]$ready.mainUnixMs - $launch } else { $null } }
    backdrop = if ($ready) { $ready.backdrop } else { $null }; seeThroughCornerDiff = $cornerDiff
    memUp = $mem; cpuUp = $cpuUp; memAfterClicks = $memAfter; focus = $focus
    shots = @((Split-Path -Leaf $shotBar), (Split-Path -Leaf $shotFull)) + $(if ($shotFocus) { @(Split-Path -Leaf $shotFocus) } else { @() })
  }
}

# ------------------------------------------------------------------ run
$canElectron = $Mode -eq 'full' -and (Test-Path $electronExe) -and (Test-Path (Join-Path $ws 'sidecar\bin\Mantle.Sidecar.exe'))
if ($Mode -eq 'full' -and -not $canElectron) { Problem 'Electron Mantle cannot run here (no node_modules electron or no built sidecar)' }
foreach ($theme in 'light', 'dark') {
  if ($canElectron) { $r = Run-Electron $theme; if ($r) { $results.runs += $r } }
  $r = Run-Native $theme; if ($r) { $results.runs += $r }
}

$results.problems = @($problems)
$results | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $Out 'results.json') -Encoding utf8

# ------------------------------------------------------------------ summary
$md = [System.Collections.Generic.List[string]]::new()
$md.Add("| run | procs | private MB | working set MB | private WS MB | CPU bar up (% of 1 core, 60 s) | CPU bar hidden | first paint (app mark) | pixels visible | pixels settled |")
$md.Add('|---|---|---|---|---|---|---|---|---|---|')
foreach ($r in $results.runs) {
  $fp = if ($r.app -eq 'electron') { $r.firstPaint.barPaintedMs } else { $r.firstPaint.firstFrameMs }
  $hid = if ($r.cpuHidden) { $r.cpuHidden.pctOfOneCore } else { '-' }
  $md.Add("| $($r.app) $($r.theme) | $($r.memUp.processes) | $($r.memUp.privateMB) | $($r.memUp.wsMB) | $($r.memUp.privWsMB) | $($r.cpuUp.pctOfOneCore) | $hid | $fp | $($r.firstPaint.pixelsVisibleMs) | $($r.firstPaint.pixelsSettledMs) |")
}
foreach ($r in $results.runs) {
  $md.Add('')
  $md.Add("$($r.app) $($r.theme) processes:")
  foreach ($row in $r.memUp.rows) { $md.Add("  $($row.name) $($row.pid) $($row.type): private $($row.privateMB) / ws $($row.wsMB) / privWS $($row.privWsMB)") }
}
$md | Set-Content -Path (Join-Path $Out 'summary.md') -Encoding utf8
$md | ForEach-Object { Write-Host $_ }

foreach ($f in $fixtures) { try { Stop-Process -Id $f.Id -Force -ErrorAction Stop } catch { } }
if ($problems.Count -eq 0) { Write-Host "BENCH: ok ($($results.runs.Count) runs)" ; exit 0 }
Write-Host "BENCH: FAILED ($($results.runs.Count) runs; $($problems.Count) problems: $($problems -join '; '))"
exit 1
