# smoke/check.ps1 - the harness smoke run: build one WinUI 3 window with the spike's see-through
# setup, show it, and measure what it draws outside its rounded shape (the spike's frame line).
#   pwsh -NoProfile -File native/spikes/windowing/smoke/check.ps1
. (Join-Path $PSScriptRoot '..\harness\Common.ps1') -Lane smoke

$exe = Build-Spike 'smoke\Smoke.csproj'
Check 'build' ($null -ne $exe) $(if ($exe) { $Results.build_Smoke.seconds.ToString() + ' s' } else { 'see build-Smoke.log' })
if (-not $exe) { Finish-Check }

$win = @{ x = 300; y = 300; w = 400; h = 200 }
$m = 8
$area = @{ x = $win.x - $m; y = $win.y - $m; w = $win.w + 2 * $m; h = $win.h + 2 * $m }
Start-Sleep -Seconds 2
$before = Grab $area
$beforeExact = Grab $win
Save-Shot 'before.png' $area | Out-Null

$t0 = NowMs
$p = Start-Spike $exe @("--x=$($win.x)", "--y=$($win.y)", "--w=$($win.w)", "--h=$($win.h)")
$ready = Wait-Json (Join-Path $Out 'ready.json') 30
Check 'ready.json' ($null -ne $ready) $(if ($ready) { "after $((NowMs) - $t0) ms; window $($ready.window | ConvertTo-Json -Compress); style $($ready.style) ex $($ready.exStyle)" } else { 'no ready.json in 30 s' })
if ($ready) {
  Note 'ready' $ready
  Start-Sleep -Milliseconds 500
  $after = Grab $area
  $afterExact = Grab $win
  Save-Shot 'after.png' $area | Out-Null
  Save-Screen 'screen.png' | Out-Null
  # the shape in the grab's coordinates
  $s = $ready.shape
  $sx = $s.x - $area.x; $sy = $s.y - $area.y
  $r = [WinHarness]::ChangedOutside($before, $after, $area.w, $area.h, $sx, $sy, $s.w, $s.h, $s.r, 8, 2)
  Note 'strayOutsideShape' ([ordered]@{ changed = $r[0]; maxDelta = $r[2]; firstX = $r[3]; firstY = $r[4]; area = "$($area.w)x$($area.h) incl. ${m}px margin" })
  $e = [WinHarness]::ChangedOutside($beforeExact, $afterExact, $win.w, $win.h, $s.x - $win.x, $s.y - $win.y, $s.w, $s.h, $s.r, 8, 2)
  Note 'strayWindowRect' ([ordered]@{ changed = $e[0]; onOutermostRing = $e[1]; ringPixels = 2 * ($win.w + $win.h) - 4; maxDelta = $e[2] })
  # the shape's centre must be the opaque blue it was given (32, 96, 255)
  $cx = [int]($area.w / 2); $cy = [int]($area.h / 2); $i = ($cy * $area.w + $cx) * 4
  $bgr = "$($after[$i+2]),$($after[$i+1]),$($after[$i])"
  Check 'shape drawn' ([Math]::Abs($after[$i] - 255) -le 6 -and [Math]::Abs($after[$i + 2] - 32) -le 6) "centre rgb $bgr (want 32,96,255)"
  Get-Content (Join-Path $Out 'log.txt') -ErrorAction SilentlyContinue | ForEach-Object { Say "app: $_" }
}
Stop-Tree $p.Id
Finish-Check
