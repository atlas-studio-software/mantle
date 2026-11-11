<#
  Notification centre + tray strip parity (lane native-r7-notif): Electron's page vs the native bar, light and dark.
    cloudtest --recipe mantle-native-public --artifacts "native/out/parity/notifs/**" -- "pwsh -NoProfile -File native/tools/parity/notifications/run-notifs-parity.ps1 -Build"

  Electron: electron-notifs.cjs opens ui/bar/index.html?mock=1&mode=pill&tray=tones&notifs=some over its own mock bridge
  offscreen at scale 1, shoots the tray strip (plus 4 px) and, after a click on the bell, the centre (plus 12 px, 4 below)
  over one colour; it also writes the mock's notifs.get / tray.list answers (read before the panel opened).
  Native: one MantleNative --test run per theme with MANTLE_TEST_VISIBLE=1, MANTLE_TEST_NOTIFS / MANTLE_TEST_TRAY = those
  answers (their times moved on by the time since they were read, so the ages read the same), the strip's rect read
  through UI Automation, the bell pressed through UI Automation (Invoke, no mouse), the centre's rect read from its
  "pop-up Notifications: shown at" line; both grabbed with the same margins over a full-screen backdrop of that colour.
  Each pair is compared centred (SSIM on luminance, ParityNative.Diff, no masks); writes <out>/<part>-<theme>-pair.png
  (Electron above, native below) and one NOTIFS-PARITY line each, then a summary line.

  Runner only (CI / GITHUB_ACTIONS, or MANTLE_PARITY_LOCAL=1 on a machine nobody is using): it shows windows.
  ci.ps1 stage notifs runs it as
    & native/tools/parity/notifications/run-notifs-parity.ps1 -Exe <published exe> -Out native/out/parity/notifs
  gated on "NOTIFS-PARITY summary: 4 scored of 4" and per-part floors under the lowest ssim it printed.
#>
param(
  [string]$Out,
  [string]$Exe,
  [string]$Bg = '#7f8a99',
  [int]$PixelThreshold = 24,
  [switch]$Build
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
function Say($m) { Write-Host ("notifs-parity {0}: {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) }
if (-not $env:CI -and -not $env:GITHUB_ACTIONS -and $env:MANTLE_PARITY_LOCAL -ne '1') { throw 'runner only: it shows windows (set MANTLE_PARITY_LOCAL=1 only on a machine nobody is using)' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
if (-not $Out) { $Out = Join-Path $root 'native/out/parity/notifs' }
New-Item -ItemType Directory -Force $Out | Out-Null
if ($Build) { & (Join-Path $root 'native/tools/ci.ps1') -Stages build; if ($LASTEXITCODE) { throw "the build stage failed ($LASTEXITCODE)" } }
if (-not $Exe) { $Exe = if ($env:MANTLE_NATIVE_EXE) { $env:MANTLE_NATIVE_EXE } else { Join-Path $root 'native/out/bin/MantleNative/MantleNative.exe' } }
if (-not (Test-Path $Exe)) { throw "no native app at $Exe (run with -Build)" }

Add-Type -Path (Join-Path $root 'native/tools/parity/ParityNative.cs')
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
[void][ParityNative]::DpiAware()
$scr = [ParityNative]::Screen(); $SW = $scr[0]; $SH = $scr[1]

# ---- Electron (both themes, and the lists the native side takes)
$electron = Join-Path $root 'node_modules/electron/dist/electron.exe'
if (-not (Test-Path $electron)) { throw "no Electron at $electron (npm ci first; the recipe's setup does)" }
Say 'Electron page'
& $electron (Join-Path $PSScriptRoot 'electron-notifs.cjs') $Out $Bg 2>&1 | Where-Object { "$_" -match 'ELECTRON-NOTIFS' } | ForEach-Object { Write-Host "$_" }
if ($LASTEXITCODE) { throw "electron-notifs.cjs failed ($LASTEXITCODE)" }
$stateFile = Join-Path $Out 'notifs-state.json'
if (-not (Test-Path $stateFile)) { throw 'electron-notifs.cjs wrote no notifs-state.json' }
$state = Get-Content $stateFile -Raw | ConvertFrom-Json
$trayJson = $state.tray | ConvertTo-Json -Depth 30 -Compress -AsArray

# ---- the backdrop: one solid colour behind the native windows (another process, not topmost)
$c = [System.Drawing.ColorTranslator]::FromHtml($Bg)
$ps = "Add-Type -AssemblyName System.Windows.Forms; `$f = New-Object System.Windows.Forms.Form; `$f.Text = 'Notifs parity backdrop'; " +
      "`$f.FormBorderStyle = 'None'; `$f.StartPosition = 'Manual'; `$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds; " +
      "`$f.Location = New-Object System.Drawing.Point(`$b.X, `$b.Y); `$f.Size = New-Object System.Drawing.Size(`$b.Width, `$b.Height); " +
      "`$f.BackColor = [System.Drawing.Color]::FromArgb($($c.R), $($c.G), $($c.B)); [System.Windows.Forms.Application]::Run(`$f)"
$back = Start-Process powershell.exe -PassThru -ArgumentList @('-NoProfile', '-EncodedCommand', [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($ps)))
Start-Sleep -Milliseconds 2500
[ParityNative]::ParkCursor(5, 5)

function Png([byte[]]$px, [int]$w, [int]$h) {
  $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppRgb)
  $d = $bmp.LockBits((New-Object System.Drawing.Rectangle(0, 0, $w, $h)), 'WriteOnly', $bmp.PixelFormat)
  [Runtime.InteropServices.Marshal]::Copy($px, 0, $d.Scan0, $px.Length); $bmp.UnlockBits($d); return $bmp
}
function Bytes([System.Drawing.Bitmap]$bmp) {
  $w = $bmp.Width; $h = $bmp.Height
  $d = $bmp.LockBits((New-Object System.Drawing.Rectangle(0, 0, $w, $h)), 'ReadOnly', [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $px = New-Object byte[] ($w * $h * 4); [Runtime.InteropServices.Marshal]::Copy($d.Scan0, $px, 0, $px.Length); $bmp.UnlockBits($d); return , $px
}
function LogText($f) { if (Test-Path $f) { try { $s = [IO.File]::Open($f, 'Open', 'Read', 'ReadWrite,Delete'); $r = (New-Object IO.StreamReader($s)).ReadToEnd(); $s.Dispose(); return $r } catch { return '' } } return '' }
function FindId($procId, $id) {
  $pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
  $idCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  foreach ($top in [System.Windows.Automation.AutomationElement]::RootElement.FindAll('Children', $pidCond)) { $e = $top.FindFirst('Descendants', $idCond); if ($e) { return $e } }
  return $null
}
# one pair: the native grab vs Electron's file, centred, scored, saved side by side
function Score($tag, $ef, $x, $y, $w0, $h0, $procId = 0) {
  if (-not (Test-Path $ef)) { return "$tag no score: no Electron shot" }
  # the bar hides itself from capture while its glass is re-shot: a clean grab waits that out (ParityNative.CleanGrab)
  if ($procId) {
    $shot = [ParityNative]::CleanGrab(@([int]$procId), $x, $y, $w0, $h0, 60, 5000); $nat = $shot.Px
    Say ("{0}: grab at {1},{2} {3}x{4}: clean={5} ({6} own window(s) over it, {7} ms, {8} grab(s)); covering: {9}" -f $tag, $x, $y, $w0, $h0, $shot.Clean, $shot.Windows, $shot.WaitedMs, $shot.Tries, ((@([ParityNative]::Covering($x, $y, $w0, $h0, @([int]$procId))) -join '; ') -replace '^$', 'none'))
    $full = [ParityNative]::Grab(0, 0, $script:SW, $script:SH); $fb = Png $full $script:SW $script:SH; $fb.Save((Join-Path $Out "native-$tag-full.png")); $fb.Dispose()
    [ParityNative]::VisibleWindows() | Set-Content -Encoding utf8 (Join-Path $Out "native-$tag-windows.txt")
  } else { $nat = [ParityNative]::Grab($x, $y, $w0, $h0) }
  $nb = Png $nat $w0 $h0; $nb.Save((Join-Path $Out "native-$tag.png"))
  $eb = [System.Drawing.Bitmap]::FromFile($ef)
  $w = [Math]::Min($eb.Width, $nb.Width); $h = [Math]::Min($eb.Height, $nb.Height)
  $ec = $eb.Clone((New-Object System.Drawing.Rectangle([int](($eb.Width - $w) / 2), [int](($eb.Height - $h) / 2), $w, $h)), $eb.PixelFormat)
  $nc = $nb.Clone((New-Object System.Drawing.Rectangle([int](($nb.Width - $w) / 2), [int](($nb.Height - $h) / 2), $w, $h)), $nb.PixelFormat)
  $s = [ParityNative]::Diff((Bytes $ec), (Bytes $nc), $w, $h, @(), $PixelThreshold)
  $pw = [Math]::Max($eb.Width, $nb.Width)
  $pair = New-Object System.Drawing.Bitmap($pw, ($eb.Height + $nb.Height + 4)); $g = [System.Drawing.Graphics]::FromImage($pair); $g.Clear([System.Drawing.Color]::Red)
  $g.DrawImage($eb, 0, 0, $eb.Width, $eb.Height); $g.DrawImage($nb, 0, $eb.Height + 4, $nb.Width, $nb.Height); $g.Dispose(); $pair.Save((Join-Path $Out "$tag-pair.png"))
  $line = "NOTIFS-PARITY $tag`: ssim={0:N4} diffpx={1:N2}% meanabs={2:N2} electron {3}x{4} native {5}x{6}" -f $s.Ssim, $s.DiffPxPct, $s.MeanAbs, $eb.Width, $eb.Height, $nb.Width, $nb.Height
  $eb.Dispose(); $nb.Dispose(); $ec.Dispose(); $nc.Dispose(); $pair.Dispose()
  return $line
}

$rows = @(); $low = @{ tray = 1.0; centre = 1.0 }
try {
  foreach ($theme in 'light', 'dark') {
    $ud = Join-Path ([IO.Path]::GetTempPath()) ("mantle-ntfpar-" + [Guid]::NewGuid().ToString('N').Substring(0, 6))
    New-Item -ItemType Directory -Force $ud | Out-Null
    # the ages read the same: every time moved on by the time since Electron's list was read
    $delta = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() - [long]$state.at
    $notifs = $state.notifs | ConvertTo-Json -Depth 30 -Compress
    $notifs = [regex]::Replace($notifs, '"timeMs":(\d+)', { param($m) '"timeMs":' + ([long]$m.Groups[1].Value + $delta) })
    # as files ("@<path>"): the pictures make the lists too long for an environment variable
    $nf = Join-Path $ud 'notifs.json'; $tf = Join-Path $ud 'tray.json'
    [IO.File]::WriteAllText($nf, $notifs); [IO.File]::WriteAllText($tf, $trayJson)
    $env:MANTLE_TEST_VISIBLE = '1'; $env:MANTLE_TEST_NOTIFS = "@$nf"; $env:MANTLE_TEST_TRAY = "@$tf"
    $prof = "ntfpar-$theme"
    $p = Start-Process $Exe -PassThru -ArgumentList @('--test', "--profile-name=$prof", "--user-data=$ud", "--theme=$theme")
    $log = Join-Path $ud 'mantle-native.log'
    $ok = $false
    for ($i = 0; $i -lt 150 -and -not $ok; $i++) { Start-Sleep -Milliseconds 200; $t = LogText $log; $ok = ($t -match 'notifs: a --test run with MANTLE_TEST_NOTIFS') -and ($t -match 'tray: the strip shows') }
    if (-not $ok) { $rows += "$theme no score: the native app did not draw MANTLE_TEST_NOTIFS / MANTLE_TEST_TRAY in 30 s"; Copy-Item $log (Join-Path $Out "native-$theme-log.txt") -ErrorAction SilentlyContinue; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; continue }
    Start-Sleep -Milliseconds 3500   # the bar's first reveal, the tones and the pictures
    # ---- the strip
    $strip = FindId $p.Id 'bar.tray'
    if (-not $strip) { $rows += "tray-$theme no score: no bar.tray in the native bar" }
    else {
      $r = $strip.Current.BoundingRectangle; Say "native strip at $($r.X),$($r.Y) $($r.Width)x$($r.Height)"
      $line = Score "tray-$theme" (Join-Path $Out "electron-tray-$theme.png") ([int]$r.X - 4) ([int]$r.Y - 4) ([int]$r.Width + 8) ([int]$r.Height + 8) $p.Id
      Write-Host $line; $rows += $line
      $mm = [regex]::Match($line, 'ssim=([\d.]+)'); if ($mm.Success -and [double]$mm.Groups[1].Value -lt $low.tray) { $low.tray = [double]$mm.Groups[1].Value }
    }
    # ---- the centre
    $btn = FindId $p.Id 'bar.notifications'
    if (-not $btn) { $rows += "centre-$theme no score: no bar.notifications in the native bar"; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; continue }
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $m = $null
    for ($i = 0; $i -lt 40 -and -not $m; $i++) { Start-Sleep -Milliseconds 150; $m = [regex]::Match((LogText $log), 'pop-up Notifications: shown at (-?\d+),(-?\d+) (\d+)x(\d+)'); if (-not $m.Success) { $m = $null } }
    if (-not $m) { $rows += "centre-$theme no score: the native centre did not show"; Copy-Item $log (Join-Path $Out "native-$theme-log.txt"); Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; continue }
    Start-Sleep -Milliseconds 1200   # the entrance and the pictures
    $x = [int]$m.Groups[1].Value - 12; $y = [int]$m.Groups[2].Value - 12; $w0 = [int]$m.Groups[3].Value + 24; $h0 = [int]$m.Groups[4].Value + 12 + 4
    $line = Score "centre-$theme" (Join-Path $Out "electron-centre-$theme.png") $x $y $w0 $h0
    Write-Host $line; $rows += $line
    $mm = [regex]::Match($line, 'ssim=([\d.]+)'); if ($mm.Success -and [double]$mm.Groups[1].Value -lt $low.centre) { $low.centre = [double]$mm.Groups[1].Value }
    Copy-Item $log (Join-Path $Out "native-$theme-log.txt")
    $q = Start-Process $Exe -PassThru -ArgumentList @('--quit', '--test', "--profile-name=$prof", "--user-data=$ud")
    if (-not $q.WaitForExit(15000)) { Stop-Process -Id $q.Id -Force -ErrorAction SilentlyContinue }
    if (-not $p.WaitForExit(8000)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
  }
} finally {
  Stop-Process -Id $back.Id -Force -ErrorAction SilentlyContinue
  Remove-Item Env:MANTLE_TEST_NOTIFS, Env:MANTLE_TEST_TRAY -ErrorAction SilentlyContinue
}
$scored = @($rows | Where-Object { $_ -like 'NOTIFS-PARITY*' }).Count
Write-Host ("NOTIFS-PARITY summary: {0} scored of 4, lowest ssim tray {1:N4} centre {2:N4}; no masks" -f $scored, $low.tray, $low.centre)
$rows | Where-Object { $_ -notlike 'NOTIFS-PARITY*' } | ForEach-Object { Write-Host "NOTIFS-PARITY problem: $_" }
