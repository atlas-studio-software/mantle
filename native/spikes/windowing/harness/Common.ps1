# Common.ps1 - shared harness for the windowing checks. RUNNER ONLY: it changes the screen size,
# the wallpaper, the theme and the taskbar, and moves the cursor. Each check script dot-sources it:
#
#   . (Join-Path $PSScriptRoot '..\harness\Common.ps1') -Lane popup
#
# and ends with Finish-Check, whose last line is the one to judge the run by:
#   WINDOWING <lane>: ok (N of N checks)      or      WINDOWING <lane>: FAILED (...)
#
# Conventions every spike app follows: it takes --out=<dir> and writes log.txt plus its own JSON
# (ready.json and so on) there; the check script reads those and the screen, never the app's memory.
param(
  [Parameter(Mandatory)] [string]$Lane,
  [string]$Resolution = '1920x1080',
  [string]$Wallpaper = 'snow-peak.jpg'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$WinRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path        # native/spikes/windowing
$RepoRoot = (Resolve-Path (Join-Path $WinRoot '..\..\..')).Path
$Out = Join-Path $WinRoot "out\$Lane"
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Problems = [System.Collections.Generic.List[string]]::new()
$Checks = [System.Collections.Generic.List[object]]::new()
$Results = [ordered]@{}

function Say($m) { Write-Host ("{0} {1}: {2}" -f $Lane, (Get-Date -Format 'HH:mm:ss'), $m) }
function Problem($m) { $Problems.Add([string]$m); Write-Host "$Lane PROBLEM: $m" }
function NowMs { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }

# One pass/fail line per claim. $detail should carry the numbers the claim rests on.
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  $Checks.Add([ordered]@{ name = $name; ok = $ok; detail = $detail })
  Write-Host ("{0} check {1} {2}: {3}" -f $Lane, $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}
# A measured number or object for results.json (no pass/fail).
function Note([string]$key, $value) { $Results[$key] = $value; Write-Host ("{0} note {1} = {2}" -f $Lane, $key, ($value | ConvertTo-Json -Compress -Depth 6)) }

Add-Type -Path (Join-Path $PSScriptRoot 'Native.cs')
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

# ------------------------------------------------------------------ the runner
if ($Resolution -match '^(\d+)x(\d+)$') {
  $res = [WinHarness]::SetResolution([int]$Matches[1], [int]$Matches[2])
  Start-Sleep -Milliseconds 800
  Say "display: SetResolution($Resolution) -> $res (0 = ok)"
}
$Screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$os = Get-CimInstance Win32_OperatingSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$Results.runner = [ordered]@{
  os = "$($os.Caption) $($os.Version)"; cpu = $cpu.Name; cores = [Environment]::ProcessorCount
  ramGB = [Math]::Round($os.TotalVisibleMemorySize / 1MB, 1); screen = "$($Screen.Width)x$($Screen.Height)"
  monitors = @([System.Windows.Forms.Screen]::AllScreens).Count
}
Say ("runner: " + ($Results.runner | ConvertTo-Json -Compress))

$Personalize = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
New-Item -Force -Path $Personalize | Out-Null
Set-ItemProperty -Path $Personalize -Name EnableTransparency -Value 1 -Type DWord   # a server image may ship with it off
function Set-WinTheme([ValidateSet('light', 'dark')] [string]$t) {
  $v = if ($t -eq 'light') { 1 } else { 0 }
  Set-ItemProperty -Path $Personalize -Name AppsUseLightTheme -Value $v -Type DWord
  Set-ItemProperty -Path $Personalize -Name SystemUsesLightTheme -Value $v -Type DWord
}
if ($Wallpaper) {
  $wp = Join-Path $RepoRoot "assets\backgrounds\$Wallpaper"
  Say ("wallpaper ${Wallpaper}: " + [WinHarness]::SetWallpaper($wp))
}

# ------------------------------------------------------------------ build
# dotnet publish one spike app (path relative to native/spikes/windowing). Returns the exe path, or
# $null after printing the compiler errors (so a failed cloud run says why without the artifacts).
$script:Tfm = $null
function Build-Spike([string]$csproj) {
  if (-not $script:Tfm) {
    $sdks = @(& dotnet --list-sdks 2>$null)
    Say ("dotnet SDKs: " + ($sdks -join ' | '))
    $script:Tfm = if ($sdks -match '^10\.') { 'net10.0' } elseif ($sdks -match '^9\.') { 'net9.0' } else { 'net8.0' }
  }
  $proj = Join-Path $WinRoot $csproj
  $name = [IO.Path]::GetFileNameWithoutExtension($proj)
  $bin = Join-Path $WinRoot "bin\$name"
  $log = Join-Path $Out "build-$name.log"
  $t0 = Get-Date
  & dotnet publish $proj -c Release -r win-x64 --self-contained true "-p:SpikeTfm=$($script:Tfm)" -p:PublishReadyToRun=true -o $bin -nologo -clp:NoSummary *> $log
  $code = $LASTEXITCODE
  $secs = [Math]::Round(((Get-Date) - $t0).TotalSeconds)
  $exe = Join-Path $bin "$name.exe"
  if ($code -ne 0 -or -not (Test-Path $exe)) {
    $errs = @(Select-String -Path $log -Pattern ': error ' | ForEach-Object { $_.Line.Trim() } | Select-Object -Unique | Select-Object -First 40)
    if ($errs.Count -eq 0) { $errs = @(Get-Content $log -Tail 30) }
    $errs | ForEach-Object { Write-Host "  $_" }
    Problem "build $name failed (exit $code, $($script:Tfm), ${secs}s): $($errs.Count) error line(s) above, full log build-$name.log"
    return $null
  }
  $mb = [Math]::Round(((Get-ChildItem -Recurse -File $bin | Measure-Object Length -Sum).Sum) / 1MB, 1)
  Say "built $name ($($script:Tfm)) in ${secs}s, $mb MB folder"
  $Results["build_$name"] = [ordered]@{ tfm = $script:Tfm; seconds = $secs; folderMB = $mb }
  return $exe
}

# ------------------------------------------------------------------ screen
# Rects are hashtables @{ x; y; w; h } in physical pixels.
function Save-Shot([string]$name, $r) {
  $bmp = [System.Drawing.Bitmap]::new([int]$r.w, [int]$r.h)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int]$r.x, [int]$r.y, 0, 0, [System.Drawing.Size]::new([int]$r.w, [int]$r.h))
  $p = Join-Path $Out $name
  $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
  Say "shot $name ($($r.w)x$($r.h) at $($r.x),$($r.y))"
  return $p
}
function Save-Screen([string]$name) {
  $s = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  return Save-Shot $name @{ x = $s.X; y = $s.Y; w = $s.Width; h = $s.Height }
}
# BGRA bytes of a screen rect (WinHarness.Grab: BitBlt SRCCOPY|CAPTUREBLT of the composed screen).
function Grab($r) { return [WinHarness]::Grab([int]$r.x, [int]$r.y, [int]$r.w, [int]$r.h) }

# ------------------------------------------------------------------ processes
function Start-Spike([string]$exe, [string[]]$argv) {
  $all = @("--out=`"$Out`"") + $argv
  Say ("start $([IO.Path]::GetFileName($exe)) " + ($all -join ' '))
  return Start-Process -FilePath $exe -ArgumentList $all -PassThru
}
# Wait for a JSON file the app writes; returns the parsed object or $null on timeout.
function Wait-Json([string]$path, [double]$timeoutS = 20) {
  $end = (Get-Date).AddSeconds($timeoutS)
  while ((Get-Date) -lt $end) {
    if (Test-Path $path) {
      try { return Get-Content -Raw $path | ConvertFrom-Json } catch { }   # half-written: retry
    }
    Start-Sleep -Milliseconds 50
  }
  return $null
}
function Get-Mem([int]$root) {
  $pids = [WinHarness]::Tree($root)
  $raw = @{}
  Get-CimInstance Win32_PerfRawData_PerfProc_Process | ForEach-Object { $raw[[int]$_.IDProcess] = [long]$_.WorkingSetPrivate }
  $rows = @(foreach ($p in $pids) {
      try { $pr = [System.Diagnostics.Process]::GetProcessById($p) } catch { continue }
      [pscustomobject]@{ pid = $p; name = $pr.ProcessName
        privateMB = [Math]::Round($pr.PrivateMemorySize64 / 1MB, 1); wsMB = [Math]::Round($pr.WorkingSet64 / 1MB, 1)
        privWsMB = [Math]::Round(($raw[$p]) / 1MB, 1) }
    })
  return [ordered]@{
    processes = $rows.Count
    privateMB = [Math]::Round(($rows | Measure-Object privateMB -Sum).Sum, 1)
    wsMB = [Math]::Round(($rows | Measure-Object wsMB -Sum).Sum, 1)
    privWsMB = [Math]::Round(($rows | Measure-Object privWsMB -Sum).Sum, 1)
  }
}
# CPU of a process tree over $seconds, as % of ONE core.
function Get-Cpu([int]$root, [double]$seconds) {
  $start = @{}
  foreach ($p in [WinHarness]::Tree($root)) { try { $start[$p] = [System.Diagnostics.Process]::GetProcessById($p).TotalProcessorTime.TotalMilliseconds } catch { } }
  $sw = [Diagnostics.Stopwatch]::StartNew()
  Start-Sleep -Milliseconds ([int]($seconds * 1000))
  $wall = $sw.Elapsed.TotalMilliseconds
  $used = 0.0
  foreach ($p in [WinHarness]::Tree($root)) {
    try { $now = [System.Diagnostics.Process]::GetProcessById($p).TotalProcessorTime.TotalMilliseconds } catch { continue }
    $used += $now - $(if ($start.ContainsKey($p)) { $start[$p] } else { 0 })
  }
  return [ordered]@{ seconds = [Math]::Round($wall / 1000, 1); cpuMs = [Math]::Round($used); pctOfOneCore = [Math]::Round(100 * $used / $wall, 3) }
}
function Stop-Tree([int]$root) {
  foreach ($p in ([WinHarness]::Tree($root) | Sort-Object -Descending)) { try { Stop-Process -Id $p -Force -ErrorAction Stop } catch { } }
}

# ------------------------------------------------------------------ the end
function Finish-Check {
  $failed = @($Checks | Where-Object { -not $_.ok })
  $Results.checks = $Checks
  $Results.problems = $Problems
  $Results | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 (Join-Path $Out 'results.json')
  $md = @("# windowing / $Lane", '', '| check | result | detail |', '|---|---|---|')
  $md += $Checks | ForEach-Object { "| $($_.name) | $(if ($_.ok) { 'PASS' } else { 'FAIL' }) | $($_.detail -replace '\|', '/') |" }
  if ($Problems.Count) { $md += @('', '## problems', '') + ($Problems | ForEach-Object { "- $_" }) }
  $md | Set-Content -Encoding utf8 (Join-Path $Out 'summary.md')
  if ($failed.Count -eq 0 -and $Problems.Count -eq 0 -and $Checks.Count -gt 0) {
    Write-Host "WINDOWING ${Lane}: ok ($($Checks.Count) of $($Checks.Count) checks)"
    exit 0
  }
  $why = @()
  if ($failed.Count) { $why += "$($failed.Count) of $($Checks.Count) checks failed: " + (($failed | ForEach-Object { $_.name }) -join ', ') }
  if ($Problems.Count) { $why += "$($Problems.Count) problem(s): " + ($Problems -join '; ') }
  if ($Checks.Count -eq 0) { $why += 'no checks ran' }
  Write-Host "WINDOWING ${Lane}: FAILED ($($why -join ' | '))"
  exit 1
}
