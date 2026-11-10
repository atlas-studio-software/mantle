<#
  The launcher's keyboard UI tests run N times over (lane native-launcher): proof that the arrow-after-typing race is gone.
    pwsh -NoProfile -File native/tools/parity/launcher/repeat-keys.ps1 [-Times 5] [-Exe <MantleNative.exe>]
  Prints one "KEYS-REPEAT <i>: <UITEST line>" per round and a summary "KEYS-REPEAT: ok|FAILED (n of N rounds passed)".
  RUNNER ONLY (it drives a visible test window with the real keyboard).
#>
param([int]$Times = 5, [string]$Exe)
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
if (-not $env:CI -and -not $env:GITHUB_ACTIONS) { Write-Host 'KEYS-REPEAT: FAILED (refused: not on a CI runner)'; exit 2 }
if (-not $Exe) { $Exe = Join-Path $root 'native\out\bin\MantleNative\MantleNative.exe' }
if (-not (Test-Path $Exe)) { Write-Host "KEYS-REPEAT: FAILED (no native exe at $Exe)"; exit 1 }
$ok = 0
for ($i = 1; $i -le $Times; $i++) {
  $log = Join-Path $root "native\out\parity\launcher\keys-repeat-$i.log"
  & pwsh -NoProfile -File (Join-Path $root 'native\tests\Mantle.UiTests\run-ui.ps1') -Exe $Exe -Filter 'FullyQualifiedName~Typing_shows_the_fixture_rows|FullyQualifiedName~A_key_right_after_typing' *> $log
  $line = Get-Content $log | Where-Object { $_ -match 'UITEST:' } | Select-Object -Last 1
  Get-Content $log | Where-Object { $_ -match '^\s+Failed ' -or $_ -match 'did not move|moved back' } | ForEach-Object { Write-Host "  $($_.Trim())" }
  if ($line -match 'UITEST: ok') { $ok++ }
  Write-Host "KEYS-REPEAT ${i}: $line"
}
Write-Host ("KEYS-REPEAT: {0} ({1} of {2} rounds passed)" -f $(if ($ok -eq $Times) { 'ok' } else { 'FAILED' }), $ok, $Times)
exit $(if ($ok -eq $Times) { 0 } else { 1 })
