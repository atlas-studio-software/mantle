# bar/check.ps1 - primitive 3, bar placement + reveal (RUNNER ONLY: it moves the cursor, puts the
# taskbar on auto-hide and reserves screen space; everything is put back at the end).
#   pwsh -NoProfile -File native/spikes/windowing/bar/check.ps1
# Per anim (comp | swp | swpflush): hidden at rest, CPU at rest, reveal + hide traced from the screen,
# CPU over reveal cycles. On comp: topmost (normal / earlier / later topmost windows), fullscreen
# block, maximised not blocking, the taskbar's auto-hide and its restore (clean and killed), and dock.
. (Join-Path $PSScriptRoot '..\harness\Common.ps1') -Lane bar
Add-Type -Path (Join-Path $PSScriptRoot 'probe\BarProbe.cs')
Say ("probe: " + [BarProbe]::Aware())

function J($s) { $s | ConvertFrom-Json }
function Events($tag) { $f = Join-Path $Out "$tag-events.jsonl"; if (Test-Path $f) { @(Get-Content $f | ForEach-Object { $_ | ConvertFrom-Json }) } else { @() } }
function Wait-Ev($tag, $ev, $since, [double]$timeoutS = 5) {
  $end = (Get-Date).AddSeconds($timeoutS)
  while ((Get-Date) -lt $end) { $e = @(Events $tag | Where-Object { $_.ev -eq $ev -and $_.t -ge $since }); if ($e.Count) { return $e[0] }; Start-Sleep -Milliseconds 40 }
  return $null
}
function Start-Bar($tag, [string[]]$more) {
  $p = Start-Spike $exe (@("--tag=$tag") + $more)
  return @{ p = $p; tag = $tag; r = (Wait-Json (Join-Path $Out "$tag-ready.json") 40) }
}
function Stop-Bar($b) {
  [BarProbe]::Close($b.r.hwnd) | Out-Null
  $x = Wait-Json (Join-Path $Out "$($b.tag)-exit.json") 10
  if (-not $x) { Problem "$($b.tag): no exit.json 10 s after WM_CLOSE; killed"; Stop-Tree $b.p.Id }
  Start-Sleep -Milliseconds 300
  return $x
}
function TreeMs([int]$id) { $s = 0.0; foreach ($q in [WinHarness]::Tree($id)) { try { $s += [Diagnostics.Process]::GetProcessById($q).TotalProcessorTime.TotalMilliseconds } catch { } }; $s }
function DwmMs { (Get-Process dwm -ErrorAction SilentlyContinue | ForEach-Object { $_.TotalProcessorTime.TotalMilliseconds } | Measure-Object -Sum).Sum }
function Rgb($bytes, $i) { "$($bytes[$i+2]),$($bytes[$i+1]),$($bytes[$i])" }
function App-Log($tag) { Get-Content (Join-Path $Out "$tag-log.txt") -ErrorAction SilentlyContinue | Select-Object -Last 12 | ForEach-Object { Say "app ${tag}: $_" } }

$exe = Build-Spike 'bar\Bar.csproj'
Check 'build' ($null -ne $exe) $(if ($exe) { "$($Results.build_Bar.seconds) s" } else { 'see build-Bar.log' })
if (-not $exe) { Finish-Check }

$S = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$MB = $S.Y + $S.Height
$away = @{ x = [int]($S.Width / 2); y = 300 }
# the geometry main.js would give at 100 %: min(1120, w - 28) x 48, 14 above the edge
$pw = [Math]::Min(1120, $S.Width - 28); $px = $S.X + [int](($S.Width - $pw) / 2)
$want = @{ x = $px; y = $MB - 14 - 48; w = $pw; h = 48 }
$area = @{ x = $want.x - 8; y = $want.y - 40; w = $want.w + 16; h = $MB - ($want.y - 40) }
$tb0 = [BarProbe]::TaskbarState(); $wa0 = J ([BarProbe]::WorkArea())
Note 'baseline' ([ordered]@{ taskbarState = $tb0; workArea = $wa0; monitors = (J ([BarProbe]::Monitors())) })

# the backdrop for every trace: a MAXIMISED normal window (grey), in front
[BarProbe]::Cursor($away.x, $away.y) | Out-Null
$mx = [BarProbe]::Fixture('Maximised fixture', 120, 120, 130, 100, 100, 800, 600, $false, 'max')
Start-Sleep -Milliseconds 600
Check 'fixture: maximised window in front' ([BarProbe]::ForceForeground($mx)) "hwnd $mx frame $([BarProbe]::FrameRect($mx)) foreground $([BarProbe]::Foreground())"
$shotBefore = Grab $area

$perf = [ordered]@{}
foreach ($a in 'comp', 'swp', 'swpflush') {
  [BarProbe]::Cursor($away.x, $away.y) | Out-Null
  $b = Start-Bar $a @("--anim=$a", '--trace-frames=2')
  $r = $b.r
  Check "$a ready" ($null -ne $r) $(if ($r) { "first frame $($r.firstFrameMs) ms; style $($r.styleBefore) -> $($r.styleAfter), ex $($r.exStyle)" } else { 'no ready.json in 40 s' })
  if (-not $r) { App-Log $a; Stop-Tree $b.p.Id; continue }
  $pill = $r.pill; $hwnd = [long]$r.hwnd
  $cx = [int]($pill.x + $pill.w / 2); $cy = [int]($pill.y + $pill.h / 2)
  $band = @{ x = [int]$r.probeX; y = $MB - 1 }
  if ($a -eq 'comp') {
    Check 'geometry at 100 %' ($pill.x -eq $want.x -and $pill.y -eq $want.y -and $pill.w -eq $want.w -and $pill.h -eq $want.h -and $r.win.h -eq 62 -and $r.bandTop -eq ($MB - 4)) `
      "pill $($pill | ConvertTo-Json -Compress) (want $($want | ConvertTo-Json -Compress)), window $($r.win | ConvertTo-Json -Compress), band y >= $($r.bandTop)"
    $st = [BarProbe]::TaskbarState()
    Check 'taskbar: OS auto-hide only (ABM_SETSTATE), never hidden' ((($st -band 1) -eq 1) -and [BarProbe]::Visible([BarProbe]::Tray())) `
      "state $tb0 -> $st; Shell_TrayWnd visible $([BarProbe]::Visible([BarProbe]::Tray())); app: $($r.taskbar.how)"
  }
  # ---- hidden at rest
  Start-Sleep -Milliseconds 500
  $d = [WinHarness]::Diff($shotBefore, (Grab $area))
  Check "$a hidden at rest" ((-not [BarProbe]::Visible($hwnd)) -and $d -lt 1.5) "window visible $([BarProbe]::Visible($hwnd)); screen under the bar changed by $([Math]::Round($d, 2)) /255 mean"
  $rest = Get-Cpu $b.p.Id 8
  Check "$a CPU hidden at rest" ($rest.pctOfOneCore -lt 0.5) "$($rest.pctOfOneCore) % of one core over $($rest.seconds) s ($($rest.cpuMs) ms; one cursor read per 80 ms)"

  # ---- the reveal, traced from the screen
  $y0 = $pill.y - 40; $h = $MB - $y0
  $t0 = NowMs
  $tr = J ([BarProbe]::Trace($band.x, $y0, $h, $band.x, $band.y, 1400, 40, 16.667))
  $rv = Wait-Ev $a 'revealDone' $t0 3
  $box = J ([BarProbe]::FillBox($area.x, $area.y, $area.w, $area.h, $r.fill[0], $r.fill[1], $r.fill[2], 6))
  Save-Shot "$a-revealed.png" $area | Out-Null
  $ok = $tr.frames -gt 0 -and $box.found -and [Math]::Abs($box.w - $pill.w) -le 4 -and [Math]::Abs($box.y - $pill.y) -le 2
  Check "$a reveal" $ok ("cursor at the edge -> first pixel change $($tr.firstMs) ms, motion $($tr.motionMs) ms (target 560), $($tr.frames) frames seen (~$($tr.expectedFrames) refreshes), " +
    "dropped $($tr.droppedFrames), gaps median $($tr.medianGapMs) / p95 $($tr.p95GapMs) / max $($tr.maxGapMs) ms; sampler $($tr.samples) grabs, mean $($tr.sampleMeanMs) ms; " +
    "pill fill on screen $($box | ConvertTo-Json -Compress); app: revealDone $($rv.ms) ms, swp $($rv.swp | ConvertTo-Json -Compress), xaml $($rv.xaml | ConvertTo-Json -Compress), dwm $($rv.dwm | ConvertTo-Json -Compress)")
  $perf["${a}_reveal"] = [ordered]@{ trace = ($tr | Select-Object * -ExcludeProperty changes); app = $rv }
  $perf["${a}_reveal_changes"] = $tr.changes
  if ($a -eq 'comp') {
    $after = Grab $area
    $sx = $pill.x - $area.x; $sy = $pill.y - $area.y
    $ring = [WinHarness]::ChangedOutside($shotBefore, $after, $area.w, $area.h, $sx, $sy, $pill.w, $pill.h, 18, 8, 2)
    Check 'nothing drawn outside the pill (no frame ring)' ($ring[0] -le 20) "changed pixels outside the rounded pill: $($ring[0]) (max delta $($ring[2]), first at $($ring[3]),$($ring[4]) in the grab)"
  }
  # ---- held at the edge (Windows' auto-hide taskbar may pop under it): still on top
  Start-Sleep -Milliseconds 1200
  $root = [BarProbe]::RootAt($cx, $cy)
  Check "$a on top while held at the edge" ($root -eq $hwnd) "WindowFromPoint(pill centre) = $root ($([BarProbe]::ClassOf($root))), bar $hwnd; Shell_TrayWnd at $([BarProbe]::Rect([BarProbe]::Tray()))"
  [BarProbe]::Cursor($cx, $cy) | Out-Null; Start-Sleep -Milliseconds 700
  Check "$a stays while the cursor is on it" ([BarProbe]::Visible($hwnd) -and -not (Events $a | Where-Object { $_.ev -eq 'hide' -and $_.t -ge $t0 })) "700 ms on the pill: no hide"

  if ($a -eq 'comp') {
    $rev = @(Events $a | Where-Object { $_.ev -eq 'raise' -and $_.t -ge $t0 })
    # a NORMAL window in front (the maximised fixture is the foreground): covered above
    Check 'topmost over a normal foreground window' ($root -eq $hwnd -and [BarProbe]::Foreground() -eq $mx) "foreground = the maximised fixture ($mx); the pill's centre hits the bar"
    # a later HWND_TOPMOST window over the pill while revealed
    $tS = NowMs
    $tm = [BarProbe]::Fixture('Topmost fixture', 200, 40, 40, 0, $MB - 300, $S.Width, 300, $true, 'popup')
    Start-Sleep -Milliseconds 400
    $root2 = [BarProbe]::RootAt($cx, $cy)
    $why = @(Events $a | Where-Object { $_.ev -eq 'raise' -and $_.t -ge $tS } | ForEach-Object { $_.why })
    Check 'topmost: re-raised over a LATER topmost window' ($root2 -eq $hwnd) "topmost fixture $tm shown over the pill; 400 ms later the centre hits $root2 ($([BarProbe]::ClassOf($root2))); raises: $($why -join ' | ')"
    $tS = NowMs; [BarProbe]::ToTop($tm); Start-Sleep -Milliseconds 300
    $root3 = [BarProbe]::RootAt($cx, $cy)
    $n = @(Events $a | Where-Object { $_.ev -eq 'raise' -and $_.t -ge $tS }).Count
    Check 'topmost: re-raised when that window re-asserts HWND_TOPMOST' ($root3 -eq $hwnd) "SetWindowPos(fixture, HWND_TOPMOST) with no show/activate event; 300 ms later the centre hits $root3; $n raise(s) (the 60 ms shown poll)"
    Save-Shot 'comp-over-topmost.png' $area | Out-Null
    $cyc = Get-Cpu $b.p.Id 3
    Note 'comp_cpu_revealed_idle' $cyc
    [BarProbe]::Close($tm) | Out-Null
  }

  # ---- the hide, traced
  $t1 = NowMs
  $th = J ([BarProbe]::Trace($band.x, $y0, $h, $away.x, $away.y, 1200, 40, 16.667))
  $hd = Wait-Ev $a 'hidden' $t1 3
  $hs = Wait-Ev $a 'hide' $t1 1
  Check "$a hide after the grace" ($null -ne $hd -and $tr.frames -gt 0 -and $th.firstMs -ge 170 -and $th.firstMs -le 420 -and -not [BarProbe]::Visible($hwnd)) `
    ("cursor away -> first pixel change $($th.firstMs) ms (grace 180 + poll <= 60), motion $($th.motionMs) ms (comp 260 / swp 320), $($th.frames) frames, dropped $($th.droppedFrames), max gap $($th.maxGapMs) ms; " +
     "app: hide '$($hs.why)', hidden after $(if ($hd) { $hd.t - $t1 } else { '?' }) ms, window visible $([BarProbe]::Visible($hwnd))")
  $perf["${a}_hide"] = [ordered]@{ trace = ($th | Select-Object * -ExcludeProperty changes); app = (Wait-Ev $a 'hideDone' $t1 1) }

  # ---- CPU over reveal cycles (no tracing, no sampler)
  $n = 5; $c0 = TreeMs $b.p.Id; $w0 = DwmMs; $sw = [Diagnostics.Stopwatch]::StartNew()
  for ($i = 0; $i -lt $n; $i++) {
    [BarProbe]::Cursor($band.x, $band.y) | Out-Null; Start-Sleep -Milliseconds 1000
    [BarProbe]::Cursor($away.x, $away.y) | Out-Null; Start-Sleep -Milliseconds 900
  }
  $wall = $sw.Elapsed.TotalMilliseconds
  $cpu = [ordered]@{ cycles = $n; appMsPerCycle = [Math]::Round(((TreeMs $b.p.Id) - $c0) / $n, 1); dwmMsPerCycle = [Math]::Round(((DwmMs) - $w0) / $n, 1)
    appPctOfOneCore = [Math]::Round(100 * ((TreeMs $b.p.Id) - $c0) / $wall, 2); restPct = $rest.pctOfOneCore }
  $reveals = @(Events $a | Where-Object { $_.ev -eq 'revealDone' }).Count
  Check "$a CPU per reveal cycle" ($reveals -ge $n) "reveal+hide: app $($cpu.appMsPerCycle) ms, dwm $($cpu.dwmMsPerCycle) ms per cycle ($($cpu.appPctOfOneCore) % of one core over the loop); $reveals reveals logged"
  $perf["${a}_cpu"] = $cpu

  if ($a -eq 'comp') {
    # ---- fullscreen in front: no reveal
    $tF = NowMs
    $fs = [BarProbe]::Fixture('Fullscreen fixture', 40, 160, 60, $S.X, $S.Y, $S.Width, $S.Height, $false, 'popup')
    Start-Sleep -Milliseconds 400
    $fg = [BarProbe]::ForceForeground($fs)
    [BarProbe]::Cursor($band.x, $band.y) | Out-Null; Start-Sleep -Milliseconds 1200
    $blk = Wait-Ev $a 'blocked' $tF 1
    $g = Grab @{ x = $cx; y = $cy; w = 1; h = 1 }
    Check 'fullscreen window in front blocks the reveal' ($fg -and -not [BarProbe]::Visible($hwnd) -and $null -ne $blk -and $g[1] -gt 130) "foreground $fg; 1.2 s at the edge: window visible $([BarProbe]::Visible($hwnd)), pill centre rgb $(Rgb $g 0) (fixture 40,160,60); app: $($blk.reason)"
    Save-Screen 'comp-fullscreen-blocked.png' | Out-Null
    [BarProbe]::Cursor($away.x, $away.y) | Out-Null
    [BarProbe]::Close($fs) | Out-Null; Start-Sleep -Milliseconds 400
    # ---- a maximised window in front does NOT block
    $fg2 = [BarProbe]::ForceForeground($mx)
    $tM = NowMs; Start-Sleep -Milliseconds 700
    [BarProbe]::Cursor($band.x, $band.y) | Out-Null
    $rm = Wait-Ev $a 'revealDone' $tM 3
    Check 'maximised window in front does not block' ($fg2 -and $null -ne $rm -and [BarProbe]::RootAt($cx, $cy) -eq $hwnd) "foreground = maximised fixture $fg2 (frame $([BarProbe]::FrameRect($mx))); revealed: $($null -ne $rm); blocked events: $(@(Events $a | Where-Object { $_.ev -eq 'blocked' -and $_.t -ge $tM }).Count)"
    [BarProbe]::Cursor($away.x, $away.y) | Out-Null; Start-Sleep -Milliseconds 900
  }
  $x = Stop-Bar $b
  $st = [BarProbe]::TaskbarState()
  Check "$a quit: taskbar restored" ($null -ne $x -and $st -eq $tb0 -and -not (Test-Path (Join-Path $Out 'shell-state.json'))) "state $st (original $tb0); exit: $($x.taskbar)"
  App-Log $a
}
Note 'perf' $perf

# ---- killed: the next start puts the taskbar back (shell-state.json)
$k = Start-Bar 'kill' @('--anim=comp')
$sk = [BarProbe]::TaskbarState()
Stop-Process -Id $k.p.Id -Force; Start-Sleep -Milliseconds 800
$sk2 = [BarProbe]::TaskbarState()
$rp = Start-Spike $exe @('--tag=restore', '--mode=restore')
$rs = Wait-Json (Join-Path $Out 'restore-restore.json') 30
Check 'killed: taskbar put back by the next start' ($null -ne $k.r -and ($sk -band 1) -eq 1 -and $null -ne $rs -and [BarProbe]::TaskbarState() -eq $tb0) "running $sk, after kill $sk2 (nothing runs on a kill), after restore $([BarProbe]::TaskbarState()) (original $tb0): $($rs.restored)"

# ---- dock: a real app bar. The taskbar goes on auto-hide FIRST (from here, so the app finds it so and
# leaves it), so the work area measures our reservation alone, not the taskbar's strip coming back.
[BarProbe]::SetTaskbarState($tb0 -bor 1); Start-Sleep -Milliseconds 800
$wa0 = J ([BarProbe]::WorkArea())
Note 'dock_baseline_workArea' $wa0
[BarProbe]::ForceForeground($mx) | Out-Null
$d = Start-Bar 'dock' @('--mode=dock')
Start-Sleep -Milliseconds 1200
$wa1 = J ([BarProbe]::WorkArea()); $dr = J ([BarProbe]::Rect($d.r.hwnd)); $mf = J ([BarProbe]::FrameRect($mx))
Check 'dock: app bar reserves 48 px' ($wa1.h -eq ($wa0.h - 48) -or $wa1.y + $wa1.h -eq $MB - 48) "work area $($wa0 | ConvertTo-Json -Compress) -> $($wa1 | ConvertTo-Json -Compress); app: $($d.r.appbar)"
Check 'dock: the bar sits in its slot' ($dr.y -eq $wa1.y + $wa1.h -and $dr.h -eq 48 -and $dr.w -eq $S.Width) "bar window $($dr | ConvertTo-Json -Compress)"
Check 'dock: a maximised window respects it' ($mf.y + $mf.h -le $wa1.y + $wa1.h) "maximised fixture frame $($mf | ConvertTo-Json -Compress), work area bottom $($wa1.y + $wa1.h)"
Save-Shot 'dock.png' @{ x = 0; y = $MB - 120; w = $S.Width; h = 120 } | Out-Null
$tF = NowMs
$fs = [BarProbe]::Fixture('Fullscreen fixture', 40, 160, 60, $S.X, $S.Y, $S.Width, $S.Height, $false, 'popup'); Start-Sleep -Milliseconds 300
[BarProbe]::ForceForeground($fs) | Out-Null; Start-Sleep -Milliseconds 800
Note 'dock_abnFullscreen' @(Events 'dock' | Where-Object { $_.ev -eq 'abnFullscreen' -and $_.t -ge $tF } | ForEach-Object { $_.open })
[BarProbe]::Close($fs) | Out-Null; [BarProbe]::ForceForeground($mx) | Out-Null
$xd = Stop-Bar $d
Start-Sleep -Milliseconds 700
$wa2 = J ([BarProbe]::WorkArea()); $mf2 = J ([BarProbe]::FrameRect($mx))
Check 'dock quit: space released' ($wa2.h -eq $wa0.h -and $wa2.y -eq $wa0.y) "work area $($wa2 | ConvertTo-Json -Compress) (before $($wa0 | ConvertTo-Json -Compress)); maximised frame bottom $($mf2.y + $mf2.h); app: $($xd.appbar)"
# killed dock: does Explorer drop the reservation itself?
$d2 = Start-Bar 'dock2' @('--mode=dock'); Start-Sleep -Milliseconds 1000
$wk = J ([BarProbe]::WorkArea())
Stop-Process -Id $d2.p.Id -Force
$sw = [Diagnostics.Stopwatch]::StartNew(); $freed = $null
while ($sw.ElapsedMilliseconds -lt 10000) { $w = J ([BarProbe]::WorkArea()); if ($w.h -eq $wa0.h) { $freed = $sw.ElapsedMilliseconds; break }; Start-Sleep -Milliseconds 100 }
$how = if ($null -ne $freed) { "Explorer released it $freed ms after the kill" } else { 'Explorer had NOT released it 10 s after the kill' }
$rp2 = Start-Spike $exe @('--tag=restore2', '--mode=restore'); $rs2 = Wait-Json (Join-Path $Out 'restore2-restore.json') 30
$how += "; the next start (restore): $($rs2.restored)"
$wa3 = J ([BarProbe]::WorkArea())
Check 'dock killed: space released' ($wk.h -eq $wa0.h - 48 -and $wa3.h -eq $wa0.h) "reserved $($wk.h) px tall work area while running; $how; now $($wa3 | ConvertTo-Json -Compress)"

[BarProbe]::Close($mx) | Out-Null
[BarProbe]::SetTaskbarState($tb0); Start-Sleep -Milliseconds 500   # the check's own auto-hide (dock section) back off
if ([BarProbe]::TaskbarState() -ne $tb0) { [BarProbe]::SetTaskbarState($tb0); Problem "taskbar state was not back at the end; set to $tb0 by the check" }
Finish-Check
