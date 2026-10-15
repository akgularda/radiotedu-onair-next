[CmdletBinding()]
param(
    [ValidateSet('all','baseline','recovery','durable','offline','fleet','live','soak','backpressure','corrupt','host')][string]$Scenario = 'all',
    [ValidateRange(6,259200)][int]$DurationSeconds = 12,
    [switch]$SkipBootstrap,
    [switch]$SkipUnitTests
)
$ErrorActionPreference = 'Stop'
$projectRootForNext = Split-Path $PSScriptRoot -Parent
$localRootForNext = Join-Path $env:LOCALAPPDATA 'RadioTEDU\OnAirNext-Dev'
if (-not $SkipBootstrap) { & (Join-Path $PSScriptRoot 'Setup-Dev.ps1') }
$dotnetForNext = Join-Path $localRootForNext 'tools\dotnet\dotnet.exe'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Push-Location $projectRootForNext
try {
    & $dotnetForNext restore 'OnAirNext.slnx' --locked-mode --disable-parallel -m:1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Locked dependency restore failed.' }
    & $dotnetForNext build 'OnAirNext.slnx' -c Release --no-restore -m:1 /p:UseSharedCompilation=false /p:BuildInParallel=false | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Engine build failed.' }
    if (-not $SkipUnitTests) {
        & $dotnetForNext test 'tests\OnAirNext.Tests\OnAirNext.Tests.csproj' -c Release --no-build --logger 'console;verbosity=minimal' | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Engine unit tests failed.' }
    }
    & $dotnetForNext (Join-Path $localRootForNext 'build\bin\OnAirNext.Lab\release\OnAirNext.Lab.dll') --ffmpeg (Get-Command ffmpeg).Source --scenario $Scenario --duration-seconds $DurationSeconds | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Loopback integration tests failed; preserve the printed evidence directory.' }
}
finally { Pop-Location }
