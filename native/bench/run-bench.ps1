# run-bench.ps1 - build native Mantle and measure it (and, in full mode, the real Electron Mantle) on the
# same GitHub Windows runner, in one job. RUNNER ONLY: it runs Mantle LIVE (which changes the runner's
# shell), moves the cursor, clicks, changes the wallpaper, theme and screen size, and (modes) installs the
# Windows App Runtime. Never on a work machine.
#
#   pwsh -NoProfile -File native/bench/run-bench.ps1 [-Mode full|smoke|modes] [-Project <csproj>] [-Exe <path>]
#        [-Out <dir>] [-Res 1920x1080] [-BuildModes sc-r2r,sc,fd-r2r,aot,aot-partial] [-Themes light,dark]
#
# full : build (self-contained + ReadyToRun); then for light, then dark: Electron Mantle live, then native.
# smoke: build; native only, light and dark, short holds (no npm/Electron). CI's bench stage (< 3 min after build).
# modes: publish the native app once per build mode and measure each (light only by default): folder size,
#        3 launches (first frame + pixels on screen), RAM after 45 s, CPU over 60 s, build time, warnings,
#        and whether the bar works (renders, icons, click to focus / minimize / restore, see-through).
#
# -Project defaults to native/src/Mantle.App/Mantle.App.csproj; the exe name is the project's AssemblyName
# (asked of MSBuild), so MantleBar.exe and MantleNative.exe both work. -Exe skips the build (full/smoke).
# -Out defaults to native/out/bench. -Res defaults to $env:MANTLE_CLOUD_RES (cloudtest's 1920x1080).
#
# Per run: time to first paint (the app's own mark + pixels on screen), private bytes / working set /
# private working set summed over the app's whole process tree (Electron: plus every other Mantle process
# outside the tree, e.g. the crash supervisor, as its own row), CPU over 60 s with the bar up (Electron also
# with its bar hidden, its default at rest), screenshots, a per-process table. Electron's per-window marks
# and app.getAppMetrics() come from electron-probe.js inside its main process.
# Output: results.json and summary.md in -Out; the LAST line is BENCH: ok (...) or BENCH: FAILED (...).
param(
  [ValidateSet('full', 'smoke', 'modes')] [string]$Mode = 'full',
  [string]$Project,
  [string]$Exe,
  [string]$Out,
  [string]$Res = $env:MANTLE_CLOUD_RES,
  [string]$BuildModes = 'sc-r2r,sc,fd-r2r,aot,aot-partial',
  [string]$Themes
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$bench = $PSScriptRoot
if (-not $Project) { $Project = Join-Path $root 'native\src\Mantle.App\Mantle.App.csproj' }
$Project = (Resolve-Path $Project).Path
if (-not $Out) { $Out = Join-Path $root 'native\out\bench' }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path
if (-not $Themes) { $Themes = if ($Mode -eq 'modes') { 'light' } else { 'light,dark' } }
$themeList = @($Themes -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$problems = [System.Collections.Generic.List[string]]::new()
function Say($m) { Write-Host ("bench {0}: {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) }
function Problem($m) { $problems.Add($m); Write-Host "bench PROBLEM: $m" }
function NowMs { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
function Median($xs) { $v = @($xs | Where-Object { $null -ne $_ } | Sort-Object); if ($v.Count -eq 0) { return $null }; return $v[[int][Math]::Floor(($v.Count - 1) / 2)] }
function Q([string]$p) { if ($p -match '\s') { return '"' + $p + '"' } else { return $p } }

Add-Type -Path (Join-Path $bench 'BenchNative.cs')
# The runner desktop helpers (taskbar out of the way, attention requests stopped): native/tools/desk/Desk.cs
$deskCs = Join-Path $bench '..\tools\desk\Desk.cs'
if ((Test-Path $deskCs) -and -not ('Desk' -as [type])) { Add-Type -Path $deskCs }
# The runner's own taskbar must be off the bottom edge before a bar is measured or clicked: a lit taskbar
# button (a window asking for attention) keeps an auto-hide taskbar up. Returns $null when clear, else why not.
function Clear-Taskbar {
  if (-not ('Desk' -as [type])) { return $null }
  if (-not [Desk]::TaskbarShown()) { return $null }
  [BenchNative]::TaskbarAutoHide($true); [BenchNative]::SetCursorPos($away.x, $away.y) | Out-Null
  if ([Desk]::WaitHidden(2000)) { return $null }
  [void][Desk]::StopFlashing()
  if ([Desk]::WaitHidden(2000)) { return $null }
  [void][Desk]::HideTaskbar()
  if ([Desk]::WaitHidden(1000)) { Say '  the runner taskbar stayed up: hidden outright'; return $null }
  return "the runner's taskbar covers the bottom edge ($([Desk]::TaskbarShown() -join ','); $([Desk]::Snapshot()))"
}
Add-Type -AssemblyName System.Drawing

# ------------------------------------------------------------------ the runner
if ($Res -match '^(\d+)x(\d+)$') {
  $rc = [BenchNative]::SetRes([int]$Matches[1], [int]$Matches[2])
  Say "screen size ${Res}: ChangeDisplaySettings $rc"
  Start-Sleep -Seconds 2
}
$os = Get-CimInstance Win32_OperatingSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$sz = [BenchNative]::ScreenSize()
$scr = @{ Width = $sz[0]; Height = $sz[1] }
$sha = try { (& git -C $root rev-parse --short HEAD 2>$null) } catch { $null }
$sdks = @(& dotnet --list-sdks 2>$null)
$runner = [ordered]@{
  date = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm') + ' UTC'; sha = $sha
  os = "$($os.Caption) $($os.Version)"; image = $env:ImageOS + ' ' + $env:ImageVersion; cpu = $cpu.Name; cores = [Environment]::ProcessorCount
  ramGB = [Math]::Round($os.TotalVisibleMemorySize / 1MB, 1); screen = "$($scr.Width)x$($scr.Height)"
  dotnetSdks = ($sdks -join ' | '); hoverActivates = [BenchNative]::ActiveWindowTracking()
}
Say ("runner: " + (($runner.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value)" }) -join '; '))

# The same picture behind every bar: Mantle's default wallpaper. Transparency effects on (a server image may ship with them off).
Say ("wallpaper: " + [BenchNative]::SetWallpaper((Join-Path $root 'assets\backgrounds\snow-peak.jpg')))
$pers = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
New-Item -Force -Path $pers | Out-Null
Set-ItemProperty -Path $pers -Name EnableTransparency -Value 1 -Type DWord
function Set-WinTheme([string]$t) {
  $v = if ($t -eq 'light') { 1 } else { 0 }
  Set-ItemProperty -Path $pers -Name AppsUseLightTheme -Value $v -Type DWord
  Set-ItemProperty -Path $pers -Name SystemUsesLightTheme -Value $v -Type DWord
}

# ------------------------------------------------------------------ the project
# dotnet runs from the folder that holds the project's global.json (native/): the SDK pin is read from the
# CURRENT folder upward, not the project's, so from the repo root the runner's newest SDK would build it.
function SdkDir([string]$proj) {
  $d = Split-Path -Parent $proj
  while ($d) { if (Test-Path (Join-Path $d 'global.json')) { return $d }; $up = Split-Path -Parent $d; if ($up -eq $d) { break }; $d = $up }
  return (Split-Path -Parent $proj)
}
$tfm = if ($sdks -match '^10\.') { 'net10.0' } elseif ($sdks -match '^9\.') { 'net9.0' } else { 'net8.0' }
$projText = Get-Content $Project -Raw
$tfmProps = @(if ($projText -match 'SpikeTfm') { "-p:SpikeTfm=$tfm" })
$asm = $null
Push-Location (SdkDir $Project)
try {
  $j = (& dotnet msbuild $Project -nologo -getProperty:AssemblyName -getProperty:TargetFramework @tfmProps 2>$null) -join "`n"
  $pj = $j | ConvertFrom-Json
  $asm = $pj.Properties.AssemblyName; $projTfm = $pj.Properties.TargetFramework
} catch { $asm = $null } finally { Pop-Location }
if (-not $asm) { $asm = if ($projText -match '<AssemblyName>([^<]+)</AssemblyName>') { $Matches[1] } else { [IO.Path]::GetFileNameWithoutExtension($Project) } }
Say "project: $([IO.Path]::GetRelativePath($root, $Project)), exe $asm.exe, $projTfm"

# One publish. Returns what was built and how long it took; every warning line goes to build-<tag>.log.
function Publish-Native([string]$tag, [string]$proj, [string]$dest, [string[]]$props) {
  $log = Join-Path $Out "build-$tag.log"
  if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
  $cmd = @('publish', $proj, '-c', 'Release', '-r', 'win-x64', '-o', $dest, '-nologo', '-clp:NoSummary') + $tfmProps + $props
  Say "publish $tag`: dotnet $((@($cmd | Select-Object -Skip 2) | ForEach-Object { if ($_ -like '*\*') { Split-Path -Leaf $_ } else { $_ } }) -join ' ')"
  $t0 = Get-Date
  Push-Location (SdkDir $proj)
  try {
    Say "  SDK in $((Get-Location).Path | Split-Path -Leaf): $(& dotnet --version 2>$null)"
    & dotnet @cmd 2>&1 | Tee-Object -FilePath $log | Select-Object -Last 12 | ForEach-Object { Write-Host "  $_" }
    $code = $LASTEXITCODE
  } finally { Pop-Location }
  $secs = [Math]::Round(((Get-Date) - $t0).TotalSeconds)
  $exePath = Join-Path $dest "$asm.exe"
  $lines = @(Get-Content $log | Where-Object { $_ -match ':\s*(\w+ analysis )?warning ' } | Sort-Object -Unique)
  $codes = @{}
  foreach ($l in $lines) { if ($l -match 'warning ([A-Za-z]+\d+)') { $codes[$Matches[1]] = 1 + [int]$codes[$Matches[1]] } }
  $trimAot = @($lines | Where-Object { $_ -match 'warning (IL[23]\d{3}|CsWinRT\d+)' }).Count
  $errors = @(Get-Content $log | Where-Object { $_ -match ':\s*error ' } | Sort-Object -Unique)
  $files = @(if (Test-Path $dest) { Get-ChildItem -Recurse -File $dest })
  $r = [ordered]@{
    tag = $tag; ok = ($code -eq 0 -and (Test-Path $exePath)); exitCode = $code; seconds = $secs; exe = $exePath
    folderMB = [Math]::Round((($files | Measure-Object Length -Sum).Sum) / 1MB, 1); files = $files.Count
    exeMB = if (Test-Path $exePath) { [Math]::Round((Get-Item $exePath).Length / 1MB, 1) } else { $null }
    warnings = $lines.Count; trimAotWarnings = $trimAot; warningCodes = $codes; errors = $errors.Count
    firstErrors = @($errors | Select-Object -First 5); props = ($props -join ' ')
  }
  Say "  $tag`: exit $code in ${secs}s; $($r.folderMB) MB in $($r.files) files; warnings $($r.warnings) (trim/AOT $trimAot); errors $($errors.Count)"
  return $r
}

# ------------------------------------------------------------------ fixtures: the same running apps for every bar
[BenchNative]::TaskbarAutoHide($true)
$fixtures = @()
$fixtures += Start-Process notepad.exe -PassThru
$fixtures += Start-Process charmap.exe -PassThru
$fixtures += Start-Process cmd.exe -ArgumentList '/k', 'title Console' -PassThru
Start-Process explorer.exe -ArgumentList 'C:\Windows' | Out-Null
Start-Sleep -Seconds 4
# The folder window opens while another process holds the foreground, so Windows lights its taskbar button
# instead (HSHELL_FLASH, seen by ci.ps1's desk watcher), and the auto-hide taskbar comes up and STAYS up over
# the bar until that is cleared: stop the attention request now (Prepare-Screen checks again before each run).
if ('Desk' -as [type]) { $fl = [Desk]::StopFlashing(); $tbw = Clear-Taskbar; Say ("fixtures: stopped $fl attention request(s); taskbar " + $(if ($tbw) { $tbw } else { 'clear' })) }
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
  return $name
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

# The processes that make up one app. Electron: its tree + every other process running Mantle's own
# electron.exe or Mantle.Sidecar.exe (the crash supervisor, `Mantle.Sidecar.exe --supervise=<main pid>`, is
# started detached, outside the tree) + their trees. Native: its tree.
$electronExe = Join-Path $root 'node_modules\electron\dist\electron.exe'
function Get-AppPids([int]$rootPid, [bool]$electron) {
  $set = @{}   # a plain hashtable: an [ordered] one reads $set[5] as POSITION 5, not key 5
  foreach ($p in [BenchNative]::Tree($rootPid)) { $set[[int]$p] = 'tree' }
  if ($electron) {
    foreach ($p in @(Get-CimInstance Win32_Process -Filter "Name='electron.exe' OR Name='Mantle.Sidecar.exe'")) {
      $id = [int]$p.ProcessId
      if ($set.ContainsKey($id)) { continue }
      $path = [string]$p.ExecutablePath; $cl = [string]$p.CommandLine
      $sup = $cl -match "--supervise=$rootPid(\D|$)"
      if ($sup -or ($path -and $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase))) {
        foreach ($k in [BenchNative]::Tree($id)) { if (-not $set.ContainsKey([int]$k)) { $set[[int]$k] = $(if ($sup) { 'supervisor' } else { 'outside the tree' }) } }
      }
    }
  }
  return $set
}

function Get-Mem($set, $pages) {
  $pids = @($set.Keys)
  $raw = @{}
  Get-CimInstance Win32_PerfRawData_PerfProc_Process | ForEach-Object { $raw[[int]$_.IDProcess] = [long]$_.WorkingSetPrivate }
  $cmd = @{}
  Get-CimInstance Win32_Process | Where-Object { $pids -contains [int]$_.ProcessId } | ForEach-Object { $cmd[[int]$_.ProcessId] = [string]$_.CommandLine }
  $rows = foreach ($p in $pids) {
    try { $pr = [System.Diagnostics.Process]::GetProcessById($p) } catch { continue }
    $type = if ($cmd[$p] -match '--type=([a-z-]+)') { $Matches[1] } elseif ($pr.ProcessName -eq 'electron') { 'main' } else { $pr.ProcessName.ToLowerInvariant() }
    if ($cmd[$p] -match '--utility-sub-type=([A-Za-z.]+)') { $type += ':' + $Matches[1] }
    if ($pr.ProcessName -eq 'Mantle.Sidecar') { $type = if ($cmd[$p] -match '--supervise') { 'supervisor' } else { 'sidecar' } }
    $page = if ($pages) { (@($pages | Where-Object { $_.pid -eq $p -and $_.url } | ForEach-Object { ($_.url -replace '^file:///', '' -replace '\\', '/') -replace '^.*?/(ui/)', '$1' -replace '\?.*$', '' }) | Sort-Object -Unique) -join ' + ' } else { '' }
    [pscustomobject]@{ pid = $p; name = $pr.ProcessName; type = $type; scope = $set[$p]; page = $page
      privateMB = [Math]::Round($pr.PrivateMemorySize64 / 1MB, 1); wsMB = [Math]::Round($pr.WorkingSet64 / 1MB, 1)
      privWsMB = [Math]::Round(($raw[$p]) / 1MB, 1) }
  }
  $rows = @($rows)
  $sum = { param($rs) [ordered]@{ processes = @($rs).Count
      privateMB = [Math]::Round((@($rs) | Measure-Object privateMB -Sum).Sum, 1); wsMB = [Math]::Round((@($rs) | Measure-Object wsMB -Sum).Sum, 1)
      privWsMB = [Math]::Round((@($rs) | Measure-Object privWsMB -Sum).Sum, 1) } }
  $all = & $sum $rows
  $all.inTree = & $sum @($rows | Where-Object { $_.scope -eq 'tree' })
  $all.outsideTree = & $sum @($rows | Where-Object { $_.scope -ne 'tree' })
  $all.rows = $rows
  return $all
}

# CPU of the app's processes over `seconds` (the set is re-read at the end; a process that appeared meanwhile counts from 0).
function Get-Cpu([int]$rootPid, [bool]$electron, [int]$seconds) {
  $start = @{}
  foreach ($p in @((Get-AppPids $rootPid $electron).Keys)) { try { $start[$p] = [System.Diagnostics.Process]::GetProcessById($p).TotalProcessorTime.TotalMilliseconds } catch { } }
  $sw = [Diagnostics.Stopwatch]::StartNew()
  Start-Sleep -Seconds $seconds
  $wall = $sw.Elapsed.TotalMilliseconds
  $used = 0.0
  foreach ($p in @((Get-AppPids $rootPid $electron).Keys)) {
    try { $now = [System.Diagnostics.Process]::GetProcessById($p).TotalProcessorTime.TotalMilliseconds } catch { continue }
    $used += $now - $(if ($start.ContainsKey($p)) { $start[$p] } else { 0 })
  }
  return [ordered]@{ seconds = [Math]::Round($wall / 1000, 1); cpuMs = [Math]::Round($used); pctOfOneCore = [Math]::Round(100 * $used / $wall, 3) }
}

# The same foreground window before every CPU window, for both apps: charmap (it has no blinking caret; a
# console's or notepad's caret blinks twice a second, and a bar that follows window events pays for each).
# A real click on its caption, then the cursor where the measurement wants it. Returns the foreground process.
# On a small screen the tiled windows overlap: raise charmap first, and click a caption point that is
# charmap's on screen (a click that lands on notepad would make the click checks start from notepad).
# -BarUp leaves the cursor on the bottom edge (Electron's bar up); otherwise it rests inside charmap, away
# from the bar and over the window that should stay in front (also right if the runner has hover-to-activate).
function PName([uint32]$id) { try { (Get-Process -Id $id -ErrorAction Stop).ProcessName } catch { "pid $id" } }
function Focus-Steady([switch]$BarUp) {
  $note = @()
  $h = @([BenchNative]::WindowsOf([uint32]$charmapPid)) | Select-Object -First 1
  $rest = $away
  if ($h) {
    [BenchNative]::Raise($h) | Out-Null; Start-Sleep -Milliseconds 250
    $r = [BenchNative]::RectOf($h); $y = $r[1] + 12
    $rest = @{ x = [int](($r[0] + $r[2]) / 2); y = [int](($r[1] + $r[3]) / 2) }
    $cands = @([int](($r[0] + $r[2]) / 2), ($r[0] + 80), ($r[0] + 140))
    $note += "charmap [$($r -join ',')]; at y $y`: " + (($cands | ForEach-Object { "$_ $(PName ([BenchNative]::PidAt($_, $y)))" }) -join ', ')
    $x = $cands | Where-Object { [BenchNative]::PidAt($_, $y) -eq $charmapPid } | Select-Object -First 1
    if ($null -ne $x) { [BenchNative]::Click([int]$x, $y) | Out-Null; Start-Sleep -Milliseconds 400; $note += "clicked $x,$y -> $(PName ([BenchNative]::ForegroundPid()))" }
    else { $note += 'no caption point is charmap''s: not clicked' }
  } else { $note += 'charmap has no window' }
  $to = if ($BarUp) { $script:park } else { $rest }
  [BenchNative]::SetCursorPos($to.x, $to.y) | Out-Null
  Start-Sleep -Seconds 2
  $fg = PName ([BenchNative]::ForegroundPid())
  return [ordered]@{ foreground = $fg; note = ($note -join '; ') + "; cursor $($to.x),$($to.y) -> $fg" }
}

# Never walk the tree of a root that has exited: its pid may already be someone else's.
function Stop-App($proc, [bool]$electron) {
  if ($proc.HasExited) { return }
  foreach ($p in (@((Get-AppPids $proc.Id $electron).Keys) | Sort-Object -Descending)) { try { Stop-Process -Id $p -Force -ErrorAction Stop } catch { } }
}

$results = [ordered]@{ runner = $runner; mode = $Mode; project = [IO.Path]::GetRelativePath($root, $Project); exeName = "$asm.exe"; runs = @() }

# ------------------------------------------------------------------ one Electron Mantle run (live, on the runner)
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
  $env:NODE_OPTIONS = '--require=' + ((Join-Path $bench 'electron-probe.js') -replace '\\', '/')
  $env:MANTLE_BENCH_PROBE_OUT = $probe
  $launch = NowMs
  $proc = Start-Process -FilePath $electronExe -ArgumentList '.', '--live', "--log=$(Q $log)" -WorkingDirectory $root -PassThru `
    -RedirectStandardOutput (Join-Path $Out "$tag.stdout.txt") -RedirectStandardError (Join-Path $Out "$tag.stderr.txt")
  Remove-Item Env:NODE_OPTIONS, Env:MANTLE_BENCH_PROBE_OUT
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
  $shots = @((Save-Shot "$tag-bar.png" $shot), (Save-Shot "$tag-full.png" @{ x = 0; y = 0; w = $scr.Width; h = $scr.Height }))
  Set-Content -Path (Join-Path $probe 'dump-up.req') -Value 'x'
  Start-Sleep -Seconds 1
  $mUp = $null; $mp = Join-Path $probe 'metrics-up.json'
  for ($i = 0; $i -lt 20 -and -not (Test-Path $mp); $i++) { Start-Sleep -Milliseconds 250 }
  if (Test-Path $mp) { $mUp = Get-Content $mp -Raw | ConvertFrom-Json }
  $mem = Get-Mem (Get-AppPids $proc.Id $true) $(if ($mUp) { $mUp.pages } else { $null })
  $fs = Focus-Steady -BarUp
  $cpuUp = Get-Cpu $proc.Id $true 60; $cpuUp.foreground = $fs.foreground; $cpuUp.focusNote = $fs.note
  [BenchNative]::SetCursorPos($away.x, $away.y) | Out-Null
  Start-Sleep -Seconds 6
  $shots += Save-Shot "$tag-hidden.png" $shot
  $fs = Focus-Steady
  $cpuHidden = Get-Cpu $proc.Id $true 60; $cpuHidden.foreground = $fs.foreground; $cpuHidden.focusNote = $fs.note
  Set-Content -Path (Join-Path $probe 'dump-hidden.req') -Value 'x'
  $memHidden = Get-Mem (Get-AppPids $proc.Id $true) $(if ($mUp) { $mUp.pages } else { $null })
  Start-Sleep -Seconds 2

  # the probe's marks: which page is the bar, and when it painted
  $loaded = @(Get-ChildItem -Path $probe -Filter 'probe-loaded-*.json' -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json })
  $marks = $null; $mk = Join-Path $probe 'marks.json'
  if (Test-Path $mk) { $marks = Get-Content $mk -Raw | ConvertFrom-Json }
  $bar = if ($marks) { $marks.windows | Where-Object { $_.url -match 'ui/bar/(index|bar)\.html' } | Select-Object -First 1 } else { $null }
  $rel = { param($v) if ($v) { [long]$v - $launch } else { $null } }
  $barMarks = if ($bar) { [ordered]@{
      createdMs = & $rel $bar.createdUnixMs; domReadyMs = & $rel $bar.domReadyUnixMs; loadedMs = & $rel $bar.loadedUnixMs
      readyToShowMs = & $rel $bar.readyToShowUnixMs; firstPaintMs = & $rel $bar.paintEntries.'first-paint'
      firstContentfulPaintMs = & $rel $bar.paintEntries.'first-contentful-paint'; paintedMs = & $rel $bar.paintedUnixMs
      firstShowMs = & $rel $bar.firstShowUnixMs; frameAfterShowMs = & $rel $bar.frameAfterShowUnixMs } } else { $null }
  if ($loaded.Count -eq 0) { Problem "$tag probe never loaded (no probe-loaded-*.json: NODE_OPTIONS --require refused?)" }
  elseif (-not $marks -or -not $marks.bootUnixMs) { Problem "$tag probe loaded (pid $($loaded[0].pid), type $($loaded[0].processType), electron resolves to $($loaded[0].electronResolvesTo)) but never booted" }
  elseif (-not $bar) { Problem "$tag probe saw no bar window ($(@($marks.windows).Count) windows)" }
  elseif (-not ($barMarks.firstPaintMs -or $barMarks.readyToShowMs -or $barMarks.paintedMs)) { Problem "$tag bar window has no paint mark" }
  if (-not $mUp) { Problem "$tag probe answered no metrics dump" }
  $barPid = if ($mUp) { (@($mUp.pages | Where-Object { $_.url -match 'ui/bar/(index|bar)\.html' }) | Select-Object -First 1).pid } else { $null }
  # The spike's estimate of a bar-only Electron app: what it would still carry (main, which hosts the GPU
  # thread here, network service, one renderer, the sidecar). An ESTIMATE from this run's rows, not a measured bar-only build.
  $est = @($mem.rows | Where-Object { $_.scope -eq 'tree' -and ($_.type -in 'main', 'gpu-process', 'sidecar' -or $_.type -like 'utility:network*' -or ($barPid -and $_.pid -eq $barPid)) })
  $barOnly = [ordered]@{ estimate = $true; barRendererPid = $barPid; processes = $est.Count
    privateMB = [Math]::Round(($est | Measure-Object privateMB -Sum).Sum, 1); wsMB = [Math]::Round(($est | Measure-Object wsMB -Sum).Sum, 1)
    privWsMB = [Math]::Round(($est | Measure-Object privWsMB -Sum).Sum, 1); what = 'main (GPU in-process: Mantle passes --in-process-gpu) + network service + the bar renderer + sidecar(s) in the tree' }

  # quit the way the user does (Windows comes back), then make sure every Mantle process is gone
  $q = Start-Process -FilePath $electronExe -ArgumentList '.', '--quit' -WorkingDirectory $root -PassThru -NoNewWindow
  if (-not $q.WaitForExit(30000)) { Say '  quit: --quit did not return in 30 s'; try { $q.Kill() } catch { } }
  $proc.WaitForExit(20000) | Out-Null
  Say "  quit: main $(if ($proc.HasExited) { 'exited' } else { 'still running (killed)' })"
  Stop-App $proc $true
  Get-CimInstance Win32_Process -Filter "Name='electron.exe' OR Name='Mantle.Sidecar.exe'" | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop } catch { } }
  Start-Sleep -Seconds 3
  [BenchNative]::TaskbarAutoHide($true)
  return [ordered]@{
    app = 'electron'; theme = $theme; what = 'all of Mantle, live: main + GPU + utility + every renderer (bar, launcher, desktop layer, other surfaces) + sidecars, and the crash supervisor outside the tree'
    firstPaint = [ordered]@{ bar = $barMarks; pixelsVisibleMs = $px.visibleMs; pixelsSettledMs = $px.settledMs; mantleUpMs = $upMs
      probeLoadedMs = if ($loaded.Count) { [long]$loaded[0].atUnixMs - $launch } else { $null }; appReadyMs = if ($marks) { & $rel $marks.readyUnixMs } else { $null } }
    memUp = $mem; barOnlyEstimate = $barOnly; cpuUp = $cpuUp; cpuHidden = $cpuHidden; memHidden = $memHidden
    probe = [ordered]@{ loaded = $loaded; windows = if ($marks) { @($marks.windows).Count } else { 0 }; appMetricsUp = if ($mUp) { $mUp.metrics } else { $null } }
    shots = $shots
  }
}

# ------------------------------------------------------------------ native runs
function Start-Native([string]$exePath, [string]$theme, [string]$dir) {
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $dir 'ready.json'), (Join-Path $dir 'cells.json')
  $a = @("--theme=$theme", "--bench-out=$(Q $dir)", "--user-data=$(Q (Join-Path $dir 'user-data'))")
  return Start-Process -FilePath $exePath -ArgumentList $a -PassThru
}
function Prepare-Screen {
  [BenchNative]::ClearBottom($scr.Width, $scr.Height, 320) | Out-Null
  # not the bottom edge: that un-hides Windows' own auto-hide taskbar, which would land in the captured backdrop
  [BenchNative]::SetCursorPos($away.x, $away.y) | Out-Null
  Start-Sleep -Milliseconds 800
  $tb = Clear-Taskbar; if ($tb) { Problem "before a run: $tb" }
}
function Wait-Ready([string]$dir, $proc, [int]$timeoutS) {
  $f = Join-Path $dir 'ready.json'; $end = (Get-Date).AddSeconds($timeoutS)
  while ((Get-Date) -lt $end -and -not $proc.HasExited) {
    if (Test-Path $f) { try { return (Get-Content $f -Raw | ConvertFrom-Json) } catch { } }
    Start-Sleep -Milliseconds 200
  }
  if (Test-Path $f) { try { return (Get-Content $f -Raw | ConvertFrom-Json) } catch { } }
  return $null
}
function Show-NativeLog([string]$dir) {
  $l = Join-Path $dir 'native-log.txt'
  if (Test-Path $l) { Get-Content $l | Select-Object -Last 20 | ForEach-Object { Write-Host "  native-log: $_" } }
}

# A launch for start-up time only: until the pixels settle and ready.json exists, then stop it.
function Launch-Once([string]$tag, [string]$exePath, [string]$theme) {
  $dir = Join-Path $Out $tag
  Prepare-Screen
  $base = [BenchNative]::Grab($pill.x, $pill.y, $pill.w, $pill.h)
  $launch = NowMs
  $proc = Start-Native $exePath $theme $dir
  $px = Watch-Pixels $launch $base 30 $proc
  $ready = Wait-Ready $dir $proc 15
  $exited = $proc.HasExited
  if ($exited) { Show-NativeLog $dir }
  Stop-App $proc $false
  Start-Sleep -Milliseconds 1500
  return [ordered]@{ firstFrameMs = if ($ready) { [long]$ready.firstFrameUnixMs - $launch } else { $null }
    pixelsVisibleMs = $px.visibleMs; pixelsSettledMs = $px.settledMs; exitedEarly = $exited; ready = [bool]$ready }
}

# The measured run: first paint, RAM after `holdS`, CPU over `cpuS`, the click checks, see-through.
function Run-Native([string]$tag, [string]$exePath, [string]$theme, [int]$holdS, [int]$cpuS) {
  Say "=== $tag"
  Set-WinTheme $theme
  $dir = Join-Path $Out $tag
  Prepare-Screen
  $base = [BenchNative]::Grab($pill.x, $pill.y, $pill.w, $pill.h)
  $cornerAt = @{ x = $pill.x - 20; y = $pill.y - 50 }   # inside the window's shadow room, outside the pill and its shadow
  $cornerBase = [BenchNative]::Grab($cornerAt.x, $cornerAt.y, 16, 16)
  $launch = NowMs
  $proc = Start-Native $exePath $theme $dir
  $px = Watch-Pixels $launch $base 60 $proc
  $ready = Wait-Ready $dir $proc 30
  if ($proc.HasExited) { Problem "$tag exited early (exit $($proc.ExitCode))"; Show-NativeLog $dir; return $null }
  if (-not $ready) { Problem "$tag wrote no ready.json in 30 s"; Show-NativeLog $dir }
  if ($null -eq $px.visibleMs) { Problem "$tag never changed the pill's pixels in 60 s" }
  $wait = $holdS * 1000 - ((NowMs) - $launch)
  if ($wait -gt 0) { Start-Sleep -Milliseconds $wait }
  $shots = @((Save-Shot "$tag-bar.png" $shot), (Save-Shot "$tag-full.png" @{ x = 0; y = 0; w = $scr.Width; h = $scr.Height }))
  $cornerDiff = [Math]::Round([BenchNative]::Diff([BenchNative]::Grab($cornerAt.x, $cornerAt.y, 16, 16), $cornerBase), 2)
  if ($cornerDiff -gt 3) { Problem "$tag window is not see-through outside the pill (corner differs by $cornerDiff)" }
  $mem = Get-Mem (Get-AppPids $proc.Id $false) $null
  $fs = Focus-Steady
  $cpuUp = Get-Cpu $proc.Id $false $cpuS; $cpuUp.foreground = $fs.foreground; $cpuUp.focusNote = $fs.note

  # Focus on click, through the real input queue: notepad, then charmap, then charmap again (minimize), then again (restore).
  $focus = [ordered]@{}
  $cellsPath = Join-Path $dir 'cells.json'
  $cells = if (Test-Path $cellsPath) { @((Get-Content $cellsPath -Raw | ConvertFrom-Json).cells) } else { @() }
  $focus.cells = @($cells | ForEach-Object { "$($_.name)$(if ($_.hasIcon) { '' } else { ' (no icon)' })" })
  function CellOf([string]$name) { $cells | Where-Object { $_.name -ieq $name } | Select-Object -First 1 }
  # Running-only apps follow the z-order as in Electron (BAR-289): a focused app moves to the front of them and a
  # minimised one to the back, so every click finds its cell again in a fresh, settled cells.json (the strip
  # rebuilds at most once a second, so it is read 1.5 s after the last click at the earliest and only once two
  # reads 400 ms apart agree).
  $script:lastClickAt = 0
  function CellNow([string]$name) {
    $until = (NowMs) + 5000
    do {
      $since = (NowMs) - $script:lastClickAt
      if ($since -lt 1500) { Start-Sleep -Milliseconds (1500 - $since) }
      $t1 = if (Test-Path $cellsPath) { Get-Content $cellsPath -Raw } else { '' }
      Start-Sleep -Milliseconds 400
      $t2 = if (Test-Path $cellsPath) { Get-Content $cellsPath -Raw } else { '' }
    } while ($t1 -ne $t2 -and (NowMs) -lt $until)
    if (-not $t2) { return $null }
    return @(($t2 | ConvertFrom-Json).cells) | Where-Object { $_.name -ieq $name } | Select-Object -First 1
  }
  function ClickCell($cell) {
    $c = CellNow $cell.n
    if (-not $c) { Problem "$tag the $($cell.n) cell is gone from cells.json before a click"; return }
    [BenchNative]::Click([int]($c.x + $c.w / 2), [int]($c.y + $c.h / 2)) | Out-Null; $script:lastClickAt = NowMs; Start-Sleep -Milliseconds 700
  }
  $np = CellOf 'notepad'; $cm = CellOf 'charmap'
  $focus.icons = [bool]($np -and $cm -and $np.hasIcon -and $cm.hasIcon)
  if (-not $focus.icons) { Problem "$tag notepad/charmap cells missing or without their real icon (cells: $($focus.cells -join ', '))" }
  $tbWhy = Clear-Taskbar
  if ($tbWhy) { Problem "$tag focus checks not clicked: $tbWhy"; $np = $null }   # never click the runner's taskbar
  if ($np -and $cm) {
    # A click on the cell of the window in front minimizes it (as on Windows' taskbar), so start with the
    # one that is NOT in front: notepad, unless notepad already is. A = first, B = second.
    $fgNow = [BenchNative]::ForegroundPid()
    $a = @{ c = $np; pid = $notepadPid; n = 'notepad' }; $b = @{ c = $cm; pid = $charmapPid; n = 'charmap' }
    if ($fgNow -eq $notepadPid) { $a, $b = $b, $a }
    $focus.order = "$($a.n), then $($b.n) (in front before: $(PName $fgNow))"
    ClickCell $a; $focus.clickBringsAFront = ([BenchNative]::ForegroundPid() -eq $a.pid)
    ClickCell $b; $focus.clickBringsBFront = ([BenchNative]::ForegroundPid() -eq $b.pid)
    ClickCell $b; $focus.clickAgainMinimizes = [BenchNative]::AnyIconic($b.pid)
    ClickCell $b; $focus.clickAgainRestores = (-not [BenchNative]::AnyIconic($b.pid)) -and ([BenchNative]::ForegroundPid() -eq $b.pid)
    $shots += Save-Shot "$tag-after-clicks.png" $shot
    foreach ($k in 'clickBringsAFront', 'clickBringsBFront', 'clickAgainMinimizes', 'clickAgainRestores') { if (-not $focus[$k]) { Problem "$tag focus check $k failed ($($focus.order))" } }
  }
  $focus.ok = [bool]($focus.icons -and $focus.clickBringsAFront -and $focus.clickBringsBFront -and $focus.clickAgainMinimizes -and $focus.clickAgainRestores)
  [BenchNative]::SetCursorPos($away.x, $away.y) | Out-Null
  Start-Sleep -Seconds 2
  $memAfter = Get-Mem (Get-AppPids $proc.Id $false) $null
  Stop-App $proc $false
  Start-Sleep -Seconds 2
  return [ordered]@{
    app = 'native'; theme = $theme; tag = $tag; what = "$asm.exe: its whole process tree"
    firstPaint = [ordered]@{ firstFrameMs = if ($ready) { [long]$ready.firstFrameUnixMs - $launch } else { $null }
      pixelsVisibleMs = $px.visibleMs; pixelsSettledMs = $px.settledMs
      mainMs = if ($ready -and $ready.mainUnixMs) { [long]$ready.mainUnixMs - $launch } else { $null } }
    backdrop = if ($ready) { $ready.backdrop } else { $null }; seeThroughCornerDiff = $cornerDiff
    memUp = $mem; cpuUp = $cpuUp; memAfterClicks = $memAfter; focus = $focus
    works = [bool]($ready -and $null -ne $px.visibleMs -and $focus.ok -and $cornerDiff -le 3)
    shots = $shots
  }
}

# ------------------------------------------------------------------ build modes (the -Mode modes table)
$modeDefs = [ordered]@{
  'sc-r2r'   = @{ what = 'self-contained .NET + Windows App SDK, ReadyToRun (the spike''s)'; props = @('--self-contained', 'true', '-p:WindowsAppSDKSelfContained=true', '-p:PublishReadyToRun=true', '-p:PublishAot=false', '-p:PublishTrimmed=false') }
  'sc'       = @{ what = 'self-contained, no ReadyToRun (JIT at start)'; props = @('--self-contained', 'true', '-p:WindowsAppSDKSelfContained=true', '-p:PublishReadyToRun=false', '-p:PublishAot=false', '-p:PublishTrimmed=false') }
  'fd-r2r'   = @{ what = 'framework-dependent: .NET runtime and Windows App Runtime installed separately; ReadyToRun'; props = @('--self-contained', 'false', '-p:WindowsAppSDKSelfContained=false', '-p:PublishReadyToRun=true', '-p:PublishAot=false', '-p:PublishTrimmed=false') }
  'aot'      = @{ what = 'Native AOT + full trimming, the app as it is'; props = @('--self-contained', 'true', '-p:WindowsAppSDKSelfContained=true', '-p:PublishAot=true', '-p:PublishReadyToRun=false') }
  'aot-partial' = @{ what = 'Native AOT + trimming, with every type CsWinRT1028 names marked partial (a bench-only patch to the temp copy, never to the repo)'; partial = $true; props = @('--self-contained', 'true', '-p:WindowsAppSDKSelfContained=true', '-p:PublishAot=true', '-p:PublishReadyToRun=false') }
}
$tmpRoot = Join-Path $(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }) 'mantle-bench'

# A clean copy of native/ per mode (own obj/ and bin/), so each build time is a clean build with a warm NuGet cache.
function Copy-NativeTree([string]$tag) {
  $dst = Join-Path $tmpRoot $tag
  if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
  $srcNative = Join-Path $root 'native'
  $relProj = [IO.Path]::GetRelativePath($srcNative, $Project)
  if ($relProj.StartsWith('..')) { $srcNative = Split-Path -Parent $Project; $relProj = Split-Path -Leaf $Project }
  & robocopy $srcNative (Join-Path $dst 'native') /E /NFL /NDL /NJH /NJS /NP /XD bin obj out .vs | Out-Null
  if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
  $global:LASTEXITCODE = 0
  return (Join-Path (Join-Path $dst 'native') $relProj)
}

# The Windows App Runtime a framework-dependent build needs, matched to the Windows App SDK the build restored.
function Install-WinAppRuntime([string]$projCopy) {
  $assets = Join-Path (Split-Path -Parent $projCopy) 'obj\project.assets.json'
  $ver = $null
  if (Test-Path $assets) { $m = [regex]::Match((Get-Content $assets -Raw), '"Microsoft\.WindowsAppSDK/(\d+)\.(\d+)\.([0-9.]+)[^"]*"'); if ($m.Success) { $ver = $m.Value.Trim('"').Split('/')[1]; $mm = "$($m.Groups[1].Value).$($m.Groups[2].Value)" } }
  if (-not $ver) { return [ordered]@{ ok = $false; why = 'no Microsoft.WindowsAppSDK version in project.assets.json' } }
  $url = "https://aka.ms/windowsappsdk/$mm/latest/windowsappruntimeinstall-x64.exe"
  $dl = Join-Path $tmpRoot "windowsappruntimeinstall-$mm-x64.exe"
  $t0 = Get-Date
  try { Invoke-WebRequest -Uri $url -OutFile $dl -UseBasicParsing } catch { return [ordered]@{ ok = $false; sdkPackage = $ver; url = $url; why = "download failed: $($_.Exception.Message)" } }
  $p = Start-Process -FilePath $dl -ArgumentList '--quiet', '--force' -PassThru -Wait
  $pk = @(& powershell.exe -NoProfile -Command "Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.$mm*' | ForEach-Object { `$_.Name + ' ' + `$_.Version + ' ' + `$_.Architecture }" 2>$null)
  $sig = (Get-AuthenticodeSignature $dl).SignerCertificate.Subject
  $r = [ordered]@{ ok = ($p.ExitCode -eq 0 -and $pk.Count -gt 0); sdkPackage = $ver; url = $url; installerVersion = (Get-Item $dl).VersionInfo.ProductVersion
    installerMB = [Math]::Round((Get-Item $dl).Length / 1MB, 1); signer = $sig; exitCode = $p.ExitCode; seconds = [Math]::Round(((Get-Date) - $t0).TotalSeconds); packages = $pk }
  Say "  Windows App Runtime $mm (for package $ver): installer $($r.installerVersion), exit $($p.ExitCode), $($pk.Count) packages: $($pk -join '; ')"
  return $r
}

# aot-partial: mark partial every class the CsWinRT1028 warning names (it must be, for CsWinRT to generate the
# WinRT vtables an AOT build needs when the object crosses the ABI). Reads build-aot.log of this run, or makes
# one with an extra publish. Patches the temp copy only. Returns "file: class" per patch.
function Add-Partial([string]$m, [string]$projCopy, [string[]]$props) {
  $log = Join-Path $Out 'build-aot.log'
  if (-not (Test-Path $log)) { $null = Publish-Native 'aot' $projCopy (Join-Path (Join-Path $tmpRoot $m) 'probe') $props }
  $copyNative = Join-Path (Join-Path $tmpRoot $m) 'native'
  $done = @()
  foreach ($l in @(Get-Content $log | Where-Object { $_ -match 'warning CsWinRT1028' } | Sort-Object -Unique)) {
    if ($l -notmatch '\\native\\(.+?\.cs)\(\d+,\d+\): warning CsWinRT1028: Class ''([^'']+)''') { continue }
    $rel = $Matches[1]; $cls = ($Matches[2] -split '\.')[-1]
    $f = Join-Path $copyNative $rel
    if (-not (Test-Path $f)) { continue }
    $t = Get-Content $f -Raw
    if ($t -match "partial\s+class\s+$cls\b") { continue }
    $t2 = $t -replace "\bclass\s+$cls\b", "partial class $cls"
    if ($t2 -ne $t) { Set-Content -Path $f -Value $t2 -NoNewline -Encoding utf8; $done += "$($rel -replace '\\', '/'): $cls" }
  }
  Say "  $m`: marked partial: $(if ($done.Count) { $done -join ', ' } else { 'nothing (no CsWinRT1028 in build-aot.log)' })"
  return $done
}

function Run-BuildMode([string]$m) {
  $def = $modeDefs[$m]
  if (-not $def) { Problem "unknown build mode $m"; return $null }
  Say "=== mode $m`: $($def.what)"
  $projCopy = Copy-NativeTree $m
  $dest = Join-Path (Join-Path $tmpRoot $m) 'publish'
  $patches = @(if ($def.partial) { Add-Partial $m $projCopy $def.props })
  # every mode starts with cold build tools (no MSBuild node or compiler server left warm by the mode before)
  Push-Location (SdkDir $projCopy); try { & dotnet build-server shutdown 2>&1 | Out-Null } finally { Pop-Location }
  $b = Publish-Native $m $projCopy $dest $def.props
  $rec = [ordered]@{ mode = $m; what = $def.what; build = $b; patches = $patches; runtime = $null; launches = @(); run = $null; works = $false; verdict = '' }
  if (-not $b.ok) { $e1 = [string](($b.firstErrors | Select-Object -First 1) -replace '^.*?error ', 'error ' -replace ';', ',')
    $rec.verdict = "build FAILED (exit $($b.exitCode), $($b.errors) errors, first: $($e1.Substring(0, [Math]::Min(160, $e1.Length))))"; return $rec }
  if ($m -like 'fd*') {
    $rec.runtime = Install-WinAppRuntime $projCopy
    $rec.runtime.dotnetRuntimes = (@(& dotnet --list-runtimes 2>$null | Where-Object { $_ -match '^Microsoft\.NETCore\.App ' }) -join ' | ')
    if (-not $rec.runtime.ok) { $rec.verdict = "Windows App Runtime install failed ($($rec.runtime.why) exit $($rec.runtime.exitCode))" }
  }
  $theme = $themeList[0]
  Set-WinTheme $theme
  foreach ($i in 1, 2) { $rec.launches += Launch-Once "mode-$m-launch$i" $b.exe $theme }
  $run = Run-Native "mode-$m" $b.exe $theme 45 60
  $rec.run = $run
  if ($run) { $rec.launches += [ordered]@{ firstFrameMs = $run.firstPaint.firstFrameMs; pixelsVisibleMs = $run.firstPaint.pixelsVisibleMs; pixelsSettledMs = $run.firstPaint.pixelsSettledMs; ready = $null -ne $run.firstPaint.firstFrameMs } }
  $rec.firstFrameMedianMs = Median ($rec.launches | ForEach-Object { $_.firstFrameMs })
  $rec.pixelsVisibleMedianMs = Median ($rec.launches | ForEach-Object { $_.pixelsVisibleMs })
  $rec.works = [bool]($run -and $run.works)
  if (-not $rec.verdict) {
    $rec.verdict = if ($rec.works) { 'works' } elseif (-not $run) { 'did not run (exited early)' } else {
      $why = @(); if ($null -eq $run.firstPaint.firstFrameMs) { $why += 'no ready.json' }; if ($null -eq $run.firstPaint.pixelsVisibleMs) { $why += 'nothing on screen' }
      if (-not $run.focus.icons) { $why += 'icons/cells missing' } elseif (-not $run.focus.ok) { $why += 'click checks failed' }
      if ($run.seeThroughCornerDiff -gt 3) { $why += 'not see-through' }
      'NOT working: ' + ($why -join ', ') }
  }
  Say "  mode $m`: $($rec.verdict)"
  return $rec
}

# ------------------------------------------------------------------ run
if ($Mode -eq 'modes') {
  if ($Exe) { Say '-Exe is ignored in -Mode modes (every mode is published from -Project)' }
  Push-Location (SdkDir $Project)
  try { & dotnet restore $Project -r win-x64 -nologo @tfmProps 2>&1 | Select-Object -Last 3 | ForEach-Object { Write-Host "  restore: $_" } } finally { Pop-Location }   # warms the NuGet cache
  $results.modes = @()
  foreach ($m in @($BuildModes -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
    $before = $problems.Count
    $r = Run-BuildMode $m
    if ($r) {
      $r.problems = @($problems | Select-Object -Skip $before)
      $results.modes += $r
    }
    # a mode's own failures are its findings (the table says them); only the baseline mode can fail the bench
    if ($m -ne 'sc-r2r') { while ($problems.Count -gt $before) { $problems.RemoveAt($problems.Count - 1) } }
    elseif ($r -and -not $r.works) { Problem "baseline mode sc-r2r: $($r.verdict)" }
  }
} else {
  # native/tools/ci.ps1 sets MANTLE_NATIVE_EXE to the app its build stage published: no second build in CI
  if (-not $Exe -and $env:MANTLE_NATIVE_EXE -and (Test-Path $env:MANTLE_NATIVE_EXE)) { $Exe = $env:MANTLE_NATIVE_EXE; Say "MANTLE_NATIVE_EXE is set: using that build" }
  if ($Exe) {
    $exePath = (Resolve-Path $Exe).Path
    $results.build = [ordered]@{ skipped = $true; exe = $exePath }
    Say "using $exePath (no build)"
  } else {
    $b = Publish-Native 'sc-r2r' $Project (Join-Path $root 'native\bin\bench\app') $modeDefs['sc-r2r'].props
    $results.build = $b
    if (-not $b.ok) {
      Problem "dotnet publish failed (exit $($b.exitCode), $tfm, $($b.seconds)s): see build-sc-r2r.log"
      $results.problems = @($problems); $results | ConvertTo-Json -Depth 10 | Set-Content -Path (Join-Path $Out 'results.json') -Encoding utf8
      Write-Host "BENCH: FAILED ($($problems -join '; '))"
      exit 1
    }
    $exePath = $b.exe
  }
  $canElectron = $Mode -eq 'full' -and (Test-Path $electronExe) -and (Test-Path (Join-Path $root 'sidecar\bin\Mantle.Sidecar.exe'))
  if ($Mode -eq 'full' -and -not $canElectron) { Problem 'Electron Mantle cannot run here (no node_modules electron or no built sidecar)' }
  foreach ($theme in $themeList) {
    if ($canElectron) { $r = Run-Electron $theme; if ($r) { $results.runs += $r } }
    $r = if ($Mode -eq 'smoke') { Run-Native "native-$theme" $exePath $theme 12 15 } else { Run-Native "native-$theme" $exePath $theme 45 60 }
    if ($r) { $results.runs += $r }
  }
}

$results.problems = @($problems)
$results | ConvertTo-Json -Depth 10 | Set-Content -Path (Join-Path $Out 'results.json') -Encoding utf8

# ------------------------------------------------------------------ summary
$md = [System.Collections.Generic.List[string]]::new()
$md.Add("runner: $($runner.os); $($runner.cpu); $($runner.cores) cores; $($runner.ramGB) GB; screen $($runner.screen); $($runner.date); $($runner.sha)")
$md.Add('')
if ($results.runs.Count) {
  $md.Add('| run | procs | private MB | working set MB | private WS MB | CPU bar up (% of 1 core) | CPU bar hidden | own first-paint mark (ms) | pixels visible | pixels settled |')
  $md.Add('|---|---|---|---|---|---|---|---|---|---|')
  foreach ($r in $results.runs) {
    $fp = if ($r.app -eq 'electron') { $bm = $r.firstPaint.bar; if ($bm) { "first-paint $($bm.firstPaintMs) / ready-to-show $($bm.readyToShowMs) / 2nd rAF $($bm.paintedMs)" } else { '-' } } else { "first frame $($r.firstPaint.firstFrameMs)" }
    $hid = if ($r.cpuHidden) { $r.cpuHidden.pctOfOneCore } else { '-' }
    $md.Add("| $($r.app) $($r.theme) | $($r.memUp.processes) | $($r.memUp.privateMB) | $($r.memUp.wsMB) | $($r.memUp.privWsMB) | $($r.cpuUp.pctOfOneCore) ($($r.cpuUp.seconds) s) | $hid | $fp | $($r.firstPaint.pixelsVisibleMs) | $($r.firstPaint.pixelsSettledMs) |")
  }
  foreach ($r in $results.runs) {
    $md.Add('')
    $md.Add("$($r.app) $($r.theme) processes (private / ws / private ws MB):")
    foreach ($row in ($r.memUp.rows | Sort-Object { $_.scope -ne 'tree' }, { -$_.privateMB })) { $md.Add("  $($row.name) $($row.pid) $($row.type)$(if ($row.page) { " [$($row.page)]" })$(if ($row.scope -ne 'tree') { " ($($row.scope))" }): $($row.privateMB) / $($row.wsMB) / $($row.privWsMB)") }
    if ($r.app -eq 'electron') {
      $md.Add("  in the tree: $($r.memUp.inTree.processes) procs, $($r.memUp.inTree.privateMB) / $($r.memUp.inTree.wsMB) / $($r.memUp.inTree.privWsMB); outside it: $($r.memUp.outsideTree.processes) procs, $($r.memUp.outsideTree.privateMB) / $($r.memUp.outsideTree.wsMB) / $($r.memUp.outsideTree.privWsMB)")
      $e = $r.barOnlyEstimate
      $md.Add("  bar-only Electron ESTIMATE ($($e.what)): $($e.processes) procs, $($e.privateMB) / $($e.wsMB) / $($e.privWsMB)")
      if ($r.firstPaint.bar) { $md.Add("  bar marks (ms from launch): " + (($r.firstPaint.bar.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value)" }) -join ', ')) }
    }
  }
}
if ($results.modes) {
  $md.Add('| mode | build s | folder MB | files | warnings (trim/AOT) | first frame ms, 3 launches (median) | pixels visible ms (median) | private MB | WS MB | private WS MB | CPU 60 s | works |')
  $md.Add('|---|---|---|---|---|---|---|---|---|---|---|---|')
  foreach ($m in $results.modes) {
    $u = $m.run
    $ff = (@($m.launches | ForEach-Object { if ($null -ne $_.firstFrameMs) { $_.firstFrameMs } else { '-' } }) -join ', ') + " ($($m.firstFrameMedianMs))"
    $pv = (@($m.launches | ForEach-Object { if ($null -ne $_.pixelsVisibleMs) { $_.pixelsVisibleMs } else { '-' } }) -join ', ') + " ($($m.pixelsVisibleMedianMs))"
    if (-not $m.build.ok) { $ff = '-'; $pv = '-' }
    $md.Add("| $($m.mode) | $($m.build.seconds) | $($m.build.folderMB) | $($m.build.files) | $($m.build.warnings) ($($m.build.trimAotWarnings)) | $ff | $pv | $(if ($u) { $u.memUp.privateMB } else { '-' }) | $(if ($u) { $u.memUp.wsMB } else { '-' }) | $(if ($u) { $u.memUp.privWsMB } else { '-' }) | $(if ($u) { $u.cpuUp.pctOfOneCore } else { '-' }) | $($m.verdict) |")
  }
  foreach ($m in $results.modes) {
    $md.Add('')
    $md.Add("$($m.mode): $($m.what); props: $($m.build.props)")
    if ($m.build.warningCodes.Count) { $md.Add("  warning codes: " + (($m.build.warningCodes.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name) x$($_.Value)" }) -join ', ')) }
    if ($m.build.firstErrors.Count) { $m.build.firstErrors | ForEach-Object { $md.Add("  error: $_") } }
    if ($m.patches.Count) { $md.Add("  bench-only patch (temp copy): partial on " + ($m.patches -join ', ')) }
    if ($m.runtime) { $md.Add("  runtime: Windows App Runtime $($m.runtime.installerVersion) from $($m.runtime.url) (exit $($m.runtime.exitCode), $($m.runtime.seconds) s): $($m.runtime.packages -join '; '); .NET: $($m.runtime.dotnetRuntimes)") }
    if ($m.problems.Count) { $md.Add("  findings: $($m.problems -join '; ')") }
  }
}
$md | Set-Content -Path (Join-Path $Out 'summary.md') -Encoding utf8
$md | ForEach-Object { Write-Host $_ }

foreach ($f in $fixtures) { try { Stop-Process -Id $f.Id -Force -ErrorAction Stop } catch { } }
if ('Desk' -as [type]) { [void][Desk]::CloseFolderWindows() }   # the explorer folder fixture (no pid of its own)
$what = if ($results.modes) { 'modes: ' + (($results.modes | ForEach-Object { "$($_.mode) $($_.verdict)" }) -join '; ') } else { "$($results.runs.Count) runs: " + (($results.runs | ForEach-Object { "$($_.app) $($_.theme)" }) -join ', ') }
if ($problems.Count -eq 0) { Write-Host "BENCH: ok ($what)"; exit 0 }
Write-Host "BENCH: FAILED ($what; $($problems.Count) problems: $($problems -join '; '))"
exit 1
