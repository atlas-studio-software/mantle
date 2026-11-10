<#
  native/tools/desk/desk.ps1 - the runner desktop's state between the GUI stages of native/tools/ci.ps1.

    pwsh -NoProfile -File native/tools/desk/desk.ps1 -Reset -Tag <stage> [-Keep <pid,...>]
        prints "DESK <tag> before: <snapshot>", puts the desktop back (leftover test apps closed, taskbar on
        auto-hide and out of the way, a "System Properties" dialog closed, every app window minimized, the foreground on the desktop, the cursor
        mid-screen) and prints "DESK <tag> reset: <what it did>" and "DESK <tag> after: <snapshot>".
    pwsh -NoProfile -File native/tools/desk/desk.ps1 -Snapshot -Tag <t>     one "DESK <t>: <snapshot>" line
    pwsh -NoProfile -File native/tools/desk/desk.ps1 -Watch -Log <file>     runs until killed: one line per
        attention request (FLASH), activation and taskbar move, with the UTC time (ci.ps1 summarizes it per stage)

  RUNNER ONLY: it changes the desktop it runs on, so it refuses unless CI / GITHUB_ACTIONS is set.
  Desk.cs (beside it) holds the Win32 side; bench/run-bench.ps1 and tools/parity/run-parity.ps1 load it too.
#>
param(
  [switch]$Reset,
  [switch]$Snapshot,
  [switch]$Watch,
  [string]$Tag = 'desk',
  [string]$Log,
  [int[]]$Keep = @()
)
$ErrorActionPreference = 'Stop'
if (-not $env:CI -and -not $env:GITHUB_ACTIONS) { Write-Host "DESK ${Tag}: refused (not on a CI runner; it changes the desktop)"; exit 2 }
Add-Type -Path (Join-Path $PSScriptRoot 'Desk.cs')
[void][Desk]::DpiAware()

if ($Watch) {
  if (-not $Log) { throw '-Watch needs -Log <file>' }
  [Desk]::Watch($Log)
  exit 0
}
if ($Snapshot) { Write-Host "DESK ${Tag}: $([Desk]::Snapshot())"; exit 0 }
if ($Reset) {
  Write-Host "DESK $Tag before: $([Desk]::Snapshot())"
  $did = @()
  # Leftover test apps and fixtures of an earlier stage (by name: none of these is the runner's own).
  # The SystemProperties* tools are the "System Properties" dialog (one is sometimes left open on the runner;
  # Desk.Reset also closes that dialog by its title, whatever process hosts it).
  $names = 'notepad', 'charmap', 'MantleNative', 'WerFault', 'WerFaultSecure',
    'SystemPropertiesAdvanced', 'SystemPropertiesComputerName', 'SystemPropertiesPerformance', 'SystemPropertiesProtection',
    'SystemPropertiesRemote', 'SystemPropertiesHardware', 'SystemPropertiesDataExecutionPrevention'
  $left = @(Get-Process -Name $names -ErrorAction SilentlyContinue | Where-Object { $Keep -notcontains $_.Id })
  foreach ($p in $left) { try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { } }
  if ($left.Count) { $did += 'stopped ' + (($left | ForEach-Object { "$($_.ProcessName) $($_.Id)" }) -join ', ') }
  # A crash must never leave a dialog on the desktop (it takes the foreground and lights the taskbar).
  $wer = 'HKCU:\Software\Microsoft\Windows\Windows Error Reporting'
  New-Item -Force -Path $wer -ErrorAction SilentlyContinue | Out-Null
  Set-ItemProperty -Path $wer -Name DontShowUI -Value 1 -Type DWord
  if ($left.Count) { Start-Sleep -Milliseconds 500 }
  $did += [Desk]::Reset([int[]]$Keep, $true)
  Write-Host "DESK $Tag reset: $($did -join '; ')"
  Write-Host "DESK $Tag after: $([Desk]::Snapshot())"
  exit 0
}
Write-Host 'usage: desk.ps1 -Reset|-Snapshot|-Watch [-Tag t] [-Log f] [-Keep pids]'
exit 1
