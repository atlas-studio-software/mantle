<#
  Launcher parity (lane native-launcher): Electron Mantle's launcher vs native, the same query typed in both, on the
  same runner screen, light and dark; plus open-to-first-frame and keystroke-to-rows for both apps.

    cloudtest --recipe mantle-native-public -- "pwsh -NoProfile -File native/tools/parity/launcher/run-launcher.ps1 -Ui"

  Steps: (1) node make-golden.cjs --check (Electron's ranking/table vs the checked-in golden); (2) the native exe
  (native/out/bin/MantleNative, else ci.ps1 -Stages build); (3) with -Ui, the launcher UI tests; (4) per theme:
  Electron (electron-launcher.mjs: open x5, the query typed letter by letter, the window grabbed), then native
  (MantleNative --test: surface.show x5 from second starts, the query set letter by letter through UI Automation,
  the window grabbed); SSIM, a pair image (Electron above, native below) and the timings.
  Both apps index the same scratch fixture (make-golden.cjs --fixture). RUNNER ONLY: it changes the wallpaper, the
  theme and the taskbar and shows test windows. Output: native/out/parity/launcher/. Summary line: LAUNCHER: ...
#>
# -DarkDesktop solid (default): the dark pass runs over a SOLID BLACK desktop (no wallpaper picture), the fair pass: a --test
# Electron launcher is a framed opaque window (its margin is black and, in dark, it cannot picture the wallpaper), so both
# apps then stand on the same black. -DarkDesktop wallpaper: the dark pass over the wallpaper, as the light one always is.
param([string]$Query = 'steam', [int]$Opens = 5, [string[]]$Themes = @('light', 'dark'), [switch]$Ui, [string]$Exe, [ValidateSet('solid', 'wallpaper')][string]$DarkDesktop = 'solid')
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$Out = Join-Path $root 'native\out\parity\launcher'
New-Item -ItemType Directory -Force -Path $Out | Out-Null
function Say($m) { Write-Host ("launcher {0}: {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) }
$problems = [System.Collections.Generic.List[string]]::new()
function Problem($m) { $problems.Add($m); Write-Host "launcher PROBLEM: $m" }
if (-not $env:CI -and -not $env:GITHUB_ACTIONS -and $env:MANTLE_PARITY_LOCAL -ne '1') { Write-Host 'LAUNCHER: FAILED (refused: not on a CI runner)'; exit 2 }

# ------------------------------------------------------------------ 1. the golden (Electron moved on?)
$golden = (& node (Join-Path $PSScriptRoot 'make-golden.cjs') --check 2>&1 | Select-Object -Last 1)
Say "golden: $golden"
if ($golden -notmatch '^GOLDEN: ok') { Problem "make-golden --check: $golden" }

# ------------------------------------------------------------------ 2. the native exe
$bin = Join-Path $root 'native\out\bin\MantleNative'
if (-not $Exe) { $Exe = $env:MANTLE_NATIVE_EXE }
if (-not $Exe -or -not (Test-Path $Exe)) {
  $found = Get-ChildItem -Path $bin -Filter MantleNative.exe -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $found) { Say 'building the native app (ci.ps1 -Stages build)'; & pwsh -NoProfile -File (Join-Path $root 'native\tools\ci.ps1') -Stages build | Select-Object -Last 3 | ForEach-Object { Say $_ }
    $found = Get-ChildItem -Path $bin -Filter MantleNative.exe -ErrorAction SilentlyContinue | Select-Object -First 1 }
  if (-not $found) { Write-Host 'LAUNCHER: FAILED (no native exe)'; exit 1 }
  $Exe = $found.FullName
}
Say "native exe: $Exe"
$env:MANTLE_NATIVE_EXE = $Exe

# ------------------------------------------------------------------ 3. the UI tests (optional)
if ($Ui) {
  $uiLog = Join-Path $Out 'ui-launcher.log'
  & pwsh -NoProfile -File (Join-Path $root 'native\tests\Mantle.UiTests\run-ui.ps1') -Exe $Exe -Filter 'FullyQualifiedName~Mantle.UiTests.Launcher|FullyQualifiedName~Mantle.UiTests.Surfaces' *> $uiLog
  $sum = Get-Content $uiLog | Where-Object { $_ -match 'Passed!|Failed!|Total tests|Passed:|Failed:' } | Select-Object -Last 2
  Say ("UI tests: " + ($sum -join ' | '))
  Get-Content $uiLog | Where-Object { $_ -match '^\s+(Failed|Passed) ' } | ForEach-Object { Say $_.Trim() }
  if (-not ($sum -match 'Passed!') -or ($sum -match 'Failed!')) { Problem 'launcher/surface UI tests did not all pass (ui-launcher.log)' }
}

# ------------------------------------------------------------------ 4. the runner desktop
Add-Type -Path (Join-Path $root 'native\tools\parity\ParityNative.cs')
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace LauncherParity -Name W -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr h, out RECT r);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern System.IntPtr FindWindowW(string c, string t);
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@
function Save-Bgra([byte[]]$px, [int]$w, [int]$h, [string]$path) {
  $bmp = [System.Drawing.Bitmap]::new($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $d = $bmp.LockBits([System.Drawing.Rectangle]::new(0, 0, $w, $h), [System.Drawing.Imaging.ImageLockMode]::WriteOnly, $bmp.PixelFormat)
  for ($row = 0; $row -lt $h; $row++) { [System.Runtime.InteropServices.Marshal]::Copy($px, $row * $w * 4, [IntPtr]::Add($d.Scan0, $row * $d.Stride), $w * 4) }
  $bmp.UnlockBits($d); $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
[void][ParityNative]::DpiAware()
$scr = [ParityNative]::Screen(); $W = $scr[0]; $H = $scr[1]
Say "screen ${W}x${H}, dpi $([ParityNative]::PrimaryDpi())"
Add-Type -Namespace LauncherParity -Name Desk -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern bool SystemParametersInfoW(uint a, uint p, string v, uint f);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetSysColors(int n, int[] e, int[] c);
public static string Solid() { bool a = SystemParametersInfoW(0x14, 0, "", 3); bool b = SetSysColors(1, new[] { 1 }, new[] { 0 }); return "solid black desktop (no picture " + a + ", colour " + b + ")"; }
'@
$wallpaper = Join-Path $root 'assets\backgrounds\snow-peak.jpg'
function Set-Desk([string]$t) { if ($t -eq 'dark' -and $DarkDesktop -eq 'solid') { Say ("desktop: " + [LauncherParity.Desk]::Solid()) } else { Say ("wallpaper " + [ParityNative]::SetWallpaper($wallpaper)) } }
$pers = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
New-Item -Force -Path $pers | Out-Null
Set-ItemProperty -Path $pers -Name EnableTransparency -Value 1 -Type DWord
[ParityNative]::TaskbarAutoHide($true)
[ParityNative]::ParkCursor(4, 4)   # never over the launcher: a pointer over a row picks it

$fx = Join-Path ([IO.Path]::GetTempPath()) ('mantle-launcher-parity-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
Say (& node (Join-Path $PSScriptRoot 'make-golden.cjs') "--fixture=$fx" 2>&1 | Select-Object -Last 1)

function Stop-Profile([string]$profile) {
  Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.Contains("--profile-name=$profile") } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}
function Wait-Still([int]$x, [int]$y, [int]$w, [int]$h) {
  for ($i = 0; $i -lt 20; $i++) { if ([ParityNative]::Motion($x, $y, $w, $h, 300) -lt 0.5) { return $true } }
  return $false
}
function Median([double[]]$v) { if (-not $v -or $v.Count -eq 0) { return -1 }; $s = $v | Sort-Object; return $s[[int][Math]::Floor(($s.Count - 1) / 2)] }
function Log-Text([string]$ud) { try { $fs = [IO.FileStream]::new((Join-Path $ud 'mantle-native.log'), 'Open', 'Read', 'ReadWrite,Delete'); $r = [IO.StreamReader]::new($fs); $t = $r.ReadToEnd(); $r.Dispose(); return $t } catch { return '' } }
function Until([scriptblock]$f, [int]$ms) { $end = (Get-Date).AddMilliseconds($ms); while ((Get-Date) -lt $end) { if (& $f) { return $true }; Start-Sleep -Milliseconds 100 }; return $false }

# UI Automation from Windows PowerShell 5.1 (UIAutomationClient is always there): set #q's value, one text per step
$uiaSet = @'
param([int]$ProcId, [string]$Texts, [int]$GapMs)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
$c = New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $ProcId)), (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'Mantle launcher')))
$win = $A::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $c)
if (-not $win) { Write-Output 'UIA: no launcher window'; exit 3 }
$q = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, 'q')))
if (-not $q) { Write-Output 'UIA: no q'; exit 3 }
$vp = $q.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
foreach ($t in $Texts.Split('|')) { $vp.SetValue($t); Start-Sleep -Milliseconds $GapMs }
Write-Output ('UIA: set ' + $Texts)
'@
$uiaFile = Join-Path $Out 'uia-set.ps1'
Set-Content -Path $uiaFile -Value $uiaSet -Encoding utf8
$prefixes = (1..$Query.Length | ForEach-Object { $Query.Substring(0, $_) }) -join '|'

$results = @{}
foreach ($t in $Themes) {
  $dir = Join-Path $Out $t; New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $v = if ($t -eq 'light') { 1 } else { 0 }
  Set-ItemProperty -Path $pers -Name AppsUseLightTheme -Value $v -Type DWord
  Set-ItemProperty -Path $pers -Name SystemUsesLightTheme -Value $v -Type DWord
  [ParityNative]::BroadcastTheme()
  Set-Desk $t
  Start-Sleep -Seconds 2
  $r = @{ theme = $t }

  # ---- Electron
  $ej = Join-Path $dir 'electron.json'; Remove-Item $ej -ErrorAction SilentlyContinue
  $node = Start-Process node -ArgumentList (Join-Path $PSScriptRoot 'electron-launcher.mjs'), "--root=$root", "--theme=$t", "--fixture=$fx", "--out=$ej", "--query=$Query", "--opens=$Opens", '--hold=300' `
    -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $dir 'electron-launcher.stdout.txt') -RedirectStandardError (Join-Path $dir 'electron-launcher.stderr.txt')
  $end = (Get-Date).AddSeconds(240)
  while ((Get-Date) -lt $end -and -not (Test-Path $ej) -and -not $node.HasExited) { Start-Sleep -Milliseconds 500 }
  Start-Sleep -Milliseconds 300
  $e = if (Test-Path $ej) { Get-Content $ej -Raw | ConvertFrom-Json } else { $null }
  $ePx = $null; $eRect = $null
  if ($e -and $e.ok) {
    # a --test Electron launcher is a FRAMED window (title bar + menu): grab its page (innerWidth x innerHeight), the pane's own 680x460
    $s = $e.dpr; $bx = [int](($e.rect[2] - $e.inner[0]) / 2)
    $eRect = @([int](($e.rect[0] + $bx) * $s), [int](($e.rect[1] + $e.rect[3] - $e.inner[1] - $bx) * $s), [int]($e.inner[0] * $s), [int]($e.inner[1] * $s))
    [ParityNative]::ParkCursor(4, 4)
    if (-not (Wait-Still $eRect[0] $eRect[1] $eRect[2] $eRect[3])) { Problem "electron ${t}: the launcher never stood still" }
    $ePx = [ParityNative]::Grab($eRect[0], $eRect[1], $eRect[2], $eRect[3])
    Save-Bgra $ePx $eRect[2] $eRect[3] (Join-Path $dir 'electron.png')
    $r.electron = @{ rect = $eRect; opens = @($e.opens); keys = @($e.keys); rows = @($e.rows); groups = @($e.groups); hint = $e.hint; focused = $e.focused; active = $e.active }
    Say ("electron {0}: rect {1}; rows {2}; opens {3}; keys {4}" -f $t, ($eRect -join ','), ($e.rows -join ' / '), ($e.opens -join ','), ($e.keys -join ','))
    $qp = Start-Process $e.electronExe -ArgumentList '.', '--quit', '--test', "--profile-name=parity-launcher-$t" -WorkingDirectory $root -PassThru -WindowStyle Hidden
    [void]$qp.WaitForExit(20000)
  } else { Problem ("electron {0}: {1}" -f $t, $(if ($e) { $e.why } else { 'electron-launcher.mjs wrote nothing; see electron-launcher.*.txt' })) }
  Start-Sleep -Seconds 2
  Stop-Profile "parity-launcher-$t"
  if (-not $node.HasExited) { Stop-Process -Id $node.Id -Force -ErrorAction SilentlyContinue }

  # ---- native
  $ud = Join-Path ([IO.Path]::GetTempPath()) ("mantle-native-launcher-parity-$t-" + [guid]::NewGuid().ToString('N').Substring(0, 6))
  New-Item -ItemType Directory -Force -Path $ud | Out-Null
  $env:MANTLE_TEST_VISIBLE = '1'; $env:MANTLE_TEST_LAUNCHER_DIR = $fx
  $common = @('--test', "--profile-name=parity-launcher-native-$t", "--user-data=$ud", "--theme=$t")
  $np = Start-Process $Exe -ArgumentList $common -PassThru
  $nPx = $null
  if (-not (Until { (Log-Text $ud) -match 'launcher: created, hidden and parked' } 30000)) { Problem "native ${t}: the launcher was not made at start (see native-log.txt)" }
  elseif (-not (Until { (Log-Text $ud) -match 'launcher: index \d+ rows' } 15000)) { Problem "native ${t}: no index at start" }
  else {
    $opensN = @()
    for ($i = 0; $i -le $Opens; $i++) {
      $before = ([regex]::Matches((Log-Text $ud), 'first frame with rows ([\d.]+) ms')).Count
      & $Exe @common '--test-surface=show:launcher' | Out-Null
      if (-not (Until { ([regex]::Matches((Log-Text $ud), 'first frame with rows ([\d.]+) ms')).Count -gt $before } 8000)) { Problem "native ${t}: open $($i + 1) drew no rows"; break }
      Start-Sleep -Milliseconds 400
      if ($i -lt $Opens) { & $Exe @common '--test-surface=hide:launcher' | Out-Null; Start-Sleep -Milliseconds 600 }
    }
    $opensN = @([regex]::Matches((Log-Text $ud), 'first frame with rows ([\d.]+) ms') | ForEach-Object { [double]$_.Groups[1].Value })
    $uia = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $uiaFile -ProcId $np.Id -Texts $prefixes -GapMs 350 2>&1
    Say "native ${t}: $uia"
    [void](Until { (Log-Text $ud) -match ('typed "' + [regex]::Escape($Query) + '"') } 5000)
    Start-Sleep -Milliseconds 800
    $keysN = @([regex]::Matches((Log-Text $ud), 'typed "[^"]*": \d+ row\(s\) on screen ([\d.]+) ms') | ForEach-Object { [double]$_.Groups[1].Value })
    $nRect = $null
    $h = [LauncherParity.W]::FindWindowW([NullString]::Value, "Mantle launcher")
    $rc = New-Object LauncherParity.W+RECT
    if ($h -ne [IntPtr]::Zero -and [LauncherParity.W]::GetWindowRect($h, [ref]$rc)) { $nRect = @($rc.Left, $rc.Top, ($rc.Right - $rc.Left), ($rc.Bottom - $rc.Top)) }
    if ($nRect) {
      [ParityNative]::ParkCursor(4, 4)
      if (-not (Wait-Still $nRect[0] $nRect[1] $nRect[2] $nRect[3])) { Problem "native ${t}: the launcher never stood still" }
      $nPx = [ParityNative]::Grab($nRect[0], $nRect[1], $nRect[2], $nRect[3])
      Save-Bgra $nPx $nRect[2] $nRect[3] (Join-Path $dir 'native.png')
    } else { Problem "native ${t}: no launcher window to grab" }
    $r.native = @{ rect = $nRect; opens = $opensN; keys = $keysN }
    Say ("native {0}: rect {1}; opens {2}; keys {3}" -f $t, ($nRect -join ','), ($opensN -join ','), ($keysN -join ','))
  }
  Copy-Item (Join-Path $ud 'mantle-native.log') (Join-Path $dir 'native-log.txt') -ErrorAction SilentlyContinue
  if (-not $np.HasExited) { Stop-Process -Id $np.Id -Force -ErrorAction SilentlyContinue }
  Stop-Profile "parity-launcher-native-$t"

  # ---- score
  if ($ePx -and $nPx) {
    if ($eRect[2] -ne $nRect[2] -or $eRect[3] -ne $nRect[3]) { Problem ("{0}: window sizes differ (electron {1}x{2}, native {3}x{4}); scored on the common top-left part" -f $t, $eRect[2], $eRect[3], $nRect[2], $nRect[3]) }
    $cw = [Math]::Min($eRect[2], $nRect[2]); $ch = [Math]::Min($eRect[3], $nRect[3])
    function Crop([byte[]]$px, [int]$w, [int]$cw, [int]$ch) { $o = [byte[]]::new($cw * $ch * 4); for ($y = 0; $y -lt $ch; $y++) { [Array]::Copy($px, $y * $w * 4, $o, $y * $cw * 4, $cw * 4) }; return , $o }
    $a = Crop $ePx $eRect[2] $cw $ch; $b = Crop $nPx $nRect[2] $cw $ch
    $sc = [ParityNative]::Diff($a, $b, $cw, $ch, [int[]]@(), 24)
    $pair = [byte[]]::new($cw * $ch * 8); [Array]::Copy($a, 0, $pair, 0, $a.Length); [Array]::Copy($b, 0, $pair, $a.Length, $b.Length)
    Save-Bgra $pair $cw ($ch * 2) (Join-Path $dir 'launcher-pair.png')
    $r.ssim = $sc.Ssim; $r.diffPx = $sc.DiffPxPct; $r.meanAbs = $sc.MeanAbs
    Write-Host ("LAUNCHER-PARITY {0}: ssim={1:N4} diffpx={2:N2}% meanabs={3:N2} ({4}x{5}; electron above, native below in {0}/launcher-pair.png)" -f $t, $sc.Ssim, $sc.DiffPxPct, $sc.MeanAbs, $cw, $ch)
  }
  if ($r.electron -and $r.native) {
    Write-Host ("LAUNCHER-LATENCY {0}: open-to-first-frame median native {1:N1} ms vs electron {2:N1} ms; keystroke-to-rows median native {3:N1} ms vs electron {4:N1} ms" -f $t,
      (Median $r.native.opens), (Median $r.electron.opens), (Median $r.native.keys), (Median $r.electron.keys))
  }
  $results[$t] = $r
}
$results | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 (Join-Path $Out 'launcher-parity.json')
Remove-Item -Recurse -Force $fx -ErrorAction SilentlyContinue
$parts = foreach ($t in $Themes) { $r = $results[$t]; if ($r -and $r.ssim) { "{0} ssim {1:N4}" -f $t, $r.ssim } else { "$t not scored" } }
Write-Host ("LAUNCHER: {0} ({1}; {2} problem(s){3})" -f $(if ($problems.Count -eq 0) { 'ok' } else { 'FAILED' }), ($parts -join ', '), $problems.Count, $(if ($problems.Count) { ': ' + ($problems -join ' | ') } else { '' }))
exit $(if ($problems.Count -eq 0) { 0 } else { 1 })
