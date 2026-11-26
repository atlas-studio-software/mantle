<#
  native/tools/publish.ps1 - publish MantleNative.exe in one of three modes.

    pwsh -NoProfile -File native/tools/publish.ps1 -Mode sc|fd|aot -Out <dir> [-Configuration Release]

  sc   self-contained + ReadyToRun: .NET and the Windows App SDK runtime are in the folder, nothing
       to install (the spike's mode, the one the bench measured).
  fd   framework-dependent (+ ReadyToRun): the folder holds only the app; .NET 10 Desktop Runtime and
       the Windows App Runtime 1.7 (x64) must be installed on the machine.
  aot  Native AOT (trimmed): one native exe plus the Windows App SDK runtime files, no JIT.

  Prints the exact dotnet command, then one line: "PUBLISH <mode>: ok (<exe>, <MB> MB, <n> files)"
  or "PUBLISH <mode>: FAILED (<the first compiler/linker errors>)". Exit code 0 / 1.
  The full output goes to <Out>.publish.log beside the output folder.
#>
param(
  [Parameter(Mandatory = $true)][ValidateSet('sc', 'fd', 'aot')][string]$Mode,
  [Parameter(Mandatory = $true)][string]$Out,
  [string]$Configuration = 'Release',
  # -Zip (the early trial, docs/trial-early.md): also put the switch scripts in tools\ next to the exe and pack the
  # folder as <Out>-win-x64.zip with a .sha256 beside it. Off by default: the published folder is then unchanged.
  [switch]$Zip
)
$ErrorActionPreference = 'Stop'
$native = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $native 'src\Mantle.App\Mantle.App.csproj'
if (-not [System.IO.Path]::IsPathRooted($Out)) { $Out = Join-Path (Get-Location) $Out }
$Out = [System.IO.Path]::GetFullPath($Out)
$log = $Out.TrimEnd('\', '/') + '.publish.log'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Out) | Out-Null
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }

$common = @('publish', $proj, '-c', $Configuration, '-r', 'win-x64', '-o', $Out, '-nologo')
switch ($Mode) {
  'sc'  { $flags = @('--self-contained', 'true', '-p:WindowsAppSDKSelfContained=true', '-p:PublishReadyToRun=true') }
  'fd'  { $flags = @('--self-contained', 'false', '-p:WindowsAppSDKSelfContained=false', '-p:PublishReadyToRun=true') }
  'aot' { $flags = @('--self-contained', 'true', '-p:WindowsAppSDKSelfContained=true', '-p:PublishAot=true', '-p:PublishReadyToRun=false') }
}
$cmd = $common + $flags
Write-Host ("publish ${Mode}: dotnet " + ($cmd -join ' '))

# global.json lives in native/: run from there so the pinned SDK line is the one used
Push-Location $native
$sw = [System.Diagnostics.Stopwatch]::StartNew()
try {
  & dotnet @cmd *> $log
  $code = $LASTEXITCODE
} finally { Pop-Location }
$secs = [math]::Round($sw.Elapsed.TotalSeconds)

$exe = Join-Path $Out 'MantleNative.exe'
if ($code -ne 0 -or -not (Test-Path $exe)) {
  $errs = @(Select-String -Path $log -Pattern ': error |error [A-Z]+[0-9]+:|Unhandled exception' | Select-Object -First 8 | ForEach-Object { $_.Line.Trim() })
  if ($errs.Count -eq 0) { $errs = @(Get-Content $log -Tail 8) }
  $short = ($errs | ForEach-Object { ($_ -replace [regex]::Escape($native), 'native') }) -join ' | '
  Write-Host "PUBLISH ${Mode}: FAILED (exit $code after ${secs}s; $short)"
  exit 1
}
# the way back without Mantle (Mantle.App.csproj MantleRestoreScript): a folder without it is not a build to run
$restoreSet = @('restore-windows.ps1', 'restore-windows.cmd', 'restore-banners.ps1', 'restore-osd.ps1', 'ramreclaim-undo.ps1')
$missing = @($restoreSet | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Out "tools\$_") -PathType Leaf) })
if ($missing.Count -gt 0) {
  Write-Host "PUBLISH ${Mode}: FAILED (tools\ next to MantleNative.exe is missing $($missing -join ', '); see Mantle.App.csproj MantleRestoreScript)"
  exit 1
}
$files = @(Get-ChildItem -Recurse -File $Out)
$mb = [math]::Round((($files | Measure-Object -Sum Length).Sum) / 1MB, 1)
$exeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "PUBLISH ${Mode}: ok (MantleNative.exe $exeMb MB; folder $mb MB, $($files.Count) files; ${secs}s)"
if ($Zip) {
  # the switch scripts look for MantleNative.exe one folder up and for install-autostart.ps1 beside themselves
  foreach ($s in @('switch-to-native.ps1', 'switch-back.ps1', 'install-autostart.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $s) -Destination (Join-Path $Out "tools\$s") -Force
  }
  # Platform/SidecarLocator: the Mantle.Sidecar.exe next to MantleNative.exe, else a checkout's sidecar\bin. An
  # unpacked zip is no checkout, so without this copy the platform calls and the crash supervisor have no exe.
  $sidecar = Join-Path (Split-Path -Parent $native) 'sidecar\bin\Mantle.Sidecar.exe'
  if (-not (Test-Path -LiteralPath $sidecar -PathType Leaf)) {
    Write-Host "ZIP: FAILED (no $sidecar to put next to MantleNative.exe: build it first, sidecar/build.ps1)"
    exit 1
  }
  Copy-Item -LiteralPath $sidecar -Destination (Join-Path $Out 'Mantle.Sidecar.exe') -Force
  $head = ''
  try { $head = (& git -C $native rev-parse HEAD 2>$null) } catch { }
  $stamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)
  Set-Content -LiteralPath (Join-Path $Out 'BUILD.txt') -Encoding utf8 -Value @("MantleNative win-x64 ($Mode), published $stamp", "git HEAD of the publishing tree: $head (a cloudtest run's snapshot commit; its run.json names the base)")
  $zipFile = $Out.TrimEnd('\', '/') + '-win-x64.zip'
  if (Test-Path -LiteralPath $zipFile) { Remove-Item -Force -LiteralPath $zipFile }
  Compress-Archive -Path (Join-Path $Out '*') -DestinationPath $zipFile -CompressionLevel Optimal
  $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipFile).Hash.ToLowerInvariant()
  Set-Content -LiteralPath ($zipFile + '.sha256') -Encoding ascii -Value ($hash + '  ' + (Split-Path -Leaf $zipFile))
  $zmb = [math]::Round((Get-Item -LiteralPath $zipFile).Length / 1MB, 1)
  Write-Host "ZIP: ok ($(Split-Path -Leaf $zipFile) $zmb MB, sha256 $hash)"
}
exit 0
