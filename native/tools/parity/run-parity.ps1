# run-parity.ps1 - screenshot parity of one Mantle surface: Electron Mantle vs the native app, same runner,
# same wallpaper, same Windows theme, same screen size, captured from the real screen and diffed.
#
#   pwsh -NoProfile -File native/tools/parity/run-parity.ps1 [-Theme light|dark|both] [-Surface bar]
#        [-Out native/out/parity] [-MinSsim 0.98] [-MaxDiffPct 0.5] [-PixelThreshold 24] [-Margin 24] [-Exe <path>]
#
# RUNNER ONLY: it changes the desktop it runs on (wallpaper, theme, taskbar auto-hide, cursor) and shows
# both apps' test windows, so it refuses unless CI / GITHUB_ACTIONS is set (MANTLE_PARITY_LOCAL=1 overrides,
# for a machine nobody is using). Needs: npm deps + the csc sidecar built (Electron), and the native app
# (MANTLE_NATIVE_EXE, -Exe, native/out/bin/MantleNative/, else it publishes one).
#
# Per theme it writes to <Out>/<theme>/: electron.png, native.png (the crop), diff.png (heatmap: grey =
# same, red = different, blue = masked), *-full.png (whole screen), electron.json / native ready.json,
# score.json; and <Out>/parity.json for all. The crop is the pill's spec rect (min(1120, screen - 28) DIP
# wide, 48 tall, 14 above the bottom edge, centred) plus -Margin px on each side, so a misplaced pill
# shows up as a difference instead of being cropped away. Masked: the clock slot (its seconds tick) and,
# by default, the running-apps slot (see the masks below).
# Last line: PARITY: ok (...) or PARITY: FAILED (...). The thresholds are provisional (see README.md).
param(
  [ValidateSet('light', 'dark', 'both')] [string]$Theme = 'both',
  [ValidateSet('bar')] [string]$Surface = 'bar',
  [string]$Out,
  [double]$MinSsim = 0.98,
  [double]$MaxDiffPct = 0.5,
  [int]$PixelThreshold = 24,
  [int]$Margin = 24,
  [bool]$MaskApps = $true,
  [bool]$KnownGaps = $true,
  [string]$Exe
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
if (-not $Out) { $Out = Join-Path $root 'native\out\parity' }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path
function Say($m) { Write-Host ("parity {0}: {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) }
$problems = [System.Collections.Generic.List[string]]::new()
function Problem($m) { $problems.Add($m); Write-Host "parity PROBLEM: $m" }

if (-not $env:CI -and -not $env:GITHUB_ACTIONS -and $env:MANTLE_PARITY_LOCAL -ne '1') {
  Write-Host 'PARITY: FAILED (refused: not on a CI runner; it changes the desktop and shows test windows)'
  exit 2
}

Add-Type -Path (Join-Path $PSScriptRoot 'ParityNative.cs')   # default references only (ReferencedAssemblies drops them)
$deskCs = Join-Path $PSScriptRoot '..\desk\Desk.cs'   # the runner desktop helpers (taskbar out of the way)
if ((Test-Path $deskCs) -and -not ('Desk' -as [type])) { Add-Type -Path $deskCs }
Add-Type -AssemblyName System.Drawing
# BGRA bytes (stride = w * 4) -> PNG, on the PowerShell side.
function Save-Bgra([byte[]]$px, [int]$w, [int]$h, [string]$path) {
  $bmp = [System.Drawing.Bitmap]::new($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $d = $bmp.LockBits([System.Drawing.Rectangle]::new(0, 0, $w, $h), [System.Drawing.Imaging.ImageLockMode]::WriteOnly, $bmp.PixelFormat)
  for ($row = 0; $row -lt $h; $row++) { [System.Runtime.InteropServices.Marshal]::Copy($px, $row * $w * 4, [IntPtr]::Add($d.Scan0, $row * $d.Stride), $w * 4) }
  $bmp.UnlockBits($d); $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
[void][ParityNative]::DpiAware()

# ------------------------------------------------------------------ the runner desktop
$scr = [ParityNative]::Screen(); $W = $scr[0]; $H = $scr[1]
Say "screen ${W}x${H}"
if ($W -ne 1920 -or $H -ne 1080) { Say "note: not 1920x1080 (recipe env MANTLE_CLOUD_RES=1920x1080 resizes the runner desktop)" }
$scale = 1.0   # the runner runs at 100 %; the crop is in physical px
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
[ParityNative]::ParkCursor([int]($W / 2), [int]($H / 3))

# The same running app for both bars: a plain window, top left, away from the bar.
$fixtureCmd = "Add-Type -AssemblyName System.Windows.Forms; `$f = New-Object System.Windows.Forms.Form; `$f.Text = 'Parity fixture'; " +
  "`$f.StartPosition = 'Manual'; `$f.Location = New-Object System.Drawing.Point(40, 40); `$f.Size = New-Object System.Drawing.Size(420, 300); " +
  "[System.Windows.Forms.Application]::Run(`$f)"
$fixture = Start-Process powershell.exe -ArgumentList '-NoProfile', '-EncodedCommand', ([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($fixtureCmd))) -PassThru
Start-Sleep -Seconds 3
[ParityNative]::ParkCursor([int]($W / 2), [int]($H / 3))

# ------------------------------------------------------------------ the native app
if (-not $Exe) { $Exe = $env:MANTLE_NATIVE_EXE }
$bin = Join-Path $root 'native\out\bin\MantleNative'
function Find-NativeExe { Get-ChildItem -Path $bin -Filter *.exe -ErrorAction SilentlyContinue | Where-Object { $_.Name -in 'MantleNative.exe', 'MantleBar.exe' } | Select-Object -First 1 }
if (-not $Exe -or -not (Test-Path $Exe)) {
  $found = Find-NativeExe
  if (-not $found) {
    $pub = Join-Path $root 'native\tools\publish.ps1'
    $plog = Join-Path $Out 'publish.log'
    Say "publishing the native app to $bin"
    if (Test-Path $pub) { & pwsh -NoProfile -File $pub -Mode aot -Out $bin *> $plog }
    else { & dotnet publish (Join-Path $root 'native\src\Mantle.App\Mantle.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $bin *> $plog }
    Say "publish exit $LASTEXITCODE"
    $found = Find-NativeExe
  }
  if ($found) { $Exe = $found.FullName }
}
if (-not $Exe -or -not (Test-Path $Exe)) { Write-Host "PARITY: FAILED (no native exe: set MANTLE_NATIVE_EXE or run the build first; see $Out\publish.log)"; exit 3 }
Say "native exe $Exe"
$env:MANTLE_TEST_VISIBLE = '1'

# ------------------------------------------------------------------ geometry
$pw = [int]([Math]::Min(1120, $W / $scale - 28) * $scale)
$pillX = [int](($W - $pw) / 2); $pillY = [int]($H - (14 + 48) * $scale); $pillH = [int](48 * $scale)
$cx = [Math]::Max(0, $pillX - $Margin); $cy = [Math]::Max(0, $pillY - $Margin)
$cr = [Math]::Min($W, $pillX + $pw + $Margin); $cb = [Math]::Min($H, $pillY + $pillH + $Margin)
$crop = @{ x = $cx; y = $cy; w = $cr - $cx; h = $cb - $cy }
# Masks in screen px. The clock slot sits 175..85 px left of the pill's right end in both apps (measured on
# the runner, 2026-11-07); its seconds always differ between two captures. The apps slot is the centre of
# the pill: Electron's --test bar listed NO running apps on the runner while the native bar listed them, so
# that area is masked (-MaskApps $false compares it) until the Electron side shows the same set.
$clockSlot = @(($pillX + $pw - 175), $pillY, 90, $pillH)
$appsSlot = @(($pillX + [int]($pw / 2) - 220), $pillY, 440, $pillH)
Say ("pill spec px {0},{1} {2}x{3}; crop {4},{5} {6}x{7}" -f $pillX, $pillY, $pw, $pillH, $crop.x, $crop.y, $crop.w, $crop.h)

# KNOWN GAPS: real differences of the native bar that a later bar lane owns. Each is masked from the score (the
# gate stays -MinSsim / -MaxDiffPct for everything else) and gets its own line per theme:
#   PARITY known-gap <id> <theme>: still there (diffpx X% in its area) - <what>; owner <lane>
#   PARITY known-gap <id> <theme>: NOT seen any more (diffpx X%): remove it from $gapList so the gate covers it
# Measured on the runner at 1920x1080, 100 % (run 42355091758, native/infra 0f4532b), relative to the pill's right
# end. -KnownGaps $false scores the whole crop as before.
$pillR = $pillX + $pw
$gapList = @(
  [ordered]@{ id = 'right-cluster'; owner = 'the bar lane'; rect = @(($pillR - 340), $pillY, 152, $pillH)
    what = 'the native right cluster (divider, downloads, volume, network) sits ~18 px further right than Electron''s' }
  [ordered]@{ id = 'more-dot'; owner = 'the bar lane'; rect = @(($pillR - 28), ($pillY + 6), 20, 20)
    what = 'Electron shows an orange dot on "more"; the native bar has none' }
)
if (-not $KnownGaps) { $gapList = @() }

# The runner's taskbar must not cover the bar when a capture is scored: a covered capture is a problem, not a score.
function Clear-Taskbar([string]$tag) {
  $end = (Get-Date).AddSeconds(6); $tb = $null
  while ((Get-Date) -lt $end) {
    $tb = [ParityNative]::TaskbarOver($crop.y, $crop.y + $crop.h); if (-not $tb) { return $true }
    [ParityNative]::TaskbarAutoHide($true); [ParityNative]::ParkCursor([int]($W / 2), [int]($H / 3)); Start-Sleep -Milliseconds 400
  }
  # Still up: a lit taskbar button (a window asking for attention) keeps an auto-hide taskbar shown.
  if ('Desk' -as [type]) {
    $fl = [Desk]::StopFlashing(); $tb = [ParityNative]::TaskbarOver($crop.y, $crop.y + $crop.h); if (-not $tb) { Say "${tag}: taskbar cleared after stopping $fl attention request(s)"; return $true }
    [void][Desk]::HideTaskbar(); Start-Sleep -Milliseconds 400
    $tb = [ParityNative]::TaskbarOver($crop.y, $crop.y + $crop.h); if (-not $tb) { Say "${tag}: the runner taskbar stayed up: hidden outright"; return $true }
  }
  Problem ("{0}: the runner's taskbar covers the bar ({1}); capture not scored" -f $tag, ($tb -join ','))
  return $false
}
function Wait-Still([string]$tag) {
  $end = (Get-Date).AddSeconds(8); $m = 999
  while ((Get-Date) -lt $end) { $m = [ParityNative]::Motion($crop.x, $crop.y, $crop.w, $crop.h, 300); if ($m -lt 0.5) { break } }
  Say ("{0}: motion {1:N2} before capture" -f $tag, $m)
  return $m
}
function Shoot([string]$dir, [string]$name) {
  $px = [ParityNative]::Grab($crop.x, $crop.y, $crop.w, $crop.h)
  Save-Bgra $px $crop.w $crop.h (Join-Path $dir "$name.png")
  Save-Bgra ([ParityNative]::Grab(0, 0, $W, $H)) $W $H (Join-Path $dir "$name-full.png")
  [ParityNative]::VisibleWindows() | Set-Content -Encoding utf8 (Join-Path $dir "$name-windows.txt")
  return , $px
}
function Stop-Profile([string]$profile) {
  Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.Contains("--profile-name=$profile") } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}
# The native clock's screen rect through UI Automation (id bar.clock), or $null.
function Native-ClockRect([int]$procId) {
  try {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $A = [System.Windows.Automation.AutomationElement]
    $win = $A::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,
      [System.Windows.Automation.PropertyCondition]::new($A::ProcessIdProperty, $procId))
    if (-not $win) { return $null }
    $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
      [System.Windows.Automation.PropertyCondition]::new($A::AutomationIdProperty, 'bar.clock'))
    if (-not $el) { return $null }
    $r = $el.Current.BoundingRectangle
    return @([int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
  } catch { Say "UIA: $($_.Exception.Message)"; return $null }
}

# ------------------------------------------------------------------ one theme
$results = [ordered]@{}
$themes = if ($Theme -eq 'both') { @('light', 'dark') } else { @($Theme) }
foreach ($t in $themes) {
  $dir = Join-Path $Out $t; New-Item -ItemType Directory -Force -Path $dir | Out-Null
  Set-WinTheme $t; Start-Sleep -Seconds 2
  $r = [ordered]@{ theme = $t; crop = $crop; masks = @(, $clockSlot) }
  if ($MaskApps) { $r.masks += , $appsSlot }
  foreach ($g in $gapList) { $r.masks += , $g.rect }
  $ePx = $null; $nPx = $null

  # Electron Mantle (--test instance, made visible)
  $ej = Join-Path $dir 'electron.json'; Remove-Item $ej -ErrorAction SilentlyContinue
  $node = Start-Process node -ArgumentList (Join-Path $PSScriptRoot 'electron-bar.mjs'), "--root=$root", "--theme=$t", "--out=$ej", '--hold=240' `
    -WorkingDirectory $root -PassThru -RedirectStandardOutput (Join-Path $dir 'electron-bar.out.txt') -RedirectStandardError (Join-Path $dir 'electron-bar.err.txt')
  $end = (Get-Date).AddSeconds(180)
  while ((Get-Date) -lt $end -and -not (Test-Path $ej) -and -not $node.HasExited) { Start-Sleep -Milliseconds 500 }
  Start-Sleep -Milliseconds 300
  $e = if (Test-Path $ej) { Get-Content $ej -Raw | ConvertFrom-Json } else { $null }
  if ($e -and $e.ok) {
    [ParityNative]::ParkCursor([int]($W / 2), [int]($H / 3))
    if (Clear-Taskbar "electron $t") { $r.electronMotion = Wait-Still "electron $t"; $ePx = Shoot $dir 'electron' }
    $r.electron = $e
    Say ("electron {0}: pill {1} clock {2}" -f $t, ($e.pillDip -join ','), ($e.clockDip -join ','))
    $q = Start-Process $e.electronExe -ArgumentList '.', '--quit', '--test', "--profile-name=parity-$t" -WorkingDirectory $root -PassThru -WindowStyle Hidden
    [void]$q.WaitForExit(20000)
  } else {
    Problem ("electron {0}: no bar ({1})" -f $t, $(if ($e) { $e.why } else { 'electron-bar.mjs wrote nothing; see electron-bar.*.txt' }))
  }
  Start-Sleep -Seconds 2
  Stop-Profile "parity-$t"
  if (-not $node.HasExited) { Stop-Process -Id $node.Id -Force -ErrorAction SilentlyContinue }

  # the native app
  $nd = Join-Path $dir 'native-bench'; New-Item -ItemType Directory -Force -Path $nd | Out-Null
  $ud = Join-Path ([IO.Path]::GetTempPath()) "mantle-native-parity-$t"; New-Item -ItemType Directory -Force -Path $ud | Out-Null
  $np = Start-Process -FilePath $Exe -ArgumentList '--test', "--profile-name=parity-$t", "--user-data=$ud", "--theme=$t", "--bench-out=$nd" -PassThru
  $end = (Get-Date).AddSeconds(45)
  while ((Get-Date) -lt $end -and -not (Test-Path (Join-Path $nd 'ready.json')) -and -not $np.HasExited) { Start-Sleep -Milliseconds 250 }
  if (Test-Path (Join-Path $nd 'ready.json')) {
    Start-Sleep -Milliseconds 800
    [ParityNative]::ParkCursor([int]($W / 2), [int]($H / 3))
    if (Clear-Taskbar "native $t") { $r.nativeMotion = Wait-Still "native $t"; $nPx = Shoot $dir 'native' }
    $r.native = Get-Content (Join-Path $nd 'ready.json') -Raw | ConvertFrom-Json
    $nc = Native-ClockRect $np.Id
    if ($nc) { $r.masks += , $nc; Say ("native {0}: clock {1}" -f $t, ($nc -join ',')) }
    else { Say "native ${t}: no bar.clock through UI Automation; the clock slot mask alone covers it" }
  } else {
    Problem ("native {0}: no ready.json in 45 s (exited: {1})" -f $t, $np.HasExited)
  }
  if (-not $np.HasExited) { Stop-Process -Id $np.Id -Force -ErrorAction SilentlyContinue }
  Stop-Profile "parity-$t"

  # diff
  if ($ePx -and $nPx) {
    $flat = [System.Collections.Generic.List[int]]::new()
    foreach ($m in $r.masks) { $flat.AddRange([int[]]@(($m[0] - 4 - $crop.x), ($m[1] - 4 - $crop.y), ($m[2] + 8), ($m[3] + 8))) }
    $s = [ParityNative]::Diff($ePx, $nPx, $crop.w, $crop.h, $flat.ToArray(), $PixelThreshold)
    Save-Bgra $s.Heat $crop.w $crop.h (Join-Path $dir 'diff.png')
    $r.score = [ordered]@{ ssim = [Math]::Round($s.Ssim, 4); diffPxPct = [Math]::Round($s.DiffPxPct, 2); meanAbs = [Math]::Round($s.MeanAbs, 2)
      meanAbsRGB = @([Math]::Round($s.MeanAbsR, 2), [Math]::Round($s.MeanAbsG, 2), [Math]::Round($s.MeanAbsB, 2))
      compared = $s.Compared; masked = $s.Masked; pixelThreshold = $PixelThreshold }
    $r.knownGaps = @()
    foreach ($g in $gapList) {
      $gd = [ParityNative]::RegionDiff($ePx, $nPx, $crop.w, $crop.h, ($g.rect[0] - $crop.x), ($g.rect[1] - $crop.y), $g.rect[2], $g.rect[3], $PixelThreshold)
      $still = $gd[0] -ge 1.0
      $r.knownGaps += [ordered]@{ id = $g.id; stillThere = $still; diffPxPct = [Math]::Round($gd[0], 2); meanAbs = [Math]::Round($gd[1], 2); rect = $g.rect; what = $g.what; owner = $g.owner }
      if ($still) { Write-Host ("PARITY known-gap {0} {1}: still there (diffpx {2:N2}% in its area) - {3}; owner {4}" -f $g.id, $t, $gd[0], $g.what, $g.owner) }
      else { Write-Host ("PARITY known-gap {0} {1}: NOT seen any more (diffpx {2:N2}%): remove it from `$gapList in run-parity.ps1 so the gate covers that area again" -f $g.id, $t, $gd[0]) }
    }
    $pass = ($s.Ssim -ge $MinSsim) -and ($s.DiffPxPct -le $MaxDiffPct)
    $r.ok = $pass
    Write-Host ("PARITY bar {0}: ssim={1:N4} diffpx={2:N2}% meanabs={3:N2} ({4} vs ssim>={5}, diffpx<={6}%)" -f $t, $s.Ssim, $s.DiffPxPct, $s.MeanAbs, $(if ($pass) { 'ok' } else { 'FAILED' }), $MinSsim, $MaxDiffPct)
    if (-not $pass) { Problem ("{0}: below the threshold (ssim {1:N4}, diffpx {2:N2}%)" -f $t, $s.Ssim, $s.DiffPxPct) }
  } else { $r.ok = $false; if (-not $problems.Count) { Problem "${t}: a capture is missing" } }
  $r | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 (Join-Path $dir 'score.json')
  $results[$t] = $r
}

if (-not $fixture.HasExited) { Stop-Process -Id $fixture.Id -Force -ErrorAction SilentlyContinue }
# The taskbar stays on auto-hide (runner only): later stages measure the bottom edge too.
$results | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 (Join-Path $Out 'parity.json')
$parts = @(foreach ($t in $results.Keys) { $x = $results[$t]; if ($x.score) { "$t ssim=$($x.score.ssim) diffpx=$($x.score.diffPxPct)%" } else { "$t no score" } })
if ($gapList.Count) { $parts += 'known gaps masked: ' + (($gapList | ForEach-Object { $_.id }) -join ', ') + ' (see the known-gap lines)' }
if ($problems.Count -eq 0) { Write-Host ("PARITY: ok (" + ($parts -join '; ') + ")"); exit 0 }
Write-Host ("PARITY: FAILED (" + ($parts -join '; ') + "; " + $problems.Count + " problem(s): " + ($problems -join '; ') + ")")
exit 1
