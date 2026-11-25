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

# ------------------------------------------------------------------ the reference (plan v0.2, references.psd1)
# The same reader and rule as run-parity.ps1, for the surface 'strip'. 'electron' (no file, no entry): Electron Mantle
# vs native, as below (the 0.90 floor in ci.ps1). 'native-golden': the bar has the new look, so the strip is the dock
# island (bar.island.dock, recorded by run-parity.ps1 in score.json), cropped out of native's candidate and scored
# against goldens/strip/<theme>-<pct>.png with the SAME floor, which counts only once goldens/strip/REVIEW.md has
# non-empty reviewer:, run: and sha: lines. Without them every theme is "golden pending review" and the stage fails.
# This script never writes into goldens/.
$refFile = Join-Path $root 'native\tools\parity\references.psd1'
$refs = if (Test-Path $refFile) { Import-PowerShellDataFile $refFile } else { @{} }
function Reference([string]$surface) { if ($refs.ContainsKey($surface) -and $refs[$surface]) { [string]$refs[$surface] } else { 'electron' } }
$reference = Reference 'strip'; $barReference = Reference 'bar'
if ($reference -notin 'electron', 'native-golden') { Write-Host "STRIP-PARITY: FAILED (references.psd1: strip = '$reference'; it must be 'electron' or 'native-golden')"; exit 4 }
# the strip is shot by run-parity.ps1's bar run, so the two must agree: Electron is shot only for an 'electron' bar, and
# the dock island is recorded only for a 'native-golden' bar
if ($reference -ne $barReference) { Write-Host "STRIP-PARITY: FAILED (references.psd1: strip = '$reference' but bar = '$barReference'; the strip is cropped from the bar's run, so both must name the same reference)"; exit 4 }
$golden = $reference -eq 'native-golden'
$goldenDir = Join-Path $root 'native\tools\parity\goldens\strip'
$reviewFile = Join-Path $goldenDir 'REVIEW.md'
$reviewed = $false
if ($golden -and (Test-Path $reviewFile)) {
  $rv = Get-Content $reviewFile -Raw
  $reviewed = ($rv -match '(?im)^[ \t]*reviewer:[ \t]*\S') -and ($rv -match '(?im)^[ \t]*run:[ \t]*\S') -and ($rv -match '(?im)^[ \t]*sha:[ \t]*\S')
}
Say ("reference {0}{1}" -f $reference, $(if ($golden) { "; goldens/strip reviewed: $reviewed" } else { '' }))

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
  # the desktop as ci.ps1 leaves it before a GUI stage: the runner's own console windows minimised, so the strip's
  # running-only apps (and which window is in front) are the same for both captures
  $desk = Join-Path $root 'native\tools\desk\desk.ps1'
  if (Test-Path $desk) { & $desk -Reset -Tag strip-parity }
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
if ($golden) {
  # native-golden: the dock island out of native's candidate (run-parity's crop of the islands band), vs the reviewed
  # golden; the same STRIP-PARITY <tag>: ssim= line, so ci.ps1's floor applies unchanged. No golden = pending, a FAIL.
  $lines = @(); $pending = @(); $problems = @()
  foreach ($dir in @(Get-ChildItem -Directory $Out | Sort-Object Name)) {
    $tag = $dir.Name; $sj = Join-Path $dir.FullName 'score.json'
    if (-not (Test-Path $sj)) { continue }   # <Out>/<theme>/ of a scale other than 100 holds only candidates
    $score = Get-Content $sj -Raw | ConvertFrom-Json
    if ($score.reference -ne 'native-golden') { $problems += "${tag}: run-parity's score.json is not native-golden"; continue }
    $cand = [string]$score.candidate
    if (-not $cand -or -not (Test-Path $cand)) { $problems += "${tag}: no native candidate (see run-parity's lines above)"; Write-Host "STRIP-PARITY ${tag}: no candidate"; continue }
    if (-not $score.dock) { $problems += "${tag}: no dock island rect (bar.island.dock) in score.json"; Write-Host "STRIP-PARITY ${tag}: no dock island rect"; continue }
    $crop = $score.crop; $dk = @($score.dock | ForEach-Object { [int]$_ })
    $pct = if ($tag -match '-(\d+)$') { [int]$Matches[1] } else { 100 }
    $theme = $tag -replace '-\d+$', ''
    # the dock island in crop px, inside the candidate
    $sx = [Math]::Max(0, $dk[0] - [int]$crop.x); $sy = [Math]::Max(0, $dk[1] - [int]$crop.y)
    $sw = [Math]::Min([int]$crop.w - $sx, $dk[2]); $sh2 = [Math]::Min([int]$crop.h - $sy, $dk[3])
    if ($sw -le 0 -or $sh2 -le 0) { $problems += "${tag}: the dock island ($($dk -join ',')) is outside the crop"; Write-Host "STRIP-PARITY ${tag}: dock island outside the crop"; continue }
    $b = Read-Bgra $cand $sx $sy $sw $sh2
    $cs = Join-Path (Split-Path -Parent $cand) "strip-candidate-$theme-$pct.png"
    Save-Bgra $b $sw $sh2 $cs
    $gp = Join-Path $goldenDir "$theme-$pct.png"
    if (-not ($reviewed -and (Test-Path $gp))) {
      $pending += $tag
      $line = "STRIP-PARITY ${tag}: golden pending review (reference native-golden; candidate $cs, dock island $sx,$sy ${sw}x$sh2 of the crop; freeze only after a side-by-side review against apps-r3/mantle-bar by the CEO, the Product Designer or the user, from a committed run)"
      Write-Host $line; $lines += $line; continue
    }
    $gi = [System.Drawing.Image]::FromFile($gp); $gw = $gi.Width; $gh = $gi.Height; $gi.Dispose()
    if ($gw -ne $sw -or $gh -ne $sh2) { $problems += "${tag}: golden $gp is ${gw}x$gh, the dock island is ${sw}x$sh2; not scored"; Write-Host "STRIP-PARITY ${tag}: golden size differs (${gw}x$gh vs ${sw}x$sh2)"; continue }
    $a = Read-Bgra $gp 0 0 $gw $gh
    $s = [ParityNative]::Diff($a, $b, $sw, $sh2, [int[]]@(), $PixelThreshold)
    $pair = [byte[]]::new($sw * ($sh2 * 2 + 4) * 4)
    [Array]::Copy($a, 0, $pair, 0, $a.Length)
    for ($i = $a.Length; $i -lt $a.Length + $sw * 4 * 4; $i += 4) { $pair[$i] = 0; $pair[$i + 1] = 0; $pair[$i + 2] = 255; $pair[$i + 3] = 255 }
    [Array]::Copy($b, 0, $pair, $a.Length + $sw * 4 * 4, $b.Length)
    Save-Bgra $pair $sw ($sh2 * 2 + 4) (Join-Path $dir.FullName 'strip-pair.png')
    $line = "STRIP-PARITY ${tag}: ssim={0:N4} diffpx={1:N2}% meanabs={2:N2} (dock island {3},{4} {5}x{6} of the crop vs golden {7})" -f $s.Ssim, $s.DiffPxPct, $s.MeanAbs, $sx, $sy, $sw, $sh2, $gp
    Write-Host $line; $lines += $line
  }
  $scoredN = @($lines | Where-Object { $_ -match 'ssim=' }).Count
  if (-not $lines.Count -and -not $problems.Count) { $problems += "no score.json from run-parity in $Out" }
  if ($problems.Count) {
    Write-Host ("STRIP-PARITY: FAILED (reference native-golden; {0} scored, {1} pending; {2} problem(s): {3}; run-parity exit {4})" -f $scoredN, $pending.Count, $problems.Count, ($problems -join '; '), $code)
    exit 1
  }
  if ($pending.Count) { Write-Host ("STRIP-PARITY: FAILED (strip golden pending review: {0}; candidates in {1})" -f ($pending -join ', '), $Out); exit 1 }
  # the bar's own pending review fails run-parity's exit; the strip's verdict here is its own (ci.ps1 applies the floor)
  Write-Host ("STRIP-PARITY: done (reference native-golden; run-parity exit {0}; {1} pair(s)); look at native/out/parity/bar-tasks/*/strip-pair.png" -f $code, $scoredN)
  exit 0
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
