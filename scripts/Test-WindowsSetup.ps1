param([string]$BuildRoot,[string]$PackageRoot)
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true'){throw 'Disposable CI fixture only. Use the guided UI on the broadcasting PC.'}
$serviceName='RadioTEDU.OnAirNext.Development'
$install=[IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'RadioTEDU-OnAirNext-Development'))
$data=[IO.Path]::GetFullPath((Join-Path $env:ProgramData 'RadioTEDU-OnAirNext-Development'))
$fixture=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA ('RadioTEDU/OnAirNext-Dev/setup-fixture-'+[Guid]::NewGuid().ToString('N'))))
$expectedFixture=$fixture
$summary=Join-Path $PSScriptRoot '../artifacts/setup-smoke/summary.json'
$checks=[Collections.Generic.List[string]]::new();$completed=$false;$failure=$null;$owned=$false;$plan=$null;$webProcess=$null;$shortcutPaths=@();$maintenance=$null;$review=$null
function Check([string]$name,[bool]$value){if(-not $value){throw ('Setup check failed: '+$name)};$checks.Add($name)}
function Run([string]$exe,[string[]]$arguments){
 $start=[Diagnostics.ProcessStartInfo]::new($exe);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 foreach($argument in $arguments){$start.ArgumentList.Add($argument)}
 $process=[Diagnostics.Process]::Start($start);$out=$process.StandardOutput.ReadToEndAsync();$err=$process.StandardError.ReadToEndAsync()
 try{if(-not $process.WaitForExit(240000)){$process.Kill($true);$process.WaitForExit(10000)|Out-Null;throw 'Owned setup fixture process exceeded its deadline'};if($process.ExitCode -ne 0){throw ('Setup process failed ('+$process.ExitCode+'): '+$err.GetAwaiter().GetResult())};return $out.GetAwaiter().GetResult()}finally{$process.Dispose()}
}
try{
 if((Test-Path -LiteralPath $install) -or (Test-Path -LiteralPath $data) -or (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)){throw 'The isolated roots or service already exist; fixture refused to overwrite them'}
 $null=New-Item -ItemType Directory -Path $fixture
 foreach($pair in @(@{Project='OnAirNext.Host';Folder='bin'},@{Project='OnAirNext.Web';Folder='console'},@{Project='OnAirNext.Setup';Folder='setup'})){
  $destination=Join-Path $fixture $pair.Folder;$null=New-Item -ItemType Directory -Path $destination
  $source=if($PackageRoot){Join-Path ([IO.Path]::GetFullPath($PackageRoot)) $pair.Folder}else{Join-Path $BuildRoot ($pair.Project+'/release')}
  foreach($entry in Get-ChildItem -LiteralPath $source -Force){Copy-Item -LiteralPath $entry.FullName -Destination $destination -Recurse}
 }
 $files=@(foreach($file in Get-ChildItem -LiteralPath $fixture -Recurse -File){@{Path=[IO.Path]::GetRelativePath($fixture,$file.FullName).Replace('\','/');Bytes=$file.Length;Sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}})
 @{Source=$env:GITHUB_SHA;Files=$files}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $fixture 'bundle.json') -Encoding utf8NoBOM
 $runtime=Join-Path $fixture 'runtime';$null=New-Item -ItemType Directory -Path $runtime
 foreach($entry in Get-ChildItem -LiteralPath (Join-Path $BuildRoot 'OnAirNext.Lab/release') -Force){Copy-Item -LiteralPath $entry.FullName -Destination $runtime -Recurse}
 Copy-Item -LiteralPath (Join-Path $runtime 'OnAirNext.Lab.exe') -Destination (Join-Path $runtime 'ffmpeg.exe')
 $request=Join-Path $fixture 'request.json';@{RuntimeFolder=$runtime;StartService=$true}|ConvertTo-Json|Set-Content -LiteralPath $request -Encoding utf8NoBOM
 $setup=Join-Path $fixture 'setup/OnAirNext.Setup.exe'
 $env:ONAIR_SETUP_TEST_RUNTIME='1'
 $prepared=Run $setup @('--prepare',$request,(Get-FileHash -LiteralPath $request -Algorithm SHA256).Hash)
 $plan=$prepared|ConvertFrom-Json
 Check 'Preparation keeps the service and machine roots absent' (-not (Test-Path -LiteralPath $data) -and -not (Test-Path -LiteralPath $install) -and -not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue))
 Check 'Preparation preserves the invoking operator SID' ($plan.OperatorSid -eq [Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
 $planPath=Join-Path $fixture 'plan.json';$prepared|Set-Content -LiteralPath $planPath -Encoding utf8NoBOM
 $hash=(Get-FileHash -LiteralPath $planPath -Algorithm SHA256).Hash
 $owned=$true
 $null=Run $setup @('--install',$planPath,$hash)
 $receipt=Get-Content -LiteralPath (Join-Path $data 'setup-status.json') -Raw|ConvertFrom-Json
 Check 'Installer records the exact plan and source version' ($receipt.Phase -eq 'Installed' -and $receipt.PlanId -eq $plan.Id -and $receipt.Source -eq $env:GITHUB_SHA)
 $config=Get-Content -LiteralPath (Join-Path $data 'host.json') -Raw|ConvertFrom-Json
 Check 'Initial installation contains eight stopped channels and no outputs' ($config.Stations.Count -eq 8 -and @($config.Stations|Where-Object {$_.AutoStart -or $_.Outputs.Count -ne 0}).Count -eq 0)
 $service=Get-CimInstance Win32_Service -Filter ("Name='"+$serviceName+"'")
 Check 'Only the isolated LocalService image is registered' ($service.StartName -eq 'NT AUTHORITY\LocalService' -and $service.PathName.StartsWith('"'+[IO.Path]::GetFullPath((Join-Path $install 'bin/OnAirNext.Host.exe'))+'"',[StringComparison]::OrdinalIgnoreCase))
 $lab=Join-Path $BuildRoot 'OnAirNext.Lab/release/OnAirNext.Lab.exe';$deadline=[DateTime]::UtcNow.AddSeconds(45);$state=$null
 do{try{$state=(Run $lab @('--service-probe','status'))|ConvertFrom-Json;if($state.stations.Count -eq 8){break}}catch{};Start-Sleep -Milliseconds 200}while([DateTime]::UtcNow -lt $deadline)
 Check 'Installed engine is authenticated and all eight requested states are stopped' ($state.stations.Count -eq 8 -and @($state.stations|Where-Object RequestedRunning).Count -eq 0)
 $runtimeHash=(Get-FileHash -LiteralPath (Join-Path $runtime 'ffmpeg.exe') -Algorithm SHA256).Hash
 if($PackageRoot){$hostProcess=[Diagnostics.Process]::GetProcessById($service.ProcessId);try{Check 'Installed service uses its packaged runtime without global dotnet' (@($hostProcess.Modules|Where-Object {$_.ModuleName -eq 'coreclr.dll' -and $_.FileName.StartsWith((Join-Path $install 'bin')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)}).Count -eq 1)}finally{$hostProcess.Dispose()}}
 Check 'Runtime copy preserves the selected bytes' ((Get-FileHash -LiteralPath (Join-Path $install 'tools/ffmpeg.exe') -Algorithm SHA256).Hash -eq $runtimeHash)
 if($PackageRoot){
  $ids=@(foreach($folder in @('bin','console','setup')){$result=& "$env:SystemRoot/System32/fsutil.exe" file queryfileid (Join-Path $install ($folder+'/coreclr.dll'));if($LASTEXITCODE -ne 0){throw 'Runtime file identity query failed'};[regex]::Match(($result -join ' '),'0x[0-9a-fA-F]+').Value})
  Check 'Identical immutable runtimes share one installed file identity' ($ids.Count -eq 3 -and $ids[0] -ne '' -and @($ids|Select-Object -Unique).Count -eq 1)
 }
 $before=(Get-FileHash -LiteralPath (Join-Path $data 'host.json') -Algorithm SHA256).Hash
 $null=Run $setup @('--install',$planPath,$hash)
 Check 'Same reviewed plan retries without replacing configuration or restarting service' ((Get-FileHash -LiteralPath (Join-Path $data 'host.json') -Algorithm SHA256).Hash -eq $before -and (Get-CimInstance Win32_Service -Filter ("Name='"+$serviceName+"'")).ProcessId -eq $service.ProcessId)
 # Exercise the actual packaged HTTP/CSRF route; this is engineering integration, not mouse acceptance.
 $shortcutCandidates=@([Environment]::GetFolderPath('DesktopDirectory'),[Environment]::GetFolderPath('Programs'))|ForEach-Object {Join-Path $_ 'RadioTEDU OnAir Next.lnk'}
 foreach($path in $shortcutCandidates){if(Test-Path -LiteralPath $path){throw 'Unexpected existing CI shortcut; fixture refuses to replace it'}}
 $shortcutPaths=$shortcutCandidates
 $webStart=[Diagnostics.ProcessStartInfo]::new((Join-Path $install 'console/OnAirNext.Web.exe'));$webStart.UseShellExecute=$false;$webStart.CreateNoWindow=$true
 $webProcess=[Diagnostics.Process]::Start($webStart);$deadline=[DateTime]::UtcNow.AddSeconds(25);$session=$null
 do{try{$endpointPath=Join-Path $env:LOCALAPPDATA 'RadioTEDU/OnAirNext-Dev/browser-console/access/console.json';$endpoint=Get-Content -LiteralPath $endpointPath -Raw|ConvertFrom-Json;$session=Invoke-RestMethod -Uri 'http://127.0.0.1:18121/api/session' -Headers @{'X-Studio-Bootstrap'=$endpoint.Token} -SessionVariable setupWebSession -TimeoutSec 2;break}catch{};Start-Sleep -Milliseconds 200}while([DateTime]::UtcNow -lt $deadline)
 Check 'Packaged console starts without Codex or a global SDK' ([bool]$session.token)
 $anonymousRejected=$false;try{$null=Invoke-RestMethod -Uri 'http://127.0.0.1:18121/api/session' -TimeoutSec 3}catch{$anonymousRejected=([int]$_.Exception.Response.StatusCode -eq 403)}
 Check 'Anonymous loopback callers cannot obtain an operator session' $anonymousRejected
 $browseRejected=$false;try{$null=Invoke-RestMethod -Uri 'http://127.0.0.1:18121/api/folders' -TimeoutSec 3}catch{$browseRejected=([int]$_.Exception.Response.StatusCode -eq 403)}
 Check 'Read-only folder browsing requires the authorized console session' $browseRejected
 $allowed=@([Security.Principal.WindowsIdentity]::GetCurrent().User.Value,'S-1-5-18','S-1-5-32-544');$rules=(Get-Acl -LiteralPath $endpointPath).GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])
 Check 'Startup capability is stored without unlisted account access' (@($rules|Where-Object {$_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -notin $allowed}).Count -eq 0)

 $health=Invoke-RestMethod -Uri 'http://127.0.0.1:18121/api/health' -TimeoutSec 3
 Check 'Console health exposes the exact compiled build identity' ($health.product -eq 'RadioTEDU-OnAirNext' -and $health.version.EndsWith($env:GITHUB_SHA))
 $headers=@{'X-Studio-Token'=$session.token;Origin='http://127.0.0.1:18121'}
 $created=Invoke-RestMethod -Uri 'http://127.0.0.1:18121/api/setup/shortcuts' -Method Post -WebSession $setupWebSession -Headers $headers -TimeoutSec 10
 $shell=New-Object -ComObject WScript.Shell
 try{foreach($path in $shortcutPaths){$shortcut=$shell.CreateShortcut($path);try{Check 'Per-user shortcut targets the installed launcher with console arguments' ($shortcut.TargetPath -eq [IO.Path]::GetFullPath((Join-Path $install 'setup/OnAirNext.Setup.exe')) -and $shortcut.Arguments -eq '--open-console')}finally{[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)|Out-Null}}}finally{[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)|Out-Null}
 $shortcutHash=(Get-FileHash -LiteralPath $shortcutPaths[0] -Algorithm SHA256).Hash
 $null=Invoke-RestMethod -Uri 'http://127.0.0.1:18121/api/setup/shortcuts' -Method Post -WebSession $setupWebSession -Headers $headers -TimeoutSec 10
 Check 'Repeating shortcut creation preserves existing matching shortcut bytes' ((Get-FileHash -LiteralPath $shortcutPaths[0] -Algorithm SHA256).Hash -eq $shortcutHash)
 $null=Invoke-RestMethod -Uri 'http://127.0.0.1:18121/api/console/close' -Method Post -WebSession $setupWebSession -Headers $headers -TimeoutSec 10
 Check 'Visible console-close route releases its own web process' ($webProcess.WaitForExit(15000))
 $afterConsole=Get-CimInstance Win32_Service -Filter ("Name='"+$serviceName+"'")
 Check 'Closing the console preserves the same running engine service' ($afterConsole.State -eq 'Running' -and $afterConsole.ProcessId -eq $service.ProcessId)
 # Read-only package review must not reuse the currently installed manifest.
 $samePackageRejected=$false;try{$null=Run $setup @('--prepare-update')}catch{$samePackageRejected=$true}
 Check 'Read-only maintenance review rejects the already installed package' $samePackageRejected
 # Synthetic replacement identity only: all executable bytes remain this CI build.
 $replacementManifest=Get-Content -LiteralPath (Join-Path $fixture 'bundle.json') -Raw|ConvertFrom-Json
 $replacementManifest.Source=('a'*40)
 $replacementManifest|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $fixture 'bundle.json') -Encoding utf8NoBOM
 $review=(Run $setup @('--prepare-update'))|ConvertFrom-Json
 Check 'Read-only maintenance review binds both manifests and schema-one rollback contract' ($review.Files.Original.BundleSha256 -eq $plan.BundleSha256 -and $review.Files.Replacement.BundleSha256 -eq (Get-FileHash -LiteralPath (Join-Path $fixture 'bundle.json') -Algorithm SHA256).Hash -and $review.JournalSchemaVersion -eq 1)
 Check 'Read-only review retains original operator configuration and exact service process' ($review.OperatorSid -eq $plan.OperatorSid -and $review.ConfigurationSha256 -eq $before -and $review.ServiceProcessId -eq $service.ProcessId -and (Get-FileHash -LiteralPath (Join-Path $data 'host.json') -Algorithm SHA256).Hash -eq $before)
 $maintenance=(Run $lab @('--service-maintenance-probe'))|ConvertFrom-Json
 foreach($check in $maintenance.Checks){Check $check.Description $check.Passed}
 Check 'Maintenance fixture completed and restored its owned service' $maintenance.Passed
 $completed=$true
}catch{$failure=$_.Exception.Message;throw}
finally{
 if($webProcess){try{if(-not $webProcess.HasExited){$webProcess.Kill($true);$webProcess.WaitForExit(10000)|Out-Null}}finally{$webProcess.Dispose()}}
 foreach($path in $shortcutPaths){if(Test-Path -LiteralPath $path){$full=[IO.Path]::GetFullPath($path);if([IO.Path]::GetFileName($full) -ne 'RadioTEDU OnAir Next.lnk' -or (Get-Item -LiteralPath $full).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)){throw 'Shortcut cleanup validation failed'};Remove-Item -LiteralPath $full -Force}}
 $env:ONAIR_SETUP_TEST_RUNTIME=$null
 if(-not $completed -and -not $failure){$failure='Setup fixture interrupted before completion'}
 if($owned -and (Test-Path -LiteralPath (Join-Path $data 'setup-ownership.json'))){
  $owner=Get-Content -LiteralPath (Join-Path $data 'setup-ownership.json') -Raw|ConvertFrom-Json
  if($owner.PlanId -ne $plan.Id){throw 'Setup fixture ownership mismatch; cleanup refused'}
  if(Get-Service -Name $serviceName -ErrorAction SilentlyContinue){Stop-Service -Name $serviceName -ErrorAction SilentlyContinue;& "$env:SystemRoot/System32/sc.exe" delete $serviceName|Out-Null}
  if($completed -and -not $failure){foreach($target in @(@{Path=$install;Expected=(Join-Path $env:ProgramFiles 'RadioTEDU-OnAirNext-Development')},@{Path=$data;Expected=(Join-Path $env:ProgramData 'RadioTEDU-OnAirNext-Development')})){$full=[IO.Path]::GetFullPath($target.Path);if($full -ne [IO.Path]::GetFullPath($target.Expected) -or (Get-Item -LiteralPath $full).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)){throw 'Setup cleanup target validation failed'};Remove-Item -LiteralPath $full -Recurse -Force}}
 }
 $null=New-Item -ItemType Directory -Path (Split-Path -Parent $summary) -Force
 @{Source=$env:GITHUB_SHA;Scope='Disposable Windows installer software fixture; runtime encoder listing is an explicit test double, no real audio or broadcasting-PC commissioning';Completed=$completed;Passed=($completed -and -not $failure);Checks=$checks.ToArray();Maintenance=$maintenance;UpdateReview=$review;Error=$failure;ProductionChanged=$false}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $summary -Encoding utf8NoBOM
}
