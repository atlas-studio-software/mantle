<#
  Top bar parity (lane native-r7-topbar): the strip, Electron's page vs the native window, light and dark, 100 % and 150 %.
    cloudtest --recipe mantle-native-public --artifacts "native/out/parity/topbar/**" -- "pwsh -NoProfile -File native/tools/parity/topbar/run-topbar-parity.ps1 -Build"

  Per scale (the runner's primary display set with ParityNative.SetScale, put back to 100 % at the end):
  Electron: electron-topbar.cjs opens ui/topbar/index.html?app=npp&stats=ok over its own mock bridge offscreen, at the
  screen's width in DIP and force-device-scale-factor = the scale, over one colour, and shoots the 28 DIP strip.
  Native: one MantleNative --test run per theme with MANTLE_TEST_VISIBLE=1 and MANTLE_TEST_TOPBAR=app=npp&stats=ok (the
  same made-up Notepad++ and stats, TopBarFixtures), the strip's rect read from its "topbar: placed at" line and grabbed
  over a full-screen backdrop window of that colour (another process, not topmost; the --test strip is never topmost).
  The cursor is parked mid-screen, off the strip. Both crops are compared top-left aligned at the smaller size (SSIM on
  luminance, ParityNative.Diff, no masks); writes <out>/topbar-<theme>-<pct>-pair.png (Electron above, native below)
  and one TOPBAR-PARITY line each, then a summary line.

  Runner only (CI / GITHUB_ACTIONS, or MANTLE_PARITY_LOCAL=1 on a machine nobody is using): it shows windows and
  changes the display scale. ci.ps1 stage topbar runs it as
    & native/tools/parity/topbar/run-topbar-parity.ps1 -Exe <published exe> -Out native/out/parity/topbar
  gated on "TOPBAR-PARITY summary: 4 scored of 4" and per-theme floors under the lowest ssim it printed.
#>
param(
  [string]$Out,
  [string]$Exe,
  [string]$Bg = '#7f8a99',
  [int[]]$Scales = @(100, 150),
  [int]$PixelThreshold = 24,
  [switch]$Build
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
function Say($m) { Write-Host ("topbar-parity {0}: {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) }
if (-not $env:CI -and -not $env:GITHUB_ACTIONS -and $env:MANTLE_PARITY_LOCAL -ne '1') { throw 'runner only: it shows windows and changes the display scale (set MANTLE_PARITY_LOCAL=1 only on a machine nobody is using)' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
if (-not $Out) { $Out = Join-Path $root 'native/out/parity/topbar' }
New-Item -ItemType Directory -Force $Out | Out-Null
if ($Build) { & (Join-Path $root 'native/tools/ci.ps1') -Stages build; if ($LASTEXITCODE) { throw "the build stage failed ($LASTEXITCODE)" } }
if (-not $Exe) { $Exe = if ($env:MANTLE_NATIVE_EXE) { $env:MANTLE_NATIVE_EXE } else { Join-Path $root 'native/out/bin/MantleNative/MantleNative.exe' } }
if (-not (Test-Path $Exe)) { throw "no native app at $Exe (run with -Build)" }
$electron = Join-Path $root 'node_modules/electron/dist/electron.exe'
if (-not (Test-Path $electron)) { throw "no Electron at $electron (npm ci first; the recipe's setup does)" }

Add-Type -Path (Join-Path $root 'native/tools/parity/ParityNative.cs')
Add-Type -AssemblyName System.Drawing
[void][ParityNative]::DpiAware()

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
function SetScale([int]$pct) {
  $want = [int][Math]::Round(96 * $pct / 100.0)
  if ([ParityNative]::PrimaryDpi() -eq $want) { return "already $pct %" }
  $r = [ParityNative]::SetScale($pct)
  for ($i = 0; $i -lt 50 -and [ParityNative]::PrimaryDpi() -ne $want; $i++) { Start-Sleep -Milliseconds 200 }
  Start-Sleep -Milliseconds 2000   # the shell and the backdrop settle at the new dpi
  return "$r; dpi now $([ParityNative]::PrimaryDpi())"
}

# ---- the backdrop: one solid colour behind the native strip (another process, not topmost)
$c = [System.Drawing.ColorTranslator]::FromHtml($Bg)
$ps = "Add-Type -AssemblyName System.Windows.Forms; `$f = New-Object System.Windows.Forms.Form; `$f.Text = 'Top bar parity backdrop'; " +
      "`$f.FormBorderStyle = 'None'; `$f.StartPosition = 'Manual'; `$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds; " +
      "`$f.Location = New-Object System.Drawing.Point(`$b.X, `$b.Y); `$f.Size = New-Object System.Drawing.Size(`$b.Width, `$b.Height); " +
      "`$f.BackColor = [System.Drawing.Color]::FromArgb($($c.R), $($c.G), $($c.B)); [System.Windows.Forms.Application]::Run(`$f)"

$rows = @(); $low = @{}; $want = 0
try {
  foreach ($pct in $Scales) {
    Say "scale $pct %: $(SetScale $pct)"
    $dpi = [ParityNative]::PrimaryDpi()
    if ($dpi -ne [int][Math]::Round(96 * $pct / 100.0)) { $rows += "$pct no score: the display stayed at dpi $dpi"; $want += 2; continue }
    $scr = [ParityNative]::Screen(); $sw = $scr[0]; $sh = $scr[1]
    $scale = $pct / 100.0
    Say "Electron page at $sw px / $scale"
    & $electron (Join-Path $PSScriptRoot 'electron-topbar.cjs') $Out $Bg $sw $scale $pct 2>&1 | Where-Object { "$_" -match 'ELECTRON-TOPBAR' } | ForEach-Object { Write-Host "$_" }
    if ($LASTEXITCODE) { $rows += "$pct no score: electron-topbar.cjs failed ($LASTEXITCODE)"; $want += 2; continue }
    # a fresh backdrop per scale, so it covers the screen at this dpi
    $back = Start-Process powershell.exe -PassThru -ArgumentList @('-NoProfile', '-EncodedCommand', [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($ps)))
    Start-Sleep -Milliseconds 2500
    [ParityNative]::ParkCursor([int]($sw / 2), [int]($sh / 2))
    try {
      foreach ($theme in 'light', 'dark') {
        $want++
        $tag = "$theme-$pct"
        $ud = Join-Path ([IO.Path]::GetTempPath()) ("mantle-tbpar-" + [Guid]::NewGuid().ToString('N').Substring(0, 6))
        New-Item -ItemType Directory -Force $ud | Out-Null
        $env:MANTLE_TEST_VISIBLE = '1'; $env:MANTLE_TEST_TOPBAR = 'app=npp&stats=ok'
        $prof = "tbpar-$tag"
        $p = Start-Process $Exe -PassThru -ArgumentList @('--test', "--profile-name=$prof", "--user-data=$ud", "--theme=$theme")
        $log = Join-Path $ud 'mantle-native.log'
        $m = $null
        for ($i = 0; $i -lt 150 -and -not $m; $i++) {
          Start-Sleep -Milliseconds 200
          $t = LogText $log
          if ($t -match 'topbar: shown \(') { $mm = [regex]::Matches($t, "topbar: placed at (-?\d+),(-?\d+) (\d+)x(\d+) px at $pct %"); if ($mm.Count) { $m = $mm[$mm.Count - 1] } }
        }
        if (-not $m) { $rows += "$tag no score: the native strip was not shown and placed at $pct % in 30 s"; Copy-Item $log (Join-Path $Out "native-$tag-log.txt") -ErrorAction SilentlyContinue; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; continue }
        Start-Sleep -Milliseconds 1500   # the fit, the stats paint and the theme
        $x = [int]$m.Groups[1].Value; $y = [int]$m.Groups[2].Value; $w0 = [int]$m.Groups[3].Value; $h0 = [int]$m.Groups[4].Value
        $ef = Join-Path $Out "electron-topbar-$tag.png"
        $ej = Join-Path $Out "electron-topbar-$tag.json"
        if (Test-Path $ej) { Say "Electron $tag page: $((Get-Content $ej -Raw).Trim())" }
        if (-not (Test-Path $ef)) { $rows += "$tag no score: no Electron shot"; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; continue }
        $nat = [ParityNative]::Grab($x, $y, $w0, $h0)
        $nb = Png $nat $w0 $h0; $nb.Save((Join-Path $Out "native-topbar-$tag.png"))
        $eb = [System.Drawing.Bitmap]::FromFile($ef)
        $w = [Math]::Min($eb.Width, $nb.Width); $h = [Math]::Min($eb.Height, $nb.Height)
        $ec = $eb.Clone((New-Object System.Drawing.Rectangle(0, 0, $w, $h)), $eb.PixelFormat)
        $nc = $nb.Clone((New-Object System.Drawing.Rectangle(0, 0, $w, $h)), $nb.PixelFormat)
        $s = [ParityNative]::Diff((Bytes $ec), (Bytes $nc), $w, $h, @(), $PixelThreshold)
        $pw = [Math]::Max($eb.Width, $nb.Width)
        $pair = New-Object System.Drawing.Bitmap($pw, ($eb.Height + $nb.Height + 4)); $g = [System.Drawing.Graphics]::FromImage($pair); $g.Clear([System.Drawing.Color]::Red)
        $g.DrawImage($eb, 0, 0, $eb.Width, $eb.Height); $g.DrawImage($nb, 0, $eb.Height + 4, $nb.Width, $nb.Height); $g.Dispose(); $pair.Save((Join-Path $Out "topbar-$tag-pair.png"))
        $line = "TOPBAR-PARITY $tag`: ssim={0:N4} diffpx={1:N2}% meanabs={2:N2} electron {3}x{4} native {5}x{6}" -f $s.Ssim, $s.DiffPxPct, $s.MeanAbs, $eb.Width, $eb.Height, $nb.Width, $nb.Height
        Write-Host $line; $rows += $line
        if (-not $low.ContainsKey($theme) -or $s.Ssim -lt $low[$theme]) { $low[$theme] = $s.Ssim }
        $eb.Dispose(); $nb.Dispose(); $ec.Dispose(); $nc.Dispose(); $pair.Dispose()
        Copy-Item $log (Join-Path $Out "native-$tag-log.txt")
        $q = Start-Process $Exe -PassThru -ArgumentList @('--quit', '--test', "--profile-name=$prof", "--user-data=$ud")
        if (-not $q.WaitForExit(15000)) { Stop-Process -Id $q.Id -Force -ErrorAction SilentlyContinue }
        if (-not $p.WaitForExit(8000)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
      }
    } finally { Stop-Process -Id $back.Id -Force -ErrorAction SilentlyContinue }
  }
} finally {
  if ([ParityNative]::PrimaryDpi() -ne 96) { Say "scale back to 100 %: $(SetScale 100)" }
  Remove-Item Env:MANTLE_TEST_TOPBAR -ErrorAction SilentlyContinue
}
$scored = @($rows | Where-Object { $_ -like 'TOPBAR-PARITY*' }).Count
$lows = ($low.Keys | Sort-Object | ForEach-Object { "{0} {1:N4}" -f $_, $low[$_] }) -join ', '
Write-Host ("TOPBAR-PARITY summary: {0} scored of {1}, lowest ssim per theme: {2}; no masks" -f $scored, $want, $lows)
$rows | Where-Object { $_ -notlike 'TOPBAR-PARITY*' } | ForEach-Object { Write-Host "TOPBAR-PARITY problem: $_" }
