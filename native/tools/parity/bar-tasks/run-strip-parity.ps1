<#
  Strip parity (native-bar-tasks): the bar's apps strip, Electron Mantle vs native, with the SAME scratch pins.

    cloudtest --recipe mantle-native-public -- "pwsh -NoProfile -File native/tools/parity/bar-tasks/run-strip-parity.ps1 -Build"

  1. A scratch %APPDATA% with a TaskBar pin folder of real .lnk files (WScript.Shell): Alpha Shell (powershell.exe,
     which run-parity.ps1's "Parity fixture" window runs, so that pin is open), Beta Map (charmap.exe) and Gamma Reg
     (regedit.exe), neither running.
  2. The runner's Taskband Favorites value is removed for the run (and put back after), so both apps fall back to
     alphabetical order: Electron's lib/pinned.js reads %APPDATA% and that value; a native test run reads only
     MANTLE_TEST_PINS_DIR (and MANTLE_TEST_TASKBAND_KEY, left unset).
  3. run-parity.ps1 (unchanged) with -MaskApps $false, into native/out/parity/bar-tasks/<theme>[-<pct>]/, so the
     strip counts in its score; then this script crops the apps slot (the pill's centre, 440 DIP) out of both
     captures, writes electron-strip.png / native-strip.png / strip-pair.png (Electron above, native below) and
     prints one STRIP-PARITY line per capture plus a summary line.

  Runner only (CI / GITHUB_ACTIONS, or MANTLE_PARITY_LOCAL=1 on a machine nobody is using): it changes the
  wallpaper, theme, taskbar and a Taskband value, and shows both apps.
#>
param(
  [ValidateSet('light', 'dark', 'both')] [string]$Theme = 'both',
  [string]$Scales = '100',
  [string]$Out,
  [int]$PixelThreshold = 24,
  # run ci.ps1's build stage first (the recipe's command override starts from a clean checkout)
  [switch]$Build
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
function Say($m) { Write-Host ("strip-parity {0}: {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) }
if (-not $env:CI -and -not $env:GITHUB_ACTIONS -and $env:MANTLE_PARITY_LOCAL -ne '1') {
  Write-Host 'STRIP-PARITY: refused (runner only: it changes the wallpaper, theme, taskbar and the Taskband Favorites value, and shows both apps; MANTLE_PARITY_LOCAL=1 overrides it on a machine nobody is using)'
  exit 2
}
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
if (-not $Out) { $Out = Join-Path $root 'native\out\parity\bar-tasks' }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path

if ($Build) {
  & (Join-Path $root 'native/tools/ci.ps1') -Stages build
  if (-not (Test-Path (Join-Path $root 'native/out/bin/MantleNative/MantleNative.exe'))) { Write-Host 'STRIP-PARITY: FAILED (the build stage made no MantleNative.exe)'; exit 1 }
}

# 1. the scratch pins
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('mantle-strip-parity-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
$appData = Join-Path $scratch 'Roaming'
$pinsDir = Join-Path $appData 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
New-Item -ItemType Directory -Force -Path $pinsDir | Out-Null
$sys = [Environment]::SystemDirectory
$pins = @(
  @('Alpha Shell', (Join-Path $sys 'WindowsPowerShell\v1.0\powershell.exe')),
  @('Beta Map', (Join-Path $sys 'charmap.exe')),
  @('Gamma Reg', (Join-Path $env:SystemRoot 'regedit.exe'))
)
$sh = New-Object -ComObject WScript.Shell
foreach ($p in $pins) { $l = $sh.CreateShortcut((Join-Path $pinsDir ($p[0] + '.lnk'))); $l.TargetPath = $p[1]; $l.Save() }
Say ("pins: {0} in {1}" -f (@(Get-ChildItem $pinsDir -Filter *.lnk | ForEach-Object Name) -join ', '), $pinsDir)

# 2. no Favorites order on the runner for the run
$tbKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband'
$savedFav = $null
if (Test-Path $tbKey) {
  $savedFav = (Get-ItemProperty -Path $tbKey -Name Favorites -ErrorAction SilentlyContinue).Favorites
  if ($null -ne $savedFav) { Remove-ItemProperty -Path $tbKey -Name Favorites; Say "the runner's Taskband Favorites value ($($savedFav.Length) bytes) is off for the run" }
  else { Say 'the runner has no Taskband Favorites value' }
}

# 3. both apps inherit these (run-parity.ps1 and electron-bar.mjs start them with this process's environment)
$oldAppData = $env:APPDATA
$env:APPDATA = $appData
$env:MANTLE_TEST_PINS_DIR = $pinsDir
$code = 1
try {
  & (Join-Path $root 'native\tools\parity\run-parity.ps1') -Theme $Theme -Scales $Scales -Out $Out -MaskApps $false
  $code = $LASTEXITCODE
} finally {
  $env:APPDATA = $oldAppData
  Remove-Item Env:MANTLE_TEST_PINS_DIR -ErrorAction SilentlyContinue
  if ($null -ne $savedFav) { New-ItemProperty -Path $tbKey -Name Favorites -PropertyType Binary -Value $savedFav -Force | Out-Null; Say 'Taskband Favorites put back' }
}

# 4. the apps slot of both captures
Add-Type -AssemblyName System.Drawing
if (-not ('ParityNative' -as [type])) { Add-Type -Path (Join-Path $root 'native\tools\parity\ParityNative.cs') }
function Read-Bgra([string]$path, [int]$x, [int]$y, [int]$w, [int]$h) {
  $bmp = [System.Drawing.Bitmap]::FromFile($path)
  try {
    $r = [System.Drawing.Rectangle]::new($x, $y, $w, $h)
    $d = $bmp.LockBits($r, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
      $px = [byte[]]::new($w * $h * 4)
      for ($row = 0; $row -lt $h; $row++) { [Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($d.Scan0, $row * $d.Stride), $px, $row * $w * 4, $w * 4) }
      return , $px
    } finally { $bmp.UnlockBits($d) }
  } finally { $bmp.Dispose() }
}
function Save-Bgra([byte[]]$px, [int]$w, [int]$h, [string]$path) {
  $bmp = [System.Drawing.Bitmap]::new($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $d = $bmp.LockBits([System.Drawing.Rectangle]::new(0, 0, $w, $h), [System.Drawing.Imaging.ImageLockMode]::WriteOnly, $bmp.PixelFormat)
  for ($row = 0; $row -lt $h; $row++) { [Runtime.InteropServices.Marshal]::Copy($px, $row * $w * 4, [IntPtr]::Add($d.Scan0, $row * $d.Stride), $w * 4) }
  $bmp.UnlockBits($d); $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
$lines = @()
foreach ($dir in @(Get-ChildItem -Directory $Out | Sort-Object Name)) {
  $tag = $dir.Name
  $ep = Join-Path $dir.FullName 'electron.png'; $np = Join-Path $dir.FullName 'native.png'; $sj = Join-Path $dir.FullName 'score.json'
  if (-not ((Test-Path $ep) -and (Test-Path $np) -and (Test-Path $sj))) { $lines += "STRIP-PARITY ${tag}: no pair (see run-parity's lines above)"; continue }
  $score = Get-Content $sj -Raw | ConvertFrom-Json
  $crop = $score.crop
  $pct = if ($tag -match '-(\d+)$') { [int]$Matches[1] } else { 100 }
  $scale = $pct / 100.0
  $full = [System.Drawing.Image]::FromFile((Join-Path $dir.FullName 'native-full.png')); $W = $full.Width; $H = $full.Height; $full.Dispose()
  # the same arithmetic as run-parity.ps1: the pill's spec rect, then its centre 440 DIP (the apps slot), in crop px
  $pw = [int]([Math]::Min(1120, $W / $scale - 28) * $scale)
  $pillX = [int](($W - $pw) / 2); $pillY = [int]($H - (14 + 48) * $scale)
  $sx = $pillX + [int]($pw / 2) - [int](220 * $scale) - [int]$crop.x; $sw = [int](440 * $scale)
  $sy = $pillY - [int]$crop.y; $sh2 = [int](48 * $scale)
  $a = Read-Bgra $ep $sx $sy $sw $sh2
  $b = Read-Bgra $np $sx $sy $sw $sh2
  $s = [ParityNative]::Diff($a, $b, $sw, $sh2, [int[]]@(), $PixelThreshold)
  Save-Bgra $a $sw $sh2 (Join-Path $dir.FullName 'electron-strip.png')
  Save-Bgra $b $sw $sh2 (Join-Path $dir.FullName 'native-strip.png')
  $pair = [byte[]]::new($sw * ($sh2 * 2 + 4) * 4)
  [Array]::Copy($a, 0, $pair, 0, $a.Length)
  for ($i = $a.Length; $i -lt $a.Length + $sw * 4 * 4; $i += 4) { $pair[$i] = 0; $pair[$i + 1] = 0; $pair[$i + 2] = 255; $pair[$i + 3] = 255 }
  [Array]::Copy($b, 0, $pair, $a.Length + $sw * 4 * 4, $b.Length)
  Save-Bgra $pair $sw ($sh2 * 2 + 4) (Join-Path $dir.FullName 'strip-pair.png')
  $line = "STRIP-PARITY ${tag}: ssim={0:N4} diffpx={1:N2}% meanabs={2:N2} (apps slot {3},{4} {5}x{6} of the crop)" -f $s.Ssim, $s.DiffPxPct, $s.MeanAbs, $sx, $sy, $sw, $sh2
  Write-Host $line
  $lines += $line
}
Write-Host ("STRIP-PARITY: done (run-parity exit {0}; {1} pair(s)); look at native/out/parity/bar-tasks/*/strip-pair.png" -f $code, @($lines | Where-Object { $_ -match 'ssim=' }).Count)
exit $code
