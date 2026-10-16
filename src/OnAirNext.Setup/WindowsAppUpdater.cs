using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using OnAirNext.ServiceControl;

namespace OnAirNext.Installation;

[SupportedOSPlatform("windows")]
internal static class WindowsAppUpdater
{
    public static async Task<int> RunAsync(string request,string expected,UpdateDirection direction)
    {
        using var identity=WindowsIdentity.GetCurrent();
        try{using var process=Process.GetCurrentProcess();process.PriorityClass=ProcessPriorityClass.BelowNormal;}catch(System.ComponentModel.Win32Exception){}
        if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))throw new UnauthorizedAccessException("Approve Windows administrator access for the reviewed update.");
        if(!Enum.IsDefined(direction))throw new ArgumentException("Choose update or rollback.");
        var review=await SetupPaths.ReadVerifiedAsync<AppUpdateReview>(request,expected,CancellationToken.None);
        if(review.JournalSchemaVersion!=1||review.Files is null||!Guid.TryParseExact(review.Files.Id,"N",out _)||review.ServiceStartedAtUtc==default)
            throw new ArgumentException("Prepare a fresh compatible application update review.");
        var account=new SecurityIdentifier(review.OperatorSid);var id=review.Files.Id;
        var swap=new UpdateSwap(ServiceEndpoint.InstallRoot,id,review.Files.Original,review.Files.Replacement);
        var external=Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.FullName;
        external=SetupPaths.CanonicalRoot(external);
        if(external.Equals(ServiceEndpoint.InstallRoot,StringComparison.OrdinalIgnoreCase)||external.StartsWith(ServiceEndpoint.InstallRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
            external.Equals(ServiceEndpoint.DataRoot,StringComparison.OrdinalIgnoreCase)||external.StartsWith(ServiceEndpoint.DataRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
            external.StartsWith(ServiceEndpoint.InstallRoot+".candidate-",StringComparison.OrdinalIgnoreCase)||external.StartsWith(ServiceEndpoint.InstallRoot+".previous-",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Run this update from the complete external package used for its review.");
        await SetupPaths.VerifyBundleAsync(external,review.Files.Replacement.BundleSha256,CancellationToken.None);
        foreach(var path in new[]{ServiceEndpoint.DataRoot,ServiceEndpoint.ConfigPath,Path.Combine(ServiceEndpoint.DataRoot,"setup-plan.json"),Path.Combine(ServiceEndpoint.DataRoot,"setup-ownership.json"),Path.Combine(ServiceEndpoint.DataRoot,"setup.lock")})WindowsInstaller.AssertProtected(path);
        await using var setupLock=new FileStream(Path.Combine(ServiceEndpoint.DataRoot,"setup.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        await VerifyRetainedConfigurationAsync(review);
        var updates=Path.Combine(ServiceEndpoint.DataRoot,"updates");WindowsInstaller.CreateProtected(updates,account,false);
        var folder=Path.Combine(updates,id);WindowsInstaller.CreateProtected(folder,account,false);
        await WindowsInstaller.WriteNewAsync(Path.Combine(folder,"app-review.json"),review);
        var reviewBytes=JsonSerializer.SerializeToUtf8Bytes(review);var fullHash=Convert.ToHexString(SHA256.HashData(reviewBytes));
        var store=new UpdateOperationStore(ServiceEndpoint.InstallRoot,folder,WindowsInstaller.SetOwner);
        var fileHash=await store.SaveReviewAsync(review.Files);
        var prior=await store.ReadIntentAsync(id,fileHash);
        if(prior?.Direction==UpdateDirection.Rollback&&direction==UpdateDirection.Apply)throw new InvalidOperationException("This operation has retained rollback intent. Prepare a new review before applying another update.");
        FileStream? writer=null;var maintenance=false;var executionPhase="Staging candidate";
        try
        {
            // Staging resumes verified manifest-owned files, retaining all other data.
            // It never copies over the running installation or changes its initial plan.
            if(!Directory.Exists(swap.PreviousRoot)&&Directory.Exists(swap.InstallRoot))
            {
                var installedHash=await SetupPaths.HashAsync(Path.Combine(swap.InstallRoot,"bundle.json"));
                if(installedHash.Equals(review.Files.Original.BundleSha256,StringComparison.OrdinalIgnoreCase))await StageAsync(review,swap,account,external);
            }
            var disk=await swap.InspectAsync();
            var wanted=direction==UpdateDirection.Apply?UpdateDiskState.Applied:UpdateDiskState.Prepared;
            var current=SetupService.Read()??throw new IOException("The owned service is missing.");SetupService.RequireOwned(current);
            // A lost post-start acknowledgement must not stop an already verified image.
            if(prior?.Direction==direction&&disk==wanted&&current.State==4&&current.StartType==2&&current.RecoveryActions==3)
            {
                await VerifyReadyAsync(review);await CompleteAsync(folder,fullHash,direction,review);return 0;
            }
            async Task Fence()
            {
                SetupService.RequireMaintenanceStopped();
                if(writer is null)throw new IOException("The new playback writer lock is not held.");
                await VerifyRetainedConfigurationAsync(review);
            }
            async Task Progress(string phase)
            {
                executionPhase=phase;
                if(phase is not ("Update requested" or "Rollback requested"))return;
                await VerifyRetainedConfigurationAsync(review);
                var service=SetupService.Read()??throw new IOException("The owned service is missing.");SetupService.RequireOwned(service);
                if(service.State==4)
                {
                    UpdateAdmission.RequireUnarmedStatus(await AppUpdatePreparation.StatusAsync(CancellationToken.None));
                    if(prior is null)
                    {
                        using var process=Process.GetProcessById(checked((int)service.ProcessId));
                        if(service.ProcessId!=review.ServiceProcessId||new DateTimeOffset(process.StartTime.ToUniversalTime())!=review.ServiceStartedAtUtc)
                            throw new InvalidOperationException("The reviewed process changed. Prepare a fresh update review.");
                    }
                }
                maintenance=true;
                await SetupService.SuspendForMaintenanceAsync(prior is null?review.ServiceProcessId:null,CancellationToken.None);
                var database=Path.Combine(ServiceEndpoint.EngineData,"playback.db");SetupPaths.RequireNoLinks(database+".writer.lock");
                writer=new FileStream(database+".writer.lock",FileMode.Open,FileAccess.ReadWrite,FileShare.None);
                await Fence();
                var inspector=Directory.Exists(swap.CandidateRoot)?Path.Combine(swap.CandidateRoot,"bin","OnAirNext.Host.exe"):Path.Combine(swap.InstallRoot,"bin","OnAirNext.Host.exe");
                executionPhase="Inspecting stopped journal";await InspectJournalAsync(inspector,database);
                executionPhase="Verifying metadata snapshot";await SnapshotAsync(review,folder,fullHash,account,inspector);
            }
            await store.RunAsync(id,fileHash,direction,Fence,Progress);
            if(writer is not null){await writer.DisposeAsync();writer=null;}
            executionPhase="Restoring and verifying service";await RestoreAsync(review);await CompleteAsync(folder,fullHash,direction,review);return 0;
        }
        catch(Exception error)
        {
            if(writer is not null){await writer.DisposeAsync();writer=null;}
            string recovery="Retain both images and use the same reviewed operation to resume or roll back.";
            if(maintenance)
            {
                try
                {
                    var disk=await swap.InspectAsync();
                    if(disk is UpdateDiskState.Prepared or UpdateDiskState.Applied or UpdateDiskState.Original)
                    {await RestoreAsync(review);recovery="The verified installed image and canonical service policy were restored. Review the retained operation before retrying.";}
                }
                catch{recovery="The new service remains in maintenance or needs attention. Keep all images and use the targeted rollback controls.";}
            }
            var detail=error is InvalidOperationException or ArgumentException?error.Message:"The reviewed update could not complete. Check its retained files, permissions, free space and service state.";
            await WindowsInstaller.WriteNewAsync(Path.Combine(folder,"failure-"+Guid.NewGuid().ToString("N")+".json"),new{AtUtc=DateTimeOffset.UtcNow,ReviewSha256=fullHash,Direction=direction,Phase=executionPhase,Detail=detail,Recovery=recovery,ErrorType=error.GetType().Name,HResult=error.HResult,Diagnostics=error.ToString()});
            return 1;
        }
        finally{if(writer is not null)await writer.DisposeAsync();}
    }
    private static async Task VerifyRetainedConfigurationAsync(AppUpdateReview review)
    {
        foreach(var pair in new[]{(ServiceEndpoint.ConfigPath,review.ConfigurationSha256),(Path.Combine(ServiceEndpoint.DataRoot,"setup-plan.json"),review.InitialPlanSha256),(Path.Combine(ServiceEndpoint.DataRoot,"setup-ownership.json"),review.OwnershipSha256)})
        {
            WindowsInstaller.AssertProtected(pair.Item1);
            if(!string.Equals(await SetupPaths.HashAsync(pair.Item1),pair.Item2,StringComparison.OrdinalIgnoreCase))throw new IOException("The protected installation or configuration changed after review.");
        }
        var initial=await SetupPaths.ReadVerifiedAsync<SetupPlan>(Path.Combine(ServiceEndpoint.DataRoot,"setup-plan.json"),review.InitialPlanSha256,CancellationToken.None);
        var owner=await SetupPaths.ReadVerifiedAsync<JsonElement>(Path.Combine(ServiceEndpoint.DataRoot,"setup-ownership.json"),review.OwnershipSha256,CancellationToken.None);
        var claim=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(initial)));
        if(initial.OperatorSid!=review.OperatorSid||owner.GetProperty("PlanId").GetString()!=initial.Id||!string.Equals(owner.GetProperty("PlanSha256").GetString(),claim,StringComparison.OrdinalIgnoreCase))throw new IOException("The initial installation ownership no longer matches this update.");
        if(initial.RuntimeFiles.Length!=review.Files.Original.RuntimeFiles.Length||initial.RuntimeFiles.Any(f=>!review.Files.Original.RuntimeFiles.Any(r=>r.Path.Equals(f.Path,StringComparison.OrdinalIgnoreCase)&&r.Bytes==f.Bytes&&r.Sha256.Equals(f.Sha256,StringComparison.OrdinalIgnoreCase))))throw new IOException("The reviewed codec runtime differs from the original installation.");
        var config=await SetupPaths.ReadVerifiedAsync<JsonElement>(ServiceEndpoint.ConfigPath,review.ConfigurationSha256,CancellationToken.None);
        UpdateAdmission.RequireUnarmedConfiguration(config,review.OperatorSid);
    }
    private static async Task StageAsync(AppUpdateReview review,UpdateSwap swap,SecurityIdentifier account,string package)
    {
        await UpdateSwap.VerifyImageAsync(swap.InstallRoot,review.Files.Original);
        var bundle=await SetupPaths.VerifyBundleAsync(package,review.Files.Replacement.BundleSha256,CancellationToken.None);
        var contractFile=bundle.Files.SingleOrDefault(f=>f.Path=="setup/maintenance-contract.json")??throw new IOException("The replacement maintenance contract is missing.");
        var contract=await SetupPaths.ReadVerifiedAsync<MaintenanceContract>(SetupPaths.Resolve(package,contractFile.Path),contractFile.Sha256,CancellationToken.None);
        if(contract!=new MaintenanceContract(1,1,true))throw new InvalidOperationException("This update needs an explicit journal migration.");
        var bytes=bundle.Files.Sum(f=>f.Bytes)+review.Files.Replacement.RuntimeFiles.Sum(f=>f.Bytes);
        if(new DriveInfo(Path.GetPathRoot(swap.InstallRoot)!).AvailableFreeSpace<bytes+2L*1024*1024*1024)throw new IOException("Application maintenance needs the candidate size plus 2 GiB free.");
        WindowsInstaller.CreateProtected(swap.CandidateRoot,account,false);var copied=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in bundle.Files)await WindowsInstaller.CopyAsync(SetupPaths.Resolve(package,file.Path),SetupPaths.Resolve(swap.CandidateRoot,file.Path),file,account,false,copied);
        foreach(var file in review.Files.Replacement.RuntimeFiles)
        {
            var source=SetupPaths.Resolve(swap.InstallRoot,"tools/"+file.Path);WindowsInstaller.AssertProtected(source);
            copied[file.Sha256+":"+file.Bytes]=source;
            await WindowsInstaller.CopyAsync(source,SetupPaths.Resolve(swap.CandidateRoot,"tools/"+file.Path),file,account,false,copied);
        }
        var manifest=Path.Combine(package,"bundle.json");
        await WindowsInstaller.CopyAsync(manifest,Path.Combine(swap.CandidateRoot,"bundle.json"),new("bundle.json",new FileInfo(manifest).Length,review.Files.Replacement.BundleSha256),account,false,copied);
        await UpdateSwap.VerifyImageAsync(swap.CandidateRoot,review.Files.Replacement);
    }
    private static async Task SnapshotAsync(AppUpdateReview review,string folder,string fullHash,SecurityIdentifier account,string inspector)
    {
        var manifest=Path.Combine(folder,"snapshot.json");
        if(File.Exists(manifest))
        {
            WindowsInstaller.AssertProtected(manifest);var saved=await SetupPaths.ReadAsync<AppUpdateSnapshot>(manifest);
            if(saved.ReviewSha256!=fullHash||!saved.Folder.StartsWith("snapshot-",StringComparison.Ordinal)||!Guid.TryParseExact(saved.Folder[9..],"N",out _)||saved.CapturedAtUtc==default||saved.CapturedAtUtc.Offset!=TimeSpan.Zero)throw new IOException("The retained snapshot belongs to another review or has an invalid identity.");
            var retained=SetupPaths.Resolve(folder,saved.Folder);
            foreach(var item in saved.Files)
            {
                var path=SetupPaths.Resolve(retained,item.Path);WindowsInstaller.AssertProtected(path);
                if(new FileInfo(path).Length!=item.Bytes||!string.Equals(await SetupPaths.HashAsync(path),item.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("A retained snapshot file changed.");
            }
            await InspectJournalAsync(inspector,Path.Combine(retained,"data","journal","playback.db"));return;
        }
        // An incomplete snapshot is never reused after the service may have reopened
        // and checkpointed its journal. Retain it and create another bounded attempt.
        var snapshotName="snapshot-"+Guid.NewGuid().ToString("N");var snapshot=SetupPaths.Resolve(folder,snapshotName);
        WindowsInstaller.CreateProtected(snapshot,account,false);
        var sources=new List<(string Source,string Relative)>();
        foreach(var name in new[]{"host.json","setup-plan.json","setup-ownership.json"})sources.Add((Path.Combine(ServiceEndpoint.DataRoot,name),name));
        foreach(var name in new[]{"playback.db","playback.db-wal"})
        {var source=Path.Combine(ServiceEndpoint.EngineData,name);if(File.Exists(source)&&(name=="playback.db"||new FileInfo(source).Length>0))sources.Add((source,"data/journal/"+name));}
        var previews=Path.Combine(ServiceEndpoint.EngineData,"migration-previews");SetupPaths.RequireNoLinks(previews);
        var previewCount=0;
        if(Directory.Exists(previews))
            foreach(var source in Directory.EnumerateFiles(previews))
            {
                var name=Path.GetFileName(source);
                if(++previewCount>1024)throw new IOException("The preview snapshot exceeds its entry bound.");
                if(name=="latest"||name.EndsWith(".json",StringComparison.Ordinal)&&Guid.TryParseExact(Path.GetFileNameWithoutExtension(name),"N",out _))sources.Add((source,"data/migration-previews/"+name));
            }
        var files=new List<SetupFile>();long total=0;var copied=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var (source,relative) in sources)
        {
            SetupPaths.RequireNoLinks(source);var bytes=new FileInfo(source).Length;
            if(bytes<1||bytes>536870912||(total+=bytes)>805306368)throw new IOException("The metadata snapshot exceeds its bounded maintenance size.");
            var file=new SetupFile(relative,bytes,await SetupPaths.HashAsync(source));files.Add(file);
            await WindowsInstaller.CopyAsync(source,SetupPaths.Resolve(snapshot,relative),file,account,false,copied);
        }
        await InspectJournalAsync(inspector,Path.Combine(snapshot,"data","journal","playback.db"));
        await WindowsInstaller.WriteNewAsync(manifest,new AppUpdateSnapshot(fullHash,snapshotName,DateTimeOffset.UtcNow,files.ToArray()));
    }
    private static async Task InspectJournalAsync(string executable,string database)
    {
        SetupPaths.RequireNoLinks(executable);SetupPaths.RequireNoLinks(database);WindowsInstaller.AssertProtected(executable);
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=ServiceEndpoint.DataRoot};
        start.ArgumentList.Add("--inspect-maintenance-journal");start.ArgumentList.Add(database);
        using var process=Process.Start(start)??throw new IOException("The read-only journal inspector did not start.");
        var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            await process.WaitForExitAsync(deadline.Token);var json=await output;var diagnostics=await errors;
            if(process.ExitCode!=0||json.Length>16384)throw new IOException("The retained journal did not pass compatible stopped-state inspection (inspector exit "+process.ExitCode+").",new IOException(diagnostics[..Math.Min(diagnostics.Length,8192)]));
            using var document=JsonDocument.Parse(json);var state=document.RootElement;
            if(state.GetProperty("SchemaVersion").GetInt32()!=1||state.GetProperty("Integrity").GetString()!="ok"||state.GetProperty("Stations").GetArrayLength()!=8||state.GetProperty("Stations").EnumerateArray().Any(s=>s.GetProperty("RequestedRunning").GetBoolean()))throw new IOException("The inspected journal is not compatible and explicitly stopped.");
        }
        finally{if(!process.HasExited){process.Kill();using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));await process.WaitForExitAsync(cleanup.Token);}}
    }
    private static async Task RestoreAsync(AppUpdateReview review)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(75));
        await SetupService.InstallAsync(true,deadline.Token);await VerifyReadyAsync(review);
    }
    private static async Task VerifyReadyAsync(AppUpdateReview review)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while(true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try
            {
                var service=SetupService.Read()??throw new IOException("Service readback is unavailable.");SetupService.RequireOwned(service);
                if(service.State!=4||service.StartType!=2||!service.Delayed||!service.FailureFlag||service.RecoveryActions!=3)throw new IOException("Canonical service startup or recovery policy is not restored.");
                UpdateAdmission.RequireUnarmedStatus(await AppUpdatePreparation.StatusAsync(deadline.Token));await VerifyRetainedConfigurationAsync(review);return;
            }
            catch(Exception error)when(error is IOException or InvalidOperationException or OperationCanceledException){await Task.Delay(200,deadline.Token);}
        }
    }
    private static async Task CompleteAsync(string folder,string hash,UpdateDirection direction,AppUpdateReview review)
    {
        var image=direction==UpdateDirection.Apply?review.Files.Replacement:review.Files.Original;
        await UpdateSwap.VerifyImageAsync(ServiceEndpoint.InstallRoot,image);
        var path=Path.Combine(folder,direction==UpdateDirection.Apply?"apply.completed.json":"rollback.completed.json");
        if(File.Exists(path))
        {
            WindowsInstaller.AssertProtected(path);var saved=await SetupPaths.ReadAsync<AppUpdateCompletion>(path);
            if(saved.ReviewSha256!=hash||saved.Direction!=direction||saved.ImageSha256!=image.BundleSha256||saved.VerifiedAtUtc==default||saved.VerifiedAtUtc.Offset!=TimeSpan.Zero)throw new IOException("The retained completion receipt does not match this operation.");
        }
        else await WindowsInstaller.WriteNewAsync(path,new AppUpdateCompletion(hash,direction,image.BundleSha256,DateTimeOffset.UtcNow));
    }
}
