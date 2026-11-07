# popup/check.ps1 - the popup lane: primitives 1 (borderless see-through popup, no frame line),
# 5 (click-through and hit-testing) and 7 (instant-open cost). RUNNER ONLY (moves the cursor, clicks,
# flips the Windows theme).
#   pwsh -NoProfile -File native/spikes/windowing/popup/check.ps1
# Last line: WINDOWING popup: ok (N of N checks) | WINDOWING popup: FAILED (...)
. (Join-Path $PSScriptRoot '..\harness\Common.ps1') -Lane popup
Add-Type -Path (Join-Path $PSScriptRoot 'CheckNative.cs')

# The technique each primitive's Checks hold to the bar; the others are measured as Notes.
$FrameWinner = 'nc'
$HitWorks = @('nchittest', 'layered', 'region', 'ncregion')   # pass clicks to ANOTHER process (runs 1-2)
$SameThreadWorks = @('layered', 'region')         # also to a window of the popup's own thread; nchittest does NOT (runs 1-2)
$ParkWinner = 'apphide'
$Tol = 8

$exe = Build-Spike 'popup\Popup.csproj'
Check 'build' ($null -ne $exe) $(if ($exe) { "$($Results.build_Popup.seconds) s, $($Results.build_Popup.folderMB) MB" } else { 'see build-Popup.log' })
if (-not $exe) { Finish-Check }
# The runner's console window sits over the test area and comes forward when focus moves (run 1).
Note 'minimized' ([PopupProbe]::MinimizeOthers())
Start-Sleep -Milliseconds 500

# Lines of a file another process is still writing.
function Read-Shared([string]$path) {
  if (-not (Test-Path $path)) { return @() }
  $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
  try { return @(([IO.StreamReader]::new($fs)).ReadToEnd() -split "`r?`n" | Where-Object { $_ }) } finally { $fs.Dispose() }
}
# A plain WinForms window of ANOTHER process (fixture.ps1): the click-through target, and the
# known-colour backdrop under the frame tests. Returns the process, or $null.
function Start-Fixture([string]$name, $r, [string]$color) {
  $log = Join-Path $Out "$name.log"
  Remove-Item $log -ErrorAction SilentlyContinue
  $fp = Start-Process pwsh -PassThru -ArgumentList @('-NoProfile', '-File', "`"$(Join-Path $PSScriptRoot 'fixture.ps1')`"", '-Log', "`"$log`"",
    '-X', $r.x, '-Y', $r.y, '-W', $r.w, '-H', $r.h, '-Color', $color, '-Title', $name)
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.ElapsedMilliseconds -lt 30000) {
    if (@(Read-Shared $log) -match '^shown') { Start-Sleep -Milliseconds 300; return $fp }
    Start-Sleep -Milliseconds 100
  }
  Problem "fixture $name never showed (30 s)"
  return $null
}

# ------------------------------------------------------------------ helpers
$script:pipeN = 0
function Start-Popup([string]$tag) {
  $script:pipeN++
  $pipe = "mantle-popup-$PID-$($script:pipeN)"
  $p = Start-Spike $exe @("--pipe=$pipe")
  if (-not [PopupProbe]::Connect($pipe, 30000)) { Problem "Popup app ($tag): no pipe in 30 s"; Stop-Tree $p.Id; return $null }
  Say "popup app ($tag): pid $($p.Id), pipe $pipe"
  return $p
}
function Stop-Popup($p) {
  if (-not $p) { return }
  [PopupProbe]::Send('quit', 3000) | Out-Null
  [PopupProbe]::Disconnect()
  Start-Sleep -Milliseconds 400
  Stop-Tree $p.Id
}
function Parse-Reply([string]$r) { try { return $r | ConvertFrom-Json } catch { return $null } }
function Cmd([string]$c, [int]$timeout = 15000) {
  $r = [PopupProbe]::Send($c, $timeout)
  $o = Parse-Reply $r
  if (-not $o -or -not $o.ok) { Problem "'$c' -> $r"; return $null }
  return $o
}
function PreciseNow { ([DateTime]::UtcNow - [DateTime]::UnixEpoch).TotalMilliseconds }
function Rgb($bytes, [int]$w, [int]$x, [int]$y) { $i = ($y * $w + $x) * 4; return @([int]$bytes[$i + 2], [int]$bytes[$i + 1], [int]$bytes[$i]) }
function Over($argb, $bg) { $a = $argb[0] / 255.0; return @(0..2 | ForEach-Object { $argb[$_ + 1] * $a + $bg[$_] * (1 - $a) }) }
function Err($got, $want) { $m = 0.0; for ($k = 0; $k -lt 3; $k++) { $m = [Math]::Max($m, [Math]::Abs($got[$k] - $want[$k])) }; return [Math]::Round($m, 1) }
function Fmt($c) { ($c | ForEach-Object { [int][Math]::Round($_) }) -join ',' }
function Stats($vals) {
  $s = @($vals | Where-Object { $null -ne $_ -and $_ -ge 0 } | ForEach-Object { [double]$_ } | Sort-Object)
  if ($s.Count -eq 0) { return [ordered]@{ n = 0 } }
  $med = if ($s.Count % 2) { $s[[int][Math]::Floor($s.Count / 2)] } else { ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2 }
  $p95 = $s[[Math]::Min($s.Count - 1, [int][Math]::Ceiling(0.95 * $s.Count) - 1)]
  return [ordered]@{ n = $s.Count; median = [Math]::Round($med, 1); p95 = [Math]::Round($p95, 1); min = [Math]::Round($s[0], 1); max = [Math]::Round($s[-1], 1) }
}
function S($st) { if ($st.n -eq 0) { return 'n 0' }; return "median $($st.median) ms, p95 $($st.p95) ms (min $($st.min), max $($st.max), n $($st.n))" }
# Stray pixels outside the shape (the harness count: tol $Tol, 2 px AA band left out).
function Stray($before, $after, $area, $shape) {
  $r = [WinHarness]::ChangedOutside($before, $after, $area.w, $area.h, $shape.x - $area.x, $shape.y - $area.y, $shape.w, $shape.h, $shape.r, $Tol, 2)
  return [ordered]@{ changed = $r[0]; onAreaEdge = $r[1]; maxDelta = $r[2]; first = "$($r[3]),$($r[4])" }
}
# Plain WS_POPUP: no caption, frame, border, sysmenu or min/max box.
function StyleClean([string]$hex) { $v = [Convert]::ToInt64($hex.Substring(2), 16); return (($v -band 0x00CF0000L) -eq 0) -and (($v -band 0x80000000L) -ne 0) }
function ClientIsWindow($o) { return [bool]$o -and $o.client.x -eq $o.window.x -and $o.client.y -eq $o.window.y -and $o.client.w -eq $o.window.w -and $o.client.h -eq $o.window.h }
# Wait until the rect shows what it showed in $empty again.
function Wait-Empty($empty, $r, [int]$timeoutMs = 3000) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
    if ([PopupProbe]::CountChanged($empty, (Grab $r), $Tol) -eq 0) { return $true }
    Start-Sleep -Milliseconds 30
  }
  return $false
}
# Glass shape drawn as asked: centre = fill over the desktop; top and left hairline = edge over the
# desktop (WinUI's Border default, background inside the border) or edge over fill (background under it).
function Test-Glass($o, $before, $after, $area) {
  $s = $o.shape
  $sx = $s.x - $area.x; $sy = $s.y - $area.y
  $cx = $sx + [int]($s.w / 2); $cy = $sy + [int]($s.h / 2)
  $res = [ordered]@{}
  $bg = Rgb $before $area.w $cx $cy; $got = Rgb $after $area.w $cx $cy; $want = Over $o.fill $bg
  $res.centre = [ordered]@{ got = (Fmt $got); want = (Fmt $want); err = (Err $got $want) }
  foreach ($e in @(@('top', $cx, $sy), @('left', $sx, $cy), @('bottom', $cx, ($sy + $s.h - 1)))) {
    $bg = Rgb $before $area.w $e[1] $e[2]; $got = Rgb $after $area.w $e[1] $e[2]
    $w1 = Over $o.edge $bg; $w2 = Over $o.edge (Over $o.fill $bg)
    $e1 = Err $got $w1; $e2 = Err $got $w2
    $res[$e[0]] = [ordered]@{ got = (Fmt $got); want = $(if ($e1 -le $e2) { Fmt $w1 } else { Fmt $w2 }); err = [Math]::Min($e1, $e2); bg = (Fmt $bg) }
  }
  $res.ok = ($res.centre.err -le 6) -and ($res.top.err -le 6) -and ($res.left.err -le 6) -and ($res.bottom.err -le 6)
  return $res
}
function GlassText($g) { "centre $($g.centre.got) want $($g.centre.want) (err $($g.centre.err)); hairline top $($g.top.got) want $($g.top.want) (err $($g.top.err), desktop $($g.top.bg)), left err $($g.left.err), bottom err $($g.bottom.err)" }
function Rect($r) { "$($r.w)x$($r.h) at $($r.x),$($r.y)" }

$id = 0

# ================================================================== 1. no frame line
$FW = @{ x = 300; y = 300; w = 400; h = 200 }
$RW = @{ w = 520; h = 240 }                       # resize target, same top-left
$M = 32                                           # margin grabbed round the window: a frame line AND any DWM shadow land in it
$FA = @{ x = $FW.x - $M; y = $FW.y - $M; w = $RW.w + 2 * $M; h = $RW.h + 2 * $M }
$frameModes = @('spike', 'popup', 'nc', 'ncp')
foreach ($theme in 'light', 'dark') {
  Set-WinTheme $theme
  $bc = [PopupProbe]::BroadcastTheme()
  Start-Sleep -Milliseconds 1500
  Say "theme $theme (WM_SETTINGCHANGE broadcast: $bc)"
  # a known desktop under the test: bright for light (the ink hairline shows), deep blue-grey for dark
  $bdColor = if ($theme -eq 'light') { '205,210,220' } else { '40,52,72' }
  $bd = Start-Fixture "backdrop-$theme" @{ x = $FA.x - 20; y = $FA.y - 20; w = $FA.w + 40; h = $FA.h + 40 } $bdColor
  $p = Start-Popup "frame-$theme"
  if (-not $p) { if ($bd) { Stop-Tree $bd.Id }; continue }
  [void][PopupProbe]::RaiseByTitle("backdrop-$theme")
  Start-Sleep -Milliseconds 500
  $empty = Grab $FA
  foreach ($mode in $frameModes) {
    $id++
    if (-not (Wait-Empty $empty $FA)) { Problem "frame $theme ${mode}: the area never went back to the empty desktop" }
    $before = Grab $FA
    $o = Cmd "open id=$id frame=$mode theme=$theme fill=glass x=$($FW.x) y=$($FW.y) w=$($FW.w) h=$($FW.h)"
    if (-not $o) { continue }
    Start-Sleep -Milliseconds 700
    $after = Grab $FA
    Save-Shot "frame-$theme-$mode.png" $FA | Out-Null
    $st = Stray $before $after $FA $o.shape
    $g = Test-Glass $o $before $after $FA
    Note "frame_${theme}_$mode" ([ordered]@{ stray = $st; glass = $g; style = $o.style; exStyle = $o.exStyle; window = $o.window; client = $o.client; dwmFrame = $o.dwmFrame; notes = $o.notes; renderMs = $o.renderMs })
    if ($mode -eq $FrameWinner) {
      Check "no stray pixels: frame=$mode, $theme" ($st.changed -eq 0) "changed $($st.changed) px outside the shape at tol $Tol (maxDelta $($st.maxDelta)) in $(Rect $FA) incl. $M px margin; window $(Rect $o.window), client $(Rect $o.client); style $($o.style) ex $($o.exStyle)"
      Check "client = window (no non-client frame): $theme" ((ClientIsWindow $o)) "client $(Rect $o.client) vs window $(Rect $o.window); DWM frame $(Rect $o.dwmFrame); style $($o.style) (the presenter keeps WS_DLGFRAME|WS_SYSMENU: plain WS_POPUP $(StyleClean $o.style); WM_NCCALCSIZE takes the frame away)"
      Check "glass fill and hairline drawn: $theme" $g.ok (GlassText $g)
      # hide / show
      $h = Cmd "park id=$id park=apphide"
      Start-Sleep -Milliseconds 400
      $hid = Grab $FA
      $gone = [PopupProbe]::CountChanged($before, $hid, $Tol)
      $sh = Cmd "show id=$id"
      Start-Sleep -Milliseconds 700
      $again = Grab $FA
      Save-Shot "frame-$theme-$mode-reshown.png" $FA | Out-Null
      $same = [PopupProbe]::CountChanged($after, $again, $Tol)
      $shStyle = if ($sh) { $sh.style } else { '?' }
      Check "hidden then shown again: $theme" ($gone -eq 0 -and $same -eq 0 -and $sh -and (ClientIsWindow $sh)) "hidden: $gone px differ from the empty desktop; shown again: $same px differ from the first show (maxDelta $([PopupProbe]::MaxDelta($after, $again))); style after show $shStyle"
      # resize
      $rz = Cmd "resize id=$id w=$($RW.w) h=$($RW.h)"
      Start-Sleep -Milliseconds 700
      $big = Grab $FA
      Save-Shot "frame-$theme-$mode-resized.png" $FA | Out-Null
      if ($rz) {
        $st2 = Stray $before $big $FA $rz.shape
        $g2 = Test-Glass $rz $before $big $FA
        Check "after a resize to $($RW.w)x$($RW.h): $theme" ($st2.changed -eq 0 -and $g2.ok -and $rz.window.w -eq $RW.w -and $rz.client.w -eq $RW.w -and (ClientIsWindow $rz)) "window $(Rect $rz.window), client $(Rect $rz.client); stray $($st2.changed) (maxDelta $($st2.maxDelta)); $(GlassText $g2)"
      } else { Check "after a resize to $($RW.w)x$($RW.h): $theme" $false 'no reply' }
    }
    Cmd "close id=$id" | Out-Null
  }
  # anti-aliased corners, shape not clipped: an opaque magenta shape (coverage is unambiguous)
  $id++
  [void](Wait-Empty $empty $FA)
  $before = Grab $FA
  $o = Cmd "open id=$id frame=$FrameWinner theme=$theme fill=solid x=$($FW.x) y=$($FW.y) w=$($FW.w) h=$($FW.h)"
  if ($o) {
    Start-Sleep -Milliseconds 700
    $after = Grab $FA
    $s = $o.shape
    Save-Shot "corner-$theme.png" @{ x = $s.x - 6; y = $s.y - 6; w = 48; h = 48 } | Out-Null
    $aa = [PopupProbe]::CornerAA($before, $after, $FA.w, $s.x - $FA.x, $s.y - $FA.y, $s.w, $s.h, $s.r, 255, 0, 255)
    $st = Stray $before $after $FA $s
    $diag = @(0..12 | ForEach-Object { [Math]::Round([PopupProbe]::Coverage($before, $after, $FA.w, $s.x - $FA.x + $_, $s.y - $FA.y + $_, 255, 0, 255), 2) }) -join ' '
    Note "corner_$theme" ([ordered]@{ arcBand = $aa[0]; partial = $aa[1]; insideNotFull = $aa[2]; minInsidePct = $aa[3]; paintedOutsideArc = $aa[4]; diagonalCoverage = $diag; stray = $st })
    Check "corners anti-aliased, shape whole: $theme" ($aa[1] -ge 8 -and $aa[2] -eq 0 -and $aa[4] -eq 0 -and $st.changed -eq 0) "arc band $($aa[0]) px, $($aa[1]) partly covered; inside pixels not full $($aa[2]) (min $($aa[3])%); corner pixels painted outside the arc $($aa[4]); stray $($st.changed); top-left diagonal coverage $diag"
    Cmd "close id=$id" | Out-Null
  }
  Stop-Popup $p
  if ($bd) { Stop-Tree $bd.Id }
}
Note 'dpi' 'runner is 1920x1080 at 100% (one display); 150% not tested in this lane'

# ================================================================== 5. click-through
Set-WinTheme light
[void][PopupProbe]::BroadcastTheme()
Start-Sleep -Milliseconds 1000
$HW = @{ x = 600; y = 300; w = 400; h = 200 }     # shape 300x100 at 650,350
$HA = @{ x = $HW.x - 8; y = $HW.y - 8; w = $HW.w + 16; h = $HW.h + 16 }
$FX = @{ x = 560; y = 260; w = 120; h = 120 }     # the other process's window, under the popup's top-left transparent corner
$ptPass = @(612, 312); $ptCtl = @(570, 270); $ptShape = @(800, 400); $ptAway = @(1500, 900)
$fixLog = Join-Path $Out 'popup-fixture.log'
$fixP = Start-Fixture 'popup-fixture' $FX '90,90,90'
function Fix-Lines { return @(Read-Shared $fixLog) }
function Fix-Since([int]$n) {
  $l = @(Fix-Lines)
  if ($l.Count -le $n) { return @() }
  return @($l[$n..($l.Count - 1)] | ForEach-Object { $a = $_ -split ' '; [pscustomobject]@{ kind = $a[0]; x = [int]$a[1]; y = [int]$a[2]; t = [double]$a[3] } })
}
$fixShown = @(Fix-Lines) -match '^shown'
Check 'fixture window of another process up' ([bool]$fixShown) "$(if ($fixShown) { $fixShown[0] } else { 'no shown line in 30 s' }); pwsh pid $(if ($fixP) { $fixP.Id }), check pid $PID"
Save-Screen 'hit-screen.png' | Out-Null

# Each technique in a FRESH app process, between a hit=none control before and after it (run 3: a
# plain window passed clicks after the other techniques had run in the same process). Then the
# same-thread test in that process.
$hitTechs = @('ncregion', 'nchittest', 'region', 'layered')
foreach ($tech in $hitTechs) {
$p = Start-Popup "hit-$tech"
foreach ($mode in @('none', $tech, 'none-after')) {
  if (-not $p) { break }
  $id++
  [void][PopupProbe]::Move($ptAway[0], $ptAway[1])
  [void][PopupProbe]::RaiseByTitle('popup-fixture')
  Start-Sleep -Milliseconds 300
  $before = Grab $HA
  $o = Cmd "open id=$id frame=$FrameWinner theme=light fill=glass hit=$($mode -replace '-after$', '') x=$($HW.x) y=$($HW.y) w=$($HW.w) h=$($HW.h)"
  if (-not $o) { continue }
  Start-Sleep -Milliseconds 800
  $after = Grab $HA
  Save-Shot "hit-$mode.png" $HA | Out-Null
  $g = Test-Glass $o $before $after $HA
  $st = Stray $before $after $HA $o.shape
  Cmd 'events' | Out-Null
  # a. control: the fixture outside the popup
  $n = @(Fix-Lines).Count
  [void][PopupProbe]::Click($ptCtl[0], $ptCtl[1]); Start-Sleep -Milliseconds 300
  $ctl = @(Fix-Since $n | Where-Object kind -EQ 'down').Count
  [void][PopupProbe]::Move($ptAway[0], $ptAway[1]); Start-Sleep -Milliseconds 200
  # b. moves over the transparent corner
  Cmd 'events' | Out-Null
  $n = @(Fix-Lines).Count
  foreach ($k in 0..5) { [void][PopupProbe]::Move($ptPass[0] + 3 * $k, $ptPass[1] + 2 * $k); Start-Sleep -Milliseconds 30 }
  Start-Sleep -Milliseconds 200
  $mv = @(Fix-Since $n | Where-Object { $_.kind -eq 'move' -and $_.x -ge $HW.x -and $_.y -ge $HW.y }).Count
  $ev = Cmd 'events'
  $popMoves = if ($ev) { [int]$ev.moves."$id" } else { -1 }
  # c. click in the transparent corner
  $n = @(Fix-Lines).Count
  [void][PopupProbe]::Click($ptPass[0], $ptPass[1]); Start-Sleep -Milliseconds 300
  $fixDown = @(Fix-Since $n | Where-Object { $_.kind -eq 'down' -and $_.x -ge $HW.x -and $_.y -ge $HW.y }).Count
  $ev = Cmd 'events'
  $popPass = @($ev.events | Where-Object { $_.kind -eq 'pressed' }).Count
  # d. click on the shape, after the cursor has rested there
  [void][PopupProbe]::Move($ptShape[0], $ptShape[1]); Start-Sleep -Milliseconds 200
  Cmd 'events' | Out-Null
  $n = @(Fix-Lines).Count
  [void][PopupProbe]::Click($ptShape[0], $ptShape[1]); Start-Sleep -Milliseconds 300
  $ev = Cmd 'events'
  $popShape = @($ev.events | Where-Object { $_.kind -eq 'pressed' }).Count
  $fixShape = @(Fix-Since $n | Where-Object kind -EQ 'down').Count
  # e. click on the shape straight after arriving (no rest: the poll's worst case)
  [void][PopupProbe]::Move($ptAway[0], $ptAway[1]); Start-Sleep -Milliseconds 200
  Cmd 'events' | Out-Null
  [void][PopupProbe]::Click($ptShape[0], $ptShape[1]); Start-Sleep -Milliseconds 300
  $ev = Cmd 'events'
  $popNoRest = @($ev.events | Where-Object { $_.kind -eq 'pressed' }).Count
  $info = Cmd "info id=$id"
  $r = [ordered]@{
    rendered = $g.ok; glass = (GlassText $g); stray = $st.changed; strayMaxDelta = $st.maxDelta
    controlClickReachedFixture = $ctl; passMovesAtFixture = $mv; movesSeenByPopup = $popMoves
    passClickAtFixture = $fixDown; passClickAtPopup = $popPass
    shapeClickAtPopup = $popShape; shapeClickAtFixture = $fixShape; shapeClickNoRestAtPopup = $popNoRest
    exStyle = $(if ($info) { $info.exStyle } else { '?' }); children = $o.children; notes = $(if ($info) { $info.notes } else { $o.notes }); hitTransparent = $(if ($info) { $info.hitTransparent } else { $null })
  }
  $r.passesToOtherProcess = ($fixDown -ge 1 -and $popPass -eq 0 -and $mv -ge 1)
  $r.shapeTakesClicks = ($popShape -ge 1 -and $fixShape -eq 0)
  Note $(if ($mode -like 'none*') { "hit_${tech}_$mode" } else { "hit_$mode" }) $r
  if ($mode -like 'none*') {
    Check "control in the $tech process: hit=none takes the corner click itself ($mode)" ($popPass -ge 1 -and $fixDown -eq 0 -and $ctl -ge 1) "transparent corner: popup $popPass press(es), fixture $fixDown; control click on the fixture $ctl"
  }
  if ($mode -eq 'region') {
    # a window region on a WinUI window: still drawn after a resize (region re-applied), corners still anti-aliased
    $rz = Cmd "resize id=$id w=440 h=220"
    Start-Sleep -Milliseconds 700
    $big = Grab $HA
    Save-Shot 'hit-region-resized.png' $HA | Out-Null
    $g2 = if ($rz) { Test-Glass $rz $before $big $HA } else { @{ ok = $false } }
    Check 'region: still drawn after a resize' ([bool]$rz -and $g2.ok) "$(if ($rz) { GlassText $g2 } else { 'no reply' })"
    Cmd "close id=$id" | Out-Null
    $id++
    $o2 = Cmd "open id=$id frame=$FrameWinner theme=light fill=solid hit=region x=$($HW.x) y=$($HW.y) w=$($HW.w) h=$($HW.h)"
    Start-Sleep -Milliseconds 700
    $after2 = Grab $HA
    Save-Shot 'hit-region-solid.png' $HA | Out-Null
    if ($o2) {
      $s = $o2.shape
      Save-Shot 'corner-region.png' @{ x = $s.x - 6; y = $s.y - 6; w = 48; h = 48 } | Out-Null
      $aa = [PopupProbe]::CornerAA($before, $after2, $HA.w, $s.x - $HA.x, $s.y - $HA.y, $s.w, $s.h, $s.r, 255, 0, 255)
      $st3 = Stray $before $after2 $HA $s
      Check 'region: corners still anti-aliased, nothing outside' ($aa[1] -ge 8 -and $aa[2] -eq 0 -and $aa[4] -eq 0 -and $st3.changed -eq 0) "arc band $($aa[0]) px, $($aa[1]) partly covered; inside not full $($aa[2]); painted outside the arc $($aa[4]); stray $($st3.changed)"
    } else { Check 'region: corners still anti-aliased, nothing outside' $false 'no reply' }
  }
  if ($mode -eq $tech -or ($mode -eq 'none' -and $tech -eq $hitTechs[0])) {
    [void][PopupProbe]::Move($ptAway[0], $ptAway[1]); Start-Sleep -Milliseconds 300
    Note "hit_${mode}_cpu" (Get-Cpu $p.Id 10)
  }
  if ($HitWorks -contains $mode) {
    Check "click-through to another process: hit=$mode" ($r.passesToOtherProcess -and $r.shapeTakesClicks -and $g.ok -and $ctl -ge 1) "transparent corner: fixture got $fixDown down(s) and $mv move(s), popup $popPass press(es); shape: popup $popShape, fixture $fixShape (no rest first: popup $popNoRest); control click on the fixture $ctl; $(GlassText $g); ex $($r.exStyle)"
    Check "shape still clean with hit=${mode}" ($st.changed -eq 0) "stray $($st.changed) (maxDelta $($st.maxDelta)) in $(Rect $HA)"
  }
  if ($mode -eq 'layered') {
    $lat = @()
    for ($k = 0; $k -lt 12; $k++) {
      [void][PopupProbe]::Move($ptAway[0], $ptAway[1]); Start-Sleep -Milliseconds 120
      Cmd 'events' | Out-Null
      $t0 = PreciseNow
      [void][PopupProbe]::Move($ptShape[0], $ptShape[1]); Start-Sleep -Milliseconds 150
      $ev = Cmd 'events'
      $tg = @($ev.events | Where-Object { $_.kind -eq 'toggle' -and $_.inside })
      if ($tg.Count) { $lat += $tg[0].t - $t0 }
    }
    $ls = Stats $lat
    Note "hit_${mode}_toggleLatency" $ls
    Check "layered: the poll makes the shape clickable within 50 ms" ($ls.n -ge 10 -and $ls.p95 -le 50) "cursor enters the shape -> WS_EX_TRANSPARENT cleared: $(S $ls)"
  }
  Cmd "close id=$id" | Out-Null
  Start-Sleep -Milliseconds 300
}
# A window of the popup's OWN thread under the transparent corner (Mantle's case: a popup over its
# own bar). Documented: HTTRANSPARENT passes only to those. Measured here (runs 1-2): the opposite.
if ($p) {
  foreach ($mode in @($tech)) {
    $id++
    [void][PopupProbe]::Move($ptAway[0], $ptAway[1])
    Cmd 'fixture x=560 y=560 w=120 h=120' | Out-Null
    $o = Cmd "open id=$id frame=$FrameWinner theme=light fill=glass hit=$mode x=600 y=600 w=400 h=200"
    Start-Sleep -Milliseconds 800
    Save-Shot "hit-samethread-$mode.png" @{ x = 552; y = 552; w = 456; h = 256 } | Out-Null
    Cmd 'events' | Out-Null
    [void][PopupProbe]::Click(612, 612); Start-Sleep -Milliseconds 300
    $ev = Cmd 'events'
    $same = [ordered]@{ fixtureSameThreadPressed = @($ev.events | Where-Object { $_.kind -eq 'fixturePressed' }).Count; popupPressed = @($ev.events | Where-Object { $_.kind -eq 'pressed' }).Count }
    Note "hit_${mode}_sameThread" $same
    $sd = "same-thread WinUI window under the corner got $($same.fixtureSameThreadPressed) press(es), the popup $($same.popupPressed)"
    if ($SameThreadWorks -contains $mode) {
      Check "click-through to a window of the popup's own thread: hit=$mode" ($same.fixtureSameThreadPressed -ge 1 -and $same.popupPressed -eq 0) $sd
    } elseif ($mode -eq 'nchittest') {
      Check "HTTRANSPARENT does NOT reach a window of the popup's own thread (the popup keeps the click)" ($same.fixtureSameThreadPressed -eq 0 -and $same.popupPressed -ge 1) $sd
    }
    Cmd "close id=$id" | Out-Null
    Cmd 'unfixture' | Out-Null
    Start-Sleep -Milliseconds 300
  }
}
  Stop-Popup $p
}
if ($fixP) { Stop-Tree $fixP.Id }

# ================================================================== 7. instant-open cost
$OW = @{ x = 300; y = 300; w = 400; h = 200 }
$WR = @{ x = 496; y = 396; w = 8; h = 8 }         # centre of the (solid) shape
$OA = @{ x = $OW.x; y = $OW.y; w = $OW.w; h = $OW.h }
[void][PopupProbe]::Move($ptAway[0], $ptAway[1])
Start-Sleep -Milliseconds 500
$emptyW = Grab $WR
$spec = "frame=$FrameWinner theme=light fill=solid x=$($OW.x) y=$($OW.y) w=$($OW.w) h=$($OW.h)"

# (a) cold: new process -> first pixels
$cold = @(); $coldApp = @(); $coldLaunch = @()
for ($k = 1; $k -le 20; $k++) {
  $dir = Join-Path $Out "cold\$k"
  [void](Wait-Empty $emptyW $WR)
  $argv = "--out=`"$dir`" --cold " + (($spec -split ' ' | ForEach-Object { "--$_" }) -join ' ')
  $r = [PopupProbe]::StartAndWatch($exe, $argv, $WR.x, $WR.y, $WR.w, $WR.h, $Tol, 15000)
  $cj = Wait-Json (Join-Path $dir 'cold.json') 10
  $cold += $r.Ms
  if ($cj) { $coldApp += $cj.startToFirstFrameMs; $coldLaunch += $cj.startToLaunchedMs }
  Stop-Tree $r.Pid
  Start-Sleep -Milliseconds 300
}
$cs = Stats $cold; $cs2 = Stats ($cold | Select-Object -Skip 1); $ca = Stats $coldApp; $cl = Stats $coldLaunch
Note 'cold' ([ordered]@{ pixels = $cs; pixelsWithoutFirst = $cs2; first = $cold[0]; appStartToFirstFrame = $ca; appStartToOnLaunched = $cl; trials = $cold })
Check 'cold open measured, 20 of 20' ($cs.n -eq 20) "Process.Start -> first pixels: $(S $cs); first trial $([Math]::Round($cold[0])) ms; app's own process start -> first Rendering: $(S $ca); process start -> OnLaunched $(S $cl)"
Note 'cold_rule_1s_p95' ([ordered]@{ meets = ($cs.n -gt 0 -and $cs.p95 -le 1000); p95 = $cs.p95 })

# (b) warm process, a NEW window each time
$p = Start-Popup 'warm'
if ($p) {
  Start-Sleep -Seconds 3
  $mem0 = Get-Mem $p.Id
  $cpu0 = Get-Cpu $p.Id 10
  $warm = @(); $warmApp = @(); $warmCtor = @(); $closeMs = @(); $mem1 = $null
  for ($k = 1; $k -le 20; $k++) {
    $id++
    [void](Wait-Empty $emptyW $WR)
    $r = [PopupProbe]::SendAndWatch("open id=$id $spec", $WR.x, $WR.y, $WR.w, $WR.h, $Tol, 5000)
    $o = Parse-Reply $r.Reply
    $warm += $r.Ms
    if ($o) { $warmApp += $o.renderMs; $warmCtor += $o.ctorMs }
    if ($k -eq 1) { Start-Sleep -Seconds 2; $mem1 = Get-Mem $p.Id }
    $c = [PopupProbe]::SendAndWatch("close id=$id", $WR.x, $WR.y, $WR.w, $WR.h, $Tol, 3000)
    $closeMs += $c.Ms
    Start-Sleep -Milliseconds 200
  }
  Start-Sleep -Seconds 2
  $memClosed = Get-Mem $p.Id
  $gc = Cmd 'gc'
  Start-Sleep -Seconds 1
  $memGc = Get-Mem $p.Id
  $ws = Stats $warm; $ws2 = Stats ($warm | Select-Object -Skip 1)
  Note 'warm_new' ([ordered]@{ pixels = $ws; pixelsWithoutFirst = $ws2; first = $warm[0]; appToFirstRendering = (Stats $warmApp); windowCtorMs = (Stats $warmCtor); closeToGone = (Stats $closeMs); trials = $warm })
  Note 'mem_warm' ([ordered]@{ idleNoWindowEver = $mem0; oneShown = $mem1; afterClosing20 = $memClosed; afterGc = $memGc; managedMBAfterGc = $(if ($gc) { $gc.managedMB } else { $null }) })
  Note 'cpu_idle_no_window' $cpu0
  Check 'warm new-window open measured, 20 of 20' ($ws.n -eq 20) "command -> first pixels: $(S $ws); first window of the process $([Math]::Round($warm[0])) ms; without it $(S $ws2); app: command -> first Rendering $(S (Stats $warmApp)); Window ctor $(S (Stats $warmCtor))"
  Note 'warm_new_rule_100ms_p95' ([ordered]@{ meets = ($ws.n -gt 0 -and $ws.p95 -le 100); p95 = $ws.p95; p95WithoutFirst = $ws2.p95 })
  Stop-Popup $p
}

# (c) parked: created earlier, hidden, shown on demand
$p = Start-Popup 'parked'
$parkRes = [ordered]@{}
if ($p) {
  Start-Sleep -Seconds 3
  $pm0 = Get-Mem $p.Id
  [void](Wait-Empty $emptyW $WR)
  Cmd "open id=101 $spec" | Out-Null
  Start-Sleep -Seconds 2
  $pm1 = Get-Mem $p.Id
  Cmd "park id=101 park=$ParkWinner" | Out-Null
  Start-Sleep -Seconds 2
  $pm1p = Get-Mem $p.Id
  foreach ($k in 102, 103) { Cmd "open id=$k $spec" | Out-Null; Start-Sleep -Milliseconds 500; Cmd "park id=$k park=$ParkWinner" | Out-Null }
  Start-Sleep -Seconds 2
  $pm3p = Get-Mem $p.Id
  $cpu3 = Get-Cpu $p.Id 30
  $mem = [ordered]@{ idleNoWindowEver = $pm0; oneShown = $pm1; oneParked = $pm1p; threeParked = $pm3p
    perExtraParkedPrivateMB = [Math]::Round(($pm3p.privateMB - $pm1p.privateMB) / 2, 1); perExtraParkedPrivWsMB = [Math]::Round(($pm3p.privWsMB - $pm1p.privWsMB) / 2, 1)
    firstWindowPrivateMB = [Math]::Round($pm1p.privateMB - $pm0.privateMB, 1); firstWindowPrivWsMB = [Math]::Round($pm1p.privWsMB - $pm0.privWsMB, 1) }
  Note 'mem_parked' $mem
  Note 'cpu_three_parked_30s' $cpu3
  Check 'parked (hidden) windows cost no CPU' ($cpu3.cpuMs -le 32) "3 windows parked ($ParkWinner) for $($cpu3.seconds) s: $($cpu3.cpuMs) ms CPU ($($cpu3.pctOfOneCore)% of one core; 32 ms = two 15.6 ms scheduler ticks)"
  Cmd 'close id=102' | Out-Null; Cmd 'close id=103' | Out-Null
  foreach ($mode in 'apphide', 'swhide', 'offscreen', 'cloak') {
    # move window 101 into this park mode: show (in its old mode), park (in the new one)
    Cmd 'show id=101' | Out-Null; Start-Sleep -Milliseconds 300
    Cmd "park id=101 park=$mode" | Out-Null; Start-Sleep -Milliseconds 300
    $okEmpty = Wait-Empty $emptyW $WR
    $sh = @(); $shApp = @(); $hd = @(); $styles = @()
    for ($k = 1; $k -le 20; $k++) {
      [void](Wait-Empty $emptyW $WR)
      $r = [PopupProbe]::SendAndWatch('show id=101', $WR.x, $WR.y, $WR.w, $WR.h, $Tol, 3000)
      $o = Parse-Reply $r.Reply
      $sh += $r.Ms
      if ($o) { $shApp += $o.renderMs; $styles += $o.style }
      Start-Sleep -Milliseconds 100
      $h = [PopupProbe]::SendAndWatch("park id=101 park=$mode", $WR.x, $WR.y, $WR.w, $WR.h, $Tol, 3000)
      $hd += $h.Ms
      Start-Sleep -Milliseconds 100
    }
    $cpu = Get-Cpu $p.Id 10
    $ss = Stats $sh
    $parkRes[$mode] = [ordered]@{ show = $ss; hide = (Stats $hd); appShowToRendering = (Stats $shApp); renderingFired = @($shApp | Where-Object { $null -ne $_ }).Count
      cpuParked10s = $cpu; emptyWhenParked = $okEmpty; styles = @($styles | Select-Object -Unique) }
    Note "parked_$mode" $parkRes[$mode]
    Check "parked show measured: $mode, 20 of 20" ($ss.n -eq 20 -and $okEmpty) "show -> first pixels $(S $ss); park -> gone $(S (Stats $hd)); parked CPU $($cpu.cpuMs) ms in $($cpu.seconds) s; desktop clean while parked: $okEmpty"
  }
  Stop-Popup $p
}
# The recommendation's claim: one way to open a popup meets the warm rule (100 ms p95).
$paths = [ordered]@{}
if ($ws) { $paths['new window'] = $ws.p95 }
foreach ($k in $parkRes.Keys) { if ($parkRes[$k].show.n) { $paths["parked $k"] = $parkRes[$k].show.p95 } }
$best = $paths.GetEnumerator() | Sort-Object Value | Select-Object -First 1
Note 'open_paths_p95' $paths
Check 'an open path meets 100 ms p95' ($best -and $best.Value -le 100) "p95 to first pixels: $(($paths.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value) ms" }) -join '; ')"

Finish-Check
