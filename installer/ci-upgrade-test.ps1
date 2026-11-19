# ci-upgrade-test.ps1 - the upgrade-from-previous check (lane release-gate, 2026-11-18). RUNS ON A THROWAWAY
# RUNNER ONLY (cloudtest or GitHub Actions; it refuses anywhere else): it installs Mantle, runs it LIVE, updates
# it, and uninstalls it, like installer/ci-install-test.ps1. Never run it on a PC someone uses.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File installer/ci-upgrade-test.ps1 [-From <tag>] [-Setup <exe>] [-Out <dir>]
#
#   1. the PREVIOUS release: GitHub's releases/latest (or -From <tag>, published only). Its Mantle-Setup.exe is
#      downloaded and must match the sha256 digest GitHub keeps for the asset.
#   2. the CANDIDATE: this checkout built the way .github/workflows/installer.yml builds it (sidecar, icon,
#      electron-builder 26.18.0), or -Setup <exe>.
#   3. the previous release is installed, started, taken live through the first-start warning ("Start Mantle"),
#      so it writes its own profile (%APPDATA%\mantle: consent, settings, shell-state.json, features).
#   4. the candidate's Mantle-Setup.exe /S runs over it WHILE IT IS LIVE (what a person does after "Update").
#   5. the upgraded copy is started and must come up live ON THE OLD PROFILE. What proves it started:
#        - mantle.log, after the upgrade, has the "features: started ..." line (lib/features.js), the same proof
#          ci-install-test.ps1 uses for "comes up live";
#        - exactly one Mantle.exe main process (no --type=), and its installed sidecar runs;
#        - shell-state.json was written again with restored:false (the sidecar recorded the shell and went live);
#        - every feature that started on the previous release starts again (no feature lost by the upgrade);
#        - no first-start warning (the old consent was kept), then Mantle.exe --quit exits 0, shell-state.json
#          says restored:true, and the taskbar reads exactly as before anything was installed.
#   6. uninstall, and the taskbar as before.
#
# Every check prints "ok" or "FAIL"; the last line is the summary the release gate reads (tools/release-gate.js,
# gate "upgrade"):   UPGRADE TEST: <previous tag> -> <candidate version>: n passed, m failed
param(
  [string]$From = '',
  [string]$Setup = '',
  [string]$Out = 'upgrade-out',
  [string]$Repo = 'atlas-studio-software/mantle'
)
$ErrorActionPreference = 'Stop'
$script:fromTag = if ($From) { $From } else { 'v?' }
$script:toVer = '?'
try { $script:toVer = (Get-Content -LiteralPath 'package.json' -Raw | ConvertFrom-Json).version } catch { }
function Summary {
  Write-Output ''
  foreach ($f in $script:failed) { Write-Output "FAIL $f" }
  Write-Output "UPGRADE TEST: $script:fromTag -> $script:toVer`: $script:passed passed, $($script:failed.Count) failed"
}
$script:passed = 0
$script:failed = New-Object System.Collections.ArrayList
# $ok must be true/false (or a number, as a [bool] parameter takes); anything else is a harness bug, counted as a
# FAIL with its type named, never a crash that loses the summary line.
function Check([string]$name, $ok, [string]$detail = '') {
  if (-not ($ok -is [bool] -or $ok -is [int] -or $ok -is [long])) {
    $what = if ($null -eq $ok) { 'null' } else { $ok.GetType().Name }
    $detail = "harness bug: the check got a $what, not true/false$(if ($detail) { "; $detail" })"; $ok = $false
  }
  if ($ok) { $script:passed++; Write-Output "ok   $name" }
  else { [void]$script:failed.Add($name); Write-Output "FAIL $name$(if ($detail) { " -- $detail" })" }
}
function Step([string]$text) { Write-Output ''; Write-Output "== $text" }
function Give-Up([string]$why) { Check $why $false; if (@(From-Install).Count) { Kill-All }; Summary; exit 1 }
# Anything that stops the script (a terminating error anywhere) still ends on the summary line, with a failure counted.
trap {
  if ($null -eq $script:failed) { $script:failed = New-Object System.Collections.ArrayList }
  if ($null -eq $script:passed) { $script:passed = 0 }
  $at = "line $($_.InvocationInfo.ScriptLineNumber): $($_.Exception.Message)"
  Write-Output "HARNESS ERROR at $at"
  [void]$script:failed.Add("the script ran to its end (it stopped at $at)")
  try { if (@(From-Install).Count) { Kill-All } } catch { }
  Summary
  exit 1
}

if ($env:CLOUDTEST -ne '1' -and $env:GITHUB_ACTIONS -ne 'true') {
  Write-Output 'refused: this runs Mantle live and installs over it; it runs only on a throwaway runner (CLOUDTEST=1 or GITHUB_ACTIONS=true)'
  Check 'on a throwaway runner' $false
  Summary
  exit 2
}
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class MantleUp {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint msg; public uint edge; public RECT rc; public IntPtr lParam; }
  [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
  [DllImport("user32.dll")] static extern IntPtr FindWindow(string c, string t);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool GetClipCursor(out RECT r);
  [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint a, uint b, ref RECT r, uint c);
  [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
  public static string State() {
    APPBARDATA d = new APPBARDATA(); d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
    IntPtr tray = FindWindow("Shell_TrayWnd", null);
    uint st = (uint)SHAppBarMessage(4, ref d);
    RECT clip; GetClipCursor(out clip);
    RECT wa = new RECT(); SystemParametersInfo(0x30, 0, ref wa, 0);
    return String.Format("tray={0} visible={1} abm={2} clip={3},{4},{5},{6} workarea={7},{8},{9},{10} screen={11}x{12}",
      tray != IntPtr.Zero, tray != IntPtr.Zero && IsWindowVisible(tray), st, clip.L, clip.T, clip.R, clip.B,
      wa.L, wa.T, wa.R, wa.B, GetSystemMetrics(0), GetSystemMetrics(1));
  }
}
'@

$inst = Join-Path $env:LOCALAPPDATA 'Programs\Mantle'
$exe = Join-Path $inst 'Mantle.exe'
$app = Join-Path $inst 'resources\app'
$profileDir = Join-Path $env:APPDATA 'mantle'
$log = Join-Path $profileDir 'mantle.log'
$consent = Join-Path $profileDir 'installed-consent.json'
$stateFile = Join-Path $profileDir 'shell-state.json'
$prefix = $inst.ToLowerInvariant() + '\'

function From-Install { @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.ToLowerInvariant().StartsWith($prefix) }) }
function Wait-Gone([int]$seconds) {
  $end = (Get-Date).AddSeconds($seconds)
  do { if (@(From-Install).Count -eq 0) { return $true }; Start-Sleep -Milliseconds 500 } while ((Get-Date) -lt $end)
  return $false
}
function Kill-All { From-Install | ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force } catch { } } }
function Wait-Baseline([string]$why, [int]$seconds = 15) {
  $end = (Get-Date).AddSeconds($seconds)
  do { $now = [MantleUp]::State(); if ($now -eq $script:baseline) { break }; Start-Sleep -Milliseconds 500 } while ((Get-Date) -lt $end)
  Check "taskbar as before Mantle ($why)" ($now -eq $script:baseline) "now: $now | before: $script:baseline"
}
function Log-Offset { if (Test-Path -LiteralPath $log) { (Get-Item -LiteralPath $log).Length } else { 0 } }
# -> the "features: ..." line Mantle wrote after byte $from of mantle.log, or $null after $seconds
function Wait-LiveUp([long]$from, [int]$seconds = 150) {
  $end = (Get-Date).AddSeconds($seconds)
  do {
    if (Test-Path -LiteralPath $log) {
      $fs = [System.IO.File]::Open($log, 'Open', 'Read', 'ReadWrite'); $fs.Seek([Math]::Min($from, $fs.Length), 'Begin') | Out-Null
      $text = (New-Object System.IO.StreamReader($fs)).ReadToEnd(); $fs.Close()
      if ($text -match 'features: (started|none started)[^\r\n]*') { return $Matches[0] }
    }
    Start-Sleep -Seconds 1
  } while ((Get-Date) -lt $end)
  # Write-Host, not output: these lines must not become part of what the function returns
  if (Test-Path -LiteralPath $log) { Write-Host '---- mantle.log (tail)'; Get-Content -LiteralPath $log -Tail 40 | ForEach-Object { Write-Host $_ } }
  return $null
}
function Started([string]$line) {
  if ($line -and $line -match 'features: started (.+?)(?:; NOT started: .*?)? \(from ') { return @($Matches[1] -split ', ' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
  return @()
}
function Find-Window([int]$procId, [int]$seconds) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
  $end = (Get-Date).AddSeconds($seconds)
  do {
    $w = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if ($w) { return $w }
    Start-Sleep -Milliseconds 500
  } while ((Get-Date) -lt $end)
  return $null
}
function Named($root, [string]$name) {
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
  $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Run-Setup([string]$file, [string]$why) {
  $p = Start-Process -FilePath $file -ArgumentList '/S' -PassThru -Wait
  Check "$why`: Mantle-Setup.exe /S exits 0" ($p.ExitCode -eq 0) "exit $($p.ExitCode)"
}
function Uninstall-Key { Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' | Get-ItemProperty | Where-Object { $_.DisplayName -eq 'Mantle' } | Select-Object -First 1 }
function Installed-Version { try { (Get-Content -LiteralPath (Join-Path $app 'package.json') -Raw | ConvertFrom-Json).version } catch { $null } }
# Electron's main process: the installed Mantle.exe without --type= (renderers, GPU, crashpad all carry one). Not
# matched on "--live": a consented plain start adds --live to its own argv (lib/packaged.js plan()), never to the
# OS command line; being live is proven by mantle.log's features line and shell-state.json instead.
function Mains { @(From-Install | Where-Object { $_.Name -ieq 'Mantle.exe' -and -not ("$($_.CommandLine)" -match '--type=') }) }
function Procs-Said { $m = @(Mains); "$($m.Count) main(s) [$(($m | ForEach-Object { "$($_.CommandLine)" }) -join ' | ')], $(@(Sidecars).Count) sidecar(s)" }
function Sidecars { @(From-Install | Where-Object { $_.Name -ieq 'Mantle.Sidecar.exe' }) }
function Shell-State { try { Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json } catch { $null } }
# a native command with its output in $Out\<name>.txt; -> its exit code, and only that (the last lines are shown
# with Write-Host: Write-Output here would join the exit code in what the function returns)
function Native([string]$name, [scriptblock]$run) {
  $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  $o = @(& $run 2>&1); $code = $LASTEXITCODE
  $ErrorActionPreference = $old
  $o | ForEach-Object { "$_" } | Set-Content -LiteralPath (Join-Path $Out "$name.txt")
  $o | Select-Object -Last 3 | ForEach-Object { Write-Host "  $name> $_" }
  if ($null -eq $code) { $code = -1 }
  return [int]$code
}
# "1.2.3..." -> [version] 1.2.3 (the numbers only; a pre-release tail like -ci.4 is dropped), or $null
function Core([string]$v) { if ($v -match '^v?(\d+)\.(\d+)\.(\d+)') { [version]"$($Matches[1]).$($Matches[2]).$($Matches[3])" } else { $null } }

$script:baseline = [MantleUp]::State()
Write-Output "taskbar before anything: $script:baseline"
Check 'the runner has a Windows taskbar to protect' ($script:baseline -match 'tray=True visible=True')
Check 'nothing is installed yet' (-not (Test-Path -LiteralPath $exe))

# ---------------------------------------------------------------- 1. the previous release
Step 'the previous release: its Mantle-Setup.exe, checked against GitHub''s digest'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$ProgressPreference = 'SilentlyContinue'
$hdr = @{ 'User-Agent' = 'mantle-ci-upgrade-test'; 'Accept' = 'application/vnd.github+json' }
$api = if ($From) { "https://api.github.com/repos/$Repo/releases/tags/$From" } else { "https://api.github.com/repos/$Repo/releases/latest" }
try { $rel = Invoke-RestMethod -Uri $api -Headers $hdr } catch { Give-Up "the previous release could be read ($api): $($_.Exception.Message)" }
$script:fromTag = "$($rel.tag_name)"
Check "the previous release $($rel.tag_name) is published (not a draft, not a pre-release)" (-not $rel.draft -and -not $rel.prerelease)
$asset = @($rel.assets | Where-Object { $_.name -eq 'Mantle-Setup.exe' }) | Select-Object -First 1
if (-not $asset) { Give-Up "$($rel.tag_name) holds a Mantle-Setup.exe" }
if (-not ("$($asset.digest)" -match '^sha256:([0-9a-f]{64})$')) { Give-Up "$($rel.tag_name)'s Mantle-Setup.exe has a sha256 digest (got '$($asset.digest)')" }
$wantSha = $Matches[1]
$prevDir = Join-Path $Out 'previous'
New-Item -ItemType Directory -Force -Path $prevDir | Out-Null
$prevSetup = Join-Path $prevDir 'Mantle-Setup.exe'
try { Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $prevSetup -Headers @{ 'User-Agent' = 'mantle-ci-upgrade-test' } -UseBasicParsing } catch { Give-Up "the previous Mantle-Setup.exe downloads: $($_.Exception.Message)" }
$gotSha = (Get-FileHash -LiteralPath $prevSetup -Algorithm SHA256).Hash.ToLowerInvariant()
Check "the previous Mantle-Setup.exe matches its release digest (sha256 $wantSha)" ($gotSha -eq $wantSha) "got $gotSha"
if ($gotSha -ne $wantSha) { Give-Up 'a previous installer that is not the release''s' }
$prevVer = "$($rel.tag_name)".TrimStart('v')

# ---------------------------------------------------------------- 2. the candidate
Step "the candidate: Mantle $script:toVer"
if ($Setup) {
  $candSetup = (Resolve-Path $Setup).Path
} else {
  # A build at the same version as the previous release would be a reinstall, not an upgrade. On this runner only,
  # such a candidate builds as the next patch with a -ci pre-release tail (package.json is put back right after the
  # build; the committed version is untouched). The summary then names that version, and tools/release-gate.js
  # refuses it (its gate wants "-> <committed x.y.z>"), so only a bumped release commit can pass the gate.
  $pkgPath = (Resolve-Path 'package.json').Path
  $pkgText = [System.IO.File]::ReadAllText($pkgPath)
  $pc = Core $prevVer; $cc = Core $script:toVer
  if ($pc -and -not ($cc -and $cc -gt $pc)) {
    $n = if ($env:GITHUB_RUN_NUMBER) { $env:GITHUB_RUN_NUMBER } else { '0' }
    $ciVer = "$($pc.Major).$($pc.Minor).$($pc.Build + 1)-ci.$n"
    $bumped = ([regex]'"version"\s*:\s*"[^"]*"').Replace($pkgText, "`"version`": `"$ciVer`"", 1)
    [System.IO.File]::WriteAllText($pkgPath, $bumped, (New-Object System.Text.UTF8Encoding($false)))
    Write-Output "CANDIDATE VERSION: package.json says $script:toVer, not newer than $($rel.tag_name); on this runner it builds as $ciVer (not committed; the release gate refuses this run)"
    $script:toVer = $ciVer
  }
  $c = Native 'build-sidecar' { powershell -NoProfile -ExecutionPolicy Bypass -File sidecar/build.ps1 -NoUnion }
  $so = Get-Content -LiteralPath (Join-Path $Out 'build-sidecar.txt') -Raw
  Check 'build: the sidecar (in-box csc.exe, the split-winmd route)' ($c -eq 0 -and (Test-Path 'sidecar/bin/Mantle.Sidecar.exe') -and $so -match 'bluetooth and media use in-process WinRT through the split winmds') "exit $c"
  $c = Native 'build-icon' { node installer/make-icon.js installer/icon.ico }
  Check 'build: the icon' ($c -eq 0) "exit $c"
  $env:CSC_IDENTITY_AUTO_DISCOVERY = 'false'
  $c = Native 'build-installer' { npx --yes electron-builder@26.18.0 --win nsis --x64 --config installer/electron-builder.yml --publish never }
  $candSetup = Join-Path (Get-Location).Path 'dist-installer\Mantle-Setup.exe'
  Check 'build: Mantle-Setup.exe (electron-builder 26.18.0, as installer.yml)' ($c -eq 0 -and (Test-Path -LiteralPath $candSetup)) "exit $c"
  [System.IO.File]::WriteAllText($pkgPath, $pkgText, (New-Object System.Text.UTF8Encoding($false)))
  if ($script:failed.Count) { Give-Up 'the candidate built' }
}
$f = Get-Item -LiteralPath $candSetup
Write-Output "CANDIDATE: Mantle-Setup.exe $([math]::Round($f.Length / 1MB, 1)) MB, sha256 $((Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash)"
$pv = Core $prevVer; $cv = Core $script:toVer
Check "an upgrade: $script:toVer is newer than $prevVer" ([bool]($pv -and $cv -and $cv -gt $pv)) "previous $pv, candidate $cv"

# ---------------------------------------------------------------- 3. the previous release, installed and live
Step "install $($rel.tag_name) and take it live"
Run-Setup $prevSetup "install $($rel.tag_name)"
Check "installed: $($rel.tag_name) ($prevVer)" ((Installed-Version) -eq $prevVer) "$(Installed-Version)"
Check 'a silent install starts nothing' (@(From-Install).Count -eq 0)
$from = Log-Offset
$g = Start-Process -FilePath $exe -WorkingDirectory $inst -PassThru
$w = Find-Window $g.Id 60
Check "$($rel.tag_name): first start shows the warning" ($null -ne $w)
if ($w) { (Named $w 'Start Mantle').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
Check "$($rel.tag_name): the warning hands over and exits" ($g.WaitForExit(90000))
$before = Wait-LiveUp $from
Check "$($rel.tag_name): comes up live" ($null -ne $before) ''
Write-Output "  $($rel.tag_name) $before"
Write-Output "  $($rel.tag_name) processes: $(Procs-Said)"
Check "$($rel.tag_name): one Mantle.exe main process, its sidecar runs" (@(Mains).Count -eq 1 -and @(Sidecars).Count -ge 1) (Procs-Said)
$st = Shell-State
Check "$($rel.tag_name): shell-state.json restored:false (it went live)" ($st -and $st.restored -eq $false)
Check "$($rel.tag_name): the consent is recorded" (Test-Path -LiteralPath $consent)
if ($null -eq $before) { Give-Up "$($rel.tag_name) went live, so there is nothing to upgrade from" }

# ---------------------------------------------------------------- 4. the candidate over it, while live
Step "upgrade to $script:toVer while $($rel.tag_name) is live"
Remove-Item -LiteralPath (Join-Path $env:TEMP 'mantle-setup-install.log') -ErrorAction SilentlyContinue
Run-Setup $candSetup 'upgrade'
$il = Get-Content -LiteralPath (Join-Path $env:TEMP 'mantle-setup-install.log') -Raw -ErrorAction SilentlyContinue
if ($il) { $il | Set-Content -LiteralPath (Join-Path $Out 'mantle-setup-install.log') }
Check "upgrade: $($rel.tag_name) was asked to quit and did (not killed)" ($il -match 'Mantle quit and put Windows back') ''
Check 'upgrade: nothing runs after a silent upgrade' (Wait-Gone 20)
Wait-Baseline 'after the upgrade'
Check "upgrade: installed $script:toVer" ((Installed-Version) -eq $script:toVer) "$(Installed-Version)"
$un = Uninstall-Key
Check "upgrade: Settings > Apps says $script:toVer" ($un -and "$($un.DisplayVersion)" -eq $script:toVer) "$($un.DisplayVersion)"
Check 'upgrade: the profile and its consent are kept' ((Test-Path -LiteralPath $consent) -and (Test-Path -LiteralPath $stateFile))

# ---------------------------------------------------------------- 5. the upgraded copy starts on the old profile
Step "start $script:toVer on $($rel.tag_name)'s profile"
$from = Log-Offset
$g = Start-Process -FilePath $exe -WorkingDirectory $inst -PassThru
$after = Wait-LiveUp $from
Check "upgraded: Mantle comes up live on the old profile (mantle.log: features line after the upgrade)" ($null -ne $after) ''
Write-Output "  $script:toVer $after"
$warn = if ($g.HasExited) { $null } else { Find-Window $g.Id 1 }
Check 'upgraded: no first-start warning (the old consent was kept)' ($null -ne $after -or $null -eq $warn)
Write-Output "  $script:toVer processes: $(Procs-Said)"
Check 'upgraded: one Mantle.exe main process, its sidecar runs' (@(Mains).Count -eq 1 -and @(Sidecars).Count -ge 1) (Procs-Said)
$mainExe = @(Mains | ForEach-Object { $_.ExecutablePath }) | Select-Object -First 1
Check 'upgraded: the live Mantle.exe is the installed one' ($mainExe -and $mainExe -ieq $exe) "$mainExe"
$st = Shell-State
Check 'upgraded: shell-state.json restored:false again (the sidecar recorded the shell and went live)' ($st -and $st.restored -eq $false)
$lost = @(Started $before | Where-Object { (Started $after) -notcontains $_ })
Check "upgraded: every feature that started on $($rel.tag_name) starts ($(@(Started $after).Count) started)" ($null -ne $after -and $lost.Count -eq 0) "lost: $($lost -join ', ')"

Step 'quit the upgraded Mantle'
$q = Start-Process -FilePath $exe -ArgumentList '--quit' -PassThru -Wait
Check 'upgraded: Mantle.exe --quit exits 0 (it reached the running Mantle)' ($q.ExitCode -eq 0) "exit $($q.ExitCode)"
Check 'upgraded: every Mantle process exits' (Wait-Gone 45)
Wait-Baseline 'after quitting the upgraded Mantle'
$st = Shell-State
Check 'upgraded: shell-state.json says restored' ($st -and $st.restored -eq $true)

# ---------------------------------------------------------------- 6. uninstall
Step 'uninstall'
$u = Start-Process -FilePath (Join-Path $inst 'Uninstall Mantle.exe') -ArgumentList '/S' -PassThru -Wait
$end = (Get-Date).AddSeconds(120)
while ((Test-Path -LiteralPath $exe) -and (Get-Date) -lt $end) { Start-Sleep -Seconds 1 }
Check "uninstall: the program folder is gone (uninstaller exit $($u.ExitCode))" (-not (Test-Path -LiteralPath $exe))
Check 'uninstall: nothing runs' (Wait-Gone 20)
Wait-Baseline 'after uninstall'

if (@(From-Install).Count) { Kill-All }
Summary
if ($script:failed.Count) { exit 1 }
exit 0
