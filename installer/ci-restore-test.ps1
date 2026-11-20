# ci-restore-test.ps1 - the out-of-band restore check (lane mantle-restore-installer, 2026-11-19). RUNS ON A
# THROWAWAY RUNNER ONLY (cloudtest or GitHub Actions; it refuses anywhere else): it installs Mantle, runs it LIVE,
# kills it, puts the previous release back and uninstalls it, like installer/ci-upgrade-test.ps1. Never run it on
# a PC someone uses.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File installer/ci-restore-test.ps1 [-From <tag>] [-Setup <exe>] [-Out <dir>] [-UninstallFirst]
#
#   1. the PREVIOUS release: GitHub's releases/latest (or -From <tag>, published only); its Mantle-Setup.exe must
#      match the sha256 digest GitHub keeps for the asset.
#   2. the CANDIDATE: this checkout built the way .github/workflows/installer.yml builds it, or -Setup <exe>. One
#      that is not newer than the previous release builds as <prev patch+1>-ci.<run> on this runner only
#      (package.json is put back right after the build).
#   3. the previous release is installed and taken live (its own profile), then the candidate is installed over
#      it and taken live on that profile.
#   4. THE INSTALLER STOPS ONLY ITS OWN SUPERVISORS: a stand-in Mantle.Sidecar.exe --supervise= runs from a folder
#      outside the install (as a git checkout's would), another one from inside it, and the candidate's
#      Mantle-Setup.exe runs over the live candidate. The outside one must survive; the inside one and the live
#      Mantle must be gone after the install; the candidate then starts again with its own real supervisor.
#   5. THE CHECKOUT GUARD: the INSTALLED put-back-previous.ps1 -Run must refuse, and change nothing, while a Mantle
#      from outside the install folder runs (the stand-in sidecar, then a stand-in electron.exe . --live).
#   6. the candidate is killed (every process from the install folder), as if it were broken.
#   7. the INSTALLED tools\put-back-previous.ps1 -Tag <previous> -Run (with -UninstallFirst when that is asked
#      for, or when the plain run says the older installer did not go over the newer copy).
#   8. the previous release must come back live on the old profile: mantle.log's "features: started" line, one
#      Mantle.exe main process, its supervisor, shell-state.json restored:false, no first-start warning, every
#      feature it started the first time; then --quit, restored:true, and the taskbar as before.
#   9. uninstall, and the taskbar as before.
#
# Every check prints "ok" or "FAIL"; a crash anywhere is a counted FAIL plus the summary. The last line:
#   RESTORE TEST: <previous tag> -> <candidate version> -> <previous tag>: n passed, m failed
param(
  [string]$From = '',
  [string]$Setup = '',
  [string]$Out = 'restore-out',
  [string]$Repo = 'atlas-studio-software/mantle',
  [switch]$UninstallFirst
)
$ErrorActionPreference = 'Stop'
$script:fromTag = if ($From) { $From } else { 'v?' }
$script:toVer = '?'
$script:standIns = New-Object System.Collections.ArrayList
try { $script:toVer = (Get-Content -LiteralPath 'package.json' -Raw | ConvertFrom-Json).version } catch { }
function Summary {
  Write-Host ''
  foreach ($f in $script:failed) { Write-Host "FAIL $f" }
  Write-Host "RESTORE TEST: $script:fromTag -> $script:toVer -> $script:fromTag`: $script:passed passed, $($script:failed.Count) failed"
}
$script:passed = 0
$script:failed = New-Object System.Collections.ArrayList
# $ok must be true/false (or a number); anything else is a harness bug, counted as a FAIL with its type named.
function Check([string]$name, $ok, [string]$detail = '') {
  if (-not ($ok -is [bool] -or $ok -is [int] -or $ok -is [long])) {
    $what = if ($null -eq $ok) { 'null' } else { $ok.GetType().Name }
    $detail = "harness bug: the check got a $what, not true/false$(if ($detail) { "; $detail" })"; $ok = $false
  }
  if ($ok) { $script:passed++; Write-Host "ok   $name" }
  else { [void]$script:failed.Add($name); Write-Host "FAIL $name$(if ($detail) { " -- $detail" })" }
}
function Step([string]$text) { Write-Host ''; Write-Host "== $text" }
function Stop-StandIns { foreach ($p in @($script:standIns)) { try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { } }; $script:standIns.Clear() }
function Cleanup { try { if (@(From-Install).Count) { Kill-All } } catch { }; try { Stop-StandIns } catch { } }
function Give-Up([string]$why) { Check $why $false; Cleanup; Summary; exit 1 }
trap {
  if ($null -eq $script:failed) { $script:failed = New-Object System.Collections.ArrayList }
  if ($null -eq $script:passed) { $script:passed = 0 }
  $at = "line $($_.InvocationInfo.ScriptLineNumber): $($_.Exception.Message)"
  Write-Host "HARNESS ERROR at $at"
  [void]$script:failed.Add("the script ran to its end (it stopped at $at)")
  Cleanup
  Summary
  exit 1
}

if ($env:CLOUDTEST -ne '1' -and $env:GITHUB_ACTIONS -ne 'true') {
  Write-Host 'refused: this runs Mantle live, kills it and installs over it; it runs only on a throwaway runner (CLOUDTEST=1 or GITHUB_ACTIONS=true)'
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
public static class MantleBack {
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
$installLog = Join-Path $env:TEMP 'mantle-setup-install.log'

function From-Install { @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.ToLowerInvariant().StartsWith($prefix) }) }
function Wait-Gone([int]$seconds) {
  $end = (Get-Date).AddSeconds($seconds)
  do { if (@(From-Install).Count -eq 0) { return $true }; Start-Sleep -Milliseconds 500 } while ((Get-Date) -lt $end)
  return $false
}
# supervisors first in the same sweep, so none of them reads the kill as a crash and starts Mantle again
function Kill-All {
  $all = @(From-Install)
  $all | Where-Object { $_.Name -ieq 'Mantle.Sidecar.exe' } | ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force } catch { } }
  $all | Where-Object { $_.Name -ine 'Mantle.Sidecar.exe' } | ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force } catch { } }
}
function Wait-Baseline([string]$why, [int]$seconds = 15) {
  $end = (Get-Date).AddSeconds($seconds)
  do { $now = [MantleBack]::State(); if ($now -eq $script:baseline) { break }; Start-Sleep -Milliseconds 500 } while ((Get-Date) -lt $end)
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
# Electron's main process: the installed Mantle.exe without --type= (a consented plain start shows no --live on its
# OS command line; being live is proven by mantle.log's features line and shell-state.json).
function Mains { @(From-Install | Where-Object { $_.Name -ieq 'Mantle.exe' -and -not ("$($_.CommandLine)" -match '--type=') }) }
function Sidecars { @(From-Install | Where-Object { $_.Name -ieq 'Mantle.Sidecar.exe' }) }
function Supervisors { @(Sidecars | Where-Object { "$($_.CommandLine)" -match '--supervise=' }) }
function Procs-Said { $m = @(Mains); "$($m.Count) main(s) [$(($m | ForEach-Object { "$($_.CommandLine)" }) -join ' | ')], $(@(Sidecars).Count) sidecar(s), $(@(Supervisors).Count) supervisor(s)" }
function Shell-State { try { Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json } catch { $null } }
function Alive([int]$procId) { $null -ne (Get-Process -Id $procId -ErrorAction SilentlyContinue) }
# a native command with its output in $Out\<name>.txt; -> its exit code, and only that
function Native([string]$name, [scriptblock]$run) {
  $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  $o = @(& $run 2>&1); $code = $LASTEXITCODE
  $ErrorActionPreference = $old
  $o | ForEach-Object { "$_" } | Set-Content -LiteralPath (Join-Path $Out "$name.txt")
  $o | Select-Object -Last 3 | ForEach-Object { Write-Host "  $name> $_" }
  if ($null -eq $code) { $code = -1 }
  return [int]$code
}
function Last-Line([string]$name) { @(Get-Content -LiteralPath (Join-Path $Out "$name.txt") | ForEach-Object { "$_".Trim() } | Where-Object { $_ }) | Select-Object -Last 1 }
function Core([string]$v) { if ($v -match '^v?(\d+)\.(\d+)\.(\d+)') { [version]"$($Matches[1]).$($Matches[2]).$($Matches[3])" } else { $null } }
# Start a stand-in (a do-nothing exe that sleeps) under a Mantle process name, from any folder; -> its Process
function Start-StandIn([string]$path, [string]$argText) {
  $p = Start-Process -FilePath $path -ArgumentList $argText -WindowStyle Hidden -PassThru
  [void]$script:standIns.Add($p)
  Start-Sleep -Milliseconds 800
  return $p
}
# the installed put-back-previous.ps1 (its own copy, as the Start menu entry runs it); -> exit code, output in $Out\<name>.txt.
# A 403 from GitHub's anonymous release list is retried once after a minute.
function Put-Back([string]$name, [string[]]$more) {
  $pb = Join-Path $app 'tools\put-back-previous.ps1'
  if (-not (Test-Path -LiteralPath $pb)) {
    "PUT BACK: (harness) no $pb in the installed copy ($(Installed-Version))" | Set-Content -LiteralPath (Join-Path $Out "$name.txt")
    Write-Host "  $name> no installed put-back-previous.ps1 ($(Installed-Version) is installed)"
    return -1
  }
  $code = Native $name { powershell -NoProfile -ExecutionPolicy Bypass -File $pb -Tag $script:fromTag @more }
  if ((Last-Line $name) -match 'release list could not be read') {
    Write-Host "  $name`: GitHub's release list could not be read; once more in 60 s"
    Start-Sleep -Seconds 60
    $code = Native $name { powershell -NoProfile -ExecutionPolicy Bypass -File $pb -Tag $script:fromTag @more }
  }
  return $code
}

$script:baseline = [MantleBack]::State()
Write-Host "taskbar before anything: $script:baseline"
Check 'the runner has a Windows taskbar to protect' ($script:baseline -match 'tray=True visible=True')
Check 'nothing is installed yet' (-not (Test-Path -LiteralPath $exe))

# the stand-ins: a console exe that sleeps 30 minutes, built with the in-box C# compiler, copied under the two names
# put-back-previous.ps1 and restore-windows.ps1 look for. They live in this checkout, outside the install folder.
$outside = Join-Path $Out 'outside'
New-Item -ItemType Directory -Force -Path $outside | Out-Null
$standSidecar = Join-Path $outside 'Mantle.Sidecar.exe'
$standElectron = Join-Path $outside 'electron.exe'
Add-Type -TypeDefinition 'public static class MantleStandIn { public static void Main(string[] a) { System.Threading.Thread.Sleep(1800000); } }' -OutputAssembly $standSidecar -OutputType WindowsApplication
Copy-Item -LiteralPath $standSidecar -Destination $standElectron -Force
Check 'stand-ins built (Mantle.Sidecar.exe and electron.exe that only sleep)' ((Test-Path -LiteralPath $standSidecar) -and (Test-Path -LiteralPath $standElectron))

# ---------------------------------------------------------------- 1. the previous release
Step 'the previous release: its Mantle-Setup.exe, checked against GitHub''s digest'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$ProgressPreference = 'SilentlyContinue'
$hdr = @{ 'User-Agent' = 'mantle-ci-restore-test'; 'Accept' = 'application/vnd.github+json' }
if ($env:GITHUB_TOKEN) { $hdr['Authorization'] = "Bearer $($env:GITHUB_TOKEN)" }
$api = if ($From) { "https://api.github.com/repos/$Repo/releases/tags/$From" } else { "https://api.github.com/repos/$Repo/releases/latest" }
$rel = $null
foreach ($try in 1, 2) {
  try { $rel = Invoke-RestMethod -Uri $api -Headers $hdr; break }
  catch { if ($try -eq 2) { Give-Up "the previous release could be read ($api): $($_.Exception.Message)" }; Write-Host "  release read failed ($($_.Exception.Message)); once more in 60 s"; Start-Sleep -Seconds 60 }
}
$script:fromTag = "$($rel.tag_name)"
Check "the previous release $($rel.tag_name) is published (not a draft, not a pre-release)" (-not $rel.draft -and -not $rel.prerelease)
$asset = @($rel.assets | Where-Object { $_.name -eq 'Mantle-Setup.exe' }) | Select-Object -First 1
if (-not $asset) { Give-Up "$($rel.tag_name) holds a Mantle-Setup.exe" }
if (-not ("$($asset.digest)" -match '^sha256:([0-9a-f]{64})$')) { Give-Up "$($rel.tag_name)'s Mantle-Setup.exe has a sha256 digest (got '$($asset.digest)')" }
$wantSha = $Matches[1]
$prevDir = Join-Path $Out 'previous'
New-Item -ItemType Directory -Force -Path $prevDir | Out-Null
$prevSetup = Join-Path $prevDir 'Mantle-Setup.exe'
try { Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $prevSetup -Headers @{ 'User-Agent' = 'mantle-ci-restore-test' } -UseBasicParsing } catch { Give-Up "the previous Mantle-Setup.exe downloads: $($_.Exception.Message)" }
$gotSha = (Get-FileHash -LiteralPath $prevSetup -Algorithm SHA256).Hash.ToLowerInvariant()
Check "the previous Mantle-Setup.exe matches its release digest (sha256 $wantSha)" ($gotSha -eq $wantSha) "got $gotSha"
if ($gotSha -ne $wantSha) { Give-Up 'a previous installer that is not the release''s' }
$prevVer = "$($rel.tag_name)".TrimStart('v')

# ---------------------------------------------------------------- 2. the candidate
Step "the candidate: Mantle $script:toVer"
if ($Setup) {
  $candSetup = (Resolve-Path $Setup).Path
} else {
  $pkgPath = (Resolve-Path 'package.json').Path
  $pkgText = [System.IO.File]::ReadAllText($pkgPath)
  $pc = Core $prevVer; $cc = Core $script:toVer
  if ($pc -and -not ($cc -and $cc -gt $pc)) {
    $n = if ($env:GITHUB_RUN_NUMBER) { $env:GITHUB_RUN_NUMBER } else { '0' }
    $ciVer = "$($pc.Major).$($pc.Minor).$($pc.Build + 1)-ci.$n"
    $bumped = ([regex]'"version"\s*:\s*"[^"]*"').Replace($pkgText, "`"version`": `"$ciVer`"", 1)
    [System.IO.File]::WriteAllText($pkgPath, $bumped, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "CANDIDATE VERSION: package.json says $script:toVer, not newer than $($rel.tag_name); on this runner it builds as $ciVer (not committed)"
    $script:toVer = $ciVer
  }
  try {
    $c = Native 'build-sidecar' { powershell -NoProfile -ExecutionPolicy Bypass -File sidecar/build.ps1 -NoUnion }
    $so = Get-Content -LiteralPath (Join-Path $Out 'build-sidecar.txt') -Raw
    Check 'build: the sidecar (in-box csc.exe, the split-winmd route)' ($c -eq 0 -and (Test-Path 'sidecar/bin/Mantle.Sidecar.exe') -and $so -match 'bluetooth and media use in-process WinRT through the split winmds') "exit $c"
    $c = Native 'build-icon' { node installer/make-icon.js installer/icon.ico }
    Check 'build: the icon' ($c -eq 0) "exit $c"
    $env:CSC_IDENTITY_AUTO_DISCOVERY = 'false'
    $c = Native 'build-installer' { npx --yes electron-builder@26.18.0 --win nsis --x64 --config installer/electron-builder.yml --publish never }
    $candSetup = Join-Path (Get-Location).Path 'dist-installer\Mantle-Setup.exe'
    Check 'build: Mantle-Setup.exe (electron-builder 26.18.0, as installer.yml)' ($c -eq 0 -and (Test-Path -LiteralPath $candSetup)) "exit $c"
  } finally {
    [System.IO.File]::WriteAllText($pkgPath, $pkgText, (New-Object System.Text.UTF8Encoding($false)))
  }
  if ($script:failed.Count) { Give-Up 'the candidate built' }
}
$f = Get-Item -LiteralPath $candSetup
Write-Host "CANDIDATE: Mantle-Setup.exe $([math]::Round($f.Length / 1MB, 1)) MB, sha256 $((Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash)"
$pv = Core $prevVer; $cv = Core $script:toVer
Check "the candidate $script:toVer is newer than $prevVer" ([bool]($pv -and $cv -and $cv -gt $pv)) "previous $pv, candidate $cv"

# ---------------------------------------------------------------- 3. previous installed and live, then the candidate over it
Step "install $($rel.tag_name) and take it live"
Run-Setup $prevSetup "install $($rel.tag_name)"
Check "installed: $($rel.tag_name) ($prevVer)" ((Installed-Version) -eq $prevVer) "$(Installed-Version)"
$from = Log-Offset
$g = Start-Process -FilePath $exe -WorkingDirectory $inst -PassThru
$w = Find-Window $g.Id 60
Check "$($rel.tag_name): first start shows the warning" ($null -ne $w)
if ($w) { (Named $w 'Start Mantle').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
Check "$($rel.tag_name): the warning hands over and exits" ($g.WaitForExit(90000))
$before = Wait-LiveUp $from
Check "$($rel.tag_name): comes up live" ($null -ne $before)
Write-Host "  $($rel.tag_name) $before"
Write-Host "  $($rel.tag_name) processes: $(Procs-Said)"
Check "$($rel.tag_name): one Mantle.exe main process, its sidecar runs" (@(Mains).Count -eq 1 -and @(Sidecars).Count -ge 1) (Procs-Said)
if ($null -eq $before) { Give-Up "$($rel.tag_name) went live, so there is nothing to put back to" }

Step "install $script:toVer over the live $($rel.tag_name) and take it live"
Run-Setup $candSetup 'install the candidate'
Check "installed: $script:toVer" ((Installed-Version) -eq $script:toVer) "$(Installed-Version)"
Check 'nothing runs after a silent install' (Wait-Gone 20)
$from = Log-Offset
$g = Start-Process -FilePath $exe -WorkingDirectory $inst -PassThru
$cand = Wait-LiveUp $from
Check "$script:toVer`: comes up live on the old profile" ($null -ne $cand)
Write-Host "  $script:toVer $cand"
Write-Host "  $script:toVer processes: $(Procs-Said)"
Check "$script:toVer`: one Mantle.exe main process and its crash-recovery supervisor from the install folder" (@(Mains).Count -eq 1 -and @(Supervisors).Count -ge 1) (Procs-Said)
if ($null -eq $cand) { Give-Up "$script:toVer went live" }

# ---------------------------------------------------------------- 4. the installer stops only its own supervisors
Step 'an install over a live Mantle while another Mantle''s supervisor runs from outside the install folder'
$realSup = @(Supervisors | ForEach-Object { [int]$_.ProcessId })
$oldMain = @(Mains | ForEach-Object { [int]$_.ProcessId })
$out1 = Start-StandIn $standSidecar "--supervise=$PID --supervise-dir=`"$outside`""
$inDir = Join-Path $inst 'ci-standin'
New-Item -ItemType Directory -Force -Path $inDir | Out-Null
Copy-Item -LiteralPath $standSidecar -Destination (Join-Path $inDir 'Mantle.Sidecar.exe') -Force
$in1 = Start-StandIn (Join-Path $inDir 'Mantle.Sidecar.exe') "--supervise=$PID --supervise-dir=`"$inDir`""
Check 'the outside stand-in supervisor runs (from this checkout, not the install folder)' (Alive $out1.Id)
Check 'the inside stand-in supervisor runs (from the install folder)' (Alive $in1.Id)
Remove-Item -LiteralPath $installLog -ErrorAction SilentlyContinue
Run-Setup $candSetup "reinstall $script:toVer over itself"
$il = Get-Content -LiteralPath $installLog -Raw -ErrorAction SilentlyContinue
if ($il) { $il | Set-Content -LiteralPath (Join-Path $Out 'mantle-setup-install-reinstall.log') }
Check 'reinstall: the OUTSIDE supervisor survived the installer (it belongs to another Mantle)' (Alive $out1.Id)
Check 'reinstall: the restore step says it spared one supervisor of another Mantle' ($il -match 'spared 1 Mantle\.Sidecar\.exe') ''
Check 'reinstall: the INSIDE supervisor was stopped' (-not (Alive $in1.Id))
Check "reinstall: the installed copy's own supervisor is gone ($($realSup -join ', '))" (@($realSup | Where-Object { Alive $_ }).Count -eq 0)
Check "reinstall: the live Mantle is gone ($($oldMain -join ', '))" (@($oldMain | Where-Object { Alive $_ }).Count -eq 0)
Check 'reinstall: nothing runs from the install folder' (Wait-Gone 20)
Wait-Baseline 'after the reinstall'
Remove-Item -LiteralPath $inDir -Recurse -Force -ErrorAction SilentlyContinue
$from = Log-Offset
$g = Start-Process -FilePath $exe -WorkingDirectory $inst -PassThru
$again = Wait-LiveUp $from
Check "reinstalled: $script:toVer comes up live again" ($null -ne $again)
Check 'reinstalled: one main process and a NEW supervisor of its own from the install folder' (@(Mains).Count -eq 1 -and @(Supervisors | Where-Object { $realSup -notcontains [int]$_.ProcessId }).Count -ge 1) (Procs-Said)
Check 'reinstalled: the outside supervisor still runs' (Alive $out1.Id)

# ---------------------------------------------------------------- 5. the checkout guard of put-back-previous.ps1 -Run
Step 'put-back-previous.ps1 -Run while a Mantle from outside the install folder runs'
# its own outside supervisor, so a failure in step 4 can never turn this into a real put back
Stop-StandIns
$out2 = Start-StandIn $standSidecar "--supervise=$PID --supervise-dir=`"$outside`""
Check 'the outside stand-in Mantle.Sidecar.exe runs' (Alive $out2.Id)
if (-not (Alive $out2.Id)) { Give-Up 'an outside Mantle runs for the guard check' }
$c = Put-Back 'putback-guard-sidecar' @('-Run')
$ll = Last-Line 'putback-guard-sidecar'
Check 'guard (outside Mantle.Sidecar.exe): -Run refuses with exit 2' ($c -eq 2 -and $ll -match '^PUT BACK: refused \(a Mantle that is not the installed copy is running') "exit $c, $ll"
Stop-StandIns
$el = Start-StandIn $standElectron '. --live'
Check 'the outside stand-in electron.exe . --live runs' (Alive $el.Id)
$c = Put-Back 'putback-guard-electron' @('-Run')
$ll = Last-Line 'putback-guard-electron'
Check 'guard (outside electron.exe --live): -Run refuses with exit 2' ($c -eq 2 -and $ll -match '^PUT BACK: refused \(a Mantle that is not the installed copy is running') "exit $c, $ll"
Stop-StandIns
Check "guard: nothing changed (still $script:toVer, one main process still live)" ((Installed-Version) -eq $script:toVer -and @(Mains).Count -eq 1) "$(Installed-Version); $(Procs-Said)"

# ---------------------------------------------------------------- 6. the candidate dies
Step "kill $script:toVer, as if it were broken"
$end = (Get-Date).AddSeconds(30)
do { Kill-All; Start-Sleep -Seconds 2 } while (@(From-Install).Count -and (Get-Date) -lt $end)
Check 'killed: nothing runs from the install folder (and nothing started it again)' (@(From-Install).Count -eq 0) (Procs-Said)
$st = Shell-State
Check 'killed: shell-state.json still says restored:false (Mantle did not put Windows back)' ($st -and $st.restored -eq $false)
Write-Host "  taskbar now: $([MantleBack]::State())"

# ---------------------------------------------------------------- 7. put the previous release back
$mode = if ($UninstallFirst) { 'uninstall-first' } else { 'plain' }
Step "the installed put-back-previous.ps1 -Tag $script:fromTag -Run ($mode)"
$args1 = @('-Run'); if ($UninstallFirst) { $args1 += '-UninstallFirst' }
$c = Put-Back "putback-$mode" $args1
$ll = Last-Line "putback-$mode"
Copy-Item -LiteralPath $installLog -Destination (Join-Path $Out "mantle-setup-install-putback-$mode.log") -ErrorAction SilentlyContinue
Check "put back ($mode): exit 0, PUT BACK: done" ($c -eq 0 -and $ll -match "^PUT BACK: done \(Mantle $([regex]::Escape($prevVer)) is installed") "exit $c, $ll"
if (-not $UninstallFirst -and $c -ne 0 -and $ll -match 'UninstallFirst|is installed, not') {
  Step "the older installer did not go over the newer copy: -UninstallFirst"
  $c = Put-Back 'putback-uninstall-first' @('-Run', '-UninstallFirst')
  $ll = Last-Line 'putback-uninstall-first'
  Check 'put back (-UninstallFirst): exit 0, PUT BACK: done' ($c -eq 0 -and $ll -match '^PUT BACK: done') "exit $c, $ll"
}
Check "put back: installed $prevVer" ((Installed-Version) -eq $prevVer) "$(Installed-Version)"
$un = Uninstall-Key
Check "put back: Settings > Apps says $prevVer" ($un -and "$($un.DisplayVersion)" -eq $prevVer) "$($un.DisplayVersion)"
Check 'put back: nothing was started by it' (Wait-Gone 10)
Wait-Baseline 'after putting the previous release back'
Check 'put back: the profile and its consent are kept' ((Test-Path -LiteralPath $consent) -and (Test-Path -LiteralPath $stateFile))
$st = Shell-State
Check 'put back: shell-state.json says restored (the installer put Windows back)' ($st -and $st.restored -eq $true)

# ---------------------------------------------------------------- 8. the previous release comes back live
Step "start $script:fromTag on the profile $script:toVer used"
$from = Log-Offset
$g = Start-Process -FilePath $exe -WorkingDirectory $inst -PassThru
$back = Wait-LiveUp $from
Check "$script:fromTag again: comes up live (mantle.log: features line)" ($null -ne $back)
Write-Host "  $script:fromTag $back"
$warn = if ($g.HasExited) { $null } else { Find-Window $g.Id 1 }
Check "$script:fromTag again: no first-start warning (the consent was kept)" ($null -ne $back -or $null -eq $warn)
Write-Host "  $script:fromTag processes: $(Procs-Said)"
Check "$script:fromTag again: one Mantle.exe main process, its sidecar runs" (@(Mains).Count -eq 1 -and @(Sidecars).Count -ge 1) (Procs-Said)
$mainExe = @(Mains | ForEach-Object { $_.ExecutablePath }) | Select-Object -First 1
Check "$script:fromTag again: the live Mantle.exe is the installed one" ($mainExe -and $mainExe -ieq $exe) "$mainExe"
Check "$script:fromTag again: installed version is still $prevVer" ((Installed-Version) -eq $prevVer) "$(Installed-Version)"
$st = Shell-State
Check "$script:fromTag again: shell-state.json restored:false (the sidecar recorded the shell and went live)" ($st -and $st.restored -eq $false)
$lost = @(Started $before | Where-Object { (Started $back) -notcontains $_ })
Check "$script:fromTag again: every feature it started the first time starts ($(@(Started $back).Count) started)" ($null -ne $back -and $lost.Count -eq 0) "lost: $($lost -join ', ')"

Step "quit $script:fromTag"
$q = Start-Process -FilePath $exe -ArgumentList '--quit' -PassThru -Wait
Check "$script:fromTag again: Mantle.exe --quit exits 0" ($q.ExitCode -eq 0) "exit $($q.ExitCode)"
Check "$script:fromTag again: every Mantle process exits" (Wait-Gone 45)
Wait-Baseline "after quitting $script:fromTag"
$st = Shell-State
Check "$script:fromTag again: shell-state.json says restored" ($st -and $st.restored -eq $true)

# ---------------------------------------------------------------- 9. uninstall
Step 'uninstall'
$u = Start-Process -FilePath (Join-Path $inst 'Uninstall Mantle.exe') -ArgumentList '/S' -PassThru -Wait
$end = (Get-Date).AddSeconds(120)
while ((Test-Path -LiteralPath $exe) -and (Get-Date) -lt $end) { Start-Sleep -Seconds 1 }
Check "uninstall: the program folder is gone (uninstaller exit $($u.ExitCode))" (-not (Test-Path -LiteralPath $exe))
Check 'uninstall: nothing runs' (Wait-Gone 20)
Wait-Baseline 'after uninstall'

Cleanup
Summary
if ($script:failed.Count) { exit 1 }
exit 0
