[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$toolsRootForNext = Join-Path $env:LOCALAPPDATA 'RadioTEDU\OnAirNext-Dev\tools'
New-Item -ItemType Directory -Path $toolsRootForNext -Force | Out-Null
$sevenZipForNext = Join-Path $env:ProgramFiles '7-Zip\7z.exe'
if (-not (Test-Path -LiteralPath $sevenZipForNext)) { throw 'Developer bootstrap requires 7-Zip to extract the official Icecast installer without installing services.' }

$packagesForNext = @(
    @{ Name = 'dotnet-sdk-10.0.402-win-x64.zip'; Url = 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.402/dotnet-sdk-10.0.402-win-x64.zip'; Algorithm = 'SHA512'; Hash = 'ae4fd528cb7e9f6af6aaf7de36715a69f761f183744f1bb3025d19f5a92447090ec561410e46266c1d10a63781ed6f3de771760bc76c876d87daabc367b898cd'; Destination = 'dotnet'; Marker = 'dotnet.exe' },
    @{ Name = 'icecast_win64_2.5.0.exe'; Url = 'https://downloads.xiph.org/releases/icecast/icecast_win64_2.5.0.exe'; Algorithm = 'SHA256'; Hash = 'ad412cbe5d3d3804ea6208dfb4be8d8a25ecb94d62f46bc5a9aa8fa0f2f647c9'; Destination = 'icecast'; Marker = 'bin\icecast.exe' }
)
foreach ($packageForNext in $packagesForNext) {
    $archiveForNext = Join-Path $toolsRootForNext $packageForNext.Name
    if (-not (Test-Path -LiteralPath $archiveForNext)) { Invoke-WebRequest -Uri $packageForNext.Url -OutFile $archiveForNext }
    $actualHashForNext = (Get-FileHash -LiteralPath $archiveForNext -Algorithm $packageForNext.Algorithm).Hash.ToLowerInvariant()
    if ($actualHashForNext -ne $packageForNext.Hash) { throw "Checksum mismatch: $($packageForNext.Name)" }
    $destinationForNext = Join-Path $toolsRootForNext $packageForNext.Destination
    if (-not (Test-Path -LiteralPath (Join-Path $destinationForNext $packageForNext.Marker))) {
        & $sevenZipForNext x $archiveForNext ('-o' + $destinationForNext) -y -bso0 -bsp0
        if ($LASTEXITCODE -ne 0) { throw "Extraction failed: $($packageForNext.Name)" }
    }
}
$ffmpegForNext = Get-Command ffmpeg -ErrorAction Stop
$dotnetForNext = Join-Path $toolsRootForNext 'dotnet\dotnet.exe'
& $dotnetForNext --version
if ($LASTEXITCODE -ne 0) { throw 'Private SDK failed to start.' }
Write-Output "Private development tools ready in $toolsRootForNext. FFmpeg: $($ffmpegForNext.Source)"
Write-Output 'No system service, machine PATH, production configuration, or existing installation was changed.'
