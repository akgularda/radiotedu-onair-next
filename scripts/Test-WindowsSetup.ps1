param([string]$BuildRoot,[string]$PackageRoot)
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true'){throw 'Disposable CI fixture only. Use the guided UI on the broadcasting PC.'}
$serviceName='RadioTEDU.OnAirNext.Development'
$install=[IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'RadioTEDU-OnAirNext-Development'))
$data=[IO.Path]::GetFullPath((Join-Path $env:ProgramData 'RadioTEDU-OnAirNext-Development'))
$fixture=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA ('RadioTEDU/OnAirNext-Dev/setup-fixture-'+[Guid]::NewGuid().ToString('N'))))
$expectedFixture=$fixture
$summary=Join-Path $PSScriptRoot '../artifacts/setup-smoke/summary.json'
$checks=[Collections.Generic.List[string]]::new();$completed=$false;$failure=$null;$owned=$false;$plan=$null
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
 $before=(Get-FileHash -LiteralPath (Join-Path $data 'host.json') -Algorithm SHA256).Hash
 $null=Run $setup @('--install',$planPath,$hash)
 Check 'Same reviewed plan retries without replacing configuration or restarting service' ((Get-FileHash -LiteralPath (Join-Path $data 'host.json') -Algorithm SHA256).Hash -eq $before -and (Get-CimInstance Win32_Service -Filter ("Name='"+$serviceName+"'")).ProcessId -eq $service.ProcessId)
 $completed=$true
}catch{$failure=$_.Exception.Message;throw}
finally{
 $env:ONAIR_SETUP_TEST_RUNTIME=$null
 if(-not $completed -and -not $failure){$failure='Setup fixture interrupted before completion'}
 if($owned -and (Test-Path -LiteralPath (Join-Path $data 'setup-ownership.json'))){
  $owner=Get-Content -LiteralPath (Join-Path $data 'setup-ownership.json') -Raw|ConvertFrom-Json
  if($owner.PlanId -ne $plan.Id){throw 'Setup fixture ownership mismatch; cleanup refused'}
  if(Get-Service -Name $serviceName -ErrorAction SilentlyContinue){Stop-Service -Name $serviceName -ErrorAction SilentlyContinue;& "$env:SystemRoot/System32/sc.exe" delete $serviceName|Out-Null}
  if($completed -and -not $failure){foreach($target in @(@{Path=$install;Expected=(Join-Path $env:ProgramFiles 'RadioTEDU-OnAirNext-Development')},@{Path=$data;Expected=(Join-Path $env:ProgramData 'RadioTEDU-OnAirNext-Development')})){$full=[IO.Path]::GetFullPath($target.Path);if($full -ne [IO.Path]::GetFullPath($target.Expected) -or (Get-Item -LiteralPath $full).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)){throw 'Setup cleanup target validation failed'};Remove-Item -LiteralPath $full -Recurse -Force}}
 }
 $null=New-Item -ItemType Directory -Path (Split-Path -Parent $summary) -Force
 @{Source=$env:GITHUB_SHA;Scope='Disposable Windows installer software fixture; runtime encoder listing is an explicit test double, no real audio or broadcasting-PC commissioning';Completed=$completed;Passed=($completed -and -not $failure);Checks=$checks.ToArray();Error=$failure;ProductionChanged=$false}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $summary -Encoding utf8NoBOM
}
