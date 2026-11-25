# native/tools/install-autostart.ps1 - register the NATIVE "Mantle Native" logon task (or remove it).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File native\tools\install-autostart.ps1 -Exe <MantleNative.exe>
#   ... -Uninstall                      remove the task
#   ... -Uninstall -OnlyIfExe <exe>     remove it ONLY if one of its Exec actions is exactly that exe
#                                       (full path, case-insensitive, quotes stripped); else it is left alone and said
#   ... -TaskName <name>                a different task name (default: Mantle Native)
#   ... -User <DOMAIN\name>             the account the task runs as (default: the current one)
#   ... -Log <path>                     the --log the task passes (default: %APPDATA%\mantle\mantle-native.log)
#   ... -DryRun                         print the task XML and every schtasks command; change NOTHING
#   ... -AssumeTask <x>                 -DryRun only, for tests: pretend the task is '<none>' (absent), '<no-exec>'
#                                       (no Exec action) or has these Exec commands ('|' between them)
#
# The task is Electron's tools/mantle-logon-task.xml, setting for setting (15 s after logon, InteractiveToken,
# LeastPrivilege, IgnoreNew, no battery stops, StartWhenAvailable, PT0S, priority 4, RestartOnFailure 1 min x3:
# read that file for why), with the native action: "<exe>" --live "--log=<log>" in the exe's folder.
# Its name is NOT Electron's "Mantle" (Mantle.Core/Boot/LogonTask.cs says why), and the XML here must stay
# the same as LogonTask.Xml: LogonTaskScriptTests compares this script's -DryRun output with it.
#
# Windows PowerShell 5.1 on purpose (no ternary, no '&&', no '?.'): pwsh is not on every box.
# Stop a running native Mantle cleanly with `MantleNative.exe --quit`, never `schtasks /End` (a kill).
param(
  [string]$Exe = '',
  [switch]$Uninstall,
  [string]$OnlyIfExe = '',
  [string]$TaskName = 'Mantle Native',
  [string]$Log = '',
  [string]$User = '',
  [switch]$DryRun,
  [string]$AssumeTask = ''
)
$ErrorActionPreference = 'Stop'

function J([string]$a, [string]$b) { return [System.IO.Path]::Combine($a, $b) }
function Parent([string]$p) { return [System.IO.Path]::GetDirectoryName($p) }
function Say([string]$text) { Write-Output $text }
function Fail([string]$text) { Write-Output "install-autostart: $text"; exit 1 }
function Esc([string]$s) { return [System.Security.SecurityElement]::Escape($s) }
# How a command line for schtasks is shown in a dry run (each value with a space quoted).
function Show([string[]]$argv) {
  $parts = @('schtasks.exe')
  foreach ($a in $argv) { if ($a -match '\s') { $parts += '"' + $a + '"' } else { $parts += $a } }
  return ($parts -join ' ')
}
function Run-Schtasks([string[]]$argv, [string]$what) {
  if ($DryRun) { Say ("would run: " + (Show $argv)); return }
  $out = & schtasks.exe @argv 2>&1
  if ($LASTEXITCODE -ne 0) { Fail "schtasks $what failed: $($out -join ' ')" }
}

# The OnlyIfExe rule (LogonTask.NormaliseCommand / NormaliseWanted / OnlyIfExe in C#, unit-tested there):
# a command that is empty or not a full path never matches (Task Scheduler would look it up on PATH).
function Norm-Command([string]$c) {
  if ($null -eq $c) { return $null }
  $c = $c.Trim().Trim('"').Trim()
  if ($c.Length -eq 0) { return $null }
  if (-not ($c -match '^[A-Za-z]:[\\/]' -or $c -match '^[\\/]{2}')) { return $null }
  try { return [System.IO.Path]::GetFullPath($c).ToLowerInvariant() } catch { return $null }
}
function Norm-Wanted([string]$e) {
  $e = $e.Trim().Trim('"').Trim()
  return [System.IO.Path]::GetFullPath([System.IO.Path]::Combine((Get-Location).ProviderPath, $e)).ToLowerInvariant()
}

if ($AssumeTask -and -not $DryRun) { Fail "-AssumeTask is for -DryRun only (it pretends what the Task Scheduler holds)" }
if ($DryRun) { Say "install-autostart: DRY RUN, nothing is changed" }

if ($Uninstall -and $OnlyIfExe) {
  $cmds = $null
  if ($AssumeTask) {
    if ($AssumeTask -eq '<none>') { $cmds = $null }
    elseif ($AssumeTask -eq '<no-exec>') { $cmds = @() }
    else { $cmds = @($AssumeTask -split '\|') }
    $found = ($AssumeTask -ne '<none>')
  } else {
    try {
      $svc = New-Object -ComObject Schedule.Service
      $svc.Connect()
    } catch { Fail "could not read the Task Scheduler: $($_.Exception.Message)" }
    $task = $null
    try { $task = $svc.GetFolder('\').GetTask($TaskName) } catch { $task = $null }
    $found = [bool]$task
    if ($found) { $cmds = @($task.Definition.Actions | Where-Object { $_.Type -eq 0 } | ForEach-Object { [string]$_.Path }) }
  }
  if (-not $found) { Say "no '$TaskName' logon task; nothing to remove"; exit 0 }
  $want = Norm-Wanted $OnlyIfExe
  $mine = @($cmds | Where-Object { (Norm-Command $_) -eq $want })
  if ($mine.Count -eq 0) {
    $starts = 'no program'
    if ($cmds.Count -gt 0) { $starts = $cmds -join ', ' }
    Say "left the '$TaskName' logon task alone: it starts $starts, not $OnlyIfExe"
    exit 0
  }
  Run-Schtasks @('/Delete', '/TN', $TaskName, '/F') '/Delete'
  if ($DryRun) { Say "would have removed the '$TaskName' logon task (it starts $OnlyIfExe)"; exit 0 }
  Say "removed the '$TaskName' logon task (it started $OnlyIfExe)"
  exit 0
}

if ($Uninstall) {
  # Native has no banner or Windows-pieces journal of its own yet, so this only takes away the task.
  Run-Schtasks @('/Delete', '/TN', $TaskName, '/F') '/Delete'
  if ($DryRun) { Say "would have removed the '$TaskName' logon task (a running Mantle is untouched)"; exit 0 }
  Say "removed the '$TaskName' logon task; native Mantle will not start at logon any more (a running Mantle is untouched)"
  exit 0
}

if (-not $Exe) { Fail "-Exe <path to MantleNative.exe> is required (the task runs that exe --live)" }
$full = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine((Get-Location).ProviderPath, $Exe))
if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { Fail "no MantleNative.exe at $full" }
$root = Parent $full
if (-not $Log) { $Log = J (J $env:APPDATA 'mantle') 'mantle-native.log' }
$account = $User
if (-not $account) { $account = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name }

# Every value XML-escaped: the folder is under the user's profile, whose name may hold an & or an apostrophe.
$u = Esc $account; $e = Esc $full; $r = Esc $root; $a = Esc ('--live "--log=' + $Log + '"')
$lines = @(
  '<?xml version="1.0" encoding="UTF-16"?>',
  '<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">',
  '  <RegistrationInfo>',
  ('    <Description>Mantle (native), the user&apos;s shell layer over Windows: starts 15 s after logon. Stop it cleanly with &quot;' + $e + '&quot; --quit; put Windows back without Mantle with tools\restore-windows.ps1 next to it. Registered by native/tools/install-autostart.ps1.</Description>'),
  ('    <URI>' + (Esc ('\' + $TaskName)) + '</URI>'),
  '  </RegistrationInfo>',
  '  <Triggers>',
  '    <LogonTrigger>',
  '      <Enabled>true</Enabled>',
  ('      <UserId>' + $u + '</UserId>'),
  '      <Delay>PT15S</Delay>',
  '    </LogonTrigger>',
  '  </Triggers>',
  '  <Principals>',
  '    <Principal id="Author">',
  ('      <UserId>' + $u + '</UserId>'),
  '      <LogonType>InteractiveToken</LogonType>',
  '      <RunLevel>LeastPrivilege</RunLevel>',
  '    </Principal>',
  '  </Principals>',
  '  <Settings>',
  '    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>',
  '    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>',
  '    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>',
  '    <AllowHardTerminate>true</AllowHardTerminate>',
  '    <StartWhenAvailable>true</StartWhenAvailable>',
  '    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>',
  '    <IdleSettings>',
  '      <StopOnIdleEnd>false</StopOnIdleEnd>',
  '      <RestartOnIdle>false</RestartOnIdle>',
  '    </IdleSettings>',
  '    <AllowStartOnDemand>true</AllowStartOnDemand>',
  '    <Enabled>true</Enabled>',
  '    <Hidden>false</Hidden>',
  '    <RunOnlyIfIdle>false</RunOnlyIfIdle>',
  '    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>',
  '    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>',
  '    <WakeToRun>false</WakeToRun>',
  '    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>',
  '    <Priority>4</Priority>',
  '    <RestartOnFailure>',
  '      <Interval>PT1M</Interval>',
  '      <Count>3</Count>',
  '    </RestartOnFailure>',
  '  </Settings>',
  '  <Actions Context="Author">',
  '    <Exec>',
  ('      <Command>' + $e + '</Command>'),
  ('      <Arguments>' + $a + '</Arguments>'),
  ('      <WorkingDirectory>' + $r + '</WorkingDirectory>'),
  '    </Exec>',
  '  </Actions>',
  '</Task>'
)
$xml = ($lines -join "`r`n") + "`r`n"
$tmp = J $env:TEMP 'mantle-native-logon-task.xml'

if ($DryRun) {
  Say "would write $tmp (UTF-16 LE with BOM):"
  Say "----- task xml -----"
  foreach ($l in $lines) { Say $l }
  Say "----- end task xml -----"
  Run-Schtasks @('/Create', '/TN', $TaskName, '/XML', $tmp, '/F') '/Create'
  Say "would delete $tmp"
  Say "would register the '$TaskName' logon task for $account"
  Say "  runs: `"$full`" --live `"--log=$Log`""
  Say "  in:   $root, 15 s after logon, no time limit"
  exit 0
}

# UTF-16 LE with a BOM, to match the declaration schtasks expects.
[System.IO.File]::WriteAllText($tmp, $xml, (New-Object System.Text.UnicodeEncoding($false, $true)))
try {
  Run-Schtasks @('/Create', '/TN', $TaskName, '/XML', $tmp, '/F') '/Create'
} finally {
  Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
}
Say "registered the '$TaskName' logon task for $account"
Say "  runs: `"$full`" --live `"--log=$Log`""
Say "  in:   $root, 15 s after logon, no time limit"
Say "  log:  $Log"
exit 0
