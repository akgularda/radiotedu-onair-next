[CmdletBinding()]
param([Parameter(Mandatory)][string]$RuntimeRoot, [switch]$IntegrityOnly)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows') { throw 'Codec qualification is restricted to a disposable Windows CI runner.' }
$codecRepo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$codecEvidence = Join-Path $codecRepo 'artifacts\windows-codec-qualification'
New-Item -ItemType Directory -Path $codecEvidence -Force | Out-Null
$codecRoot = [IO.Path]::GetFullPath($RuntimeRoot)
$codecPrefix = $codecRoot.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
$codecSummary = [ordered]@{ Source=$env:GITHUB_SHA; Completed=$false; Passed=$false; ProductionChanged=$false;
    Scope='Disposable Windows runtime dependency integrity and real AAC-LC/HE-AACv2/24-bit Ogg FLAC smoke encodes/decodes only. Not the 18-output engine, worst-case encoded headroom, actual-PC, long-run or mouse qualification.';
    Profiles=@(); Mode=$(if($IntegrityOnly){'Dependency integrity only'}else{'Real profile encode/decode smoke'}); Error=$null }
function Run-CodecTool([string]$Tool, [string[]]$ToolArguments, [string]$LogName) {
    $codecLines = @(& $Tool @ToolArguments 2>&1 | ForEach-Object { $_.ToString() })
    $codecExit = $LASTEXITCODE
    $codecLines | Set-Content -LiteralPath (Join-Path $codecEvidence $LogName) -Encoding utf8
    if ($codecExit -ne 0) { throw "Codec tool failed: $LogName (exit $codecExit)" }
    return $codecLines
}
try {
    $codecManifest = Get-Content -LiteralPath (Join-Path $codecRoot 'runtime-manifest.json') -Raw | ConvertFrom-Json
    if ($codecManifest.schemaVersion -ne 1 -or $codecManifest.target -ne 'win-x64') { throw 'Unexpected codec dependency manifest.' }
    $codecLockPath = Join-Path $PSScriptRoot 'windows-codec.lock.json'
    $codecActualLockHash=(Get-FileHash -LiteralPath $codecLockPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $codecActualBuildHash=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'build-windows-codecs.py') -Algorithm SHA256).Hash.ToLowerInvariant()
    $codecSummary.Inputs=@{LockExpected=$codecManifest.dependencyLockSha256;LockActual=$codecActualLockHash;BuildScriptExpected=$codecManifest.buildScriptSha256;BuildScriptActual=$codecActualBuildHash}
    if ($codecManifest.dependencyLockSha256 -ne $codecActualLockHash) { throw 'Codec dependency lock bytes differ from the pinned build.' }
    if ($codecManifest.buildScriptSha256 -ne $codecActualBuildHash) { throw 'Codec build-script bytes differ from the pinned build; verify canonical LF checkout.' }
    $codecKnown = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($codecFile in $codecManifest.files) {
        $codecPath = [IO.Path]::GetFullPath((Join-Path $codecRoot $codecFile.path))
        if (-not $codecPath.StartsWith($codecPrefix,[StringComparison]::OrdinalIgnoreCase) -or -not $codecKnown.Add($codecPath)) { throw 'Invalid or duplicate runtime manifest path.' }
        $codecInfo = Get-Item -LiteralPath $codecPath
        if ($codecInfo.Attributes -band [IO.FileAttributes]::ReparsePoint -or $codecInfo.Length -ne $codecFile.bytes -or
            (Get-FileHash -LiteralPath $codecPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $codecFile.sha256) { throw "Codec dependency integrity failed: $($codecFile.path)" }
    }
    $codecExtra = @(Get-ChildItem -LiteralPath $codecRoot -Recurse -File | Where-Object { $_.Name -ne 'runtime-manifest.json' -and -not $codecKnown.Contains($_.FullName) })
    if ($codecExtra.Count -ne 0) { throw 'Unmanifested runtime files are not allowed.' }
    $codecFfmpeg = Join-Path $codecRoot 'bin\ffmpeg.exe'
    $codecProbe = Join-Path $codecRoot 'bin\ffprobe.exe'
    $codecVersion = @(Run-CodecTool $codecFfmpeg @('-version') 'version.txt')
    if ($codecVersion[0] -notmatch '^ffmpeg version n?9\.0\.2(?:[-\s]|$)') { throw 'Pinned FFmpeg version did not start.' }
    $codecConfig = (Run-CodecTool $codecFfmpeg @('-hide_banner','-buildconf') 'buildconf.txt') -join [Environment]::NewLine
    if ($codecConfig -match '--enable-(?:gpl|nonfree|version3)' -or $codecConfig -notmatch '--enable-libfdk-aac') { throw 'Codec build configuration differs from the required license/features.' }
    $null = Run-CodecTool $codecFfmpeg @('-hide_banner','-L') 'license.txt'
    $codecEncoders = (Run-CodecTool $codecFfmpeg @('-hide_banner','-encoders') 'encoders.txt') -join [Environment]::NewLine
    foreach ($codecEncoder in @('aac','libfdk_aac','flac','pcm_f32le')) {
        if ($codecEncoders -notmatch ('(?m)^\s*A\S*\s+' + [regex]::Escape($codecEncoder) + '\s')) { throw "Missing required encoder: $codecEncoder" }
    }
    if ($IntegrityOnly) {
        $codecSummary.Scope='Pinned runtime source/configuration/file integrity and encoder availability only; no audio was encoded in this verification.'
        $codecSummary.DependencyManifestSha256=(Get-FileHash -LiteralPath (Join-Path $codecRoot 'runtime-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        $codecSummary.VerifiedFiles=$codecKnown.Count
        $codecSummary.Completed=$true; $codecSummary.Passed=$true
        Write-Output 'Reverified downloaded runtime integrity and required encoder availability.'
        return
    }
    $codecPcm = Join-Path $codecEvidence 'input.f32'
    $codecWriter = [IO.BinaryWriter]::new([IO.File]::Create($codecPcm))
    try {
        for ($codecFrame=0; $codecFrame -lt 192000; $codecFrame++) {
            $codecWriter.Write([float](0.08 * [Math]::Sin(2 * [Math]::PI * 1200 * $codecFrame / 48000)))
            $codecWriter.Write([float](0.06 * [Math]::Sin(2 * [Math]::PI * 1379 * $codecFrame / 48000)))
        }
    } finally { $codecWriter.Dispose() }
    $codecCases = @(
        @{ Name='aac-lc'; Encoder='aac'; Profile='aac_low'; ExpectedProfile='LC'; Rate='192k'; Extension='aac'; Mux='adts' },
        @{ Name='he-aac-v2'; Encoder='libfdk_aac'; Profile='aac_he_v2'; ExpectedProfile='HE-AACv2'; Rate='64k'; Extension='aac'; Mux='adts' },
        @{ Name='ogg-flac-24'; Encoder='flac'; Extension='ogg'; Mux='ogg' }
    )
    foreach ($codecCase in $codecCases) {
        $codecSummary.CurrentProfile=$codecCase.Name
        $codecEncoded = Join-Path $codecEvidence ($codecCase.Name + '.' + $codecCase.Extension)
        $codecArgs = @('-nostdin','-hide_banner','-y','-f','f32le','-ar','48000','-ac','2','-i',$codecPcm,'-c:a',$codecCase.Encoder)
        if ($codecCase.Profile) { $codecArgs += @('-profile:a',$codecCase.Profile,'-b:a',$codecCase.Rate) }
        else { $codecArgs += @('-sample_fmt','s32','-bits_per_raw_sample','24') }
        $codecArgs += @('-f',$codecCase.Mux,$codecEncoded)
        $null = Run-CodecTool $codecFfmpeg $codecArgs ($codecCase.Name + '-encode.txt')
        $codecProbeText = (Run-CodecTool $codecProbe @('-v','error','-show_streams','-of','json',$codecEncoded) ($codecCase.Name + '-probe.json')) -join [Environment]::NewLine
        $codecStream = @((ConvertFrom-Json $codecProbeText).streams)
        if ($codecStream.Count -ne 1 -or $codecStream[0].channels -ne 2 -or $codecStream[0].sample_rate -ne '48000') { throw 'Encoded profile/rate/stereo changed.' }
        if ($codecCase.Profile -and ($codecStream[0].codec_name -ne 'aac' -or $codecStream[0].profile -ne $codecCase.ExpectedProfile)) { throw "Wrong actual AAC profile: $($codecCase.Name)" }
        if (-not $codecCase.Profile -and ($codecStream[0].codec_name -ne 'flac' -or $codecStream[0].bits_per_raw_sample -ne '24')) { throw 'FLAC lost its 24-bit format.' }
        $codecDecoded = Join-Path $codecEvidence ($codecCase.Name + '-decoded.f32')
        $codecDecodeArgs = @('-nostdin','-hide_banner','-y')
        if ($codecCase.Profile) { $codecDecodeArgs += @('-c:a','aac') }
        $codecDecodeArgs += @('-i',$codecEncoded,'-ar','48000','-ac','2','-c:a','pcm_f32le','-f','f32le',$codecDecoded)
        $null = Run-CodecTool $codecFfmpeg $codecDecodeArgs ($codecCase.Name + '-decode.txt')
        $codecFrames = (Get-Item -LiteralPath $codecDecoded).Length / 8
        $codecPaddingBudget=if($codecCase.Name -eq 'he-aac-v2'){12000}elseif($codecCase.Name -eq 'aac-lc'){2048}else{0}
        $codecFrameJson=(Run-CodecTool $codecProbe @('-v','error','-select_streams','a:0','-show_frames','-show_entries','frame=nb_samples','-of','json',$codecEncoded) ($codecCase.Name + '-frames-probe.json')) -join [Environment]::NewLine
        $codecProbedFrames=0L
        foreach($codecFrameInfo in (ConvertFrom-Json $codecFrameJson).frames){$codecProbedFrames += [long]$codecFrameInfo.nb_samples}
        $codecSummary.LastDecode=@{Profile=$codecCase.Name;InputFrames=192000;DecodedFrames=$codecFrames;IndependentProbedFrameSamples=$codecProbedFrames;PrimingAndFlushFrames=$codecFrames-192000;MaximumPrimingAndFlushFrames=$codecPaddingBudget}
        if ($codecFrames -ne $codecProbedFrames -or $codecFrames -lt 192000 -or $codecFrames -gt 192000+$codecPaddingBudget -or $codecFrames -ne [Math]::Floor($codecFrames)) { throw 'Native decoded frame count differs from the independent frame sum or exceeds the explicit codec priming/flush budget.' }
        $null = Run-CodecTool $codecFfmpeg @('-nostdin','-hide_banner','-f','f32le','-ar','48000','-ac','2','-i',$codecDecoded,'-af','ebur128=peak=true','-f','null','-') ($codecCase.Name + '-ebur128.txt')
        $codecSummary.Profiles += @{ Name=$codecCase.Name; ActualStream=$codecStream[0]; DecodedFrames=$codecFrames;ProbedFrameSamples=$codecProbedFrames;PrimingAndFlushFrames=$codecFrames-192000;MaximumPrimingAndFlushFrames=$codecPaddingBudget; Encoder=$codecCase.Encoder; FileSha256=(Get-FileHash -LiteralPath $codecEncoded -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $codecSummary.Version=$codecVersion[0]
    $codecSummary.DependencyManifestSha256=(Get-FileHash -LiteralPath (Join-Path $codecRoot 'runtime-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    $codecSummary.VerifiedFiles=$codecKnown.Count
    $codecSummary.Completed=$codecSummary.Profiles.Count -eq 3
    $codecSummary.Passed=$codecSummary.Completed
    if (-not $codecSummary.Passed) { throw 'All actual codec profiles must complete.' }
    Write-Output 'Verified exact runtime inputs/files and three real encode/native-decode profiles, including libfdk HE-AACv2.'
} catch { $codecSummary.Error=$_.Exception.Message; throw }
finally { $codecSummary | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $codecEvidence 'summary.json') -Encoding utf8 }
