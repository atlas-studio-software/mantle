# hotkeys/check.ps1 - primitive 8, global shortcuts in the WinUI process: a solo Win tap, Win+Tab and
# Alt+Space taken by a low-level keyboard hook on its own thread (KeyHook.cs), handlers on the UI
# thread. RUNNER ONLY: it types on the runner's desktop with SendInput.
#   pwsh -NoProfile -File native/spikes/windowing/hotkeys/check.ps1
# The keys are INJECTED (SendInput), which the sidecar ignores on purpose; the spike runs with
# --accept-injected for that reason, and one run without it proves the default ignores them.
. (Join-Path $PSScriptRoot '..\harness\Common.ps1') -Lane hotkeys
Add-Type -IgnoreWarnings -Path (Join-Path $PSScriptRoot 'probe\KeyProbe.cs')

$exe = Build-Spike 'hotkeys\HotkeySpike.csproj'
Check 'build' ($null -ne $exe) $(if ($exe) { "$($Results.build_HotkeySpike.seconds) s" } else { 'see build-HotkeySpike.log' })
if (-not $exe) { Finish-Check }

$LWIN = 0x5B; $TAB = 0x09; $LALT = 0xA4; $SPACE = 0x20; $R = 0x52; $ESC = 0x1B; $F24 = 0x87
$Freq = [KeyProbe]::Freq()
function Ms([long]$a, [long]$b) { return [Math]::Round(($b - $a) * 1000.0 / $Freq, 2) }
function TapWin { [KeyProbe]::Down($LWIN) | Out-Null; Start-Sleep -Milliseconds 40; return [KeyProbe]::Up($LWIN) }
# modifier down, key down (its send time returned), key up, modifier up
function Chord([int]$mod, [int]$key) {
  [KeyProbe]::Down($mod) | Out-Null; Start-Sleep -Milliseconds 30
  $t = [KeyProbe]::Down($key); Start-Sleep -Milliseconds 30
  [KeyProbe]::Up($key) | Out-Null; Start-Sleep -Milliseconds 30
  [KeyProbe]::Up($mod) | Out-Null
  return $t
}
function PressEsc { [KeyProbe]::Down($ESC) | Out-Null; [KeyProbe]::Up($ESC) | Out-Null; Start-Sleep -Milliseconds 600 }
function Stats($xs) {
  $s = @($xs | Sort-Object)
  if ($s.Count -eq 0) { return [ordered]@{ n = 0 } }
  return [ordered]@{ n = $s.Count; median = $s[[int][Math]::Floor(($s.Count - 1) / 2)]; p95 = $s[[int][Math]::Ceiling(0.95 * $s.Count) - 1]; max = $s[-1]; min = $s[0] }
}

# a normal app in front (the Alt+Space target, and where focus goes back after each test)
$np = Start-Process notepad.exe -PassThru
Start-Sleep -Seconds 2
$npWin = 0
foreach ($q in @(Get-Process -Name notepad -ErrorAction SilentlyContinue)) { if ($q.MainWindowHandle -ne 0) { $npWin = [long]$q.MainWindowHandle } }
function FocusNotepad { if ($npWin) { [KeyProbe]::Focus($npWin) | Out-Null; Start-Sleep -Milliseconds 300 } }
FocusNotepad
Note 'start' ([ordered]@{ notepadWindow = $npWin; foreground = [KeyProbe]::Foreground(); shown = [KeyProbe]::Shown() })

# ------------------------------------------------------------------ baselines: what Windows does with no hook
TapWin | Out-Null; Start-Sleep -Milliseconds 1500
$bStart = [KeyProbe]::StartOpen(); $bShownStart = [KeyProbe]::Shown()
PressEsc; FocusNotepad
Chord $LWIN $TAB | Out-Null; Start-Sleep -Milliseconds 1800
$bTask = [KeyProbe]::TaskViewOpen(); $bShownTask = [KeyProbe]::Shown(); $bFgTask = [KeyProbe]::Foreground()
PressEsc; FocusNotepad
Chord $LALT $SPACE | Out-Null; Start-Sleep -Milliseconds 800
$bMenu = [KeyProbe]::MenuOpen()
PressEsc; FocusNotepad
Chord $LWIN $R | Out-Null; Start-Sleep -Milliseconds 1800
$bRun = [KeyProbe]::RunOpen()
PressEsc; FocusNotepad
Note 'baseline' ([ordered]@{ winTapStart = $bStart; winTabTaskView = $bTask; winTabForeground = $bFgTask; altSpaceMenu = $bMenu; winRRun = $bRun
    shownAfterWinTap = $bShownStart; shownAfterWinTab = $bShownTask })
$canStart = $bStart -ne ''; $canTask = $bTask -ne ''; $canMenu = $bMenu -ne ''; $canRun = $bRun -ne ''
foreach ($x in @(@('Start menu', $canStart), @('Task View', $canTask), @('window menu (Alt+Space)', $canMenu), @('Run dialog (Win+R)', $canRun))) {
  if (-not $x[1]) { Note "runnerCannotShow_$($x[0] -replace '\W', '')" "the injected key did not open the $($x[0]) on this runner with no hook running, so 'it does not open' cannot be proved here" }
}

# ------------------------------------------------------------------ app helpers
$script:cmdId = 0
function Start-App([string[]]$argv) {
  foreach ($f in 'events.jsonl', 'status.json', 'ready.json', 'cmd.txt', 'log.txt') { Remove-Item (Join-Path $Out $f) -ErrorAction SilentlyContinue }
  $p = Start-Spike $exe $argv
  $ready = Wait-Json (Join-Path $Out 'ready.json') 30
  Start-Sleep -Milliseconds 500
  FocusNotepad
  return @{ p = $p; ready = $ready }
}
function Events { $f = Join-Path $Out 'events.jsonl'; if (Test-Path $f) { return @(Get-Content $f | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json }) } ; return @() }
function Wait-Events([int]$n, [double]$timeoutS = 5) {
  $end = (Get-Date).AddSeconds($timeoutS)
  while ((Get-Date) -lt $end -and @(Events).Count -lt $n) { Start-Sleep -Milliseconds 50 }
  return @(Events)
}
function Status { try { return Get-Content -Raw (Join-Path $Out 'status.json') | ConvertFrom-Json } catch { return $null } }
function Cmd([string]$verb, [int]$arg) {
  $script:cmdId++
  Set-Content -Path (Join-Path $Out 'cmd.txt') -Value "$($script:cmdId) $verb $arg"
  $end = (Get-Date).AddSeconds(3)
  while ((Get-Date) -lt $end) { $s = Status; if ($s -and $s.lastCmd -ge $script:cmdId) { return $true } ; Start-Sleep -Milliseconds 30 }
  return $false
}
# n presses of a gesture, one every gapMs; returns the send times (the key that fires it)
function Presses([string]$what, [int]$n, [int]$gapMs) {
  $sends = [System.Collections.Generic.List[long]]::new()
  for ($i = 0; $i -lt $n; $i++) {
    if ($what -eq 'winTap') { $sends.Add((TapWin)) } else { $sends.Add((Chord $LALT $SPACE)) }
    Start-Sleep -Milliseconds $gapMs
  }
  return , $sends.ToArray()
}
function Latency($events, $sends) {
  $k2u = @(); $k2h = @(); $h2u = @()
  for ($i = 0; $i -lt [Math]::Min($events.Count, $sends.Count); $i++) {
    $k2u += Ms $sends[$i] $events[$i].uiQpc; $k2h += Ms $sends[$i] $events[$i].hookQpc; $h2u += Ms $events[$i].hookQpc $events[$i].uiQpc
  }
  return [ordered]@{ keyToUiMs = (Stats $k2u); keyToHookMs = (Stats $k2h); hookToUiMs = (Stats $h2u) }
}
function New-Since($n0, [string]$ev) { return @(Events | Select-Object -Skip $n0 | Where-Object { -not $ev -or $_.ev -eq $ev }) }

# ------------------------------------------------------------------ A. the hook on its own thread
$a = Start-App @('--accept-injected')
Check 'started' ($null -ne $a.ready) $(if ($a.ready) { "hook on $($a.ready.hookOn) thread; LowLevelHooksTimeout registry value: $(if ($a.ready.lowLevelHooksTimeout) { $a.ready.lowLevelHooksTimeout } else { 'unset' })" } else { 'no ready.json in 30 s' })
if (-not $a.ready) { Stop-Tree $a.p.Id; Finish-Check }
Note 'ready' $a.ready

$n0 = @(Events).Count
TapWin | Out-Null; Start-Sleep -Milliseconds 1200
$ev = New-Since $n0; $st = [KeyProbe]::StartOpen()
Check 'Win tap fires' ($ev.Count -eq 1 -and $ev[0].ev -eq 'winTap' -and $ev[0].onUiThread) "events: $(@($ev | ForEach-Object { "$($_.ev) via $($_.via) on UI thread $($_.onUiThread)" }) -join '; ')"
if ($canStart) { Check 'Win tap: Start stays shut' ($st -eq '') "Start shown: '$st' (with no hook the same key opened: $bStart)" }
if ($st) { PressEsc }
FocusNotepad

$n0 = @(Events).Count
Chord $LWIN $R | Out-Null; Start-Sleep -Milliseconds 1800
$ev = New-Since $n0; $run = [KeyProbe]::RunOpen()
Check 'Win+R: Windows still gets it, no tap' ($ev.Count -eq 0 -and (($run -ne '') -or -not $canRun)) "Run dialog: '$run'; spike events: $($ev.Count)$(if (-not $canRun) { ' (the runner showed no Run dialog without the hook either)' })"
if ($run) { PressEsc }
FocusNotepad

$n0 = @(Events).Count
Chord $LALT $SPACE | Out-Null; Start-Sleep -Milliseconds 900
$ev = New-Since $n0; $menu = [KeyProbe]::MenuOpen()
Check 'Alt+Space fires' ($ev.Count -eq 1 -and $ev[0].ev -eq 'altSpace') "events: $(@($ev | ForEach-Object { "$($_.ev) via $($_.via)" }) -join '; ')"
if ($canMenu) { Check 'Alt+Space: no window menu' ($menu -eq '') "menu shown: '$menu' (with no hook: $bMenu)" }
if ($menu) { PressEsc }
FocusNotepad

$n0 = @(Events).Count
Chord $LWIN $TAB | Out-Null; Start-Sleep -Milliseconds 1800
$ev = New-Since $n0; $tv = [KeyProbe]::TaskViewOpen(); $st = [KeyProbe]::StartOpen()
Check 'Win+Tab fires' ($ev.Count -eq 1 -and $ev[0].ev -eq 'winTab') "events: $(@($ev | ForEach-Object { "$($_.ev) via $($_.via)" }) -join '; ')"
if ($canTask) { Check 'Win+Tab: Task View stays shut' ($tv -eq '') "Task View: '$tv' (with no hook: $bTask)" }
if ($canStart) { Check 'Win+Tab: Start stays shut' ($st -eq '') "Start: '$st'" }
if ($tv -or $st) { PressEsc }
FocusNotepad

# latency: 50 Win taps, 50 Alt+Space
$lat = [ordered]@{}
foreach ($g in 'winTap', 'altSpace') {
  $n0 = @(Events).Count
  $sends = Presses $g 50 120
  Wait-Events ($n0 + 50) 5 | Out-Null
  $ev = New-Since $n0 $g
  $lat[$g] = Latency $ev $sends
  $lat[$g].fired = $ev.Count
  $opened = if ($g -eq 'winTap') { [KeyProbe]::StartOpen() } else { [KeyProbe]::MenuOpen() }
  $l = $lat[$g].keyToUiMs
  Check "$g x50: all fire, fast" ($ev.Count -eq 50 -and $l.p95 -lt 50 -and $opened -eq '') "$($ev.Count) of 50 fired; key sent -> handler on the UI thread median $($l.median) ms, p95 $($l.p95) ms, max $($l.max) ms (hook -> UI median $($lat[$g].hookToUiMs.median) ms); shown afterwards: '$opened'"
  if ($opened) { PressEsc }
  FocusNotepad
}
Note 'latency' $lat

# the UI thread blocked for 2 s: keys must not wait for it, the hook must survive
$n0 = @(Events).Count
$ok = Cmd 'block' 2000
Start-Sleep -Milliseconds 200
$sends = Presses 'winTap' 5 300
Wait-Events ($n0 + 5) 5 | Out-Null
$ev = New-Since $n0 'winTap'
$lb = Latency $ev $sends
$st = [KeyProbe]::StartOpen()
Check 'UI thread blocked 2 s: keys do not wait' ($ok -and $ev.Count -eq 5 -and $lb.keyToHookMs.max -lt 50 -and $st -eq '') "block acked $ok; $($ev.Count) of 5 taps fired; key -> hook max $($lb.keyToHookMs.max) ms (the key never waited for the UI thread); key -> UI handler median $($lb.keyToUiMs.median) ms max $($lb.keyToUiMs.max) ms (queued until the UI thread was free); Start: '$st'"
if ($st) { PressEsc }
FocusNotepad
$n0 = @(Events).Count
TapWin | Out-Null; Start-Sleep -Milliseconds 800
$after = New-Since $n0 'winTap'
$s = Status
Check 'hook alive after the block' ($after.Count -eq 1 -and $s.hook.heals -eq 0) "taps after the block: $($after.Count); heals $($s.hook.heals); hook $($s.hook | ConvertTo-Json -Compress)"
Note 'uiBlocked' $lb

# HookHealth: a hook callback stalled for 3 s (past LowLevelHooksTimeout) -> Windows drops the hook
$s0 = Status
Cmd 'stall' 3000 | Out-Null
[KeyProbe]::Down($F24) | Out-Null; [KeyProbe]::Up($F24) | Out-Null       # this key's callback sleeps 3 s
Start-Sleep -Milliseconds 3600
$s1 = Status
for ($i = 0; $i -lt 6; $i++) { [KeyProbe]::Down($F24) | Out-Null; [KeyProbe]::Up($F24) | Out-Null; Start-Sleep -Milliseconds 300 }
Start-Sleep -Milliseconds 600
$s2 = Status
$n0 = @(Events).Count
TapWin | Out-Null; Start-Sleep -Milliseconds 900
$after = New-Since $n0 'winTap'
$st = [KeyProbe]::StartOpen()
Note 'hookHealth' ([ordered]@{ before = $s0.hook; afterStall = $s1.hook; afterKeys = $s2.hook })
$dropped = $s2.hook.heals -ge 1
Check 'stalled hook: Windows dropped it, it was re-armed' $dropped "heals $($s2.hook.heals): $($s2.hook.lastHeal); callbacks $($s1.hook.callbacks) -> $($s2.hook.callbacks) over 12 key events after the stall; raw $($s1.hook.raw) -> $($s2.hook.raw)"
Check 'Win tap fires after the stall' ($after.Count -eq 1 -and $st -eq '') "taps: $($after.Count); Start: '$st'"
if ($st) { PressEsc }
FocusNotepad
Note 'memory' (Get-Mem $a.p.Id)
Note 'cpuIdle' (Get-Cpu $a.p.Id 5)
Get-Content (Join-Path $Out 'log.txt') -ErrorAction SilentlyContinue | Select-Object -Last 12 | ForEach-Object { Say "app: $_" }
Stop-Tree $a.p.Id; Start-Sleep -Milliseconds 500

# ------------------------------------------------------------------ B. Alt+Space through RegisterHotKey
$b = Start-App @('--accept-injected', '--altspace=registerhotkey')
Check 'RegisterHotKey(Alt+Space) taken' ($b.ready.registerHotKey -eq $true) "RegisterHotKey returned $($b.ready.registerHotKey)"
$n0 = @(Events).Count
$sends = Presses 'altSpace' 50 120
Wait-Events ($n0 + 50) 5 | Out-Null
$ev = New-Since $n0 'altSpace'
$lr = Latency $ev $sends
$menu = [KeyProbe]::MenuOpen()
Check 'RegisterHotKey Alt+Space x50' ($ev.Count -eq 50 -and $menu -eq '') "$($ev.Count) of 50 fired via $(@($ev | Select-Object -First 1 | ForEach-Object { $_.via })); key -> UI median $($lr.keyToUiMs.median) ms p95 $($lr.keyToUiMs.p95) ms (hook route: median $($lat.altSpace.keyToUiMs.median) p95 $($lat.altSpace.keyToUiMs.p95)); menu: '$menu'"
if ($menu) { PressEsc }
FocusNotepad
$n0 = @(Events).Count
TapWin | Out-Null; Start-Sleep -Milliseconds 900
Check 'Win tap still by the hook beside RegisterHotKey' (@(New-Since $n0 'winTap').Count -eq 1) "taps $(@(New-Since $n0 'winTap').Count)"
Note 'registerHotKeyLatency' $lr
Stop-Tree $b.p.Id; Start-Sleep -Milliseconds 500

# ------------------------------------------------------------------ C. contrast: the hook on the UI thread
$c = Start-App @('--accept-injected', '--hook-on=ui')
$n0 = @(Events).Count
Cmd 'block' 2000 | Out-Null
Start-Sleep -Milliseconds 200
$sends = Presses 'winTap' 5 300
Wait-Events ($n0 + 5) 6 | Out-Null
$ev = New-Since $n0 'winTap'
$lc = Latency $ev $sends
$stC = [KeyProbe]::StartOpen()
if ($stC) { PressEsc }
FocusNotepad
$n1 = @(Events).Count
TapWin | Out-Null; Start-Sleep -Milliseconds 900
$afterC = @(New-Since $n1 'winTap').Count
$stC2 = [KeyProbe]::StartOpen()
if ($stC2) { PressEsc }
Note 'contrastHookOnUiThread' ([ordered]@{ firedOf5WhileBlocked = $ev.Count; latency = $lc; startOpenedDuringBlock = $stC; tapsAfterBlock = $afterC; startAfterBlock = $stC2; status = (Status).hook })
Stop-Tree $c.p.Id; Start-Sleep -Milliseconds 500
FocusNotepad

# ------------------------------------------------------------------ D. the default: injected keys ignored
$d = Start-App @()
$n0 = @(Events).Count
TapWin | Out-Null; Start-Sleep -Milliseconds 1200
$evD = @(New-Since $n0); $stD = [KeyProbe]::StartOpen()
Check 'default ignores injected keys' ($evD.Count -eq 0) "events $($evD.Count) for an injected Win tap without --accept-injected; Start: '$stD' (Windows' own, as with no hook)"
if ($stD) { PressEsc }
Stop-Tree $d.p.Id

# never leave a modifier down on the runner
foreach ($k in $LWIN, $LALT) { if ([KeyProbe]::Held($k)) { [KeyProbe]::Up($k) | Out-Null } }
Get-Process -Name notepad -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Finish-Check
