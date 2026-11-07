# check.ps1 - build sidecar/*.cs LINKED under modern .NET (Mantle.Sidecar.csproj) and say what breaks.
#
# Runs on a Windows box with the .NET SDK (the cloud runner; nothing here is meant for the laptop).
#
#   pwsh -NoProfile -File native/src/Mantle.Sidecar/check.ps1            build the 3 variants (+ tests when they build)
#   pwsh -NoProfile -File native/src/Mantle.Sidecar/check.ps1 -Compare   also build the csc exe (sidecar/build.ps1
#                                                                        -NoUnion) and the .NET exe, and compare
#                                                                        them over stdio in the test project
#
# Writes compact summaries to native/out/sidecar/ (per variant: errors.txt, by-code.txt, by-file.txt,
# warnings-by-code.txt; tests/ for the test run) and ends with ONE line:
#   SIDECAR-LINK: ok (...)  |  SIDECAR-LINK: FAILED (...)
param(
  [switch]$Compare,
  [string[]]$Variants = @('winmd', 'winrt', 'none'),
  [switch]$NoTests
)
$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = (Resolve-Path (Join-Path $here '..\..\..')).Path
$out  = Join-Path $root 'native\out\sidecar'
$bin  = Join-Path $here 'bin'
New-Item -ItemType Directory -Force $out | Out-Null
$proj = Join-Path $here 'Mantle.Sidecar.csproj'
$testProj = Join-Path $root 'native\tests\Mantle.Sidecar.Tests\Mantle.Sidecar.Tests.csproj'
$log = New-Object System.Collections.Generic.List[string]
function Say([string]$s) { Write-Output $s; $log.Add($s) }
function Rel([string]$p) {
  if (-not $p) { return $p }
  $p = $p.Trim()
  if ($p.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { $p = $p.Substring($root.Length).TrimStart('\', '/') }
  return ($p -replace '\\', '/')
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
Say ("dotnet " + (dotnet --version))
Say ("sdks: " + ((dotnet --list-sdks | ForEach-Object { ($_ -split ' ')[0] }) -join ', '))

# One msbuild diagnostic line -> object. Lines look like
#   D:\a\...\sidecar\Foo.cs(12,5): error CS0234: text [D:\a\...\Mantle.Sidecar.csproj]
#   ...\Microsoft.NET.Sdk.targets(10,5): error NETSDK1004: text [proj]
#   error NU1101: text [proj]
function Parse-Diag([string]$line, [string]$kind) {
  $m = [regex]::Match($line, '^\s*(?<file>.+?)\((?<ln>\d+),(?<col>\d+)(?:,\d+,\d+)?\): ' + $kind + ' (?<code>[A-Za-z]+\d+): (?<msg>.*?)(?: \[(?<proj>[^\]]+)\])?\s*$')
  if ($m.Success) {
    return [pscustomobject]@{ File = (Rel $m.Groups['file'].Value); Line = [int]$m.Groups['ln'].Value; Col = [int]$m.Groups['col'].Value; Code = $m.Groups['code'].Value; Msg = $m.Groups['msg'].Value }
  }
  $m = [regex]::Match($line, '(?:^|: )' + $kind + ' (?<code>[A-Za-z]+\d+): (?<msg>.*?)(?: \[(?<proj>[^\]]+)\])?\s*$')
  if ($m.Success) {
    return [pscustomobject]@{ File = '(no file)'; Line = 0; Col = 0; Code = $m.Groups['code'].Value; Msg = $m.Groups['msg'].Value }
  }
  return $null
}

function Summarise($diags, [string]$dir, [string]$kind) {
  $diags = @($diags | Sort-Object File, Line, Col, Code -Unique)
  $lines = $diags | ForEach-Object { '{0}({1},{2}): {3}: {4}' -f $_.File, $_.Line, $_.Col, $_.Code, $_.Msg }
  Set-Content -Encoding utf8 -Path (Join-Path $dir ($kind + 's.txt')) -Value $lines
  $byCode = $diags | Group-Object Code | Sort-Object Count -Descending | ForEach-Object {
    $files = ($_.Group | Group-Object { Split-Path -Leaf $_.File } | Sort-Object Count -Descending | ForEach-Object { '{0}x{1}' -f $_.Name, $_.Count }) -join ' '
    '{0,-10} {1,5}  {2}  | e.g. {3}' -f $_.Name, $_.Count, $files, $_.Group[0].Msg
  }
  Set-Content -Encoding utf8 -Path (Join-Path $dir ($kind + 's-by-code.txt')) -Value $byCode
  $byFile = $diags | Group-Object File | Sort-Object Count -Descending | ForEach-Object {
    $codes = ($_.Group | Group-Object Code | ForEach-Object { '{0}x{1}' -f $_.Name, $_.Count }) -join ' '
    '{0,-50} {1,5}  {2}' -f $_.Name, $_.Count, $codes
  }
  Set-Content -Encoding utf8 -Path (Join-Path $dir ($kind + 's-by-file.txt')) -Value $byFile
  return , $diags
}

# --------------------------------------------------------------------- the library, per variant
$results = [ordered]@{}
foreach ($v in $Variants) {
  $vdir = Join-Path $out $v
  New-Item -ItemType Directory -Force $vdir | Out-Null
  $errLog = Join-Path $vdir 'msbuild-errors.log'
  $wrnLog = Join-Path $vdir 'msbuild-warnings.log'
  $outDir = Join-Path $bin $v
  $t0 = Get-Date
  dotnet build $proj -c Release --no-incremental -nologo "-p:SidecarVariant=$v" "-p:OutDir=$outDir" `
    '-p:TreatWarningsAsErrors=false' '-clp:ErrorsOnly;NoSummary' `
    "-flp1:errorsonly;logfile=$errLog" "-flp2:warningsonly;logfile=$wrnLog" | Out-Null
  $code = $LASTEXITCODE
  $secs = [int]((Get-Date) - $t0).TotalSeconds
  $errs = @(Get-Content $errLog -ErrorAction SilentlyContinue | ForEach-Object { Parse-Diag $_ 'error' } | Where-Object { $_ })
  $wrns = @(Get-Content $wrnLog -ErrorAction SilentlyContinue | ForEach-Object { Parse-Diag $_ 'warning' } | Where-Object { $_ })
  $errs = Summarise $errs $vdir 'error'
  $wrns = Summarise $wrns $vdir 'warning'
  $errFiles = @($errs | Select-Object -ExpandProperty File -Unique).Count
  $ok = ($code -eq 0)
  $results[$v] = [pscustomobject]@{ Ok = $ok; Errors = $errs.Count; ErrFiles = $errFiles; Warnings = $wrns.Count }
  Say ('variant {0}: {1} (exit {2}, {3} s): {4} errors in {5} files, {6} warnings' -f $v, ($(if ($ok) { 'BUILT' } else { 'FAILED' })), $code, $secs, $errs.Count, $errFiles, $wrns.Count)
  if ($errs.Count) {
    Get-Content (Join-Path $vdir 'errors-by-code.txt') | Select-Object -First 40 | ForEach-Object { Say ('  ' + $(if ($_.Length -gt 260) { $_.Substring(0, 260) + '...' } else { $_ })) }
  }
  if ($wrns.Count) {
    Say '  warnings by code:'
    Get-Content (Join-Path $vdir 'warnings-by-code.txt') | Select-Object -First 25 | ForEach-Object { Say ('    ' + $(if ($_.Length -gt 200) { $_.Substring(0, 200) + '...' } else { $_ })) }
  }
}

# How many sidecar files went in linked, and how many shims of ours.
$linked = 0; $shims = 0
try {
  $items = (dotnet msbuild $proj -nologo '-getItem:Compile' | Out-String | ConvertFrom-Json).Items.Compile
  foreach ($i in $items) { if ($i.Link -like 'sidecar*') { $linked++ } else { $shims++ } }
} catch { Say ('could not list the Compile items: ' + $_.Exception.Message) }
$total = @(Get-ChildItem (Join-Path $root 'sidecar\*.cs')).Count
Say ('compile items: {0} of {1} sidecar/*.cs linked, {2} shim files' -f $linked, $total, $shims)

# --------------------------------------------------------------------- exe + tests
$testLine = 'tests not run'
$primaryOk = $results.Contains('winmd') -and $results['winmd'].Ok
if ($primaryOk -and -not $NoTests -and (Test-Path $testProj)) {
  $netExe = $null; $cscExe = $null
  if ($Compare) {
    $exeDir = Join-Path $bin 'exe'
    dotnet build $proj -c Release -nologo '-p:SidecarVariant=winmd' '-p:SidecarExe=true' --no-incremental "-p:OutDir=$exeDir" '-clp:ErrorsOnly;NoSummary' | Select-Object -Last 15 | ForEach-Object { Say ('  exe: ' + $_) }
    if ($LASTEXITCODE -eq 0) { $netExe = Join-Path $exeDir 'Mantle.Sidecar.exe'; Say 'net exe: built (winmd variant)' } else { Say 'net exe: build FAILED' }
    $cscOut = Join-Path $bin 'csc\Mantle.Sidecar.exe'
    $cscLog = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'sidecar\build.ps1') -NoUnion -Out $cscOut 2>&1
    if ($LASTEXITCODE -eq 0 -and (Test-Path $cscOut)) { $cscExe = $cscOut; Say ('csc exe: built (-NoUnion = MANTLE_WINMD, the shipped variant): ' + (@($cscLog) | Select-Object -Last 1)) }
    else { Say 'csc exe: build FAILED'; @($cscLog) | Select-Object -Last 10 | ForEach-Object { Say ('  csc: ' + $_) } }
  }
  $tdir = Join-Path $out 'tests'
  New-Item -ItemType Directory -Force $tdir | Out-Null
  $env:MANTLE_SIDECAR_OUT = $tdir
  if ($netExe) { $env:MANTLE_SIDECAR_NET_EXE = $netExe }
  if ($cscExe) { $env:MANTLE_SIDECAR_CSC_EXE = $cscExe }
  $tlog = dotnet test $testProj -c Release -nologo '-p:SidecarVariant=winmd' --logger 'trx;LogFileName=sidecar-tests.trx' --logger 'console;verbosity=normal' --results-directory $tdir 2>&1
  $tcode = $LASTEXITCODE
  Set-Content -Encoding utf8 -Path (Join-Path $tdir 'dotnet-test.log') -Value $tlog
  @($tlog) | Where-Object { $_ -match '^\s*(Passed|Failed|Skipped)\s' -or $_ -match 'error' } | Select-Object -First 60 | ForEach-Object { Say ('  ' + $_.ToString().Trim()) }
  $sumLine = @($tlog) | Where-Object { $_ -match '(Passed|Failed)!\s+-\s+Failed:' } | Select-Object -Last 1
  if (-not $sumLine) { $sumLine = @($tlog) | Where-Object { $_ -match 'Total tests|Test summary' } | Select-Object -Last 1 }
  $testLine = $(if ($sumLine) { ($sumLine.ToString().Trim() -replace '\s+', ' ') } else { 'test run produced no summary line (exit ' + $tcode + ')' })
  Say ('tests: ' + $testLine)
  $cmp = Join-Path $tdir 'compare.txt'
  if (Test-Path $cmp) { Get-Content $cmp | Select-Object -First 60 | ForEach-Object { Say ('  compare: ' + $_) } }
} elseif (-not $primaryOk) {
  $testLine = 'tests not run (the winmd variant did not build)'
}

# --------------------------------------------------------------------- verdict
$parts = @()
foreach ($k in $results.Keys) {
  $r = $results[$k]
  $parts += $(if ($r.Ok) { $k + ' built' } else { '{0}: {1} errors in {2} files' -f $k, $r.Errors, $r.ErrFiles })
}
$allOk = @($results.Values | Where-Object { -not $_.Ok }).Count -eq 0
$testsOk = $testLine -match '^Passed!' -or ($testLine -match 'Failed: 0,')
$verdict = '{0}; {1} of {2} sidecar files linked, {3} shims; {4}' -f ($parts -join ', '), $linked, $total, $shims, $testLine
Set-Content -Encoding utf8 -Path (Join-Path $out 'summary.txt') -Value $log
if ($allOk -and ($NoTests -or $testsOk)) { Write-Output ('SIDECAR-LINK: ok (' + $verdict + ')'); exit 0 }
Write-Output ('SIDECAR-LINK: FAILED (' + $verdict + ')')
exit 1
