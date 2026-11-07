# glass/check.ps1 - primitive 2, the island's glass. Builds glass/Glass.csproj and, in light and dark,
# runs each way of drawing the glass (cpu = Electron's method ported, gpu = DWM's host backdrop through
# the system compositor, acrylic = DesktopAcrylicController), and for each one:
#   - shoots the bench's rectangle (pill + 40 px each side, 60 above, to the bottom edge) and compares it
#     with Electron's own shot from the bench run (ref/, same runner type, wallpaper and screen size):
#     mean / 95th percentile / max |RGB difference| over the glass only (the pill minus where Electron's
#     bar draws icons and text), over the whole pill, the rim, and in light the shadow band above it;
#   - CPU of the app's process tree and of dwm.exe, idle for 30 s and for 30 s while the wallpaper swaps
#     every second, and whether (and how fast) the glass follows each swap.
#   pwsh -NoProfile -File native/spikes/windowing/glass/check.ps1 [-Seconds 30]
param([int]$Seconds = 30)
. (Join-Path $PSScriptRoot '..\harness\Common.ps1') -Lane glass
Add-Type -Path (Join-Path $PSScriptRoot 'tools\GlassTools.cs')
$Ref = Join-Path $PSScriptRoot 'ref'

function Read-Png([string]$path) {
  $bmp = [System.Drawing.Bitmap]::new($path)
  $r = [System.Drawing.Rectangle]::new(0, 0, $bmp.Width, $bmp.Height)
  $d = $bmp.LockBits($r, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $px = [byte[]]::new($bmp.Width * $bmp.Height * 4)
  [Runtime.InteropServices.Marshal]::Copy($d.Scan0, $px, 0, $px.Length)
  $bmp.UnlockBits($d)
  $o = @{ w = $bmp.Width; h = $bmp.Height; px = $px }
  $bmp.Dispose()
  return $o
}
function Write-Png([byte[]]$px, [int]$w, [int]$h, [string]$name) {
  $bmp = [System.Drawing.Bitmap]::new($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppRgb)
  $r = [System.Drawing.Rectangle]::new(0, 0, $w, $h)
  $d = $bmp.LockBits($r, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppRgb)
  [Runtime.InteropServices.Marshal]::Copy($px, 0, $d.Scan0, $px.Length)
  $bmp.UnlockBits($d)
  $p = Join-Path $Out $name
  $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  return $p
}
# a grab as ONE byte[] (Common.ps1's Grab returns through the pipeline, which unrolls it into object[])
function G($r) { return , ([WinHarness]::Grab([int]$r.x, [int]$r.y, [int]$r.w, [int]$r.h)) }
function Fmt($s) { "mean $($s[0]), p95 $($s[1]), max $($s[2]) ($($s[3]) px)" }
function StatObj($s) { [ordered]@{ mean = $s[0]; p95 = $s[1]; max = $s[2]; pixels = $s[3] } }

# CPU of several process trees over the same stretch of time (% of ONE core each)
function Cpu-Begin([hashtable]$roots) {
  $snap = @{ sw = [Diagnostics.Stopwatch]::StartNew(); start = @{} }
  foreach ($k in $roots.Keys) {
    $t = @{}
    foreach ($p in [WinHarness]::Tree($roots[$k])) { try { $t[$p] = [System.Diagnostics.Process]::GetProcessById($p).TotalProcessorTime.TotalMilliseconds } catch { } }
    $snap.start[$k] = @{ root = $roots[$k]; t = $t }
  }
  return $snap
}
function Cpu-End($snap) {
  $wall = $snap.sw.Elapsed.TotalMilliseconds
  $o = [ordered]@{ seconds = [Math]::Round($wall / 1000, 1) }
  foreach ($k in $snap.start.Keys) {
    $used = 0.0
    foreach ($p in [WinHarness]::Tree($snap.start[$k].root)) {
      try { $now = [System.Diagnostics.Process]::GetProcessById($p).TotalProcessorTime.TotalMilliseconds } catch { continue }
      $used += $now - $(if ($snap.start[$k].t.ContainsKey($p)) { $snap.start[$k].t[$p] } else { 0 })
    }
    $o[$k] = [Math]::Round(100 * $used / $wall, 2)
  }
  return $o
}

# ------------------------------------------------------------------ build
$exe = Build-Spike 'glass\Glass.csproj'
Check 'build' ($null -ne $exe) $(if ($exe) { "$($Results.build_Glass.seconds) s, $($Results.build_Glass.folderMB) MB" } else { 'see build-Glass.log' })
if (-not $exe) { Finish-Check }

# ------------------------------------------------------------------ the bench's desktop
[WinHarness]::TaskbarAutoHide($true)
$moved = [WinHarness]::ClearBottom($Screen.Width, $Screen.Height, 240)
[WinHarness]::SetCursorPos([int]($Screen.Width / 2), [int]($Screen.Height / 3)) | Out-Null
Start-Sleep -Seconds 2
Say "taskbar auto-hide on; $moved window(s) moved off the bottom"
$wpA = Join-Path $RepoRoot 'assets\backgrounds\snow-peak.jpg'
$wpB = Join-Path $RepoRoot 'assets\backgrounds\desert-dunes.jpg'

$pillW = [Math]::Min(1120, $Screen.Width - 28)
$pill = @{ x = [int](($Screen.Width - $pillW) / 2); y = $Screen.Height - 14 - 48; w = $pillW; h = 48 }
$shot = @{ x = $pill.x - 40; y = $pill.y - 60; w = $pill.w + 80; h = $Screen.Height - ($pill.y - 60) }
$inShot = @{ x = 40; y = 60 }                            # the pill in the shot's coordinates
$follow = @{ x = $pill.x + 100; y = $pill.y + 12; w = 300; h = 24 }      # glass only (left of the dock)
$wallRect = @{ x = $pill.x + 100; y = $pill.y - 50; w = 300; h = 10 }    # bare wallpaper above the island

$refHidden = Read-Png (Join-Path $Ref 'electron-hidden.png')
$refBar = @{ light = (Read-Png (Join-Path $Ref 'electron-light-bar.png')); dark = (Read-Png (Join-Path $Ref 'electron-dark-bar.png')) }
$recipeDark = Read-Png (Join-Path $Ref 'recipe-dark.png')
Check 'shot rectangle = the bench''s' ($shot.w -eq $refHidden.w -and $shot.h -eq $refHidden.h) "here $($shot.w)x$($shot.h) at $($shot.x),$($shot.y); reference $($refHidden.w)x$($refHidden.h)"

$hidden = G $shot
Write-Png $hidden $shot.w $shot.h 'hidden.png' | Out-Null
$all = [bool[]]::new($shot.w * $shot.h); for ($i = 0; $i -lt $all.Length; $i++) { $all[$i] = $true }
$hs = [GlassTools]::Stats($hidden, $refHidden.px, $all)
Note 'hiddenVsReference' (StatObj $hs)
Check 'the screen behind the island = the reference run''s' ($hs[0] -le 2) ("bare wallpaper here vs the bench run's: " + (Fmt $hs) + ' (pass: mean <= 2)')

$masks = @{}
foreach ($t in 'light', 'dark') {
  $r = if ($t -eq 'light') { 24 } else { 18 }
  $masks[$t] = @{
    pill = [GlassTools]::Mask($shot.w, $shot.h, $inShot.x, $inShot.y, $pill.w, $pill.h, $r, 0)
    glass = [GlassTools]::Mask($shot.w, $shot.h, $inShot.x, $inShot.y, $pill.w, $pill.h, $r, 1)
    halo = [GlassTools]::Mask($shot.w, $shot.h, $inShot.x, $inShot.y, $pill.w, $pill.h, $r, 2)
    rim = [GlassTools]::Mask($shot.w, $shot.h, $inShot.x, $inShot.y, $pill.w, $pill.h, $r, 3)
  }
}
$dwm = Get-Process dwm | Where-Object SessionId -eq (Get-Process -Id $PID).SessionId | Select-Object -First 1
Say "dwm pid $($dwm.Id)"

# The changing backdrop: the wallpaper swaps every second for $Seconds s. After each swap, poll the bare
# wallpaper above the island and the glass (3 grabs in a row must agree, so a grab that lands inside the cpu
# mode's 30 ms capture exclusion cannot pass for the glass following). Returns lags in ms.
function Run-Changing([hashtable]$roots) {
  $snap = Cpu-Begin $roots
  $t0 = [Diagnostics.Stopwatch]::StartNew()
  $swaps = 0; $wallLag = @(); $glassLag = @(); $missed = 0
  $useB = $true
  while ($t0.Elapsed.TotalSeconds -lt $Seconds - 1) {
    $tick = [Diagnostics.Stopwatch]::StartNew()
    $g0 = [GlassTools]::Mean((G $follow)); $w0 = [GlassTools]::Mean((G $wallRect))
    [WinHarness]::SetWallpaper($(if ($useB) { $wpB } else { $wpA })) | Out-Null
    $useB = -not $useB; $swaps++
    $wl = $null; $gl = $null; $run = 0
    while ($tick.ElapsedMilliseconds -lt 950) {
      if ($null -eq $wl -and [Math]::Abs([GlassTools]::Mean((G $wallRect)) - $w0) -gt 6) { $wl = $tick.ElapsedMilliseconds }
      if ($null -eq $gl) {
        if ([Math]::Abs([GlassTools]::Mean((G $follow)) - $g0) -gt 4) { $run++; if ($run -ge 3) { $gl = $tick.ElapsedMilliseconds } } else { $run = 0 }
      }
      if ($null -ne $wl -and $null -ne $gl) { break }
      Start-Sleep -Milliseconds 25
    }
    if ($null -ne $wl) { $wallLag += $wl }
    if ($null -ne $gl) { $glassLag += $gl } else { $missed++ }
    while ($tick.ElapsedMilliseconds -lt 1000) { Start-Sleep -Milliseconds 10 }
  }
  $cpu = Cpu-End $snap
  [WinHarness]::SetWallpaper($wpA) | Out-Null
  Start-Sleep -Milliseconds 1500
  $med = { param($a) if ($a.Count) { ($a | Sort-Object)[[int][Math]::Floor($a.Count / 2)] } else { $null } }
  return [ordered]@{
    cpu = $cpu; swaps = $swaps; wallpaperPaintedMs = (& $med $wallLag)
    glassFollowed = $glassLag.Count; glassMissed = $missed; glassMedianMs = (& $med $glassLag); glassMaxMs = $(if ($glassLag.Count) { ($glassLag | Measure-Object -Maximum).Maximum } else { $null })
  }
}

# ------------------------------------------------------------------ baseline: no island
Set-WinTheme light
Start-Sleep -Seconds 1
$snap = Cpu-Begin @{ dwm = $dwm.Id }
Start-Sleep -Seconds $Seconds
$base = [ordered]@{ idle = (Cpu-End $snap) }
$base.changing = Run-Changing @{ dwm = $dwm.Id }
Note 'baseline' $base

# ------------------------------------------------------------------ the runs
$runs = @(
  @{ theme = 'light'; mode = 'cpu' }, @{ theme = 'light'; mode = 'gpu' }, @{ theme = 'light'; mode = 'gpu'; extra = '--raw'; tag = 'gpu-raw'; quick = $true },
  @{ theme = 'light'; mode = 'acrylic' }, @{ theme = 'dark'; mode = 'cpu' }, @{ theme = 'dark'; mode = 'gpu' }, @{ theme = 'dark'; mode = 'acrylic' }
)
$Results.runs = [ordered]@{}
foreach ($run in $runs) {
  $t = $run.theme; $tag = if ($run.tag) { $run.tag } else { $run.mode }
  $name = "$t-$tag"
  if ((Get-ItemProperty -Path $Personalize).AppsUseLightTheme -ne $(if ($t -eq 'light') { 1 } else { 0 })) { Set-WinTheme $t; Start-Sleep -Seconds 2 }
  $dir = Join-Path $Out $name
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $argv = @("--out=`"$dir`"", "--mode=$($run.mode)", "--theme=$t")
  if ($run.sigma) { $argv += "--sigma=$($run.sigma)" }
  if ($run.extra) { $argv += $run.extra }
  Say "start $name"
  $t0 = NowMs
  $p = Start-Process -FilePath $exe -ArgumentList $argv -PassThru
  $ready = Wait-Json (Join-Path $dir 'ready.json') 30
  $ok = $null -ne $ready -and ($ready.state -notmatch 'FAILED')
  Check "$name ready" $ok $(if ($ready) { "after $((NowMs) - $t0) ms; $($ready.state); style $($ready.style) ex $($ready.exStyle); window $($ready.window | ConvertTo-Json -Compress)" } else { 'no ready.json in 30 s' })
  $res = [ordered]@{ ready = $ready }
  if ($ready) {
    Start-Sleep -Milliseconds 1500
    $pause = Join-Path $dir 'pause'
    New-Item -ItemType File -Force -Path $pause | Out-Null   # cpu mode: no capture exclusion during the shot
    Start-Sleep -Milliseconds 400
    $g = G $shot
    Remove-Item $pause -Force
    Write-Png $g $shot.w $shot.h "native-$name.png" | Out-Null
    $E = $refBar[$t].px
    Write-Png ([GlassTools]::SideBySide($E, $g, $shot.w, $shot.h, 4)) $shot.w (3 * $shot.h + 8) "sbs-$name.png" | Out-Null
    $m = $masks[$t]
    $sGlass = [GlassTools]::Stats($g, $E, $m.glass); $sPill = [GlassTools]::Stats($g, $E, $m.pill); $sRim = [GlassTools]::Stats($g, $E, $m.rim)
    $res.vsElectron = [ordered]@{ glass = (StatObj $sGlass); pill = (StatObj $sPill); rim = (StatObj $sRim) }
    $res.meanRgb = [ordered]@{ native = [GlassTools]::MeanRgb($g, $m.glass); electron = [GlassTools]::MeanRgb($E, $m.glass); bare = [GlassTools]::MeanRgb($hidden, $m.glass) }
    $drawn = [GlassTools]::Stats($g, $hidden, $m.glass)
    # (a claim only for the cpu mode: a gpu backdrop that comes out black also differs from the wallpaper)
    if ($run.mode -eq 'cpu') { Check "$name glass drawn" ($drawn[0] -ge 5) ("glass vs the bare wallpaper under it: " + (Fmt $drawn) + ' (pass: mean >= 5)') }
    else { Note "${name}_vsBare" ("glass vs the bare wallpaper under it: " + (Fmt $drawn)) }
    if ($t -eq 'light') {
      $sHalo = [GlassTools]::Stats($g, $E, $m.halo)
      $res.vsElectron.shadowBand = StatObj $sHalo
      $line = "glass only " + (Fmt $sGlass) + "; whole pill " + (Fmt $sPill) + "; rim " + (Fmt $sRim) + "; shadow band " + (Fmt $sHalo)
      if ($run.mode -eq 'cpu') { Check "$name matches Electron's light glass" ($sGlass[0] -le 3) "$line (pass: glass-only mean <= 3)" }
      else { Note "${name}_vsElectron" $line }
    } else {
      $sRecipe = [GlassTools]::Stats($g, $recipeDark.px, $m.glass)
      $sRecipePill = [GlassTools]::Stats($g, $recipeDark.px, $m.pill)
      $res.vsRecipe = [ordered]@{ glass = (StatObj $sRecipe); pill = (StatObj $sRecipePill) }
      $line = "vs Electron's dark recipe over this wallpaper: glass only " + (Fmt $sRecipe) + ", whole pill " + (Fmt $sRecipePill) + "; vs the Electron dark shot as is (its still held a grey band, see README): glass only " + (Fmt $sGlass) + ", rim " + (Fmt $sRim)
      if ($run.mode -eq 'cpu') { Check "$name matches Electron's dark recipe" ($sRecipe[0] -le 3) "$line (pass: glass-only mean <= 3 vs the recipe)" }
      else { Note "${name}_vsElectron" $line }
    }
    # CPU: idle with the island up, then the wallpaper swapping every second (not for a diagnostic run)
    if ($run.quick) { $res.mem = Get-Mem $p.Id; Get-Content (Join-Path $dir 'log.txt') -ErrorAction SilentlyContinue | Select-Object -Last 8 | ForEach-Object { Say "app ${name}: $_" }; Stop-Tree $p.Id; $Results.runs[$name] = $res; Start-Sleep -Seconds 1; continue }
    $roots = @{ app = $p.Id; dwm = $dwm.Id }
    $snap = Cpu-Begin $roots
    Start-Sleep -Seconds $Seconds
    $res.idle = Cpu-End $snap
    $res.changing = Run-Changing $roots
    Note "${name}_cpu" ([ordered]@{ idle = $res.idle; changing = $res.changing })
    if ($run.mode -eq 'cpu' -and $t -eq 'light') {
      $c = $res.changing
      Check "$name glass follows the screen" ($null -ne $c.glassMedianMs -and $c.glassFollowed -ge [Math]::Ceiling(0.8 * $c.swaps) -and $c.glassMedianMs -le 600) "followed $($c.glassFollowed) of $($c.swaps) wallpaper swaps, median $($c.glassMedianMs) ms after the swap (wallpaper itself painted at median $($c.wallpaperPaintedMs) ms); pass: >= 80 % within a median of 600 ms"
    }
    $res.mem = Get-Mem $p.Id
  }
  Get-Content (Join-Path $dir 'log.txt') -ErrorAction SilentlyContinue | Select-Object -Last 12 | ForEach-Object { Say "app ${name}: $_" }
  Stop-Tree $p.Id
  $Results.runs[$name] = $res
  Start-Sleep -Seconds 1
}
Set-WinTheme light
Finish-Check
