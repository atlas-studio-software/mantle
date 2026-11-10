<#
  Run the core unit tests (or a filtered part of them) many times in one job, to tell a flaky test from a steady one.

    pwsh -NoProfile -File native/tools/test-loop.ps1 [-Runs 25] [-Filter "FullyQualifiedName~Look.LookShellTests"] [-Out native/out]

  Builds tests/Mantle.Core.Tests once (Release), then runs `dotnet test --no-build` -Runs times. Each run's trx is kept
  under <Out>/test-loop/run-<n>.trx; for a failed test the message (first 60 lines) is printed. The last line is the
  summary to judge by:

    TEST-LOOP: <clean> of <runs> runs clean (<filter>); failures: <test> x<count>, ...

  An empty filter runs the whole suite in parallel, the way ci.ps1's unit stage does (the load some flakes need).
  Never touches the user's data: the unit tests use scratch folders only.
#>
param(
  [int]$Runs = 25,
  [string]$Filter = '',
  [string]$Out = ''
)
$ErrorActionPreference = 'Stop'
$native = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $native 'out' }
if (-not [System.IO.Path]::IsPathRooted($Out)) { $Out = Join-Path (Split-Path -Parent $native) $Out }
$dir = Join-Path $Out 'test-loop'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$env:DOTNET_NOLOGO = '1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$proj = Join-Path $native 'tests\Mantle.Core.Tests\Mantle.Core.Tests.csproj'
$what = if ($Filter) { $Filter } else { 'whole suite' }

Push-Location $native   # native/global.json pins the SDK line
try {
  Write-Host "test-loop: building $proj (Release)"
  & dotnet build $proj -c Release -nologo -clp:'Summary;ErrorsOnly' *> (Join-Path $dir 'build.log')
  if ($LASTEXITCODE -ne 0) {
    Get-Content (Join-Path $dir 'build.log') -Tail 30 | ForEach-Object { Write-Host "    $_" }
    Write-Host "TEST-LOOP: FAILED (the test project did not build, exit $LASTEXITCODE)"
    exit 1
  }
  $clean = 0
  $fails = [ordered]@{}
  # the look tests stamp each 1 s promise they time (MANTLE_TEST_TIMINGS: "<promise>\t<ms>" per line); summed at the end
  $timings = Join-Path $dir 'timings.tsv'
  Remove-Item -Force -ErrorAction SilentlyContinue $timings
  $env:MANTLE_TEST_TIMINGS = $timings
  for ($i = 1; $i -le $Runs; $i++) {
    $trx = "run-$i.trx"
    $argv = @('test', $proj, '-c', 'Release', '--no-build', '-nologo', '--logger', "trx;LogFileName=$trx", '--results-directory', $dir)
    if ($Filter) { $argv += @('--filter', $Filter) }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & dotnet @argv *> (Join-Path $dir "run-$i.log")
    $path = Join-Path $dir $trx
    if (-not (Test-Path $path)) {
      Write-Host ("RUN {0}: no test results (dotnet test exit {1})" -f $i, $LASTEXITCODE)
      $fails['(no results)'] = 1 + [int]$fails['(no results)']
      continue
    }
    [xml]$x = Get-Content $path -Raw
    $c = $x.TestRun.ResultSummary.Counters
    $bad = @($x.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' })
    Write-Host ("RUN {0}: {1}/{2} passed, {3} failed [{4:n1}s]" -f $i, $c.passed, $c.total, $c.failed, $sw.Elapsed.TotalSeconds)
    if ($bad.Count -eq 0 -and [int]$c.total -gt 0) { $clean++ }
    foreach ($b in $bad) {
      $fails[$b.testName] = 1 + [int]$fails[$b.testName]
      Write-Host "  FAIL $($b.testName)"
      $msg = [string]$b.Output.ErrorInfo.Message
      ($msg -split "`n" | Select-Object -First 60) | ForEach-Object { Write-Host "    | $($_.TrimEnd())" }
    }
  }
} finally { Pop-Location; $env:MANTLE_TEST_TIMINGS = $null }

if (Test-Path $timings) {
  $groups = Get-Content $timings | Where-Object { $_ -match "`t" } | ForEach-Object { $p = $_ -split "`t"; [pscustomobject]@{ What = $p[0]; Ms = [int]$p[1] } } | Group-Object What
  foreach ($g in $groups) {
    $ms = @($g.Group.Ms | Sort-Object)
    Write-Host ("TIMING {0}: n={1}, median {2} ms, p95 {3} ms, max {4} ms" -f $g.Name, $ms.Count, $ms[[int][math]::Floor(($ms.Count - 1) / 2)], $ms[[int][math]::Floor(($ms.Count - 1) * 0.95)], $ms[-1])
  }
}

$list = if ($fails.Count) { ($fails.GetEnumerator() | ForEach-Object { "$($_.Key) x$($_.Value)" }) -join ', ' } else { 'none' }
Write-Host "TEST-LOOP: $clean of $Runs runs clean ($what); failures: $list"
if ($clean -ne $Runs) { exit 1 }
