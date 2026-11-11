<#
  Desktop parity (lane native-r7-desktop-2): the desktop layer's System widget, its Needs attention strip and its
  right-click menu, Electron's page vs the native windows, light and dark, over one fixture and one frozen instant.
    cloudtest --recipe mantle-native-public --artifacts "native/out/parity/desk/**" -- "pwsh -NoProfile -File native/tools/parity/desktop/run-desk-parity.ps1 -Build"

  Electron: electron-desk.cjs opens ui/desktop/index.html at 960x540 offscreen at scale 1 over a fake bridge that
  answers with the fixture (sys.stats, net.probe, a 'state' whose failing pieces are the fixture's strip rows), the
  page's Date frozen at the fixture's "now"; it shoots each pane plus 12 px over one colour, then fires deskmenu.prepare
  and deskmenu.show and shoots the menu's own window (the panel plus its 20 px inset) at the size the page asked for.
  Native: one MantleNative --test run per theme with MANTLE_TEST_VISIBLE=1 and MANTLE_TEST_DESKTOP = the same fixture
  (the test layer is see-through, so the backdrop of that colour shows through it as Electron's page background does);
  the panes' rects are read from its "desktop: panes (physical px on screen)" line and grabbed with the same margins;
  a second start with --test-surface=show:deskmenu opens the menu, whose rect is read from "desktop: menu opened at".
  Each pair is compared bottom-aligned and centred (SSIM on luminance, ParityNative.Diff, no masks); writes
  <out>/desk-<theme>-<part>-pair.png (Electron above, native below) and one DESK-PARITY line each, then a summary line.

  Runner only (CI / GITHUB_ACTIONS, or MANTLE_PARITY_LOCAL=1 on a machine nobody is using): it shows windows.
  ci.ps1 stage desktop runs it as
    & native/tools/parity/desktop/run-desk-parity.ps1 -Exe <published exe> -Out native/out/parity/desk
  gated on "DESK-PARITY summary: 6 scored of 6" and per-shot floors under the lowest ssim it printed.
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
function Say($m) { Write-Host ("desk-parity {0}: {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) }
if (-not $env:CI -and -not $env:GITHUB_ACTIONS -and $env:MANTLE_PARITY_LOCAL -ne '1') { throw 'runner only: it shows windows (set MANTLE_PARITY_LOCAL=1 only on a machine nobody is using)' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
if (-not $Out) { $Out = Join-Path $root 'native/out/parity/desk' }
New-Item -ItemType Directory -Force $Out | Out-Null
if ($Build) { & (Join-Path $root 'native/tools/ci.ps1') -Stages build; if ($LASTEXITCODE) { throw "the build stage failed ($LASTEXITCODE)" } }
if (-not $Exe) { $Exe = if ($env:MANTLE_NATIVE_EXE) { $env:MANTLE_NATIVE_EXE } else { Join-Path $root 'native/out/bin/MantleNative/MantleNative.exe' } }
if (-not (Test-Path $Exe)) { throw "no native app at $Exe (run with -Build)" }

Add-Type -Path (Join-Path $root 'native/tools/parity/ParityNative.cs')
Add-Type -AssemblyName System.Drawing
[void][ParityNative]::DpiAware()
# The test layer is an ordinary window, and its first show hands the foreground straight back to whatever had it (the
# backdrop), which lifts the backdrop over it, and the runner's console can sit over it too: the layer is made
# topmost while its panes are shot; then the backdrop is, under the menu.
Add-Type -Namespace DeskPar -Name Z -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string title);
[DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
public static bool Raise(string title) { IntPtr h = FindWindowW(null, title); return h != IntPtr.Zero && SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, 0x0013); }
public static bool Topmost(string title, bool on) { IntPtr h = FindWindowW(null, title); return h != IntPtr.Zero && SetWindowPos(h, new IntPtr(on ? -1 : -2), 0, 0, 0, 0, 0x0013); }
public static bool Under(string top, string below) { IntPtr t = FindWindowW(null, top), b = FindWindowW(null, below); return t != IntPtr.Zero && b != IntPtr.Zero && SetWindowPos(b, t, 0, 0, 0, 0, 0x0013); }
'@

# ---- the fixture both sides draw (the UI tests' own: a frozen 4:15:32 PM, numbers, no dashboard, two strip rows)
$now = '2026-11-06T16:15:32'
$fixture = [ordered]@{
  now = $now
  stats = [ordered]@{ cpu = 7; memUsed = 20830591385; memTotal = 34359738368; battery = [ordered]@{ percent = 81; charging = $true }; batteryReason = 'ok' }
  mc = 'connect ECONNREFUSED 127.0.0.1:7373'
  strip = @(
    , @('Sidecar', 'it exited (code 3), so Mantle cannot read the taskbar or the tray; restart Mantle')
    , @('Desktop layer', 'the desktop layer could not go under the wallpaper, so it is floating as an ordinary window')
  )
  menuAt = @(300, 200)
}
$fxJson = $fixture | ConvertTo-Json -Depth 6 -Compress
$fxFile = Join-Path $Out 'desk-fixture.json'
[IO.File]::WriteAllText($fxFile, $fxJson)
$nowMs = ([DateTimeOffset]([DateTime]::ParseExact($now, 's', [Globalization.CultureInfo]::InvariantCulture, 'AssumeLocal'))).ToUnixTimeMilliseconds()

# ---- Electron (both themes)
$electron = Join-Path $root 'node_modules/electron/dist/electron.exe'
if (-not (Test-Path $electron)) { throw "no Electron at $electron (npm ci first; the recipe's setup does)" }
Say "Electron page, Date frozen at $now local ($nowMs)"
& $electron (Join-Path $PSScriptRoot 'electron-desk.cjs') $Out $Bg $fxFile "$nowMs" 2>&1 | Where-Object { "$_" -match 'ELECTRON-DESK' } | ForEach-Object { Write-Host "$_" }
if ($LASTEXITCODE) { throw "electron-desk.cjs failed ($LASTEXITCODE)" }

# ---- the backdrop: one solid colour behind the native windows (another process, not topmost)
$c = [System.Drawing.ColorTranslator]::FromHtml($Bg)
$ps = "Add-Type -AssemblyName System.Windows.Forms; `$f = New-Object System.Windows.Forms.Form; `$f.Text = 'Desktop parity backdrop'; " +
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

# one pair: the native grab (x, y, w, h on screen) against Electron's shot, bottom-aligned and centred
function Score([string]$tag, [string]$ef, [int]$x, [int]$y, [int]$w0, [int]$h0) {
  if (-not (Test-Path $ef)) { return "$tag no score: no Electron shot ($([IO.Path]::GetFileName($ef)))" }
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
  $line = "DESK-PARITY $tag`: ssim={0:N4} diffpx={1:N2}% meanabs={2:N2} electron {3}x{4} native {5}x{6}" -f $s.Ssim, $s.DiffPxPct, $s.MeanAbs, $eb.Width, $eb.Height, $nb.Width, $nb.Height
  $eb.Dispose(); $nb.Dispose(); $ec.Dispose(); $nc.Dispose(); $pair.Dispose()
  return $line
}

$rows = @(); $low = 1.0
try {
  foreach ($theme in 'light', 'dark') {
    $ud = Join-Path ([IO.Path]::GetTempPath()) ("mantle-deskpar-" + [Guid]::NewGuid().ToString('N').Substring(0, 6))
    New-Item -ItemType Directory -Force $ud | Out-Null
    $env:MANTLE_TEST_VISIBLE = '1'; $env:MANTLE_TEST_DESKTOP = $fxJson
    $prof = "deskpar-$theme"
    $common = @('--test', "--profile-name=$prof", "--user-data=$ud", "--theme=$theme")
    $p = Start-Process $Exe -PassThru -ArgumentList $common
    $log = Join-Path $ud 'mantle-native.log'
    $re = 'desktop: panes \(physical px on screen\): clock=(-?\d+),(-?\d+),(\d+),(\d+) stats=(-?\d+),(-?\d+),(\d+),(\d+) mc=(-?\d+),(-?\d+),(\d+),(\d+) strip=(-?\d+),(-?\d+),(\d+),(\d+)'
    $m = $null
    for ($i = 0; $i -lt 150 -and -not $m; $i++) { Start-Sleep -Milliseconds 200; $all = [regex]::Matches((LogText $log), $re); if ($all.Count) { $m = $all[$all.Count - 1] } }
    if (-not $m) { $rows += "desk-$theme no score: the native layer never reported its panes in 30 s"; Copy-Item $log (Join-Path $Out "native-desk-$theme-log.txt") -ErrorAction SilentlyContinue; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; continue }
    # over everything while its panes are shot (the runner's own console can sit over an ordinary window), then back
    if (-not [DeskPar.Z]::Topmost('Mantle desktop', $true)) { Say 'the native layer could not be put over everything' }
    Start-Sleep -Milliseconds 1500   # the last paint, then the rects again (a late layout pass re-reports them)
    $all = [regex]::Matches((LogText $log), $re); $m = $all[$all.Count - 1]
    $g = $m.Groups
    try { $lay = [ParityNative]::Grab(40, 40, 960, 540); (Png $lay 960 540).Save((Join-Path $Out "native-desk-$theme-layer.png")) } catch { Say "no layer grab: $($_.Exception.Message)" }
    Write-Host "desk-parity native $theme $($m.Value)"
    foreach ($part in @(@{ n = 'stats'; i = 5 }, @{ n = 'strip'; i = 13 })) {
      $k = $part.i
      $x = [int]$g[$k].Value - 12; $y = [int]$g[$k + 1].Value - 12; $w0 = [int]$g[$k + 2].Value + 24; $h0 = [int]$g[$k + 3].Value + 24
      try { $line = Score "desk-$theme-$($part.n)" (Join-Path $Out "electron-desk-$theme-$($part.n).png") $x $y $w0 $h0 } catch { $line = "desk-$theme-$($part.n) no score: $($_.Exception.Message)" }
      Write-Host $line; $rows += $line
    }
    # the menu's see-through margin must show the backdrop, not whatever is under it: the backdrop goes over everything
    # (the menu is raised over it when it opens)
    [void][DeskPar.Z]::Topmost('Mantle desktop', $false)
    if (-not [DeskPar.Z]::Topmost('Desktop parity backdrop', $true)) { Say 'the backdrop could not be put over everything' }
    # the menu, through the test road (a second start: no mouse, no right-click hook)
    $q = Start-Process $Exe -PassThru -ArgumentList ($common + '--test-surface=show:deskmenu')
    if (-not $q.WaitForExit(15000)) { Stop-Process -Id $q.Id -Force -ErrorAction SilentlyContinue }
    $mm = $null
    for ($i = 0; $i -lt 40 -and -not $mm; $i++) { Start-Sleep -Milliseconds 150; $mm = [regex]::Match((LogText $log), 'desktop: menu opened at (-?\d+),(-?\d+) (\d+)x(\d+) px'); if (-not $mm.Success) { $mm = $null } }
    if (-not $mm) { $rows += "desk-$theme-menu no score: the native menu did not open" }
    else {
      Start-Sleep -Milliseconds 900   # the pop-in
      try { $line = Score "desk-$theme-menu" (Join-Path $Out "electron-desk-$theme-menu.png") ([int]$mm.Groups[1].Value) ([int]$mm.Groups[2].Value) ([int]$mm.Groups[3].Value) ([int]$mm.Groups[4].Value) } catch { $line = "desk-$theme-menu no score: $($_.Exception.Message)" }
      Write-Host $line; $rows += $line
    }
    Copy-Item $log (Join-Path $Out "native-desk-$theme-log.txt") -ErrorAction SilentlyContinue
    $q = Start-Process $Exe -PassThru -ArgumentList @('--quit', '--test', "--profile-name=$prof", "--user-data=$ud")
    if (-not $q.WaitForExit(15000)) { Stop-Process -Id $q.Id -Force -ErrorAction SilentlyContinue }
    if (-not $p.WaitForExit(8000)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
  }
} finally {
  Stop-Process -Id $back.Id -Force -ErrorAction SilentlyContinue
  Remove-Item Env:MANTLE_TEST_DESKTOP -ErrorAction SilentlyContinue
}
foreach ($r in $rows) { $sm = [regex]::Match($r, 'ssim=([\d.]+)'); if ($r -like 'DESK-PARITY*' -and $sm.Success) { $v = [double]::Parse($sm.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture); if ($v -lt $low) { $low = $v } } }
$scored = @($rows | Where-Object { $_ -like 'DESK-PARITY*' }).Count
Write-Host ("DESK-PARITY summary: {0} scored of 6, lowest ssim {1:N4}; no masks" -f $scored, $low)
$rows | Where-Object { $_ -notlike 'DESK-PARITY*' } | ForEach-Object { Write-Host "DESK-PARITY problem: $_" }
