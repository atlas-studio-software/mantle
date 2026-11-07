<#
  repeat-ui.ps1 - run one slice of the native UI tests several times in a row, to tell a flaky test from a fixed one.

  Lane native-bar-tasks. For a CI runner (cloudtest's mantle-native-public recipe, command override):
    pwsh -NoProfile -File native/tools/parity/bar-tasks/repeat-ui.ps1 -Build -Filter "FullyQualifiedName~BarTasks.StripTests" -Times 6

  -Build runs native/tools/ci.ps1 -Stages build first (the published exe the UI tests start). Each round resets the
  desk (desk.ps1 -Reset, runner only) and runs `dotnet test --filter <Filter>` with its own trx in
  native/out/ui/repeat-<n>/. The last line is the summary: "UI-REPEAT: <clean> of <rounds> rounds clean (...)".
  Exit 0 only when every round was clean.
#>
param(
  [string]$Filter = 'FullyQualifiedName~BarTasks.StripTests',
  [int]$Times = 6,
  [switch]$Build,
  [string]$Out = 'native/out'
)
$ErrorActionPreference = 'Stop'
$tools = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$native = Split-Path -Parent $tools
$repo = Split-Path -Parent $native
Set-Location $repo
$exe = Join-Path $Out 'bin\MantleNative\MantleNative.exe'
$deskPs = Join-Path $tools 'desk\desk.ps1'
$onRunner = [bool]($env:CI -or $env:GITHUB_ACTIONS)

if ($Build) {
  & pwsh -NoProfile -File (Join-Path $tools 'ci.ps1') -Stages build -Out $Out
  if ($LASTEXITCODE -ne 0) { Write-Host "UI-REPEAT: FAILED (the build stage failed, exit $LASTEXITCODE)"; exit 1 }
}
if (-not (Test-Path $exe)) { Write-Host "UI-REPEAT: FAILED (no published exe at $exe; pass -Build)"; exit 1 }
$proj = @(Get-ChildItem -Path (Join-Path $native 'tests\Mantle.UiTests') -Filter *.csproj)[0].FullName

$env:MANTLE_NATIVE_EXE = (Resolve-Path $exe).Path
$env:MANTLE_TEST_VISIBLE = '1'
$clean = 0; $rows = @()
for ($i = 1; $i -le $Times; $i++) {
  if ($onRunner -and (Test-Path $deskPs)) { & pwsh -NoProfile -File $deskPs -Reset -Tag "repeat-$i" 2>&1 | ForEach-Object { "  $_" } | Write-Host }
  $dir = Join-Path $Out "ui\repeat-$i"
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $t0 = Get-Date
  & dotnet test $proj -c Release -nologo --filter $Filter --logger 'trx;LogFileName=ui.trx' --results-directory $dir *> (Join-Path $dir 'test.log')
  $code = $LASTEXITCODE
  $trx = Join-Path $dir 'ui.trx'
  $row = if (Test-Path $trx) {
    [xml]$x = Get-Content $trx -Raw
    $c = $x.TestRun.ResultSummary.Counters
    $failed = @($x.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' } | ForEach-Object { $_.testName })
    if ([int]$c.failed -eq 0 -and $code -eq 0 -and [int]$c.total -gt 0) { $clean++ }
    "round ${i}: $($c.passed) of $($c.total) passed$(if ($failed.Count) { '; FAILED ' + ($failed -join ', ') })"
  } else { "round ${i}: no results (dotnet test exit $code)" }
  $row += " [$([int]((Get-Date) - $t0).TotalSeconds)s]"
  Write-Host "REPEAT $row"
  if (-not (Test-Path $trx) -or $code -ne 0) { Get-Content (Join-Path $dir 'test.log') | Select-String -Pattern 'Failed |Error Message|Assert\.' | Select-Object -First 8 | ForEach-Object { Write-Host ('    ' + $_.Line.Trim()) } }
  $rows += $row
}
$env:MANTLE_TEST_VISIBLE = $null
Write-Host "UI-REPEAT: $clean of $Times rounds clean (filter $Filter)"
exit $(if ($clean -eq $Times) { 0 } else { 1 })
