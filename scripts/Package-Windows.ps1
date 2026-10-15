param([string]$OutputRoot=(Join-Path $PSScriptRoot '../artifacts/windows-package'))
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
$files=@(foreach($file in Get-ChildItem -LiteralPath $destination -File -Recurse){@{Path=[IO.Path]::GetRelativePath($destination,$file.FullName).Replace('\','/');Bytes=$file.Length;Sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}})
@{Source=$env:GITHUB_SHA;Files=$files}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $destination 'bundle.json') -Encoding utf8NoBOM
@'
RadioTEDU OnAir Next — Windows development package

Open setup/OnAirNext.Setup.exe. It starts the local browser console without Codex.
In System, choose a locally supplied FFmpeg folder that lists libfdk_aac and FLAC.
Review the separate service installation, then approve the Windows administrator prompt.
All eight channels are initially stopped with zero outputs. The former service is not changed.

.NET is included. FFmpeg and music are not included. Preserve the runtime provider's license files.
This is a development package, not a commissioned radio release. Complete actual-PC UI,
codec/loudness, import/rollback and 24/72-hour gates before migrating one station group at a time.
'@|Set-Content -LiteralPath (Join-Path $destination 'READ-ME.txt') -Encoding utf8NoBOM
$locks=Join-Path $root 'artifacts/windows-runtime-locks';$null=New-Item -ItemType Directory -Path $locks -Force
foreach($path in $before.Keys){Copy-Item -LiteralPath $path -Destination (Join-Path $locks ((Split-Path -Leaf (Split-Path -Parent $path))+'-packages.lock.json'))}
@{Source=$env:GITHUB_SHA;Scope='Windows x64 self-contained development package; no FFmpeg, music, credentials or production configuration';Files=$files.Count;UncompressedBytes=($files|Measure-Object Bytes -Sum).Sum;UniquePayloadBytes=(@($files|Group-Object Sha256|ForEach-Object {$_.Group[0]})|Measure-Object Bytes -Sum).Sum;ManifestSha256=(Get-FileHash -LiteralPath (Join-Path $destination 'bundle.json') -Algorithm SHA256).Hash;Commissioned=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root 'artifacts/windows-package-summary.json') -Encoding utf8NoBOM
