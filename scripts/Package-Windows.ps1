param([string]$OutputRoot=(Join-Path $PSScriptRoot '../artifacts/windows-package'),[string]$CodecRuntimeRoot=(Join-Path $PSScriptRoot '../artifacts/windows-codec-runtime'))
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true'){throw 'Build this development package on the isolated build runner, not the broadcasting PC.'}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$destination=[IO.Path]::GetFullPath($OutputRoot)
if(Test-Path -LiteralPath $destination){throw 'Package output already exists; choose an unused output folder'}
$null=New-Item -ItemType Directory -Path $destination
$before=@{}
foreach($file in Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter packages.lock.json -Recurse){$before[$file.FullName]=Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json -AsHashtable}
foreach($entry in @(@{Project='OnAirNext.Host';Folder='bin'},@{Project='OnAirNext.Web';Folder='console'},@{Project='OnAirNext.Setup';Folder='setup'})){
 $project=Join-Path $root ('src/'+$entry.Project+'/'+$entry.Project+'.csproj')
 # Use the reviewed Windows runtime graph; framework and runtime dependencies both restore in locked mode.
 & dotnet restore $project -r win-x64 --locked-mode --disable-parallel -m:1 /p:SelfContained=true
 if($LASTEXITCODE -ne 0){throw ('Runtime restore failed: '+$entry.Project)}
 foreach($path in $before.Keys){
  $after=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json -AsHashtable
  foreach($framework in $before[$path].dependencies.Keys){foreach($package in $before[$path].dependencies[$framework].Keys){
   $old=$before[$path].dependencies[$framework][$package];$new=$after.dependencies[$framework][$package]
   if($old.resolved -and $old.resolved -ne $new.resolved){throw ('Runtime expansion changed a locked dependency: '+$package)}
   if($old.contentHash -and $old.contentHash -ne $new.contentHash){throw ('Runtime expansion changed locked package bytes: '+$package)}
  }}
 }
 & dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore -o (Join-Path $destination $entry.Folder) -m:1 /p:UseSharedCompilation=false /p:BuildInParallel=false /p:PublishTrimmed=false /p:DebugType=None /p:DebugSymbols=false
 if($LASTEXITCODE -ne 0){throw ('Self-contained publish failed: '+$entry.Project)}
}
$codecRoot=[IO.Path]::GetFullPath($CodecRuntimeRoot)
$codecReference=Join-Path $codecRoot 'runtime-manifest.json'
if(-not (Test-Path -LiteralPath $codecReference) -or (Get-Item -LiteralPath $codecReference).Length -gt 524288){throw 'The complete qualified codec runtime reference is required'}
Copy-Item -LiteralPath $codecReference -Destination (Join-Path $destination 'setup/codec-runtime-reference.json')
$files=@(foreach($file in Get-ChildItem -LiteralPath $destination -File -Recurse){@{Path=[IO.Path]::GetRelativePath($destination,$file.FullName).Replace('\','/');Bytes=$file.Length;Sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}})
@{Source=$env:GITHUB_SHA;Files=$files}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $destination 'bundle.json') -Encoding utf8NoBOM
@'
RadioTEDU OnAir Next — Windows development package

Open setup/OnAirNext.Setup.exe. It starts the local browser console without Codex.
Download and extract this run's windows-codec-runtime artifact. In System, select its complete
folder containing runtime-manifest.json, bin, Licenses and Sources. Setup checks every declared
file against the matching reference, preserving ffprobe, license notices and source archives.
Review the separate service installation, then approve the Windows administrator prompt.
All eight channels are initially stopped with zero outputs. The former service is not changed.
For an earlier flat installation, System > Audio tools reviews additions from its ORIGINAL
matching complete provider. Original codec binaries are retained; keep both folders until the
protected completion receipt is recorded. Application update and rollback retain that inventory.

.NET is included. FFmpeg and music are not included. Preserve the runtime provider's license files.
This is a development package, not a commissioned radio release. Complete actual-PC UI,
codec/loudness, import/rollback and 24/72-hour gates before migrating one station group at a time.
'@|Set-Content -LiteralPath (Join-Path $destination 'READ-ME.txt') -Encoding utf8NoBOM
# Qualify the actual provider inputs before the later SCM/file-copy test double.
$realPreparation=Join-Path $root 'artifacts/windows-real-runtime-preparation';$null=New-Item -ItemType Directory -Path $realPreparation -Force
$realRequest=Join-Path $realPreparation 'request.json';@{RuntimeFolder=$codecRoot;StartService=$false}|ConvertTo-Json|Set-Content -LiteralPath $realRequest -Encoding utf8NoBOM
$start=[Diagnostics.ProcessStartInfo]::new((Join-Path $destination 'setup/OnAirNext.Setup.exe'));$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
foreach($argument in @('--prepare',$realRequest,(Get-FileHash -LiteralPath $realRequest -Algorithm SHA256).Hash)){$start.ArgumentList.Add($argument)}
$process=[Diagnostics.Process]::Start($start);$realOut=$process.StandardOutput.ReadToEndAsync();$realErr=$process.StandardError.ReadToEndAsync()
try{
 if(-not $process.WaitForExit(240000)){$process.Kill($true);$process.WaitForExit(10000)|Out-Null;throw 'Owned real-runtime preparation exceeded its bound'}
 $result=$realOut.GetAwaiter().GetResult();if($process.ExitCode -ne 0 -or $result.Length -gt 524288){throw ('Real codec package preparation failed: '+$result.Substring(0,[Math]::Min(2048,$result.Length)))}
 $realPlan=$result|ConvertFrom-Json
 if($realPlan.StartService -or $realPlan.RuntimeRoot -ne $codecRoot -or -not @($realPlan.RuntimeFiles|Where-Object Path -eq 'bin/ffprobe.exe') -or -not @($realPlan.RuntimeFiles|Where-Object Path -eq 'Sources/ffmpeg-n9.0.2.tar.gz') -or -not @($realPlan.RuntimeFiles|Where-Object Path -eq 'Licenses/FDK-AAC-NOTICE.txt')){throw 'Real runtime preparation omitted required package contents'}
 @{Source=$env:GITHUB_SHA;Passed=$true;Scope='Actual CI codec-package hash and encoder-list preparation only; no service installation, target-PC, decoded-output or licensing determination';Files=$realPlan.RuntimeFiles.Count;ReferenceSha256=(Get-FileHash -LiteralPath $codecReference -Algorithm SHA256).Hash;ServiceChanged=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root 'artifacts/windows-real-runtime-preparation.json') -Encoding utf8NoBOM
}finally{$process.Dispose()}
$locks=Join-Path $root 'artifacts/windows-runtime-locks';$null=New-Item -ItemType Directory -Path $locks -Force
foreach($path in $before.Keys){Copy-Item -LiteralPath $path -Destination (Join-Path $locks ((Split-Path -Leaf (Split-Path -Parent $path))+'-packages.lock.json'))}
@{Source=$env:GITHUB_SHA;Scope='Windows x64 self-contained development package; no FFmpeg, music, credentials or production configuration';Files=$files.Count;UncompressedBytes=($files|Measure-Object Bytes -Sum).Sum;UniquePayloadBytes=(@($files|Group-Object Sha256|ForEach-Object {$_.Group[0]})|Measure-Object Bytes -Sum).Sum;ManifestSha256=(Get-FileHash -LiteralPath (Join-Path $destination 'bundle.json') -Algorithm SHA256).Hash;Commissioned=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root 'artifacts/windows-package-summary.json') -Encoding utf8NoBOM
