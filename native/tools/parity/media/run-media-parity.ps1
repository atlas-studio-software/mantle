<#
  Media card parity (lane native-r7-media): the now-playing card, Electron's page vs the native pop-up, light and dark.
    cloudtest --recipe mantle-native-public --artifacts "native/out/parity/media/**" -- "pwsh -NoProfile -File native/tools/parity/media/run-media-parity.ps1 -Build"

  Electron: electron-media.cjs opens ui/bar/index.html?media=noctl over its mock bridge offscreen at scale 1, clicks the
  chip and shoots the card plus 12 px (4 below) over one colour; it also writes the session it showed (media-state.json).
  Native: one MantleNative --test run per theme with MANTLE_TEST_VISIBLE=1 and MANTLE_TEST_MEDIA = that session (so both
  draw the same one), the chip pressed through UI Automation (Invoke, no mouse), the card's rect read from its
  "pop-up Now playing: shown at" line and grabbed with the same margins over a full-screen backdrop of that colour.
  Both crops are compared bottom-aligned and centred (SSIM on luminance, ParityNative.Diff, no masks); writes
  <out>/media-<theme>-pair.png (Electron above, native below), one MEDIA-PARITY line each, then a summary line.
  Game mode is not in the pair: its quick-settings tile is not drawn natively yet (docs/lanes/media.md).

  Runner only (CI / GITHUB_ACTIONS, or MANTLE_PARITY_LOCAL=1 on a machine nobody is using): it shows windows.
  ci.ps1 stage media runs it, gated on "MEDIA-PARITY summary: 2 scored of 2" and per-theme floors.
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
function Say($m) { Write-Host ("media-parity {0}: {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) }
if (-not $env:CI -and -not $env:GITHUB_ACTIONS -and $env:MANTLE_PARITY_LOCAL -ne '1') { throw 'runner only: it shows windows (set MANTLE_PARITY_LOCAL=1 only on a machine nobody is using)' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
if (-not $Out) { $Out = Join-Path $root 'native/out/parity/media' }
New-Item -ItemType Directory -Force $Out | Out-Null
if ($Build) { & (Join-Path $root 'native/tools/ci.ps1') -Stages build; if ($LASTEXITCODE) { throw "the build stage failed ($LASTEXITCODE)" } }
if (-not $Exe) { $Exe = if ($env:MANTLE_NATIVE_EXE) { $env:MANTLE_NATIVE_EXE } else { Join-Path $root 'native/out/bin/MantleNative/MantleNative.exe' } }
if (-not (Test-Path $Exe)) { throw "no native app at $Exe (run with -Build)" }

Add-Type -Path (Join-Path $root 'native/tools/parity/ParityNative.cs')
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
[void][ParityNative]::DpiAware()

# ---- Electron (both themes, and the session the native side takes)
$electron = Join-Path $root 'node_modules/electron/dist/electron.exe'
if (-not (Test-Path $electron)) { throw "no Electron at $electron (npm ci first; the recipe's setup does)" }
Say 'Electron page'
& $electron (Join-Path $PSScriptRoot 'electron-media.cjs') $Out $Bg 2>&1 | Where-Object { "$_" -match 'ELECTRON-MEDIA' } | ForEach-Object { Write-Host "$_" }
if ($LASTEXITCODE) { throw "electron-media.cjs failed ($LASTEXITCODE)" }
$stateFile = Join-Path $Out 'media-state.json'
if (-not (Test-Path $stateFile)) { throw 'electron-media.cjs wrote no media-state.json' }
$state = (Get-Content $stateFile -Raw).Trim()

# ---- the backdrop: one solid colour behind the native windows (another process, not topmost)
$c = [System.Drawing.ColorTranslator]::FromHtml($Bg)
$ps = "Add-Type -AssemblyName System.Windows.Forms; `$f = New-Object System.Windows.Forms.Form; `$f.Text = 'Media parity backdrop'; " +
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
function Find-Id($procId, [string]$id) {
  $pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
  $idCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  foreach ($top in [System.Windows.Automation.AutomationElement]::RootElement.FindAll('Children', $pidCond)) { $e = $top.FindFirst('Descendants', $idCond); if ($e) { return $e } }
  return $null
}

$rows = @(); $low = 1.0
try {
  foreach ($theme in 'light', 'dark') {
    $tag = "media-$theme"
    $ud = Join-Path ([IO.Path]::GetTempPath()) ("mantle-mediapar-" + [Guid]::NewGuid().ToString('N').Substring(0, 6))
    New-Item -ItemType Directory -Force $ud | Out-Null
    $env:MANTLE_TEST_VISIBLE = '1'; $env:MANTLE_TEST_MEDIA = $state
    $prof = "mediapar-$theme"
    $p = Start-Process $Exe -PassThru -ArgumentList @('--test', "--profile-name=$prof", "--user-data=$ud", "--theme=$theme")
    $log = Join-Path $ud 'mantle-native.log'
    $ok = $false
    for ($i = 0; $i -lt 150 -and -not $ok; $i++) { Start-Sleep -Milliseconds 200; $ok = (LogText $log) -match 'media: the chip shows ' }
    if (-not $ok) { $rows += "$tag no score: the native chip did not show the MANTLE_TEST_MEDIA session in 30 s"; Copy-Item $log (Join-Path $Out "native-$tag-log.txt") -ErrorAction SilentlyContinue; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; continue }
    Start-Sleep -Milliseconds 1500
    $btn = $null
    for ($i = 0; $i -lt 20 -and -not $btn; $i++) { $btn = Find-Id $p.Id 'bar.media.chip'; if (-not $btn) { Start-Sleep -Milliseconds 250 } }
    if (-not $btn) { $rows += "$tag no score: no bar.media.chip in the native bar"; Copy-Item $log (Join-Path $Out "native-$tag-log.txt") -ErrorAction SilentlyContinue; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; continue }
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $m = $null
    for ($i = 0; $i -lt 40 -and -not $m; $i++) { Start-Sleep -Milliseconds 150; $m = [regex]::Match((LogText $log), 'pop-up Now playing: shown at (-?\d+),(-?\d+) (\d+)x(\d+)'); if (-not $m.Success) { $m = $null } }
    if (-not $m) { $rows += "$tag no score: the native card did not show"; Copy-Item $log (Join-Path $Out "native-$tag-log.txt"); Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; continue }
    Start-Sleep -Milliseconds 900   # the entrance (instant) and the first paint
    $x = [int]$m.Groups[1].Value - 12; $y = [int]$m.Groups[2].Value - 12; $w0 = [int]$m.Groups[3].Value + 24; $h0 = [int]$m.Groups[4].Value + 12 + 4
    $ef = Join-Path $Out "electron-media-$theme.png"
    if (-not (Test-Path $ef)) { $rows += "$tag no score: no Electron shot"; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; continue }
    $nat = [ParityNative]::Grab($x, $y, $w0, $h0)
    $nb = Png $nat $w0 $h0; $nb.Save((Join-Path $Out "native-$tag.png"))
    $eb = [System.Drawing.Bitmap]::FromFile($ef)
    $w = [Math]::Min($eb.Width, $nb.Width); $h = [Math]::Min($eb.Height, $nb.Height)
    $ec = $eb.Clone((New-Object System.Drawing.Rectangle([int](($eb.Width - $w) / 2), ($eb.Height - $h), $w, $h)), $eb.PixelFormat)
    $nc = $nb.Clone((New-Object System.Drawing.Rectangle([int](($nb.Width - $w) / 2), ($nb.Height - $h), $w, $h)), $nb.PixelFormat)
    $s = [ParityNative]::Diff((Bytes $ec), (Bytes $nc), $w, $h, @(), $PixelThreshold)
    $pw = [Math]::Max($eb.Width, $nb.Width)
    $pair = New-Object System.Drawing.Bitmap($pw, ($eb.Height + $nb.Height + 4)); $g = [System.Drawing.Graphics]::FromImage($pair); $g.Clear([System.Drawing.Color]::Red)
    $g.DrawImage($eb, 0, 0, $eb.Width, $eb.Height); $g.DrawImage($nb, 0, $eb.Height + 4, $nb.Width, $nb.Height); $g.Dispose(); $pair.Save((Join-Path $Out "$tag-pair.png"))
    $line = "MEDIA-PARITY $tag`: ssim={0:N4} diffpx={1:N2}% meanabs={2:N2} electron {3}x{4} native {5}x{6}" -f $s.Ssim, $s.DiffPxPct, $s.MeanAbs, $eb.Width, $eb.Height, $nb.Width, $nb.Height
    Write-Host $line; $rows += $line; if ($s.Ssim -lt $low) { $low = $s.Ssim }
    $eb.Dispose(); $nb.Dispose(); $ec.Dispose(); $nc.Dispose(); $pair.Dispose()
    Copy-Item $log (Join-Path $Out "native-$tag-log.txt")
    $q = Start-Process $Exe -PassThru -ArgumentList @('--quit', '--test', "--profile-name=$prof", "--user-data=$ud")
    if (-not $q.WaitForExit(15000)) { Stop-Process -Id $q.Id -Force -ErrorAction SilentlyContinue }
    if (-not $p.WaitForExit(8000)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
  }
} finally {
  Stop-Process -Id $back.Id -Force -ErrorAction SilentlyContinue
  Remove-Item Env:MANTLE_TEST_MEDIA -ErrorAction SilentlyContinue
}
$scored = @($rows | Where-Object { $_ -like 'MEDIA-PARITY*' }).Count
Write-Host ("MEDIA-PARITY summary: {0} scored of 2, lowest ssim {1:N4}; no masks" -f $scored, $low)
$rows | Where-Object { $_ -notlike 'MEDIA-PARITY*' } | ForEach-Object { Write-Host "MEDIA-PARITY problem: $_" }
