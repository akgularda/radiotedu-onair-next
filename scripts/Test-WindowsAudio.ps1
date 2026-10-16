[CmdletBinding()]
param([Parameter(Mandatory)][string]$BuildRoot)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'This real-codec qualification runs only on a disposable CI runner; never on the broadcasting PC.' }
$audioRepo = Split-Path $PSScriptRoot -Parent
$audioEvidence = Join-Path $audioRepo 'artifacts\windows-audio'
New-Item -ItemType Directory -Path $audioEvidence -Force | Out-Null
$audioTools = Join-Path $env:RUNNER_TEMP ('onair-next-audio-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $audioTools | Out-Null
$audioSummary = [ordered]@{ Source=$env:GITHUB_SHA; Completed=$false; Passed=$false; ProductionChanged=$false;
    Scope='Real isolated AAC-LC/24-bit Ogg FLAC, synthetic live mix and recovery only. Includes five-signal fixed source gain and two low-crest normalized AAC-LC/FLAC branches. Includes synthetic final-mix limiter and prepared/audible recovery ownership. No HE-AAC/18-output/worst-case encoded limiter/headroom/actual-PC/24-72h or mouse acceptance.';
    HeAacTested=$false; Cases=@(); Tools=@(); Error=$null }
try {
    $audioArchives = @(
        @{ Name='ffmpeg-9.0.2-essentials_build.7z'; Url='https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.7z'; Hash='4705843ccaaf54257c16ad90f3e952ece33c17df964ecf7bfdbb0f49c7171077'; Folder='ffmpeg' },
        @{ Name='icecast_win64_2.5.0.exe'; Url='https://downloads.xiph.org/releases/icecast/icecast_win64_2.5.0.exe'; Hash='ad412cbe5d3d3804ea6208dfb4be8d8a25ecb94d62f46bc5a9aa8fa0f2f647c9'; Folder='icecast' }
    )
    $audioSevenZip = Join-Path $env:ProgramFiles '7-Zip\7z.exe'
    if (-not (Test-Path -LiteralPath $audioSevenZip)) { $audioSevenZip = (Get-Command 7z.exe -ErrorAction Stop).Source }
    foreach ($audioArchive in $audioArchives) {
        $audioDownload = Join-Path $audioTools $audioArchive.Name
        Invoke-WebRequest -Uri $audioArchive.Url -OutFile $audioDownload -TimeoutSec 120
        $audioHash = (Get-FileHash -LiteralPath $audioDownload -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($audioHash -ne $audioArchive.Hash) { throw "Runtime checksum mismatch: $($audioArchive.Name)" }
        $audioFolder = Join-Path $audioTools $audioArchive.Folder
        & $audioSevenZip x $audioDownload ('-o' + $audioFolder) -y -bso0 -bsp0
        if ($LASTEXITCODE -ne 0) { throw 'Runtime extraction failed.' }
        $audioSummary.Tools += @{ Name=$audioArchive.Name; Sha256=$audioHash; Url=$audioArchive.Url }
    }
    $audioFfmpeg = @(Get-ChildItem -LiteralPath (Join-Path $audioTools 'ffmpeg') -Filter ffmpeg.exe -Recurse -File)
    if ($audioFfmpeg.Count -ne 1) { throw 'Expected exactly one pinned FFmpeg runtime.' }
    $audioFfmpeg = $audioFfmpeg[0].FullName
    $audioIcecast = Join-Path $audioTools 'icecast'
    if (-not (Test-Path -LiteralPath (Join-Path $audioIcecast 'bin\icecast.exe'))) { throw 'Pinned Icecast runtime is missing.' }
    $audioSummary.FfmpegVersion = (& $audioFfmpeg -version | Select-Object -First 1)
    if ($LASTEXITCODE -ne 0 -or $audioSummary.FfmpegVersion -notmatch '^ffmpeg version 9\.0\.2') { throw 'Pinned FFmpeg version did not start.' }
    $audioDotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
    $audioLab = Join-Path $BuildRoot 'OnAirNext.Lab\release\OnAirNext.Lab.dll'
    if (-not (Test-Path -LiteralPath $audioLab)) { throw 'The compiled audio lab is missing.' }
    $audioScenarios = @('baseline','precision','normalization','processing','live','recovery','finite','backpressure','corrupt')
    foreach ($audioScenario in $audioScenarios) {
        $audioLog = Join-Path $audioEvidence ($audioScenario + '.log')
        $audioOutput = @(& (Join-Path $PSScriptRoot 'Test-Engine.ps1') -PreparedCiBuild -Scenario $audioScenario -DurationSeconds 12 -FfmpegRuntime $audioFfmpeg -IcecastRuntime $audioIcecast 2>&1)
        $audioExitCode = $LASTEXITCODE
        $audioOutput | Set-Content -LiteralPath $audioLog -Encoding utf8
        $audioLine = $audioOutput | ForEach-Object { $_.ToString() } | Where-Object { $_.StartsWith('Evidence: ') } | Select-Object -Last 1
        if (-not $audioLine) { throw "No audio evidence for $audioScenario; exit $audioExitCode" }
        $audioFile = $audioLine.Substring(10).Trim()
        $audioRunRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'RadioTEDU\OnAirNext-Dev\runs')) + [IO.Path]::DirectorySeparatorChar
        if (-not [IO.Path]::GetFullPath($audioFile).StartsWith($audioRunRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Audio result escaped the isolated run root.' }
        $audioResult = Get-Content -LiteralPath $audioFile -Raw | ConvertFrom-Json
        if ($audioResult.ExecutionSource -ne $env:GITHUB_SHA -or $audioResult.ExecutionBuild -notlike "*$($env:GITHUB_SHA)*") { throw 'Audio execution source differs from the requested build.' }
        $audioSummary.Cases += $audioResult
        $audioOwnedChildren = @($audioResult.RemainingOwnedChildren.PSObject.Properties).Count
        if ($audioExitCode -ne 0 -or @($audioResult.Results).Count -ne 1 -or $audioResult.Results[0].Passed -ne $true -or $audioOwnedChildren -ne 0) {
            throw "Audio scenario failed or left owned children: $audioScenario"
        }
        Write-Output "$audioScenario passed with $(@($audioResult.Results[0].Checks).Count) real checks."
    }
    $audioSummary.Completed = $audioSummary.Cases.Count -eq $audioScenarios.Count
    if (-not $audioSummary.Completed) { throw "Not every requested audio scenario completed." }
    $audioSummary.Passed = $audioSummary.Completed
}
catch { $audioSummary.Error = $_.Exception.Message; throw }
finally { $audioSummary | ConvertTo-Json -Depth 60 | Set-Content -LiteralPath (Join-Path $audioEvidence 'summary.json') -Encoding utf8 }
