# put-back-previous.ps1 - put the previous good Mantle back on an INSTALLED copy, with no Mantle running and no
# Node, Electron or Mantle needed (lane release-gate, 2026-11-18). Windows PowerShell 5.1 only.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File put-back-previous.ps1            dry run: says what it would do
#   ... -Run                 download the previous release's Mantle-Setup.exe, check it, install it
#   ... -Ask                 dry run, then ask "Type Y" before doing it (the Start menu entry uses this)
#   ... -VerifyOnly          download (or -SetupFile) and check the installer's sha256, install nothing
#   ... -Tag v1.2.3          that published release instead of the one just below the installed version
#   ... -UninstallFirst      with -Run: uninstall the installed copy first (settings in %APPDATA%\mantle are kept;
#                            start at sign-in is not), for when the older installer will not go over a newer copy
#   ... -InstallDir <dir>    default %LOCALAPPDATA%\Programs\Mantle
#   ... -ReleasesJson <file> read the release list from a file instead of GitHub (tests); -SetupFile <exe> check
#                            that file instead of downloading
#
# WHAT IT DOES. It reads the installed version (<InstallDir>\resources\app\package.json) and GitHub's release
# list (one anonymous GET). The target is the newest PUBLISHED release below the installed version (a release
# pulled back to draft is skipped), or -Tag. Its Mantle-Setup.exe must have the sha256 digest GitHub keeps for
# release assets; the download must match it, or it is deleted and nothing runs. Then the installer runs silently:
# it asks a running Mantle to quit and puts Windows back before it touches a file (installer/installer.nsh), and
# exits 3 having changed nothing when it cannot confirm Windows is back. Afterwards the installed version is read
# back. Nothing starts by itself: start Mantle from Start > Mantle.
#
# WHAT IT NEVER DOES. It touches only <InstallDir> (through Mantle's own installer) and a new folder in %TEMP%.
# It never touches a git clone or worktree of Mantle, and with -Run it refuses while a Mantle that is NOT the
# installed copy runs (a Mantle.Sidecar.exe outside <InstallDir>, or an electron.exe --live): the installer's
# restore step would stop that one's crash recovery and put the taskbar back under it.
#
# The last line is "PUT BACK: would|verified|done|refused|failed ...". Exit 0 for would/verified/done.
param(
  [switch]$Run,
  [switch]$Ask,
  [switch]$VerifyOnly,
  [switch]$UninstallFirst,
  [string]$Tag = '',
  [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Mantle'),
  [string]$Repo = 'atlas-studio-software/mantle',
  [string]$ReleasesJson = '',
  [string]$SetupFile = ''
)
$ErrorActionPreference = 'Stop'
function Say([string]$t) { Write-Output $t }
function Hold { if ($Ask) { Say ''; Say 'Press Enter to close this window.'; [void][Console]::ReadLine() } }
function Finish([string]$result, [string]$text, [int]$code) { Say "PUT BACK: $result ($text)"; Hold; exit $code }
function Parse-Version([string]$s) {
  if ("$s".Trim() -match '^v?(\d{1,6})\.(\d{1,6})\.(\d{1,6})$') { return [version]"$($Matches[1]).$($Matches[2]).$($Matches[3])" }
  return $null
}

if (($Run -and $VerifyOnly) -or ($Ask -and ($Run -or $VerifyOnly))) { Finish 'refused' 'use one of -Run, -Ask or -VerifyOnly' 2 }
if ($UninstallFirst -and $VerifyOnly) { Finish 'refused' '-UninstallFirst goes with -Run or -Ask' 2 }

# ---- what is installed
$appDir = Join-Path $InstallDir 'resources\app'
$exe = Join-Path $InstallDir 'Mantle.exe'
if (Test-Path -LiteralPath (Join-Path $InstallDir '.git')) { Finish 'refused' "$InstallDir is a git checkout, not an installed Mantle; this script only puts back installed copies" 2 }
$installed = $null
try { $installed = Parse-Version ((Get-Content -LiteralPath (Join-Path $appDir 'package.json') -Raw | ConvertFrom-Json).version) } catch { $installed = $null }
Say "installed: $(if ($installed) { "Mantle $installed in $InstallDir" } else { "no Mantle in $InstallDir" })"
if (-not $installed -and -not $Tag) { Finish 'refused' "no installed Mantle in $InstallDir; pass -Tag v1.2.3 to install a release" 2 }

# ---- the release list
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$ProgressPreference = 'SilentlyContinue'
$ua = @{ 'User-Agent' = 'mantle-put-back-previous'; 'Accept' = 'application/vnd.github+json' }
try {
  if ($ReleasesJson) { $list = @(Get-Content -LiteralPath $ReleasesJson -Raw | ConvertFrom-Json) }
  else { $list = @(Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases?per_page=100" -Headers $ua) }
} catch { Finish 'failed' "GitHub's release list could not be read: $($_.Exception.Message)" 1 }
$list = @($list | ForEach-Object { $_ })   # ConvertFrom-Json gives one array object; flatten it
$published = @($list | Where-Object { $_ -and -not $_.draft -and -not $_.prerelease -and (Parse-Version $_.tag_name) } |
  Sort-Object -Property @{ Expression = { Parse-Version $_.tag_name } } -Descending)
if ($Tag) {
  $target = $published | Where-Object { $_.tag_name -eq $Tag } | Select-Object -First 1
  if (-not $target) { Finish 'refused' "$Tag is not a published release (drafts and pre-releases are never put back)" 2 }
} else {
  $target = $published | Where-Object { (Parse-Version $_.tag_name) -lt $installed } | Select-Object -First 1
  if (-not $target) { Finish 'refused' "no published release is older than the installed $installed, so there is nothing to go back to" 2 }
}
$tv = Parse-Version $target.tag_name
$newer = $published | Where-Object { $installed -and (Parse-Version $_.tag_name) -gt $installed } | Select-Object -First 1
if ($newer) { Say "note: $($newer.tag_name) is published and newer than the installed $installed; it may already fix this (-Tag $($newer.tag_name))" }
$asset = @($target.assets | Where-Object { $_.name -eq 'Mantle-Setup.exe' }) | Select-Object -First 1
if (-not $asset) { Finish 'refused' "$($target.tag_name) has no Mantle-Setup.exe" 2 }
if (-not ("$($asset.digest)" -match '^sha256:([0-9a-fA-F]{64})$')) { Finish 'refused' "$($target.tag_name)'s Mantle-Setup.exe has no sha256 digest on GitHub, so a download could not be checked" 2 }
$want = $Matches[1].ToLowerInvariant()
Say "target: Mantle $($target.tag_name), $($asset.browser_download_url) ($([math]::Round([double]$asset.size / 1MB, 1)) MB, sha256 $want)"

# ---- the dry run (the default) and -Ask
$plan = "download $($target.tag_name)'s Mantle-Setup.exe, check its sha256, $(if ($UninstallFirst) { 'uninstall the installed copy (settings kept), ' })then run it silently: it asks Mantle to quit, puts Windows back, and installs $($target.tag_name)$(if ($installed) { " over $installed" })"
if (-not $Run -and -not $VerifyOnly) {
  Say "would: $plan"
  if (-not $Ask) { Finish 'would' "dry run: nothing downloaded or changed; run again with -Run to do it" 0 }
  Say ''
  Say "Type Y and press Enter to do this now, or just press Enter to stop."
  $answer = [Console]::ReadLine()
  if ("$answer".Trim() -notmatch '^[Yy]$') { Finish 'would' 'stopped: nothing downloaded or changed' 0 }
  $Run = $true
}

# ---- the installer, checked
$downloaded = $false
if ($SetupFile) { $setup = (Resolve-Path -LiteralPath $SetupFile).Path }
else {
  $dir = Join-Path $env:TEMP ('mantle-put-back-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
  New-Item -ItemType Directory -Path $dir | Out-Null
  $setup = Join-Path $dir 'Mantle-Setup.exe'
  Say "downloading to $setup"
  try { Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $setup -Headers @{ 'User-Agent' = 'mantle-put-back-previous' } -UseBasicParsing }
  catch { Finish 'failed' "the download failed: $($_.Exception.Message)" 1 }
  $downloaded = $true
}
$got = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
if ($got -ne $want) {
  if ($downloaded) { Remove-Item -LiteralPath $setup -Force -ErrorAction SilentlyContinue }
  Finish 'refused' "the installer's sha256 $got is not $($target.tag_name)'s $want; $(if ($downloaded) { 'it was deleted and ' })nothing ran" 1
}
Say "checked: sha256 $got is $($target.tag_name)'s Mantle-Setup.exe"
if ($VerifyOnly) { Finish 'verified' "$setup is $($target.tag_name)'s Mantle-Setup.exe; nothing installed" 0 }

# ---- -Run: only the installed copy may be running
$prefix = $InstallDir.TrimEnd('\').ToLowerInvariant() + '\'
$others = @(Get-CimInstance Win32_Process -Filter "Name='Mantle.Sidecar.exe' OR Name='electron.exe'" | Where-Object {
  $p = "$($_.ExecutablePath)".ToLowerInvariant()
  -not $p.StartsWith($prefix) -and ($_.Name -ieq 'Mantle.Sidecar.exe' -or "$($_.CommandLine)" -match '(^|\s)--live(\s|$)')
})
if ($others.Count) {
  $where = (@($others | ForEach-Object { "$($_.ExecutablePath)" } | Select-Object -Unique) -join '; ')
  Finish 'refused' "a Mantle that is not the installed copy is running ($where); quit that one first (Ctrl+Alt+Shift+Backspace), then run this again" 2
}
if ($UninstallFirst -and $installed) {
  $un = Join-Path $InstallDir 'Uninstall Mantle.exe'
  if (-not (Test-Path -LiteralPath $un)) { Finish 'failed' "no $un to uninstall with" 1 }
  Say "uninstalling Mantle $installed first (settings in %APPDATA%\mantle are kept)"
  $u = Start-Process -FilePath $un -ArgumentList '/S' -PassThru -Wait
  $end = (Get-Date).AddSeconds(120)
  while ((Test-Path -LiteralPath $exe) -and (Get-Date) -lt $end) { Start-Sleep -Seconds 1 }
  if (Test-Path -LiteralPath $exe) { Finish 'failed' "the uninstaller (exit $($u.ExitCode)) did not remove $exe; nothing else was done" 1 }
}
Say "installing $($target.tag_name) (silent; it puts Windows back first)"
$p = Start-Process -FilePath $setup -ArgumentList '/S' -PassThru -Wait
if ($p.ExitCode -eq 3) { Finish 'failed' "the installer could not confirm Windows is back, so it changed nothing: run Start > Mantle > Restore Windows taskbar, then this again" 1 }
if ($p.ExitCode -ne 0) { Finish 'failed' "the installer exited $($p.ExitCode)" 1 }
$now = $null
try { $now = Parse-Version ((Get-Content -LiteralPath (Join-Path $appDir 'package.json') -Raw | ConvertFrom-Json).version) } catch { $now = $null }
if ($now -ne $tv) { Finish 'failed' "the installer exited 0 but $(if ($now) { "$now" } else { 'nothing' }) is installed, not $tv; run again with -UninstallFirst" 1 }
if ($downloaded) { Remove-Item -LiteralPath (Split-Path -Parent $setup) -Recurse -Force -ErrorAction SilentlyContinue }
Finish 'done' "Mantle $tv is installed$(if ($installed) { " (was $installed)" }); start it from Start > Mantle" 0
