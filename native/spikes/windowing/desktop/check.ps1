# desktop/check.ps1 - primitive 4, the desktop layer: a WinUI 3 window under the desktop icons, above
# the wallpaper, behind every app. RUNNER ONLY (it restarts Explorer and puts a file on the desktop).
#   pwsh -NoProfile -File native/spikes/windowing/desktop/check.ps1
# 1. every attach mode once (render + icons + tree, as Notes), plus the GDI baseline;
# 2. the chosen mode (auto) with the click route: render, under the icons, z-order, a click on the
#    layer's button, a click on an icon, Win+D, an Explorer restart, memory and CPU (as Checks).
. (Join-Path $PSScriptRoot '..\harness\Common.ps1') -Lane desktop
Add-Type -IgnoreWarnings -Path (Join-Path $PSScriptRoot 'DeskIcons.cs')
Add-Type -IgnoreWarnings -Path (Join-Path $PSScriptRoot 'probe\DeskProbe.cs')

$exe = Build-Spike 'desktop\DeskSpike.csproj'
Check 'build' ($null -ne $exe) $(if ($exe) { "$($Results.build_DeskSpike.seconds) s" } else { 'see build-DeskSpike.log' })
if (-not $exe) { Finish-Check }

$Magenta = @(230, 40, 140); $Cyan = @(20, 200, 220); $Yellow = @(255, 200, 0)
$Btn = @{ x = 1300; y = 300; w = 300; h = 200 }
$BtnBand = @{ x = 1310; y = 306; w = 280; h = 40 }          # above the button's text
$SW = $Screen.Width; $SH = $Screen.Height

# ------------------------------------------------------------------ the desktop: icons on, one probe file
$adv = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
$hide = (Get-ItemProperty -Path $adv -Name HideIcons -ErrorAction SilentlyContinue).HideIcons
Note 'hideIconsBefore' $hide
function Restart-Explorer {
  $old = [DeskProbe]::Progman()
  $t0 = NowMs
  Get-Process explorer -ErrorAction SilentlyContinue | Stop-Process -Force
  $restarted = $false
  while ((NowMs) - $t0 -lt 25000) {
    Start-Sleep -Milliseconds 200
    $pm = [DeskProbe]::Progman()
    if ($pm -ne 0 -and $pm -ne $old) { break }
    if (-not $restarted -and (NowMs) - $t0 -gt 6000 -and -not (Get-Process explorer -ErrorAction SilentlyContinue)) {
      Say 'explorer did not come back on its own: starting it'; Start-Process explorer.exe; $restarted = $true
    }
  }
  return [ordered]@{ oldProgman = $old; newProgman = [DeskProbe]::Progman(); ms = (NowMs) - $t0; startedByCheck = $restarted }
}
if ($hide -eq 1) { Set-ItemProperty -Path $adv -Name HideIcons -Value 0 -Type DWord; Note 'iconsTurnedOn' (Restart-Explorer) }
Start-Sleep -Seconds 2
$deskDir = [Environment]::GetFolderPath('Desktop')
$probeFile = Join-Path $deskDir 'Mantle probe.txt'
Set-Content -Path $probeFile -Value 'the desktop layer check puts this icon on the desktop'
function Get-ProbeIcon([double]$timeoutS = 12) {
  $end = (Get-Date).AddSeconds($timeoutS)
  do {
    $err = $null
    $icons = [DeskIcons]::List([ref]$err)
    $ic = $icons | Where-Object { $_.Name -like 'Mantle probe*' } | Select-Object -First 1
    if ($ic -and $ic.W -gt 0) { return @{ icon = $ic; all = $icons; error = $err } }
    Start-Sleep -Milliseconds 300
  } while ((Get-Date) -lt $end)
  return @{ icon = $null; all = $icons; error = $err }
}
$pi = Get-ProbeIcon
Note 'iconsBefore' ([ordered]@{ error = $pi.error; icons = @($pi.all | ForEach-Object { "$($_.Name) @ $($_.X),$($_.Y) $($_.W)x$($_.H)" }) })
Check 'probe icon on the desktop' ($null -ne $pi.icon) $(if ($pi.icon) { "'$($pi.icon.Name)' at $($pi.icon.X),$($pi.icon.Y) $($pi.icon.W)x$($pi.icon.H)" } else { "not in the icon list after 12 s ($($pi.error))" })
Note 'treeBeforeLayer' ([DeskProbe]::Tree())
# the runner's own console (and anything else) out of the way: every measure is of the whole screen
(New-Object -ComObject Shell.Application).MinimizeAll(); Start-Sleep -Seconds 1
Note 'shownAfterMinimizeAll' ([DeskProbe]::Foreground())
$wall = Grab @{ x = 0; y = 0; w = $SW; h = $SH }
Save-Screen 'wallpaper.png' | Out-Null

# ------------------------------------------------------------------ helpers
function Exclude-Icons($icons, [int]$m = 6) {
  $e = [System.Collections.Generic.List[int]]::new()
  foreach ($i in $icons) { if ($i.W -gt 0) { $e.Add($i.X - $m); $e.Add($i.Y - $m); $e.Add($i.W + 2 * $m); $e.Add($i.H + 2 * $m) } }
  return , $e.ToArray()
}
function Frac($n) { if ($n[1] -eq 0) { return 0 } ; return [Math]::Round($n[0] / $n[1], 4) }
# Everything the screen says about the layer right now.
function Measure-Layer([string]$shot) {
  $p = Get-ProbeIcon 4
  $px = Grab @{ x = 0; y = 0; w = $SW; h = $SH }
  if ($shot) { Save-Screen $shot | Out-Null }
  $ex = Exclude-Icons $p.all
  $btnEx = @(($Btn.x - 4), ($Btn.y - 4), ($Btn.w + 8), ($Btn.h + 8))
  # the field: the screen above the taskbar, minus the icons, the button and the title text
  $field = [DeskProbe]::Near($px, $SW, 0, 0, $SW, $SH - 60, $Magenta[0], $Magenta[1], $Magenta[2], 6, ($ex + $btnEx + @(1290, 130, 630, 70)), 7)
  $btn = [DeskProbe]::Near($px, $SW, $BtnBand.x, $BtnBand.y, $BtnBand.w, $BtnBand.h, $Cyan[0], $Cyan[1], $Cyan[2], 6, @(), 2)
  $yel = [DeskProbe]::Near($px, $SW, $BtnBand.x, $BtnBand.y, $BtnBand.w, $BtnBand.h, $Yellow[0], $Yellow[1], $Yellow[2], 6, @(), 2)
  $wallDiff = [WinHarness]::Diff($wall, $px)
  # the outermost 3 px (left, top, right; the bottom is under the taskbar): a frame ring shows here
  $edge = [DeskProbe]::Near($px, $SW, 0, 0, 3, $SH - 60, $Magenta[0], $Magenta[1], $Magenta[2], 6, $ex, 1)
  $top = [DeskProbe]::Near($px, $SW, 0, 0, $SW, 3, $Magenta[0], $Magenta[1], $Magenta[2], 6, $ex, 1)
  $right = [DeskProbe]::Near($px, $SW, $SW - 3, 0, 3, $SH - 60, $Magenta[0], $Magenta[1], $Magenta[2], 6, $ex, 1)
  $edgeAll = @(($edge[0] + $top[0] + $right[0]), ($edge[1] + $top[1] + $right[1]))
  $r = [ordered]@{ edgeMagenta = (Frac $edgeAll); edgeSamples = $edgeAll[1]; fieldMagenta = (Frac $field); fieldSamples = $field[1]; buttonCyan = (Frac $btn); buttonYellow = (Frac $yel); meanDiffFromWallpaper = [Math]::Round($wallDiff, 1) }
  if ($p.icon) {
    $i = $p.icon
    # inside the icon rect: how much is NOT the layer's magenta (the icon and its label drawn over us)
    $in = [DeskProbe]::Near($px, $SW, $i.X, $i.Y, $i.W, $i.H, $Magenta[0], $Magenta[1], $Magenta[2], 6, @(), 1)
    # a ring 6..14 px outside it (other icons excluded): the layer itself
    $others = Exclude-Icons @($p.all | Where-Object { $_.Index -ne $i.Index }) 2
    $ringEx = $others + @(($i.X - 6), ($i.Y - 6), ($i.W + 12), ($i.H + 12))
    $ring = [DeskProbe]::Near($px, $SW, $i.X - 14, $i.Y - 14, $i.W + 28, $i.H + 28, $Magenta[0], $Magenta[1], $Magenta[2], 6, $ringEx, 1)
    $r.icon = "$($i.Name) @ $($i.X),$($i.Y) $($i.W)x$($i.H)"
    $r.iconNotMagenta = [Math]::Round(1 - (Frac $in), 4)
    $r.iconRingMagenta = (Frac $ring)
    $r.iconSelected = $i.Selected
  } else { $r.icon = $null; $r.iconError = $p.error }
  return $r
}
function Read-State { $f = Join-Path $Out 'state.json'; if (Test-Path $f) { try { return Get-Content -Raw $f | ConvertFrom-Json } catch { } } ; return $null }
function Wait-State([scriptblock]$cond, [double]$timeoutS = 25) {
  $end = (Get-Date).AddSeconds($timeoutS)
  while ((Get-Date) -lt $end) { $s = Read-State; if ($s -and (& $cond $s)) { return $s } ; Start-Sleep -Milliseconds 100 }
  return Read-State
}
function Clicks { $f = Join-Path $Out 'clicks.jsonl'; if (Test-Path $f) { return @(Get-Content $f | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json }) } ; return @() }
function Reset-Run { foreach ($f in 'state.json', 'clicks.jsonl') { Remove-Item (Join-Path $Out $f) -ErrorAction SilentlyContinue } }
function Order-Line {
  $kids = [DeskProbe]::ProgmanChildren()
  return (@($kids | ForEach-Object { "$([DeskProbe]::ClassOfHandle($_))$(if (-not [DeskProbe]::Visible($_)) { '(hidden)' })" }) -join ' > ')
}
function Stop-Spike($p) { if ($p) { Stop-Tree $p.Id; Start-Sleep -Milliseconds 800 } }

# ------------------------------------------------------------------ 1. every mode once (Notes)
$modeResults = [ordered]@{}
foreach ($run in @(@{ mode = 'auto'; kind = 'winui' }, @{ mode = 'progman-front'; kind = 'winui' }, @{ mode = 'workerw-child'; kind = 'winui' },
    @{ mode = 'workerw-sibling'; kind = 'winui' }, @{ mode = 'progman'; kind = 'winui' }, @{ mode = 'auto'; kind = 'gdi' },
    @{ mode = 'auto'; kind = 'winui'; order = 'show-first' })) {
  $tag = "$($run.kind)-$($run.mode)$(if ($run.order) { '-' + $run.order })"
  Reset-Run
  $argv = @("--mode=$($run.mode)", "--kind=$($run.kind)", '--route=none')
  if ($run.order) { $argv += "--order=$($run.order)" }
  $t0 = NowMs
  $p = Start-Spike $exe $argv
  $s = Wait-State { param($s) $s.rendered -or $s.attachError } 25
  $ms = (NowMs) - $t0
  $res = [ordered]@{ attachError = $s.attachError; mode = $s.mode; rendered = $s.rendered; afterMs = $ms; window = $s.window }
  if ($s -and -not $s.attachError) {
    Start-Sleep -Milliseconds 800
    $res.screen = Measure-Layer "mode-$tag.png"
    $res.order = Order-Line
    if ($run.mode -eq 'auto' -and $run.kind -eq 'winui' -and -not $run.order) {
      # does a plain click on the layer's button reach it (no route)? Explorer's DefView is over it.
      $res.underCursorAtButton = [DeskProbe]::At($Btn.x + 150, $Btn.y + 20)
      [WinHarness]::Click($Btn.x + 150, $Btn.y + 20) | Out-Null
      Start-Sleep -Milliseconds 700
      $res.directClicks = @(Clicks).Count
      $res.directButtonYellow = (Measure-Layer '').buttonYellow
      $res.tree = [DeskProbe]::Tree()
    }
  }
  if ($p.HasExited) { $res.exited = $p.ExitCode }
  $res.log = @(Get-Content (Join-Path $Out 'log.txt') -ErrorAction SilentlyContinue | Select-Object -Last 8)
  Remove-Item (Join-Path $Out 'log.txt') -ErrorAction SilentlyContinue
  Note "mode_$tag" $res
  $modeResults[$tag] = $res
  Stop-Spike $p
}
$auto = $modeResults['winui-auto']
if ($auto.screen) {
  Check 'auto mode picked' ($auto.mode -eq 'progman-front') "mode $($auto.mode) (progman-front expected on 24H2+ / build 26100); order $($auto.order)"
  Note 'directClickWithoutRoute' ([ordered]@{ layerGotIt = ($auto.directClicks -gt 0); clicks = $auto.directClicks; underCursor = $auto.underCursorAtButton })
}

# ------------------------------------------------------------------ 2. the chosen mode, with the click route
Reset-Run
$t0 = NowMs
$p = Start-Spike $exe @('--mode=auto', '--kind=winui', '--route=hook')
$s = Wait-State { param($s) $s.rendered -or $s.attachError } 25
Check 'attached' ($s -and -not $s.attachError -and $s.window.parentClass -eq 'Progman') $(if ($s) { "mode $($s.mode), parent $($s.window.parentClass) $($s.window.parent), style $($s.window.style) ex $($s.window.exStyle), attach $($s.attachMs) ms, fit $($s.fitRect), rendered after $((NowMs) - $t0) ms; $($s.attachError)" } else { 'no state.json' })
if (-not $s -or $s.attachError) { Stop-Spike $p; Finish-Check }
Start-Sleep -Milliseconds 800
Note 'tree' ([DeskProbe]::Tree())
$order = Order-Line
$kids = [DeskProbe]::ProgmanChildren()
$me = [array]::IndexOf($kids, [long]$s.hwnd)
$dv = [array]::IndexOf(@($kids | ForEach-Object { [DeskProbe]::ClassOfHandle($_) }), 'SHELLDLL_DefView')
Check 'z-order: straight under the icons' ($dv -ge 0 -and $me -eq $dv + 1) "Progman children top to bottom: $order (layer at $me, DefView at $dv)"

$m = Measure-Layer 'layer.png'
Note 'layer' $m
Check 'renders over the wallpaper' ($m.fieldMagenta -ge 0.98 -and $m.meanDiffFromWallpaper -gt 40) "$([Math]::Round(100 * $m.fieldMagenta, 2)) % of $($m.fieldSamples) sampled px are the layer's magenta (icons, button, taskbar excluded); mean diff from the wallpaper grab $($m.meanDiffFromWallpaper)"
Check 'no frame ring' ($m.edgeMagenta -ge 0.98) "$([Math]::Round(100 * $m.edgeMagenta, 2)) % of the $($m.edgeSamples) px in the outer 3 px (left, top, right, icons excluded) are the layer's magenta; style $($s.window.style)"
Check 'button drawn' ($m.buttonCyan -ge 0.95) "$([Math]::Round(100 * $m.buttonCyan, 1)) % of the button band is cyan"
Check 'under the icons' ($m.icon -and $m.iconNotMagenta -ge 0.03 -and $m.iconRingMagenta -ge 0.9) "probe icon $($m.icon): $([Math]::Round(100 * $m.iconNotMagenta, 1)) % of its rect is NOT magenta (icon + label drawn over the layer), ring 6-14 px outside $([Math]::Round(100 * $m.iconRingMagenta, 1)) % magenta"

# a click on the layer's own button, through the route
$route0 = (Read-State).route
[WinHarness]::Click($Btn.x + 150, $Btn.y + 20) | Out-Null
Start-Sleep -Milliseconds 700
$c = @(Clicks)
$mc = Measure-Layer 'after-button-click.png'
$st = Read-State
Check 'click on the layer button reaches it' (@($c | Where-Object { $_.via -eq 'hook' }).Count -eq 1 -and $mc.buttonYellow -ge 0.95) "clicks.jsonl: $(@($c | ForEach-Object { $_.via }) -join ','); button band $([Math]::Round(100 * $mc.buttonYellow, 1)) % yellow; route $($st.route | ConvertTo-Json -Compress)"
# a click on the probe icon: Explorer's (the icon is selected), never the layer's
$pi = Get-ProbeIcon 4
if ($pi.icon) {
  $ix = $pi.icon.X + [int]($pi.icon.W / 2); $iy = $pi.icon.Y + [int]($pi.icon.H / 3)
  $at = [DeskProbe]::At($ix, $iy)
  [WinHarness]::Click($ix, $iy) | Out-Null
  Start-Sleep -Milliseconds 700
  $pi2 = Get-ProbeIcon 4
  $c2 = @(Clicks)
  Check 'click on an icon stays Explorer''s' ($pi2.icon.Selected -and $c2.Count -eq $c.Count) "under the cursor: $at; icon selected after: $($pi2.icon.Selected); layer clicks $($c.Count) -> $($c2.Count)"
  [WinHarness]::Click(900, 800) | Out-Null   # empty desktop: deselects
  Start-Sleep -Milliseconds 400
} else { Check 'click on an icon stays Explorer''s' $false 'no probe icon to click' }

# ------------------------------------------------------------------ Win+D (show desktop)
$np = $null
try { $np = Start-Process notepad.exe -PassThru; Start-Sleep -Seconds 2 } catch { Say "notepad: $_" }
$fgBefore = [DeskProbe]::Foreground()
$sent = [DeskProbe]::Chord(0x5B, 0x44)
Start-Sleep -Milliseconds 1500
$fgAfter = [DeskProbe]::Foreground()
$npIconic = $false
foreach ($q in @(Get-Process -Name notepad -ErrorAction SilentlyContinue)) { if ([WinHarness]::AnyIconic([uint32]$q.Id)) { $npIconic = $true } }
$md = Measure-Layer 'after-win-d.png'
$sd = Read-State
Note 'winD' ([ordered]@{ sent = $sent; foregroundBefore = $fgBefore; foregroundAfter = $fgAfter; notepadMinimised = $npIconic; order = (Order-Line) })
Check 'Win+D: the layer stays' ($md.fieldMagenta -ge 0.98 -and $sd.window.parentClass -eq 'Progman' -and $sd.window.visible) "after Win+D ($sent events; notepad minimised: $npIconic; foreground $fgAfter): $([Math]::Round(100 * $md.fieldMagenta, 2)) % magenta, parent $($sd.window.parentClass), visible $($sd.window.visible), icon over it $([Math]::Round(100 * $md.iconNotMagenta, 1)) %"
Get-Process -Name notepad -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
(New-Object -ComObject Shell.Application).MinimizeAll(); Start-Sleep -Milliseconds 800

# ------------------------------------------------------------------ Explorer restart (the recheck path)
$gen0 = (Read-State).gen; $rejoin0 = [int](Read-State).rejoins
$rx = Restart-Explorer
$t1 = NowMs
$s2 = Wait-State { param($s) ($s.gen -ne $gen0 -or [int]$s.rejoins -gt $rejoin0) -and $s.rendered -and $s.window.alive -and $s.window.parent -eq [DeskProbe]::Progman() } 40
$back = (NowMs) - $t1
Start-Sleep -Seconds 3
$path = if ($s2.gen -ne $gen0) { "remade (window destroyed with the old desktop, gen $gen0 -> $($s2.gen))" } elseif ([int]$s2.rejoins -gt $rejoin0) { "rejoined (window survived, recheck re-attached: $($s2.lastMove))" } else { 'not back' }
Note 'explorerRestart' ([ordered]@{ explorer = $rx; layerBackMsAfterNewProgman = $back; path = $path; state = $s2; tree = [DeskProbe]::Tree() })
Check 'process survives an Explorer restart' (-not $p.HasExited) $(if ($p.HasExited) { "exited with $($p.ExitCode)" } else { "alive; explorer back in $($rx.ms) ms" })
$mr = Measure-Layer 'after-explorer-restart.png'
$kids = [DeskProbe]::ProgmanChildren(); $me = [array]::IndexOf($kids, [long]$s2.hwnd)
$dv = [array]::IndexOf(@($kids | ForEach-Object { [DeskProbe]::ClassOfHandle($_) }), 'SHELLDLL_DefView')
Check 'back under the icons after an Explorer restart' ($mr.fieldMagenta -ge 0.98 -and $me -eq $dv + 1 -and $dv -ge 0) "$path, $back ms after the new Progman; $([Math]::Round(100 * $mr.fieldMagenta, 2)) % magenta; order $(Order-Line)"
Check 'icons over the layer after the restart' ($mr.icon -and $mr.iconNotMagenta -ge 0.03 -and $mr.iconRingMagenta -ge 0.9) "probe icon $($mr.icon): $([Math]::Round(100 * $mr.iconNotMagenta, 1)) % not magenta, ring $([Math]::Round(100 * $mr.iconRingMagenta, 1)) % magenta"
# the route after the restart: a second button click
[WinHarness]::Click($Btn.x + 150, $Btn.y + 60) | Out-Null
Start-Sleep -Milliseconds 700
$c3 = @(Clicks | Where-Object { $_.via -eq 'hook' })
Check 'button click after the restart' ($c3.Count -ge 2) "hook clicks so far: $($c3.Count); route $((Read-State).route | ConvertTo-Json -Compress)"

# ------------------------------------------------------------------ cost
if (-not $p.HasExited) {
  Note 'memory' (Get-Mem $p.Id)
  Note 'cpuIdle' (Get-Cpu $p.Id 5)
}
Get-Content (Join-Path $Out 'log.txt') -ErrorAction SilentlyContinue | Select-Object -Last 40 | ForEach-Object { Say "app: $_" }
Stop-Spike $p
Remove-Item $probeFile -ErrorAction SilentlyContinue
Finish-Check
