# check.ps1 - build sidecar/*.cs LINKED under modern .NET (Mantle.Sidecar.csproj) and say what breaks.
#
# Runs on a Windows box with the .NET SDK (the cloud runner; nothing here is meant for the laptop).
#
#   pwsh -NoProfile -File native/src/Mantle.Sidecar/check.ps1              the 3 variants + the tests
#   pwsh -NoProfile -File native/src/Mantle.Sidecar/check.ps1 -Compare     also build the csc exe
#        (sidecar/build.ps1 -NoUnion) and the .NET exe, and compare them over stdio in the tests
#   ... -Compare -ProveEdits   then apply sidecar-edits.psd1 to THIS checkout's sidecar/ (refused
#        outside CI: it is for the runner's throwaway checkout), prove the csc build sees the original
#        text, build csc (both routes) and .NET (3 variants, without the FileStream stopgap), run the
#        tests again, and put sidecar/ back.
#
# Writes compact summaries to native/out/sidecar/ (stock build) and native/out/sidecar/edited/
# (-ProveEdits): per variant errors.txt, errors-by-code.txt, errors-by-file.txt, warnings*.txt;
# tests/ for the test run (compare.txt, inproc.txt, the trx). Ends with ONE line:
#   SIDECAR-LINK: ok (...)  |  SIDECAR-LINK: FAILED (...)
param(
  [switch]$Compare,
  [switch]$ProveEdits,
  [string[]]$Variants = @('winmd', 'winrt', 'none'),
  [switch]$NoTests
)
$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = (Resolve-Path (Join-Path $here '..\..\..')).Path
$outRoot = Join-Path $root 'native\out\sidecar'
$bin  = Join-Path $here 'bin'
New-Item -ItemType Directory -Force $outRoot | Out-Null
$proj = Join-Path $here 'Mantle.Sidecar.csproj'
$testProj = Join-Path $root 'native\tests\Mantle.Sidecar.Tests\Mantle.Sidecar.Tests.csproj'
$sidecarDir = Join-Path $root 'sidecar'
$log = New-Object System.Collections.Generic.List[string]
function Say([string]$s) { Write-Host $s; [void]$log.Add($s) }   # Write-Host: inside Invoke-Pass, output would become its return value
function Cut([string]$s, [int]$n) { if ($s.Length -gt $n) { return $s.Substring(0, $n) + '...' } return $s }
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
#   1:7>D:\a\...\sidecar\Foo.cs(12,5): error CS0234: text [D:\a\...\Mantle.Sidecar.csproj]
#   ...\Microsoft.NET.Sdk.targets(10,5): error NETSDK1004: text [proj]
#   error NU1101: text [proj]
function Parse-Diag([string]$line, [string]$kind) {
  $line = $line -replace '^\s*\d+(:\d+)?>', ''   # the "1:7>" node prefix of a multi-node build
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

# Builds the variants, the exe and the tests once. $tag names the pass in the verdict; $props are
# extra msbuild properties (the edited pass adds SidecarEdited=true). Returns a verdict object.
function Invoke-Pass([string]$tag, [string]$out, [string[]]$props) {
  New-Item -ItemType Directory -Force $out | Out-Null
  $binTag = Join-Path $bin $tag
  Say ('==== pass: ' + $tag + $(if ($props.Count) { ' (' + ($props -join ' ') + ')' } else { '' }))
  $results = [ordered]@{}
  foreach ($v in $Variants) {
    $vdir = Join-Path $out $v
    New-Item -ItemType Directory -Force $vdir | Out-Null
    $errLog = Join-Path $vdir 'msbuild-errors.log'
    $wrnLog = Join-Path $vdir 'msbuild-warnings.log'
    $t0 = Get-Date
    dotnet build $proj -c Release --no-incremental -nologo "-p:SidecarVariant=$v" "-p:OutDir=$(Join-Path $binTag $v)" @props `
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
    if ($errs.Count) { Get-Content (Join-Path $vdir 'errors-by-code.txt') | Select-Object -First 40 | ForEach-Object { Say ('  ' + (Cut $_ 260)) } }
    if ($wrns.Count) {
      Say ('  warnings by code: ' + ((Get-Content (Join-Path $vdir 'warnings-by-code.txt') | ForEach-Object { ($_ -split '\s+')[0..1] -join 'x' }) -join ', '))
    }
  }

  # How many sidecar files went in linked, and how many files of ours.
  $linked = 0; $shims = 0
  try {
    $items = (dotnet msbuild $proj -nologo '-getItem:Compile' @props | Out-String | ConvertFrom-Json).Items.Compile
    foreach ($i in $items) { if ($i.Link -like 'sidecar*') { $linked++ } else { $shims++ } }
  } catch { Say ('could not list the Compile items: ' + $_.Exception.Message) }
  $total = @(Get-ChildItem (Join-Path $sidecarDir '*.cs')).Count
  Say ('compile items: {0} of {1} sidecar/*.cs linked, {2} shim files' -f $linked, $total, $shims)

  $testLine = 'tests not run'; $testsOk = $false; $diffs = 0
  $primaryOk = $results.Contains('winmd') -and $results['winmd'].Ok
  if ($primaryOk -and -not $NoTests -and (Test-Path $testProj)) {
    $env:MANTLE_SIDECAR_NET_EXE = $null; $env:MANTLE_SIDECAR_CSC_EXE = $null
    if ($Compare) {
      $exeDir = Join-Path $binTag 'exe'
      dotnet build $proj -c Release -nologo --no-incremental '-p:SidecarVariant=winmd' '-p:SidecarExe=true' "-p:OutDir=$exeDir" @props '-clp:ErrorsOnly;NoSummary' | Select-Object -Last 8 | Where-Object { $_ -match 'error' } | ForEach-Object { Say ('  exe: ' + (Cut $_ 240)) }
      if ($LASTEXITCODE -eq 0) { $env:MANTLE_SIDECAR_NET_EXE = Join-Path $exeDir 'Mantle.Sidecar.exe'; Say 'net exe: built (winmd variant)' } else { Say 'net exe: build FAILED' }
      $cscOut = Join-Path $binTag 'csc\Mantle.Sidecar.exe'
      $cscLog = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $sidecarDir 'build.ps1') -NoUnion -Out $cscOut 2>&1
      if ($LASTEXITCODE -eq 0 -and (Test-Path $cscOut)) { $env:MANTLE_SIDECAR_CSC_EXE = $cscOut; Say ('csc exe: built with -NoUnion (MANTLE_WINMD, the shipped variant): ' + (Cut ((@($cscLog) | Select-Object -Last 1) -replace [regex]::Escape($root), '.') 200)) }
      else { Say 'csc exe: build FAILED'; @($cscLog) | Select-Object -Last 10 | ForEach-Object { Say ('  csc: ' + $_) } }
    }
    $tdir = Join-Path $out 'tests'
    if (Test-Path $tdir) { Remove-Item -Recurse -Force $tdir }
    New-Item -ItemType Directory -Force $tdir | Out-Null
    $env:MANTLE_SIDECAR_OUT = $tdir
    $tlog = dotnet test $testProj -c Release -nologo '-p:SidecarVariant=winmd' @props --logger 'trx;LogFileName=sidecar-tests.trx' --logger 'console;verbosity=normal' --results-directory $tdir 2>&1
    $tcode = $LASTEXITCODE
    Set-Content -Encoding utf8 -Path (Join-Path $tdir 'dotnet-test.log') -Value $tlog
    @($tlog) | Where-Object { $_ -match '^\s*(Passed|Failed|Skipped)\s' -or $_ -match '\berror\b' } | Select-Object -First 40 | ForEach-Object { Say ('  ' + (Cut $_.ToString().Trim() 300)) }
    # The trx Counters element is the summary of record.
    $trx = Get-ChildItem $tdir -Recurse -Filter '*.trx' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($trx) {
      $c = ([xml](Get-Content $trx.FullName -Raw)).TestRun.ResultSummary.Counters
      $skipped = [int]$c.total - [int]$c.executed + [int]$c.inconclusive + [int]$c.notExecuted
      $testLine = 'tests {0}/{1} passed, {2} failed, {3} skipped or inconclusive' -f $c.passed, $c.total, $c.failed, $skipped
      $testsOk = ([int]$c.failed -eq 0 -and [int]$c.error -eq 0 -and [int]$c.passed -gt 0 -and $tcode -eq 0)
    } else { $testLine = 'tests: no trx (dotnet test exit ' + $tcode + ')' }
    Say $testLine
    foreach ($f in @('inproc.txt', 'compare.txt')) {
      $p = Join-Path $tdir $f
      if (Test-Path $p) { Get-Content $p | Select-Object -First 50 | ForEach-Object { Say ('  ' + $f.Replace('.txt', '') + ': ' + (Cut $_ 400)) } }
    }
    $cmp = Join-Path $tdir 'compare.txt'
    if (Test-Path $cmp) { $diffs = @(Get-Content $cmp | Where-Object { $_ -match 'DIFFER|but net|but csc|NO ANSWER|MISMATCH' }).Count }
  } elseif (-not $primaryOk) { $testLine = 'tests not run (the winmd variant did not build)' }

  $parts = @()
  foreach ($k in $results.Keys) { $r = $results[$k]; $parts += $(if ($r.Ok) { $k + ' built' } else { '{0}: {1} errors in {2} files' -f $k, $r.Errors, $r.ErrFiles }) }
  $allOk = @($results.Values | Where-Object { -not $_.Ok }).Count -eq 0
  return [pscustomobject]@{
    Ok = ($allOk -and ($NoTests -or $testsOk))
    Text = '{0}: {1}; {2}/{3} sidecar files linked, {4} shims; {5}{6}' -f $tag, ($parts -join ', '), $linked, $total, $shims, $testLine, $(if ($Compare) { '; ' + $diffs + ' behaviour differences in compare.txt' } else { '' })
  }
}

$verdicts = @()
$verdicts += Invoke-Pass 'stock' $outRoot @()

# --------------------------------------------------------------------- -ProveEdits
if ($ProveEdits) {
  if ($env:GITHUB_ACTIONS -ne 'true') { Say '-ProveEdits patches sidecar/ in place, so it runs only on a CI checkout (GITHUB_ACTIONS=true)'; exit 1 }
  $edOut = Join-Path $outRoot 'edited'
  New-Item -ItemType Directory -Force $edOut | Out-Null
  $spec = Import-PowerShellDataFile (Join-Path $here 'sidecar-edits.psd1')
  $proof = New-Object System.Collections.Generic.List[string]
  function Proof([string]$s) { [void]$proof.Add($s) }   # a function: in $list.Add(... -f a, b) the commas split the method arguments
  $editOk = $true
  $origText = @{}
  foreach ($e in $spec.Edits) {
    $path = Join-Path $sidecarDir $e.File
    if (-not $origText.ContainsKey($e.File)) { $origText[$e.File] = [System.IO.File]::ReadAllText($path) }
    $text = [System.IO.File]::ReadAllText($path)
    $nl = $(if ($text.Contains("`r`n")) { "`r`n" } else { "`n" })
    $lines = [System.Collections.Generic.List[string]]($text -split "`r?`n")
    $hits = @(for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i].Contains($e.Find)) { $i } })
    if ($hits.Count -ne 1) { Proof ('{0} {1}: FAILED, the Find text occurs {2} times' -f $e.Id, $e.File, $hits.Count); $editOk = $false; continue }
    $at = $hits[0]; $orig = $lines[$at]
    $lines.RemoveAt($at)
    $lines.InsertRange($at, [string[]]@('#if NET', $orig.Replace($e.Find, $e.Replace), '#else', $orig, '#endif'))
    [System.IO.File]::WriteAllText($path, ($lines -join $nl), (New-Object System.Text.UTF8Encoding($false)))
    Proof ('{0} {1}:{2} applied' -f $e.Id, $e.File, ($at + 1))
  }
  # The csc view: drop every "#if NET ... #else" head and its "#endif"; the file must equal the original.
  foreach ($f in $origText.Keys) {
    $path = Join-Path $sidecarDir $f
    $ls = [System.IO.File]::ReadAllText($path) -split "`r?`n"
    $view = New-Object System.Collections.Generic.List[string]
    $state = 0   # 0 normal, 1 inside the NET branch, 2 inside the #else branch
    foreach ($l in $ls) {
      if ($state -eq 0 -and $l -eq '#if NET') { $state = 1; continue }
      if ($state -eq 1) { if ($l -eq '#else') { $state = 2 }; continue }
      if ($state -eq 2 -and $l -eq '#endif') { $state = 0; continue }
      $view.Add($l)
    }
    $same = (($view -join "`n") -eq (($origText[$f] -split "`r?`n") -join "`n"))
    Proof ('csc view of {0}: {1}' -f $f, $(if ($same) { 'identical to the committed file' } else { 'DIFFERENT' }))
    if (-not $same) { $editOk = $false }
  }
  # Both csc routes still compile the edited files (union = MANTLE_WINRT where the SDK is present; -NoUnion = MANTLE_WINMD).
  foreach ($route in @(@{ N = 'csc -NoUnion'; A = @('-NoUnion') }, @{ N = 'csc union'; A = @() })) {
    $o = Join-Path $bin ('edited-csc-' + ($route.N -replace '[^a-z]', '') + '\Mantle.Sidecar.exe')
    $cl = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $sidecarDir 'build.ps1') @($route.A) -Out $o 2>&1
    Proof ('{0} build of the edited sidecar: {1}' -f $route.N, $(if ($LASTEXITCODE -eq 0) { 'ok, ' + (Cut ((@($cl) | Where-Object { $_ -match 'note:' } | Select-Object -First 1) -replace [regex]::Escape($root), '.') 140) } else { 'FAILED: ' + ((@($cl) | Select-Object -Last 3) -join ' / ') }))
    if ($LASTEXITCODE -ne 0) { $editOk = $false }
  }
  Set-Content -Encoding utf8 -Path (Join-Path $edOut 'proof.txt') -Value $proof
  $proof | ForEach-Object { Say ('  proof: ' + (Cut $_ 300)) }
  $v = Invoke-Pass 'edited' $edOut @('-p:SidecarEdited=true')
  if (-not $editOk) { $v.Ok = $false; $v.Text += '; EDIT PROOF FAILED (see proof.txt)' } else { $v.Text += '; edit proof ok (' + $spec.Edits.Count + ' edits, csc view identical, both csc routes built)' }
  $verdicts += $v
  git -C $root checkout -- sidecar 2>&1 | Out-Null
  $left = @(git -C $root status --porcelain -- sidecar)
  Say $(if ($left.Count -eq 0) { 'sidecar/ restored to the committed text' } else { 'sidecar/ NOT fully restored: ' + ($left -join ', ') })
}

# --------------------------------------------------------------------- verdict
Set-Content -Encoding utf8 -Path (Join-Path $outRoot 'summary.txt') -Value $log
$text = ($verdicts | ForEach-Object { $_.Text }) -join ' || '
if (@($verdicts | Where-Object { -not $_.Ok }).Count -eq 0) { Write-Output ('SIDECAR-LINK: ok (' + $text + ')'); exit 0 }
Write-Output ('SIDECAR-LINK: FAILED (' + $text + ')')
exit 1
