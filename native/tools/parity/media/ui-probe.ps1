# Media lane probe (cloud runner only): build, then run a FILTERED set of UI and unit tests with their failure
# messages printed (ci.ps1 prints only the names). Usage:
#   pwsh -NoProfile -File native/tools/parity/media/ui-probe.ps1 -Ui "Name~A|Name~B" -Unit "Name~C"
param([string]$Ui = '', [string]$Unit = '')
$ErrorActionPreference = 'Continue'
$native = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
& pwsh -NoProfile -File (Join-Path $native 'tools\ci.ps1') -Stages build | Select-Object -Last 3
$exe = Join-Path $native 'out\bin\MantleNative\MantleNative.exe'
function Probe([string]$proj, [string]$filter) {
  if (-not $filter) { return }
  $out = & dotnet test $proj -c Release -nologo --filter $filter --logger 'console;verbosity=normal' 2>&1
  $msg = $false
  foreach ($l in $out) {
    $t = [string]$l
    if ($t -match 'Error Message:') { $msg = $true } elseif ($t -match 'Stack Trace:') { $msg = $false }
    if ($msg -or $t -match 'Passed |Failed |^\s+at Mantle|Total tests') { 'PROBE ' + $t.Substring(0, [Math]::Min(400, $t.Length)) }
  }
}
$env:MANTLE_NATIVE_EXE = $exe; $env:MANTLE_TEST_VISIBLE = '1'
Probe (Join-Path $native 'tests\Mantle.Core.Tests\Mantle.Core.Tests.csproj') $Unit
Probe (Join-Path $native 'tests\Mantle.UiTests\Mantle.UiTests.csproj') $Ui
