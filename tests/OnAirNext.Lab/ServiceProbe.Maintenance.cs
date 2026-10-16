using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using OnAirNext.Installation;
using OnAirNext.ServiceControl;

namespace OnAirNext.Lab;

internal static partial class ServiceProbe
{
    // CI-only: never exposes a broadcasting-PC maintenance command.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static async Task<int> MaintenanceAsync()
    {
        if(!OperatingSystem.IsWindows()||Environment.GetEnvironmentVariable("GITHUB_ACTIONS")!="true"||
            Environment.GetEnvironmentVariable("ONAIR_SETUP_TEST_RUNTIME")!="1"||
            Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RadioTEDU","OnAir")))
            throw new InvalidOperationException("Disposable Windows CI setup fixture only; production maintenance must use the reviewed UI.");
        var checks=new List<object>();string? error=null;var changed=false;var restored=false;
        var evidence=Path.Combine(ServiceEndpoint.DataRoot,"maintenance-fixture-result.json");
        void Check(string description,bool passed){checks.Add(new{Description=description,Passed=passed});if(!passed)throw new InvalidOperationException(description);}
        async Task ReadyAsync()
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40));
            while(true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try
                {
                    var service=SetupService.Read()??throw new IOException("Service missing.");SetupService.RequireOwned(service);
                    if(service.State==4)
                    {
                        UpdateAdmission.RequireUnarmedStatus(await RequestAsync(new{Id=Guid.NewGuid().ToString("N"),Action="status"}));
                        return;
                    }
                }
                catch(Exception ex)when(ex is IOException or InvalidOperationException or OperationCanceledException) { }
                await Task.Delay(200,deadline.Token);
            }
        }
        async Task RestoreAsync()
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(65));
            await SetupService.InstallAsync(true,deadline.Token);await ReadyAsync();
            var current=SetupService.Read()!;
            Check("Maintenance restores owned delayed-auto service and exact 5/15/60-second recovery policy",current.State==4&&current.StartType==2&&current.Delayed&&current.FailureFlag&&current.RecoveryActions==3&&current.FailureActionsCount==3&&!current.HasRecoveryCommand);
            restored=true;
        }
        try
        {
            var config=await File.ReadAllBytesAsync(ServiceEndpoint.ConfigPath);
            using(var document=JsonDocument.Parse(config))UpdateAdmission.RequireUnarmedConfiguration(document.RootElement,WindowsIdentity.GetCurrent().User!.Value);
            await ReadyAsync();var before=SetupService.Read()!;SetupService.RequireOwned(before);
            Check("Maintenance fixture starts with eight explicitly stopped channels and zero outputs",true);
            var rejected=false;
            try{await SetupService.SuspendForMaintenanceAsync(before.ProcessId==uint.MaxValue?1:before.ProcessId+1,CancellationToken.None);}
            catch(InvalidOperationException){rejected=true;}
            var unchanged=SetupService.Read()!;
            Check("A stale reviewed process is rejected before changing service policy or process",rejected&&unchanged==before);
            using var originalProcess=Process.GetProcessById(checked((int)before.ProcessId));_=originalProcess.SafeHandle;
            changed=true;await SetupService.SuspendForMaintenanceAsync(before.ProcessId,CancellationToken.None);
            SetupService.RequireMaintenanceStopped();
            Check("Maintenance gracefully stops the exact reviewed process and verifies the disabled recovery fence",originalProcess.HasExited);
            var database=Path.Combine(ServiceEndpoint.EngineData,"playback.db");
            await using(var writer=new FileStream(database+".writer.lock",FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            {
                var inspection=await InspectMaintenanceJournalAsync(database);
                Check("The packaged host inspects schema one and all eight stopped journal intents while the writer lock stays held",inspection.SchemaVersion==1&&inspection.Integrity=="ok"&&inspection.Stations.Length==8&&inspection.Stations.All(s=>!s.RequestedRunning));
            }
            var denied=await MaintenanceScAsync(["start",ServiceEndpoint.Name]);
            Check("Windows refuses an explicit start while the maintenance fence is disabled",denied==1058);
            SetupService.RequireMaintenanceStopped();
            await SetupService.SuspendForMaintenanceAsync(null,CancellationToken.None);SetupService.RequireMaintenanceStopped();
            Check("Repeated maintenance resumes the same stopped fence without starting the engine",true);
            await RestoreAsync();
            var afterFirstRestore=await File.ReadAllBytesAsync(ServiceEndpoint.ConfigPath);
            Check("Maintenance leaves all installed channel configuration bytes unchanged",SHA256.HashData(config).SequenceEqual(SHA256.HashData(afterFirstRestore)));
            // Simulate interruption after the first durable SCM mutation, before actions are cleared.
            restored=false;
            Check("Partial-fence fixture disables only its owned development service",await MaintenanceScAsync(["config",ServiceEndpoint.Name,"start=","disabled"])==0);
            var partial=SetupService.Read()!;
            Check("Partial fence retains the running process and original recovery actions",partial.State==4&&partial.StartType==4&&partial.RecoveryActions==3);
            await SetupService.SuspendForMaintenanceAsync(null,CancellationToken.None);SetupService.RequireMaintenanceStopped();
            Check("A resumed partial fence clears recovery actions and completes a graceful stop",true);
            await RestoreAsync();
            var afterSecondRestore=await File.ReadAllBytesAsync(ServiceEndpoint.ConfigPath);
            Check("Repeated maintenance and restoration retain all configuration bytes",config.SequenceEqual(afterSecondRestore));
        }
        catch(Exception ex){error=ex.ToString();}
        finally
        {
            if(changed&&!restored)
            {
                try{await RestoreAsync();}
                catch(Exception ex){error=(error??"")+"\nRestore failure: "+ex;}
            }
        }
        var result=new{Scope="Disposable Windows SCM lifecycle fixture; no image swap, real audio, production service or target-PC acceptance.",Passed=error is null,Checks=checks,Error=error};
        var json=JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true});
        await File.WriteAllTextAsync(evidence,json);Console.WriteLine(json);
        // The parent reads Passed and retains this JSON even when a check failed.
        return 0;
    }
    private static async Task<OnAirNext.Engine.MaintenanceJournalInspection> InspectMaintenanceJournalAsync(string database)
    {
        var start=new ProcessStartInfo(Path.Combine(ServiceEndpoint.InstallRoot,"bin","OnAirNext.Host.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add("--inspect-maintenance-journal");start.ArgumentList.Add(database);
        using var process=Process.Start(start)??throw new IOException("Packaged journal inspector did not start.");
        var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(deadline.Token);var json=await output;await errors;
            if(process.ExitCode!=0||json.Length>16384)throw new IOException("The packaged journal inspector did not return a bounded successful result.");
            return JsonSerializer.Deserialize<OnAirNext.Engine.MaintenanceJournalInspection>(json)??throw new IOException("Journal inspection result is missing.");
        }
        finally{if(!process.HasExited){process.Kill();using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));await process.WaitForExitAsync(cleanup.Token);}}
    }
    private static async Task<int> MaintenanceScAsync(string[] arguments)
    {
        var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"sc.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in arguments)start.ArgumentList.Add(argument);
        using var process=Process.Start(start)??throw new IOException("Fixture SCM client did not start.");
        var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try{await process.WaitForExitAsync(deadline.Token);await output;await errors;return process.ExitCode;}
        finally{if(!process.HasExited){process.Kill();using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));await process.WaitForExitAsync(cleanup.Token);}}
    }
}
