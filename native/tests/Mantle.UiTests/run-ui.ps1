# run-ui.ps1 - publish the native app if needed, then run the UI automation tests against it.
#
#   pwsh -NoProfile -File native/tests/Mantle.UiTests/run-ui.ps1 [-Exe <path>] [-Out native/out/ui] [-Filter <dotnet test filter>]
#
# RUNNER ONLY: the tests refuse (Inconclusive) unless CI / GITHUB_ACTIONS is set or MANTLE_UITEST_LOCAL=1.
# Writes ui.trx, test.log and a screenshot per failing test to -Out. Last line: UITEST: ok (...) or UITEST: FAILED (...).
param([string]$Exe, [string]$Out, [string]$Filter)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
if (-not $Out) { $Out = Join-Path $root 'native\out\ui' }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path
function Say($m) { Write-Host ("uitest {0}: {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) }

if (-not $Exe) { $Exe = $env:MANTLE_NATIVE_EXE }
$bin = Join-Path $root 'native\out\bin\MantleNative'
if (-not $Exe -or -not (Test-Path $Exe)) {
  $pick = { Get-ChildItem -Path $bin -Filter *.exe -ErrorAction SilentlyContinue | Where-Object { $_.Name -in 'MantleNative.exe', 'MantleBar.exe' } | Select-Object -First 1 }
  $found = & $pick
  if (-not $found) {
    $pub = Join-Path $root 'native\tools\publish.ps1'; $plog = Join-Path $Out 'publish.log'
    Say "publishing the native app to $bin"
    if (Test-Path $pub) { & pwsh -NoProfile -File $pub -Mode sc -Out $bin *> $plog }
    else { & dotnet publish (Join-Path $root 'native\src\Mantle.App\Mantle.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $bin *> $plog }
    Say "publish exit $LASTEXITCODE"
    $found = & $pick
  }
  if ($found) { $Exe = $found.FullName }
}
if (-not $Exe -or -not (Test-Path $Exe)) { Write-Host "UITEST: FAILED (no native exe; see $Out\publish.log)"; exit 3 }
Say "app $Exe"
$env:MANTLE_NATIVE_EXE = $Exe
$env:MANTLE_UITEST_OUT = $Out
$env:MANTLE_TEST_VISIBLE = '1'

$proj = Join-Path $PSScriptRoot 'Mantle.UiTests.csproj'
$targs = @('test', $proj, '-c', 'Release', '--logger', 'trx;LogFileName=ui.trx', '--logger', 'console;verbosity=normal', '--results-directory', $Out)
if ($Filter) { $targs += @('--filter', $Filter) }
$log = Join-Path $Out 'test.log'
& dotnet @targs *>&1 | Tee-Object -FilePath $log | Select-String -Pattern '^\s*(Passed|Failed|Skipped)\s|error|Total tests|Passed!|Failed!' | ForEach-Object { $_.Line }
$code = $LASTEXITCODE

$trx = Join-Path $Out 'ui.trx'
if (Test-Path $trx) {
  [xml]$x = Get-Content $trx -Raw
  $c = $x.TestRun.ResultSummary.Counters
  $failed = @($x.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -ne 'Passed' } | ForEach-Object { "$($_.testName) [$($_.outcome)]" })
  $line = "total $($c.total), passed $($c.passed), failed $($c.failed), not run $([int]$c.total - [int]$c.passed - [int]$c.failed)"
  if ($code -eq 0 -and [int]$c.failed -eq 0 -and [int]$c.passed -gt 0) { Write-Host "UITEST: ok ($line)"; exit 0 }
  Write-Host ("UITEST: FAILED ($line" + $(if ($failed.Count) { '; ' + ($failed -join '; ') } else { '' }) + ")")
  exit 1
}
Write-Host "UITEST: FAILED (dotnet test exit $code, no trx: build error? see $log)"
exit 1
