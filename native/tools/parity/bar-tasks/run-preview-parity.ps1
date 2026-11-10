<#
  Preview parity (native-bar-tasks): the bar's window flyout with its live DWM previews, Electron Mantle vs native,
  over the SAME two fixture windows.

    cloudtest --recipe mantle-native-public -- "pwsh -NoProfile -File native/tools/parity/bar-tasks/run-preview-parity.ps1 -Build"

  1. The desk as run-parity.ps1 sets it (wallpaper snow-peak, the Windows theme per run, the runner's taskbar on
     auto-hide, the pointer parked mid-screen), then ONE powershell.exe with two WinForms forms, "Preview fixture 0"
     (red) and "Preview fixture 1" (blue), top left, away from the bar.
  2. Electron: `electron . --test --test-live-bar --flyout=powershell --screenshot=<dir>` (main.js runFlyoutTest: the same openFly()
     a 400 ms hover runs, no input injected), visible (MANTLE_TEST_VISIBLE=1). Its own flyout.png / -page / -dwm
     are kept; the SCORED capture is the real screen, grabbed 4 s after it asked the page to open the flyout (main
     captures at 5 s), because only the screen has the glass, the page and DWM's pictures together.
  3. Native: MantleNative.exe --test with MANTLE_TEST_FLYOUT=powershell (BarWindow.Previews.cs TestFlyout, the same
     OpenFly a click runs) and an empty scratch pin folder; the screen is grabbed 700 ms after its log says the
     pictures are live.
  4. In each screen grab the two pictures are found by their colours (the bottom half of the screen only, so the
     fixture windows themselves never count), the flyout's rectangle is worked out from the left picture's box
     (bar.css: 200x112 box, 6 px card padding, 24 px head, 8 px pop padding + border; scale 1 on the runner), and
     that rectangle plus -Margin px is cropped out of both: native/out/parity/bar-tasks/preview/<theme>/
     electron-preview.png, native-preview.png, preview-pair.png (Electron above, native below), plus both full
     screens. One PREVIEW-PARITY line per theme (SSIM, diff px) and a summary line. No gate yet: the numbers are
     reported, and a missing capture is the only FAILED.

  Runner only (CI / GITHUB_ACTIONS, or MANTLE_PARITY_LOCAL=1 on a machine nobody is using): it changes the
  wallpaper, theme and taskbar, and shows both apps and the fixture windows.
#>
param(
  [ValidateSet('light', 'dark', 'both')] [string]$Theme = 'both',
  [string]$Out,
  [int]$Margin = 16,
  [int]$PixelThreshold = 24,
  [switch]$Build
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
function Say($m) { Write-Host ("preview-parity {0}: {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) }
if (-not $env:CI -and -not $env:GITHUB_ACTIONS -and $env:MANTLE_PARITY_LOCAL -ne '1') {
  Write-Host 'PREVIEW-PARITY: refused (runner only: it changes the wallpaper, theme and taskbar, and shows both apps; MANTLE_PARITY_LOCAL=1 overrides it on a machine nobody is using)'
  exit 2
}
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
if (-not $Out) { $Out = Join-Path $root 'native\out\parity\bar-tasks\preview' }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path
if ($Build) {
  & (Join-Path $root 'native/tools/ci.ps1') -Stages build
}
$exe = Join-Path $root 'native/out/bin/MantleNative/MantleNative.exe'
if (-not (Test-Path $exe)) { Write-Host 'PREVIEW-PARITY: FAILED (no native/out/bin/MantleNative/MantleNative.exe: run with -Build)'; exit 1 }
$electronExe = (& node -e "console.log(require(require('path').join(process.argv[1],'node_modules','electron')))" $root 2>$null | Select-Object -Last 1)
if (-not $electronExe -or -not (Test-Path $electronExe)) { Write-Host "PREVIEW-PARITY: FAILED (electron is not installed in $root: npm install first)"; exit 1 }

Add-Type -Path (Join-Path $PSScriptRoot '..\ParityNative.cs')
Add-Type -AssemblyName System.Drawing
function Save-Bgra([byte[]]$px, [int]$w, [int]$h, [string]$path) {
  $bmp = [System.Drawing.Bitmap]::new($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $d = $bmp.LockBits([System.Drawing.Rectangle]::new(0, 0, $w, $h), [System.Drawing.Imaging.ImageLockMode]::WriteOnly, $bmp.PixelFormat)
  for ($row = 0; $row -lt $h; $row++) { [System.Runtime.InteropServices.Marshal]::Copy($px, $row * $w * 4, [IntPtr]::Add($d.Scan0, $row * $d.Stride), $w * 4) }
  $bmp.UnlockBits($d); $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
[void][ParityNative]::DpiAware()
$scr = [ParityNative]::Screen(); $W = $scr[0]; $H = $scr[1]
Say "screen ${W}x${H}, dpi $([ParityNative]::PrimaryDpi())"
$wall = Join-Path $root 'assets\backgrounds\snow-peak.jpg'
Say ("wallpaper " + [ParityNative]::SetWallpaper($wall))
$pers = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
New-Item -Force -Path $pers | Out-Null
Set-ItemProperty -Path $pers -Name EnableTransparency -Value 1 -Type DWord
function Set-WinTheme([string]$t) {
  $v = if ($t -eq 'light') { 1 } else { 0 }
  Set-ItemProperty -Path $pers -Name AppsUseLightTheme -Value $v -Type DWord
  Set-ItemProperty -Path $pers -Name SystemUsesLightTheme -Value $v -Type DWord
  [ParityNative]::BroadcastTheme()
}
[ParityNative]::TaskbarAutoHide($true)
$park = { [ParityNative]::ParkCursor([int]($W / 2), [int]($H / 3)) }
& $park

# the two fixture windows, one process (one app in both bars)
$fx = "Add-Type -AssemblyName System.Windows.Forms; Add-Type -AssemblyName System.Drawing; `$fs = @(); " +
  "foreach (`$i in 0, 1) { `$f = New-Object System.Windows.Forms.Form; `$f.Text = ""Preview fixture `$i""; `$f.StartPosition = 'Manual'; " +
  "`$f.Location = New-Object System.Drawing.Point((40 + 60 * `$i), (40 + 40 * `$i)); `$f.Size = New-Object System.Drawing.Size(420, 280); " +
  "`$f.BackColor = [System.Drawing.Color]::FromName(@('Red', 'Blue')[`$i]); `$fs += `$f }; foreach (`$x in `$fs) { `$x.Show() }; [System.Windows.Forms.Application]::Run(`$fs[0])"
$fixture = Start-Process powershell.exe -ArgumentList '-NoProfile', '-EncodedCommand', ([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($fx))) -PassThru
Start-Sleep -Seconds 3

# the left picture's box -> the flyout's rectangle (bar.css, scale 1): box 200 x 112; card padding 6, head 24 + gap 5;
# pop padding 7 + border 1; two cards 2 x 212 + 6 wide + 16
function Find-Flyout([byte[]]$px, [int]$w, [int]$h) {
  $best = @{}
  foreach ($c in 'red', 'blue') { $best[$c] = @{ n = 0; x0 = [int]::MaxValue; x1 = -1; y0 = [int]::MaxValue; y1 = -1 } }
  for ($y = [int]($h / 2); $y -lt $h; $y += 2) {
    $o = $y * $w * 4
    for ($x = 0; $x -lt $w; $x += 2) {
      $i = $o + $x * 4; $b = $px[$i]; $g = $px[$i + 1]; $r = $px[$i + 2]
      $c = if ($r -gt 170 -and $g -lt 70 -and $b -lt 70) { 'red' } elseif ($b -gt 170 -and $r -lt 70 -and $g -lt 70) { 'blue' } else { $null }
      if ($c) { $s = $best[$c]; $s.n++; if ($x -lt $s.x0) { $s.x0 = $x }; if ($x -gt $s.x1) { $s.x1 = $x }; if ($y -lt $s.y0) { $s.y0 = $y }; if ($y -gt $s.y1) { $s.y1 = $y } }
    }
  }
  $found = @($best.Values | Where-Object { $_.n -ge 200 })
  if ($found.Count -lt 2) { return $null }
  $left = $found | Sort-Object { $_.x0 } | Select-Object -First 1
  $cx = ($left.x0 + $left.x1) / 2; $cy = ($left.y0 + $left.y1) / 2
  $fx0 = [int]($cx - 100 - 6 - 8); $fy0 = [int]($cy - 56 - 5 - 24 - 6 - 8)
  return @{ X = $fx0; Y = $fy0; W = 2 * 212 + 6 + 16; H = 176; Pictures = "red $($best.red.x0),$($best.red.y0)..$($best.red.x1),$($best.red.y1); blue $($best.blue.x0),$($best.blue.y0)..$($best.blue.x1),$($best.blue.y1)" }
}

function Crop([byte[]]$px, [int]$w, $r, [int]$m) {
  $cw = $r.W + 2 * $m; $ch = $r.H + 2 * $m; $out = New-Object byte[] ($cw * $ch * 4)
  for ($row = 0; $row -lt $ch; $row++) {
    $sy = $r.Y - $m + $row; $sx = $r.X - $m
    if ($sy -lt 0 -or $sx -lt 0) { continue }
    [Array]::Copy($px, ($sy * $w + $sx) * 4, $out, $row * $cw * 4, $cw * 4)
  }
  return @{ Px = $out; W = $cw; H = $ch }
}

$themes = if ($Theme -eq 'both') { @('light', 'dark') } else { @($Theme) }
$lines = @(); $failed = @()
foreach ($t in $themes) {
  $dir = Join-Path $Out $t; New-Item -ItemType Directory -Force -Path $dir | Out-Null
  Set-WinTheme $t; Start-Sleep -Seconds 2; & $park
  $grabs = @{}

  # Electron
  $eo = Join-Path $dir 'electron-stdout.txt'; $ee = Join-Path $dir 'electron-stderr.txt'
  $env:MANTLE_TEST_VISIBLE = '1'; $env:MANTLE_BAR_TEST_CURSOR = '1'
  $ep = Start-Process -FilePath $electronExe -ArgumentList '.', '--test', "--profile-name=prevpar-$t", "--theme=$t", '--test-live-bar', '--no-desktop', '--flyout=powershell', "--screenshot=$(Join-Path $dir 'electron-own')" `
    -WorkingDirectory $root -PassThru -RedirectStandardOutput $eo -RedirectStandardError $ee
  $end = (Get-Date).AddSeconds(150); $asked = $null
  while ((Get-Date) -lt $end -and -not $ep.HasExited) {
    if (-not $asked -and (Test-Path $eo) -and (Select-String -Path $eo -SimpleMatch 'asking the bar page to open the flyout' -Quiet)) { $asked = Get-Date }
    if ($asked -and ((Get-Date) - $asked).TotalMilliseconds -ge 4000) { $grabs.electron = [ParityNative]::Grab(0, 0, $W, $H); break }
    Start-Sleep -Milliseconds 100
  }
  if (-not $grabs.electron) { $failed += "electron $t (no flyout asked for in 150 s; exited: $($ep.HasExited))" }
  $ep.WaitForExit(30000) | Out-Null
  if (-not $ep.HasExited) { Stop-Process -Id $ep.Id -Force -ErrorAction SilentlyContinue }
  Get-Content $eo -ErrorAction SilentlyContinue | Where-Object { $_ -match '--flyout' } | ForEach-Object { Say "electron ${t}: $_" }
  & $park; Start-Sleep -Seconds 2

  # native
  $ud = Join-Path ([IO.Path]::GetTempPath()) "mantle-native-prevpar-$t"; Remove-Item -Recurse -Force $ud -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force -Path $ud | Out-Null
  $pins = Join-Path $ud 'pins'; New-Item -ItemType Directory -Force -Path $pins | Out-Null
  $env:MANTLE_TEST_FLYOUT = 'powershell'; $env:MANTLE_TEST_PINS_DIR = $pins
  $np = Start-Process -FilePath $exe -ArgumentList '--test', "--profile-name=prevpar-$t", "--user-data=$ud", "--theme=$t" -PassThru
  Remove-Item Env:MANTLE_TEST_FLYOUT, Env:MANTLE_TEST_PINS_DIR -ErrorAction SilentlyContinue
  $end = (Get-Date).AddSeconds(60); $nlog = $null
  while ((Get-Date) -lt $end -and -not $np.HasExited) {
    if (-not $nlog) { $nlog = Get-ChildItem -Path $ud -Recurse -Filter 'mantle-native.log' -ErrorAction SilentlyContinue | Select-Object -First 1 }
    if ($nlog -and (Select-String -Path $nlog.FullName -Pattern 'bar previews: .*: \d+: live' -Quiet)) { Start-Sleep -Milliseconds 700; $grabs.native = [ParityNative]::Grab(0, 0, $W, $H); break }
    Start-Sleep -Milliseconds 150
  }
  if ($nlog) { Select-String -Path $nlog.FullName -Pattern 'bar previews' | Select-Object -First 6 | ForEach-Object { Say "native ${t}: $($_.Line)" } }
  if (-not $grabs.native) { $failed += "native $t (no live preview line in 60 s; exited: $($np.HasExited))" }
  if (-not $np.HasExited) { Stop-Process -Id $np.Id -Force -ErrorAction SilentlyContinue }
  & $park; Start-Sleep -Seconds 1

  $crops = @{}
  foreach ($k in 'electron', 'native') {
    if (-not $grabs.$k) { continue }
    Save-Bgra $grabs.$k $W $H (Join-Path $dir "$k-full.png")
    $r = Find-Flyout $grabs.$k $W $H
    if (-not $r) { $failed += "$k $t (the two pictures were not found on screen: no live previews in that capture)"; continue }
    Say "$k ${t}: flyout at $($r.X),$($r.Y) $($r.W)x$($r.H) (pictures: $($r.Pictures))"
    $c = Crop $grabs.$k $W $r $Margin
    $crops.$k = $c
    Save-Bgra $c.Px $c.W $c.H (Join-Path $dir "$k-preview.png")
  }
  if ($crops.electron -and $crops.native) {
    $a = $crops.electron; $b = $crops.native
    $s = [ParityNative]::Diff($a.Px, $b.Px, $a.W, $a.H, @(), $PixelThreshold)
    $pair = New-Object byte[] ($a.W * $a.H * 8)
    [Array]::Copy($a.Px, 0, $pair, 0, $a.Px.Length); [Array]::Copy($b.Px, 0, $pair, $a.Px.Length, $b.Px.Length)
    Save-Bgra $pair $a.W (2 * $a.H) (Join-Path $dir 'preview-pair.png')
    $line = "PREVIEW-PARITY ${t}: ssim $([Math]::Round($s.Ssim, 4)) diffpx $([Math]::Round($s.DiffPxPct, 2))% mean abs $([Math]::Round($s.MeanAbs, 2)) ($($a.W)x$($a.H), threshold $PixelThreshold)"
    Write-Host $line; $lines += $line
  }
}
if ($fixture -and -not $fixture.HasExited) { Stop-Process -Id $fixture.Id -Force -ErrorAction SilentlyContinue }
[ParityNative]::TaskbarAutoHide($false)
if ($failed.Count) { Write-Host ("PREVIEW-PARITY: FAILED ({0})" -f ($failed -join '; ')); exit 1 }
Write-Host ("PREVIEW-PARITY: ok ({0})" -f ($lines -join '; '))
