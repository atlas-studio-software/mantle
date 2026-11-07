# bar/check-dpi.ps1 - primitive 6, multi-monitor + DPI (RUNNER ONLY: it changes the display scale and
# resolution, may install a virtual display driver, and puts everything back at the end).
#   pwsh -NoProfile -File native/spikes/windowing/bar/check-dpi.ps1
# (a) the one display's scale changed at runtime (DisplayConfigSetDeviceInfo, undocumented type -4):
#     the dock bar and the hover bar re-place in physical px at 100 / 125 / 150 %, the app bar re-reserves,
#     the marker square stays crisp; a change while the hover bar is hidden is picked up at the reveal.
# (c) resolution change (ChangeDisplaySettingsEx): the pill re-centres and clamps (min(1120, w - 28)).
# (b) a second monitor from a virtual display driver (time-boxed): plug, primary switch, mixed DPI, unplug.
. (Join-Path $PSScriptRoot '..\harness\Common.ps1') -Lane bar-dpi
Add-Type -Path (Join-Path $PSScriptRoot 'probe\BarProbe.cs')
Say ("probe: " + [BarProbe]::Aware())

function J($s) { $s | ConvertFrom-Json }
function Events($tag) { $f = Join-Path $Out "$tag-events.jsonl"; if (Test-Path $f) { @(Get-Content $f | ForEach-Object { $_ | ConvertFrom-Json }) } else { @() } }
function Wait-Ev($tag, $ev, $since, [double]$timeoutS, [scriptblock]$where = { $true }) {
  $end = (Get-Date).AddSeconds($timeoutS)
  while ((Get-Date) -lt $end) { $e = @(Events $tag | Where-Object { $_.ev -eq $ev -and $_.t -ge $since } | Where-Object $where); if ($e.Count) { return $e[-1] }; Start-Sleep -Milliseconds 60 }
  return $null
}
function Start-Bar($tag, [string[]]$more) { $p = Start-Spike $exe (@("--tag=$tag") + $more); return @{ p = $p; tag = $tag; r = (Wait-Json (Join-Path $Out "$tag-ready.json") 40) } }
function Stop-Bar($b) { [BarProbe]::Close($b.r.hwnd) | Out-Null; $x = Wait-Json (Join-Path $Out "$($b.tag)-exit.json") 10; if (-not $x) { Problem "$($b.tag): no exit.json"; Stop-Tree $b.p.Id }; Start-Sleep -Milliseconds 300; $x }
function Prim { (J ([BarProbe]::Monitors())) | Where-Object { $_.primary } | Select-Object -First 1 }
function Want($m, [double]$s) {   # main.js barGeometry in physical px for monitor rect $m at scale $s
  $wDip = [Math]::Min(1120, [Math]::Max(120, $m.w / $s - 28)); $pw = [int][Math]::Round($wDip * $s); $ph = [int][Math]::Round(48 * $s); $lift = [int][Math]::Round(14 * $s)
  @{ x = $m.x + [int][Math]::Floor(($m.w - $pw) / 2); y = $m.y + $m.h - $lift - $ph; w = $pw; h = $ph; lift = $lift }
}
function Same($p, $w) { $p.x -eq $w.x -and $p.y -eq $w.y -and $p.w -eq $w.w -and $p.h -eq $w.h }
function Fill($b, $p) { J ([BarProbe]::FillBox($p.x - 10, $p.y - 10, $p.w + 20, $p.h + 30, $b.r.fill[0], $b.r.fill[1], $b.r.fill[2], 6)) }
function Set-Scale([int]$pct, [string]$dev = '') {
  $r = [BarProbe]::DpiSetPercent($dev, $pct); Say "scale -> ${pct}%: $r"; Start-Sleep -Milliseconds 1500; return $r
}

$exe = Build-Spike 'bar\Bar.csproj'
Check 'build' ($null -ne $exe) $(if ($exe) { "$($Results.build_Bar.seconds) s" } else { 'see build-Bar.log' })
if (-not $exe) { Finish-Check }

$tb0 = [BarProbe]::TaskbarState()
[BarProbe]::SetTaskbarState($tb0 -bor 1); Start-Sleep -Milliseconds 800   # so the work area measures the app bar alone
$dpi0 = J ([BarProbe]::DpiInfo())
Note 'dpi_before' $dpi0
$pct0 = [int]$dpi0[0].percent
$m0 = Prim
Note 'monitors_before' (J ([BarProbe]::Monitors()))
[BarProbe]::Cursor(960, 300) | Out-Null

# ------------------------------------------------------------------ (a) scale at runtime, dock bar
$d = Start-Bar 'dpi' @('--mode=dock')
Check 'dock bar ready' ($null -ne $d.r) "dpi $($d.r.dpi), pill $($d.r.pill | ConvertTo-Json -Compress), xaml scale $($d.r.xamlScale)"
$scales = @(100, 125, 150)
foreach ($pct in $scales) {
  $s = $pct / 100.0; $t = NowMs
  $set = if ($pct -eq 100 -and $pct0 -eq 100) { 'already 100 %' } else { Set-Scale $pct }
  $pl = if ($pct -eq $pct0) { [pscustomobject]@{ dpi = $d.r.dpi; pill = $d.r.pill; win = $d.r.win } } else { Wait-Ev 'dpi' 'place' $t 8 { $_.dpi -eq [int](96 * $s) } }
  Start-Sleep -Milliseconds 900
  $xr = Wait-Ev 'dpi' 'xamlRoot' $t 1 { [Math]::Abs($_.scale - $s) -lt 0.01 }
  $xs = if ($xr) { $xr.scale } elseif ($pct -eq $pct0) { $d.r.xamlScale } else { $null }
  $mon = Prim; $want = Want $mon $s
  $wa = J ([BarProbe]::WorkArea()); $wr = J ([BarProbe]::Rect($d.r.hwnd))
  $ok = $null -ne $pl -and $pl.pill.w -eq $want.w -and $pl.pill.h -eq $want.h -and $wr.h -eq $want.h -and ($wa.y + $wa.h) -eq ($mon.rect.y + $mon.rect.h - $want.h) -and $null -ne $xs -and [Math]::Abs($xs - $s) -lt 0.01
  Check "dpi ${pct}%: dock bar re-placed" $ok ("set: $set; monitor dpi $($mon.dpi); app place $(if ($pl) { "dpi $($pl.dpi) pill $($pl.pill | ConvertTo-Json -Compress)" } else { 'NONE in 8 s' }); " +
    "want $($want.w)x$($want.h); window $($wr | ConvertTo-Json -Compress); work area bottom $($wa.y + $wa.h) (want $($mon.rect.y + $mon.rect.h - $want.h)); XAML scale $xs")
  if ($pl) {
    $p = $pl.pill; $n = [int][Math]::Round(20 * $s)
    $mk = J ([BarProbe]::Marker($p.x, $p.y, [int](70 * $s), $p.h, $d.r.ink[0], $d.r.ink[1], $d.r.ink[2], $d.r.fill[0], $d.r.fill[1], $d.r.fill[2], 24))
    Check "dpi ${pct}%: marker crisp" ($mk.found -and [Math]::Abs($mk.w - $n) -le 1 -and [Math]::Abs($mk.h - $n) -le 1 -and $mk.softRow -le 2 -and $mk.softCol -le 2) "20 DIP square measured $($mk.w)x$($mk.h) px (want $n), soft edge pixels row $($mk.softRow) col $($mk.softCol)"
    Save-Shot "dpi-$pct.png" @{ x = $p.x - 10; y = $p.y - 10; w = [Math]::Min($p.w + 20, $mon.rect.w); h = $p.h + 20 } | Out-Null
  }
}
Stop-Bar $d | Out-Null

# ------------------------------------------------------------------ (a) hover bar at 150 %, then a change while hidden
$h = Start-Bar 'dpih' @('--mode=hover', '--anim=comp')
foreach ($step in @(@{ pct = 150; set = $false }, @{ pct = 125; set = $true }, @{ pct = $pct0; set = $true })) {
  $s = $step.pct / 100.0
  if ($step.set) { Set-Scale $step.pct | Out-Null }   # the bar is hidden while this happens
  $mon = Prim; $want = Want $mon $s
  $t = NowMs
  [BarProbe]::Cursor($mon.rect.x + [int]($mon.rect.w * 0.4), $mon.rect.y + $mon.rect.h - 1) | Out-Null
  $rv = Wait-Ev 'dpih' 'revealDone' $t 3
  $rr = Wait-Ev 'dpih' 'reveal' $t 1
  Start-Sleep -Milliseconds 200
  $fb = Fill $h $want
  Check "hover at $($step.pct)%$(if ($step.set) { ' (changed while hidden)' } else { '' }): reveal geometry" ($null -ne $rv -and $null -ne $rr -and (Same $rr.pill $want) -and $fb.found -and [Math]::Abs($fb.w - $want.w) -le 4 -and [Math]::Abs($fb.h - $want.h) -le 3) `
    "band = bottom $([Math]::Round(4 * $s)) px; app reveal pill $($rr.pill | ConvertTo-Json -Compress) dpi $($rr.dpi), want $($want | ConvertTo-Json -Compress); pill fill on screen $($fb | ConvertTo-Json -Compress)"
  Save-Shot "hover-dpi-$($step.pct).png" @{ x = $want.x - 10; y = $want.y - 30; w = $want.w + 20; h = $mon.rect.y + $mon.rect.h - $want.y + 30 } | Out-Null
  [BarProbe]::Cursor(960, 300) | Out-Null
  Wait-Ev 'dpih' 'hidden' $t 3 | Out-Null
}
Stop-Bar $h | Out-Null
$dpiNow = J ([BarProbe]::DpiInfo())
Check 'scale put back' ([int]$dpiNow[0].percent -eq $pct0) "now $($dpiNow[0].percent) %, before $pct0 %"

# ------------------------------------------------------------------ (c) resolution change
$d = Start-Bar 'res' @('--mode=dock')
foreach ($res in @(@(1024, 768), @(1920, 1080))) {
  $t = NowMs
  $code = [WinHarness]::SetResolution($res[0], $res[1]); Start-Sleep -Milliseconds 1500
  $mon = Prim; $want = Want $mon 1.0
  $pl = Wait-Ev 'res' 'place' $t 6 { $_.mon.w -eq $res[0] }
  $wa = J ([BarProbe]::WorkArea()); $wr = J ([BarProbe]::Rect($d.r.hwnd))
  Check "resolution $($res[0])x$($res[1]): re-placed" ($code -eq 0 -and $null -ne $pl -and $pl.pill.w -eq $want.w -and $pl.pill.x -eq $want.x -and $wr.w -eq $mon.rect.w -and ($wa.y + $wa.h) -eq ($mon.rect.h - 48)) `
    "ChangeDisplaySettings $code; place '$($pl.why)' pill $($pl.pill | ConvertTo-Json -Compress) (want x $($want.x) w $($want.w)); window $($wr | ConvertTo-Json -Compress); work area bottom $($wa.y + $wa.h)"
}
Save-Shot 'res-1920.png' @{ x = 0; y = 1080 - 80; w = 1920; h = 80 } | Out-Null
Stop-Bar $d | Out-Null

# ------------------------------------------------------------------ (b) a second monitor (virtual display driver), time-boxed
$mm = [ordered]@{ attempted = 'Amyuni usbmmidd_v2 (IddCx virtual display), deviceinstaller64 install + enableidd 1' }
try {
  $dl = Join-Path $env:RUNNER_TEMP 'vdd-zip'; $ex = Join-Path $env:RUNNER_TEMP 'vdd'
  New-Item -ItemType Directory -Force $dl, $ex | Out-Null
  Invoke-WebRequest 'https://www.amyuni.com/downloads/usbmmidd_v2.zip' -OutFile (Join-Path $dl 'usbmmidd_v2.zip') -TimeoutSec 60
  Expand-Archive (Join-Path $dl 'usbmmidd_v2.zip') -DestinationPath $ex -Force
  $inst = Get-ChildItem $ex -Recurse -Filter 'deviceinstaller64.exe' | Select-Object -First 1
  $inf = Get-ChildItem $ex -Recurse -Filter 'usbmmidd.inf' | Select-Object -First 1
  $cat = Get-ChildItem $ex -Recurse -Filter '*.cat' | Select-Object -First 1
  if ($cat) {
    $sig = Get-AuthenticodeSignature $cat.FullName
    $mm.catalog = "$($sig.Status): $($sig.SignerCertificate.Subject)"
    if ($sig.SignerCertificate) {
      $cer = Join-Path $dl 'signer.cer'; [IO.File]::WriteAllBytes($cer, $sig.SignerCertificate.Export('Cert'))
      Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
    }
  }
  function Run-Inst([string[]]$a) {
    $o = Join-Path $dl (((('inst-' + $a[0] + '-' + $a[-1]) -replace '[^\w-]', '')) + '.txt')
    $pr = Start-Process $inst.FullName -ArgumentList $a -WorkingDirectory $inst.DirectoryName -PassThru -RedirectStandardOutput $o -NoNewWindow
    if (-not $pr.WaitForExit(90000)) { $pr.Kill(); return "timed out after 90 s" }
    return "exit $($pr.ExitCode): " + ((Get-Content $o -ErrorAction SilentlyContinue) -join ' / ')
  }
  $mm.install = Run-Inst @('install', $inf.FullName, 'usbmmidd')
  $b = Start-Bar 'mm' @('--mode=hover', '--anim=comp')
  $devs0 = J ([BarProbe]::Devices())
  $t = NowMs
  $mm.enable = Run-Inst @('enableidd', '1')
  $end = (Get-Date).AddSeconds(25)
  while ((Get-Date) -lt $end -and @(J ([BarProbe]::Monitors())).Count -lt 2) { Start-Sleep -Milliseconds 300 }
  $mons = @(J ([BarProbe]::Monitors())); $mm.monitorsAfterPlug = $mons
  $mm.devices = J ([BarProbe]::Devices())
  if ($mons.Count -lt 2) {
    $mm.result = "NOT TESTED: no second monitor appeared within 25 s of enableidd 1 (see install/enable output)"
  } else {
    $virt = $mons | Where-Object { -not $_.primary } | Select-Object -First 1
    $pl = Wait-Ev 'mm' 'place' $t 5
    $want = Want $m0.rect 1.0
    Check 'second monitor plugged: bar stays on the primary' ($null -ne $pl -and (Same $pl.pill $want)) "monitors $($mons.Count); virtual $($virt.device) at $($virt.rect | ConvertTo-Json -Compress) dpi $($virt.dpi); app place '$($pl.why)' pill $($pl.pill | ConvertTo-Json -Compress)"
    # the virtual monitor becomes the primary: the bar moves there (Electron: one bar, on the primary)
    $t = NowMs
    $mm.setPrimary = [BarProbe]::SetPrimary($virt.device); Start-Sleep -Milliseconds 2000
    $np = Prim; $want = Want $np.rect ($np.dpi / 96.0)
    $pl = Wait-Ev 'mm' 'place' $t 6 { $_.mon.x -eq $np.rect.x -and $_.mon.w -eq $np.rect.w }
    [BarProbe]::Cursor($np.rect.x + [int]($np.rect.w * 0.4), $np.rect.y + $np.rect.h - 1) | Out-Null
    $rv = Wait-Ev 'mm' 'revealDone' $t 4; Start-Sleep -Milliseconds 200
    $fb = Fill $b $want
    Check 'primary switched: the bar moves to the new primary' ($null -ne $pl -and (Same $pl.pill $want) -and $null -ne $rv -and $fb.found) "set primary: $($mm.setPrimary); new primary $($np.device) $($np.rect | ConvertTo-Json -Compress); app place '$($pl.why)' pill $($pl.pill | ConvertTo-Json -Compress); revealed there: fill $($fb | ConvertTo-Json -Compress)"
    Save-Shot 'mm-primary-virtual.png' @{ x = $want.x - 10; y = $want.y - 30; w = $want.w + 20; h = $want.h + 60 } | Out-Null
    [BarProbe]::Cursor($np.rect.x + 100, $np.rect.y + 100) | Out-Null; Wait-Ev 'mm' 'hidden' $t 3 | Out-Null
    # mixed DPI: the virtual monitor (primary) at 150 %, the runner's own display at 100 %
    $t = NowMs
    $mm.mixedDpi = Set-Scale 150 $virt.device
    $np = Prim; $want = Want $np.rect ($np.dpi / 96.0)
    [BarProbe]::Cursor($np.rect.x + [int]($np.rect.w * 0.4), $np.rect.y + $np.rect.h - 1) | Out-Null
    $rr = Wait-Ev 'mm' 'reveal' $t 4; Start-Sleep -Milliseconds 800
    $fb = Fill $b $want
    Check 'mixed DPI: the bar on a 150 % primary beside a 100 % display' ($np.dpi -eq 144 -and $null -ne $rr -and (Same $rr.pill $want) -and $fb.found -and [Math]::Abs($fb.h - $want.h) -le 3) "set: $($mm.mixedDpi); primary dpi $($np.dpi); app reveal pill $($rr.pill | ConvertTo-Json -Compress) (want $($want | ConvertTo-Json -Compress)); fill $($fb | ConvertTo-Json -Compress); others: $((J ([BarProbe]::Monitors())) | ConvertTo-Json -Compress)"
    Save-Shot 'mm-mixed-150.png' @{ x = $want.x - 10; y = $want.y - 30; w = $want.w + 20; h = $want.h + 60 } | Out-Null
    [BarProbe]::Cursor($np.rect.x + 100, $np.rect.y + 100) | Out-Null; Wait-Ev 'mm' 'hidden' $t 3 | Out-Null
    # back, then unplug
    $orig = (J ([BarProbe]::Devices())) | Where-Object { $_.name -ne $virt.device -and ($_.flags -band 1) } | Select-Object -First 1
    $mm.backPrimary = [BarProbe]::SetPrimary($orig.name); Start-Sleep -Milliseconds 1500
    $t = NowMs
    $mm.disable = Run-Inst @('enableidd', '0')
    $end = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $end -and @(J ([BarProbe]::Monitors())).Count -gt 1) { Start-Sleep -Milliseconds 300 }
    Start-Sleep -Milliseconds 1200
    $np = Prim; $want = Want $np.rect ($np.dpi / 96.0)
    $last = @(Events 'mm' | Where-Object { $_.ev -eq 'place' })[-1]
    Check 'second monitor unplugged: the bar is on the remaining display' (@(J ([BarProbe]::Monitors())).Count -eq 1 -and (Same $last.pill $want)) "monitors now $(@(J ([BarProbe]::Monitors())).Count); last place '$($last.why)' pill $($last.pill | ConvertTo-Json -Compress), want $($want | ConvertTo-Json -Compress)"
    $mm.result = 'tested'
  }
  Stop-Bar $b | Out-Null
} catch {
  $mm.result = "NOT TESTED: the attempt threw: $($_.Exception.Message)"
}
Note 'virtual_monitor' $mm

if ([int](J ([BarProbe]::DpiInfo()))[0].percent -ne $pct0) { Set-Scale $pct0 | Out-Null }
[BarProbe]::SetTaskbarState($tb0)
Finish-Check
