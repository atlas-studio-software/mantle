# diag-theme.ps1 - TEMPORARY (lane native-r4-osd): after ci.ps1's ui stage, keep the app logs of the theme, OSD and
# toasts UI tests, then run the theme + OSD tests again without the toasts test, and with it, keeping those logs too.
# Finds whether the theme-change crash follows the toasts test or the build. Removed once the cause is fixed.
$ErrorActionPreference = 'Continue'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$out = Join-Path $repo 'native\out\ui\diag'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$proj = Join-Path $repo 'native\tests\Mantle.UiTests\Mantle.UiTests.csproj'
function Grab([string]$tag) {
  Get-ChildItem -Path $env:TEMP -Directory -Filter 'mantle-native-uitest-*' -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.Name -notmatch 'look044|matchwin|themeset|uitest-osd|toasts') { return }
    $l = Get-ChildItem -Path $_.FullName -Recurse -Filter 'mantle-native.log' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($l) { Copy-Item $l.FullName (Join-Path $out "$tag-$($_.Name).log") }
  }
  Get-ChildItem -Path $env:TEMP -Directory -Filter 'mantle-native-uitest-*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
function Pass([string]$tag, [string]$filter) {
  $env:MANTLE_TEST_VISIBLE = '1'
  $r = & dotnet test $proj -c Release -nologo --no-build --filter $filter --logger "trx;LogFileName=$tag.trx" --results-directory $out 2>&1
  $env:MANTLE_TEST_VISIBLE = $null
  $sum = @($r | Where-Object { "$_" -match '^(Passed!|Failed!|Total tests|\s+Failed\s)' }) -join ' | '
  Write-Host "DIAG $tag : $sum"
  Grab $tag
}
Grab 'full'
Pass 'no-toasts' 'FullyQualifiedName~Mantle.UiTests.Theme|FullyQualifiedName~Mantle.UiTests.Osd'
Pass 'with-toasts' 'FullyQualifiedName~Mantle.UiTests.Toasts|FullyQualifiedName~Mantle.UiTests.Theme|FullyQualifiedName~Mantle.UiTests.Osd'
Write-Host 'DIAG: done'
