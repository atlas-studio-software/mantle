<#
  OSD parity (lane native-osd-toasts): the volume pop-up, Electron's page vs the native window, light and dark.
    cloudtest --recipe mantle-native-public -- "pwsh -NoProfile -File native/tools/parity/osd-toasts/run-osd-parity.ps1 -Build"
  Electron: electron-osd.cjs shoots ui/osd/index.html over its own mock bridge in headless Chromium (as ui/osd/shoot.js
  does), the pane plus the 18 DIP margin, over ONE solid colour. Native: a full-screen backdrop window of that colour
  (another process), then per state one MantleNative --test run with MANTLE_TEST_VISIBLE=1 and MANTLE_TEST_OSD = the
  same made-up reading plus "at" (the window's top left, mid-screen, away from the bar), a second start's
  --test-surface=show:osd, and a GDI grab of the real composited window. Both are diffed with ParityNative.Diff (SSIM
  on luminance); writes <out>/<state>-<theme>-pair.png (Electron above, native below) and one OSD-PARITY line each,
  then one summary line. Nothing is masked.
  Runner only (CI / GITHUB_ACTIONS, or MANTLE_PARITY_LOCAL=1 on a machine nobody is using): it shows windows.
#>
param(
  [string]$Out,
  [string]$Exe,
  [string]$Bg = '#7f8a99',
  [string]$States = 'vol-62,vol-muted,vol-0,vol-100,vol-dead,bright-70',
  [int]$PixelThreshold = 24,
  [switch]$Build
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
function Say($m) { Write-Host ("osd-parity {0}: {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) }
if (-not $env:CI -and -not $env:GITHUB_ACTIONS -and $env:MANTLE_PARITY_LOCAL -ne '1') { throw 'runner only: it shows windows (set MANTLE_PARITY_LOCAL=1 only on a machine nobody is using)' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
if (-not $Out) { $Out = Join-Path $root 'native/out/parity/osd' }
New-Item -ItemType Directory -Force $Out | Out-Null
if ($Build) { & (Join-Path $root 'native/tools/ci.ps1') -Stages build; if ($LASTEXITCODE) { throw "the build stage failed ($LASTEXITCODE)" } }
if (-not $Exe) { $Exe = if ($env:MANTLE_NATIVE_EXE) { $env:MANTLE_NATIVE_EXE } else { Join-Path $root 'native/out/bin/MantleNative/MantleNative.exe' } }
if (-not (Test-Path $Exe)) { throw "no native app at $Exe (run with -Build)" }

Add-Type -Path (Join-Path $root 'native/tools/parity/ParityNative.cs')
Add-Type -AssemblyName System.Drawing
[void][ParityNative]::DpiAware()

# the made-up readings: Electron's mock query and the native testShow args, the same state
$all = [ordered]@{
  'vol-62'    = @{ q = 'level=62';                 n = @{ level = 62 } }
  'vol-muted' = @{ q = 'level=38&muted=1';         n = @{ level = 38; muted = $true } }
  'vol-0'     = @{ q = 'level=0';                  n = @{ level = 0 } }
  'vol-100'   = @{ q = 'level=100';                n = @{ level = 100 } }
  'vol-dead'  = @{ q = 'dead=1';                   n = @{ dead = $true; reason = 'no playback device is enabled (Windows reports none)' } }
  'bright-70' = @{ q = 'kind=brightness&level=70'; n = @{ kind = 'brightness'; level = 70 } }
}
$pick = @($States -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $all.Contains($_) })

# ---- Electron
$specs = @($pick | ForEach-Object { "$_=$($all[$_].q)" })
Say "Electron page: $($specs -join ' ')"
$electron = Join-Path $root 'node_modules/electron/dist/electron.exe'
if (-not (Test-Path $electron)) { throw "no Electron at $electron (npm ci first; the recipe's setup does)" }
& $electron (Join-Path $PSScriptRoot 'electron-osd.cjs') $Out $Bg @specs 2>&1 | Where-Object { "$_" -match 'ELECTRON-OSD' } | ForEach-Object { Write-Host "$_" }
if ($LASTEXITCODE) { throw "electron-osd.cjs failed ($LASTEXITCODE)" }

# ---- the backdrop: one solid colour behind the native window (another process, not topmost)
$c = [System.Drawing.ColorTranslator]::FromHtml($Bg)
$ps = "Add-Type -AssemblyName System.Windows.Forms; `$f = New-Object System.Windows.Forms.Form; `$f.Text = 'OSD parity backdrop'; " +
      "`$f.FormBorderStyle = 'None'; `$f.StartPosition = 'Manual'; `$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds; " +
      "`$f.Location = New-Object System.Drawing.Point(`$b.X, `$b.Y); `$f.Size = New-Object System.Drawing.Size(`$b.Width, `$b.Height); " +
      "`$f.BackColor = [System.Drawing.Color]::FromArgb($($c.R), $($c.G), $($c.B)); [System.Windows.Forms.Application]::Run(`$f)"
$back = Start-Process powershell.exe -PassThru -ArgumentList @('-NoProfile', '-EncodedCommand', [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($ps)))
Start-Sleep -Milliseconds 2500
$scr = [ParityNative]::Screen()
$atX = [int]($scr[0] / 2 - 200); $atY = [int]($scr[1] / 2 - 120)
[ParityNative]::ParkCursor(5, 5)

Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class OsdWin {
  public delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(P f, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder b, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  public static int[] Find(int pid, string title) {
    int[] got = null;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); var sb = new StringBuilder(256); GetWindowTextW(h, sb, 256);
      if (p == pid && sb.ToString() == title && IsWindowVisible(h)) { R r; GetWindowRect(h, out r); got = new[] { r.L, r.T, r.Rt - r.L, r.B - r.T }; return false; } return true; }, IntPtr.Zero);
    return got;
  }
}
'@

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

$rows = @(); $low = 1.0
try {
  foreach ($theme in 'light', 'dark') {
    foreach ($st in $pick) {
      $tag = "$st-$theme"
      $ud = Join-Path ([IO.Path]::GetTempPath()) ("mantle-osdpar-" + [Guid]::NewGuid().ToString('N').Substring(0, 6))
      New-Item -ItemType Directory -Force $ud | Out-Null
      $spec = $all[$st].n.Clone(); $spec['at'] = @($atX, $atY)
      $env:MANTLE_TEST_VISIBLE = '1'; $env:MANTLE_TEST_OSD = ($spec | ConvertTo-Json -Compress)
      $prof = "osdpar-$tag"
      $p = Start-Process $Exe -PassThru -ArgumentList @('--test', "--profile-name=$prof", "--user-data=$ud", "--theme=$theme")
      $log = Join-Path $ud 'mantle-native.log'
      $ok = $false
      for ($i = 0; $i -lt 150 -and -not $ok; $i++) { Start-Sleep -Milliseconds 200; $ok = (Test-Path $log) -and ((Get-Content $log -Raw) -match 'surface osd: made at start') }
      if (-not $ok) { $rows += "$tag no score: the native osd surface was not made in 30 s"; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; continue }
      Start-Sleep -Milliseconds 800
      Start-Process $Exe -Wait -ArgumentList @('--test', "--profile-name=$prof", "--user-data=$ud", "--theme=$theme", '--test-surface=show:osd') | Out-Null
      $ok = $false
      for ($i = 0; $i -lt 50 -and -not $ok; $i++) { Start-Sleep -Milliseconds 200; $ok = (Get-Content $log -Raw) -match 'osd: page drew' }
      Start-Sleep -Milliseconds 700
      $r = [OsdWin]::Find($p.Id, 'Mantle volume')
      $ef = Join-Path $Out "electron-$tag.png"
      if (-not $ok -or -not $r) { $rows += "$tag no score: the native pop-up did not show ($(if ($ok) { 'no window' } else { 'no page drew line' }))"; Copy-Item $log (Join-Path $Out "native-$tag-log.txt"); Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; continue }
      $nat = [ParityNative]::Grab($r[0], $r[1], $r[2], $r[3])
      $nb = Png $nat $r[2] $r[3]; $nb.Save((Join-Path $Out "native-$tag.png"))
      $eb = [System.Drawing.Bitmap]::FromFile($ef)
      $w = [Math]::Min($eb.Width, $nb.Width); $h = [Math]::Min($eb.Height, $nb.Height)
      # both are the pane + 18 margin; compare bottom-aligned, centred (the pane sits at the bottom of each)
      $ec = $eb.Clone((New-Object System.Drawing.Rectangle([int](($eb.Width - $w) / 2), ($eb.Height - $h), $w, $h)), $eb.PixelFormat)
      $nc = $nb.Clone((New-Object System.Drawing.Rectangle([int](($nb.Width - $w) / 2), ($nb.Height - $h), $w, $h)), $nb.PixelFormat)
      $s = [ParityNative]::Diff((Bytes $ec), (Bytes $nc), $w, $h, @(), $PixelThreshold)
      $pair = New-Object System.Drawing.Bitmap($w, ($h * 2 + 4)); $g = [System.Drawing.Graphics]::FromImage($pair); $g.Clear([System.Drawing.Color]::Red)
      $g.DrawImage($ec, 0, 0, $w, $h); $g.DrawImage($nc, 0, $h + 4, $w, $h); $g.Dispose(); $pair.Save((Join-Path $Out "$tag-pair.png"))
      $line = "OSD-PARITY $tag`: ssim={0:N4} diffpx={1:N2}% meanabs={2:N2} electron {3}x{4} native {5}x{6}" -f $s.Ssim, $s.DiffPxPct, $s.MeanAbs, $eb.Width, $eb.Height, $nb.Width, $nb.Height
      Write-Host $line; $rows += $line; if ($s.Ssim -lt $low) { $low = $s.Ssim }
      $eb.Dispose(); $nb.Dispose(); $ec.Dispose(); $nc.Dispose(); $pair.Dispose()
      Copy-Item $log (Join-Path $Out "native-$tag-log.txt")
      Start-Process $Exe -Wait -ArgumentList @('--quit', '--test', "--profile-name=$prof", "--user-data=$ud") | Out-Null
      if (-not $p.WaitForExit(8000)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    }
  }
} finally {
  Stop-Process -Id $back.Id -Force -ErrorAction SilentlyContinue
  Remove-Item Env:MANTLE_TEST_OSD -ErrorAction SilentlyContinue
}
$scored = @($rows | Where-Object { $_ -like 'OSD-PARITY*' }).Count
Write-Host ("OSD-PARITY summary: {0} scored of {1}, lowest ssim {2:N4}; no masks" -f $scored, ($pick.Count * 2), $low)
$rows | Where-Object { $_ -notlike 'OSD-PARITY*' } | ForEach-Object { Write-Host "OSD-PARITY problem: $_" }
