# diag-theme.ps1 - TEMPORARY (lane native-r4-osd): the theme-change crash (OnDefaultSystemBackdropConfigurationChanged:
# "The parameter is incorrect: target") happens on this branch and not on native/main. Runs the quick theme.set UI
# test under temporary switches (MANTLE_DIAG_*: removed with this script) and prints how many app logs crashed.
$ErrorActionPreference = 'Continue'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$out = Join-Path $repo 'native\out\ui\diag'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$proj = Join-Path $repo 'native\tests\Mantle.UiTests\Mantle.UiTests.csproj'
$vars = 'MANTLE_DIAG_NO_SHADOW', 'MANTLE_DIAG_SHADOW', 'MANTLE_DIAG_NO_TOASTS_SURFACE', 'MANTLE_DIAG_NO_TOASTS_FEATURE', 'DOTNET_GCgen0size'
function Pass([string]$tag, [hashtable]$set, [int]$times = 2) {
  foreach ($v in $vars) { Remove-Item "env:$v" -ErrorAction SilentlyContinue }
  foreach ($k in $set.Keys) { Set-Item "env:$k" $set[$k] }
  $env:MANTLE_TEST_VISIBLE = '1'
  $crash = 0; $logs = 0; $res = @()
  for ($i = 1; $i -le $times; $i++) {
    Get-ChildItem -Path $env:TEMP -Directory -Filter 'mantle-native-uitest-*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    $r = & dotnet test $proj -c Release -nologo --no-build --filter 'FullyQualifiedName~Theme_set_switches|FullyQualifiedName~Another_apps_theme' 2>&1
    $res += (@($r | Where-Object { "$_" -match '^(Passed!|Failed!)' }) -join ' ') -replace '\s+', ' '
    Get-ChildItem -Path $env:TEMP -Directory -Filter 'mantle-native-uitest-*' -ErrorAction SilentlyContinue | ForEach-Object {
      $l = Get-ChildItem -Path $_.FullName -Recurse -Filter 'mantle-native.log' -ErrorAction SilentlyContinue | Select-Object -First 1
      if ($l) { $logs++; if (Select-String -Path $l.FullName -Pattern 'UNHANDLED' -Quiet) { $crash++ }; Copy-Item $l.FullName (Join-Path $out "$tag-$i-$($_.Name).log") }
    }
  }
  foreach ($v in $vars) { Remove-Item "env:$v" -ErrorAction SilentlyContinue }
  $env:MANTLE_TEST_VISIBLE = $null
  Write-Host "DIAG $tag : $crash of $logs app logs crashed | $($res -join ' / ')"
}
Pass 'base' @{}
Pass 'no-shadow' @{ MANTLE_DIAG_NO_SHADOW = '1' }
Pass 'shadow-hide' @{ MANTLE_DIAG_SHADOW = 'hide' }
Pass 'shadow-sbs' @{ MANTLE_DIAG_SHADOW = 'sbs' }
Pass 'shadow-nocopy' @{ MANTLE_DIAG_SHADOW = 'nocopy' }
Pass 'shadow-noimg' @{ MANTLE_DIAG_SHADOW = 'noimg' }
Write-Host 'DIAG: done'
