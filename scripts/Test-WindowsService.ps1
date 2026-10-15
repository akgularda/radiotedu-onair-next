param([string]$BuildRoot)
$ErrorActionPreference='Stop'
$serviceName='RadioTEDU.OnAirNext.Development'
$installRoot=[IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'RadioTEDU-OnAirNext-Development'))
$dataRoot=[IO.Path]::GetFullPath((Join-Path $env:ProgramData 'RadioTEDU-OnAirNext-Development'))
$expectedInstall=$installRoot; $expectedData=$dataRoot
$bin=Join-Path $installRoot 'bin'; $tools=Join-Path $installRoot 'tools'; $engineData=Join-Path $dataRoot 'data'
$config=Join-Path $dataRoot 'host.json'; $marker=Join-Path $engineData 'owned-probe.json'
$hostExe=Join-Path $bin 'OnAirNext.Host.exe'; $fakeExe=Join-Path $tools 'ffmpeg.exe'
$labExe=Join-Path ([IO.Path]::GetFullPath($BuildRoot)) 'OnAirNext.Lab/release/OnAirNext.Lab.exe'
$hostBuild=Join-Path ([IO.Path]::GetFullPath($BuildRoot)) 'OnAirNext.Host/release'
$labBuild=Split-Path -Parent $labExe
$checks=[Collections.Generic.List[string]]::new();$ownedInstall=$false;$ownedData=$false;$ownedService=$false;$failure=$null
$operatorSid=[Security.Principal.WindowsIdentity]::GetCurrent().User
$adminSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$systemSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$localServiceSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-19')
$summary=Join-Path $PSScriptRoot '../artifacts/service-smoke/summary.json'
function Check([string]$name,[bool]$condition){if(-not $condition){throw "Service check failed: $name"};$checks.Add($name)}
function Protect([string]$path,[bool]$writable){
 $full=[IO.Path]::GetFullPath($path)
 if(-not ($full -eq $dataRoot -or $full.StartsWith($dataRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $full -eq $installRoot -or $full.StartsWith($installRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase))){throw 'ACL target escaped the fixture roots'}
 $isDirectory=[IO.Directory]::Exists($full)
 $acl=if($isDirectory){[Security.AccessControl.DirectorySecurity]::new()}else{[Security.AccessControl.FileSecurity]::new()}
 $acl.SetAccessRuleProtection($true,$false);$acl.SetOwner($adminSid)
 $inherit=if($isDirectory){[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'}else{[Security.AccessControl.InheritanceFlags]::None}
 foreach($sid in @($adminSid,$systemSid)){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl',$inherit,'None','Allow'))}
 $rights=if($writable){'Modify'}else{'ReadAndExecute'}
 $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($localServiceSid,$rights,$inherit,'None','Allow'))
 $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($operatorSid,'ReadAndExecute',$inherit,'None','Allow'))
 Set-Acl -LiteralPath $full -AclObject $acl
}
function Invoke-OwnedSc([string[]]$values){$output=& "$env:SystemRoot/System32/sc.exe" @values 2>&1;if($LASTEXITCODE -ne 0){throw ('Service fixture operation failed: '+($output -join ' '))}}
function Probe([string]$action){$output=& $labExe --service-probe $action 2>&1;if($LASTEXITCODE -ne 0){throw ('Authenticated service probe failed: '+($output -join ' '))};return ($output -join [Environment]::NewLine)|ConvertFrom-Json}
function Ready([Nullable[bool]]$wanted){
 $deadline=[DateTime]::UtcNow.AddSeconds(50);$last='not ready'
 do{try{$state=Probe 'status';if($state.controller.mode -eq 'windows-service' -and $state.stations.Count -eq 1 -and ($null -eq $wanted -or $state.stations[0].RequestedRunning -eq $wanted)){return $state}}catch{$last=$_.Exception.Message};Start-Sleep -Milliseconds 200}while([DateTime]::UtcNow -lt $deadline)
 throw ('Service did not reach authenticated readiness: '+$last.Substring(0,[Math]::Min($last.Length,700)))
}
function Stopped(){
 $deadline=[DateTime]::UtcNow.AddSeconds(55)
 do{$service=Get-Service -Name $serviceName -ErrorAction Stop;if($service.Status -eq 'Stopped'){return};Start-Sleep -Milliseconds 200}while([DateTime]::UtcNow -lt $deadline)
 throw 'Service did not stop within the cleanup deadline'
}
function OwnProcess([int]$pidValue,[string]$expected,[long]$ticks){
 $process=[Diagnostics.Process]::GetProcessById($pidValue);$null=$process.SafeHandle
 if(-not $process.MainModule.FileName.Equals($expected,[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture process image does not match'}
 if($ticks -gt 0 -and $process.StartTime.ToUniversalTime().Ticks -ne $ticks){throw 'Fixture process identity was reused'}
 return $process
}
function Children(){
 $deadline=[DateTime]::UtcNow.AddSeconds(25)
 do{if(Test-Path -LiteralPath $marker){try{$m=Get-Content -LiteralPath $marker -Raw|ConvertFrom-Json;$parent=OwnProcess $m.ParentPid $fakeExe $m.ParentStartTicks;$leaf=OwnProcess $m.ChildPid $fakeExe $m.ChildStartTicks;return @($parent,$leaf)}catch{}};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline)
 throw 'Owned codec fixture descendants did not become observable'
}
function ClearMarker(){if(Test-Path -LiteralPath $marker){Remove-Item -LiteralPath $marker -Force}}
try{
 if(-not [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'CI service fixture requires its isolated elevated runner'}
 if(Get-Service -Name $serviceName -ErrorAction SilentlyContinue){throw 'An existing service must never be replaced by the fixture'}
 if((Test-Path -LiteralPath $installRoot) -or (Test-Path -LiteralPath $dataRoot)){throw 'Existing fixture roots must not be overwritten'}
 $null=New-Item -ItemType Directory -Path $installRoot;$ownedInstall=$true;Protect $installRoot $false
 $null=New-Item -ItemType Directory -Path $dataRoot;$ownedData=$true;Protect $dataRoot $false
 foreach($directory in @($bin,$tools,$engineData)){$null=New-Item -ItemType Directory -Path $directory;Protect $directory ($directory -eq $engineData)}
 foreach($entry in Get-ChildItem -LiteralPath $hostBuild -Force){Copy-Item -LiteralPath $entry.FullName -Destination $bin -Recurse}
 foreach($entry in Get-ChildItem -LiteralPath $labBuild -Force){Copy-Item -LiteralPath $entry.FullName -Destination $tools -Recurse}
 Copy-Item -LiteralPath (Join-Path $tools 'OnAirNext.Lab.exe') -Destination $fakeExe
 foreach($item in Get-ChildItem -LiteralPath $installRoot -Recurse -Force){Protect $item.FullName $false}
 $settings=@{Ffmpeg=$fakeExe;DataRoot=$engineData;PipeName='onair-next-dev-service';OperatorSid=$operatorSid.Value;Stations=@(@{Id='service-fixture';Playlist=@();Outputs=@();Repeat=$true;Enabled=$true;AutoStart=$false})}
 $settings|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $config -Encoding UTF8;Protect $config $false
 $command='"'+$hostExe+'" --windows-service --config "'+$config+'"'
 Invoke-OwnedSc @('create',$serviceName,'binPath=',$command,'start=','auto','obj=','NT AUTHORITY\LocalService');$ownedService=$true
 Invoke-OwnedSc @('config',$serviceName,'start=','delayed-auto')
 Invoke-OwnedSc @('failure',$serviceName,'reset=','86400','actions=','restart/1000/restart/5000/restart/30000')
 Invoke-OwnedSc @('failureflag',$serviceName,'1')
 $registry=Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\'+$serviceName)
 Check 'Automatic delayed startup is persisted' ($registry.Start -eq 2 -and $registry.DelayedAutoStart -eq 1 -and $registry.FailureActionsOnNonCrashFailures -eq 1)
 Start-Service -Name $serviceName;$state=Ready $false
 Check 'Authenticated operator crosses the service identity boundary' ($state.controller.mode -eq 'windows-service')
 $service=Get-CimInstance -ClassName Win32_Service -Filter ("Name='"+$serviceName+"'")
 Check 'Service uses its separate LocalService identity' ($service.StartName -eq 'NT AUTHORITY\LocalService')
 Check 'No browser console is required for service readiness' (-not (Get-Process -Name 'OnAirNext.Web' -ErrorAction SilentlyContinue))
 Check 'Developer global stop is not a service command' ((Probe 'stop').accepted -eq $false)
 Check 'Start intent is durably acknowledged' ((Probe 'start-station').accepted -eq $true)
 $null=Ready $true;$oldChildren=Children
 Stop-Service -Name $serviceName;Stopped
 foreach($child in $oldChildren){Check 'Graceful service exit releases owned descendants' ($child.WaitForExit(10000));$child.Dispose()}
 ClearMarker;Start-Service -Name $serviceName;$null=Ready $true
 Check 'SCM stop preserves requested-running intent' $true
 $children=Children;$service=Get-CimInstance -ClassName Win32_Service -Filter ("Name='"+$serviceName+"'")
 $root=OwnProcess $service.ProcessId $hostExe 0
 Add-Type -TypeDefinition 'using System.Runtime.InteropServices; using Microsoft.Win32.SafeHandles; public static class OnAirOwnedCrash { [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool TerminateProcess(SafeProcessHandle process,uint code); }'
 if(-not [OnAirOwnedCrash]::TerminateProcess($root.SafeHandle,177)){throw 'Owned fixture host could not be terminated'}
 Check 'Only the verified held host handle is terminated' ($root.WaitForExit(10000));$root.Dispose()
 foreach($child in $children){Check 'Kernel job releases descendants after parent-only crash' ($child.WaitForExit(10000));$child.Dispose()}
 $null=Ready $true
 Check 'SCM recovers the host automatically without browser or manual start' $true
 Check 'Unexpected process death preserves requested-running intent' $true
 Check 'Explicit station stop is acknowledged' ((Probe 'stop-station').accepted -eq $true)
 Stop-Service -Name $serviceName;Stopped;ClearMarker;Start-Service -Name $serviceName;$null=Ready $false
 Check 'Explicit stopped intent survives service restart' $true
 Stop-Service -Name $serviceName;Stopped
}catch{$failure=$_.Exception.Message;throw}
finally{
 if($ownedService){try{if((Get-Service -Name $serviceName).Status -ne 'Stopped'){Stop-Service -Name $serviceName;Stopped};Invoke-OwnedSc @('delete',$serviceName)}catch{if(-not $failure){$failure=$_.Exception.Message}}}
 # Retain fixture data on CI failure for runner-local diagnosis; upload only this sanitized summary.
 foreach($target in @(@{Path=$installRoot;Expected=$expectedInstall;Owned=$ownedInstall},@{Path=$dataRoot;Expected=$expectedData;Owned=$ownedData})){
  if($target.Owned -and -not $failure){$full=[IO.Path]::GetFullPath($target.Path);if($full -ne $target.Expected -or (Get-Item -LiteralPath $full).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)){throw 'Cleanup target validation failed'};Remove-Item -LiteralPath $full -Recurse -Force}
 }
 $null=New-Item -ItemType Directory -Path (Split-Path -Parent $summary) -Force
 @{Source=$env:GITHUB_SHA;Scope='Isolated Windows CI fixture; no real audio or target-PC commissioning';Passed=(-not $failure);Checks=@($checks);Error=$failure;ProductionChanged=$false}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $summary -Encoding UTF8
 if($failure){Write-Error $failure}
}
