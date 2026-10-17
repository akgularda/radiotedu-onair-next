using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using OnAirNext.ServiceControl;

namespace OnAirNext.Installation;

[SupportedOSPlatform("windows")]
internal static class WindowsRuntimeSupplement
{
    private sealed record Initial(SetupPlan Plan,string PlanHash,string OwnershipHash,string ConfigHash,string BundleHash,SetupServiceState Service);
    private static string ReceiptPath=>Path.Combine(ServiceEndpoint.DataRoot,RuntimeSupplementInventory.ReceiptName);
    private static string Package=>Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.FullName;
    private static async Task<Initial> ReadInitialAsync(string operatorSid,CancellationToken token,Action<string>? progress=null)
    {
        var planPath=Path.Combine(ServiceEndpoint.DataRoot,"setup-plan.json");var ownerPath=Path.Combine(ServiceEndpoint.DataRoot,"setup-ownership.json");
        foreach(var path in new[]{ServiceEndpoint.DataRoot,ServiceEndpoint.InstallRoot,ServiceEndpoint.ConfigPath,planPath,ownerPath,Path.Combine(ServiceEndpoint.InstallRoot,"bundle.json")})WindowsInstaller.AssertProtected(path);
        var hash=await SetupPaths.HashAsync(planPath,token);var plan=await SetupPaths.ReadVerifiedAsync<SetupPlan>(planPath,hash,token);
        if(plan.OperatorSid!=operatorSid||!Guid.TryParseExact(plan.Id,"N",out _))throw new InvalidOperationException("Review audio tools from the original Windows operator account.");
        _=SetupPaths.InstalledRuntimeFiles(plan.RuntimeFiles);var ownerHash=await SetupPaths.HashAsync(ownerPath,token);
        var owner=await SetupPaths.ReadVerifiedAsync<JsonElement>(ownerPath,ownerHash,token);
        var claim=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan)));
        if(owner.GetProperty("PlanId").GetString()!=plan.Id||!string.Equals(owner.GetProperty("PlanSha256").GetString(),claim,StringComparison.OrdinalIgnoreCase))throw new IOException("The initial installation ownership is inconsistent.");
        var configHash=await SetupPaths.HashAsync(ServiceEndpoint.ConfigPath,token);
        UpdateAdmission.RequireUnarmedConfiguration(await SetupPaths.ReadVerifiedAsync<JsonElement>(ServiceEndpoint.ConfigPath,configHash,token),operatorSid);
        progress?.Invoke("service");
        var service=SetupService.Read()??throw new InvalidOperationException("Install and connect the separate service before reviewing its audio tools.");SetupService.RequireOwned(service);
        if(service.State!=4||service.StartType!=2||!service.Delayed||service.RecoveryActions!=3||!service.FailureFlag)throw new InvalidOperationException("Connect the healthy owned service with its verified startup and recovery settings first.");
        UpdateAdmission.RequireUnarmedStatus(await AppUpdatePreparation.StatusAsync(token));
        return new(plan,hash,ownerHash,configHash,await SetupPaths.HashAsync(Path.Combine(ServiceEndpoint.InstallRoot,"bundle.json"),token),service);
    }
    public static async Task<RuntimeSupplementPlan> PrepareAsync(RuntimeSupplementRequest request,CancellationToken token,Action<string>? progress=null)
    {
        using var identity=WindowsIdentity.GetCurrent();var sid=identity.User?.Value??throw new InvalidOperationException("The Windows operator is unavailable.");
        progress?.Invoke("ownership");
        var initial=await ReadInitialAsync(sid,token,progress);
        var prior=await RuntimeSupplementInventory.ReadReceiptAsync(ReceiptPath,WindowsInstaller.AssertProtected,token);
        if(prior is not null)throw new InvalidOperationException("Read and resume the saved audio tools operation instead of preparing another one.");
        progress?.Invoke("installed-image");
        await UpdateSwap.VerifyImageAsync(ServiceEndpoint.InstallRoot,new(initial.BundleHash,SetupPaths.InstalledRuntimeFiles(initial.Plan.RuntimeFiles)),token);
        progress?.Invoke("candidate-image");
        var package=SetupPaths.CanonicalRoot(Package);var bundleHash=await SetupPaths.HashAsync(SetupPaths.Resolve(package,"bundle.json"),token);
        var bundle=await SetupPaths.VerifyBundleAsync(package,bundleHash,token);var root=SetupPaths.CanonicalRoot(request.RuntimeFolder);
        var providerManifest=SetupPaths.Resolve(root,"runtime-manifest.json");
        if(!File.Exists(providerManifest)||new FileInfo(providerManifest).Length is <1 or >524288)throw new ArgumentException("Choose the complete original audio tools package, not its bin folder.");
        var providerHash=await SetupPaths.HashAsync(providerManifest,token);SetupFile? selected=null;
        foreach(var path in new[]{CodecRuntimePackage.ReferencePath,RuntimeSupplementInventory.LegacyReferencePath})
        {
            var reference=bundle.Files.SingleOrDefault(f=>f.Path==path);if(reference is null||!reference.Sha256.Equals(providerHash,StringComparison.OrdinalIgnoreCase))continue;
            if(path==RuntimeSupplementInventory.LegacyReferencePath&&!reference.Sha256.Equals(RuntimeSupplementInventory.LegacyReferenceSha256,StringComparison.OrdinalIgnoreCase))throw new IOException("The original provider reference changed.");
            selected=reference;break;
        }
        if(selected is null)throw new ArgumentException("This provider is not a qualified reference in this application package. Select the original complete matching audio tools package.");
        progress?.Invoke("provider");
        var files=await CodecRuntimePackage.InspectAsync(root,SetupPaths.Resolve(package,selected.Path),selected.Sha256,token);
        if(RuntimeSupplementInventory.Additions(initial.Plan.RuntimeFiles,files).Length==0)throw new InvalidOperationException("The reviewed audio tools inventory is already complete.");
        progress?.Invoke("analyzer");
        var version=await SetupPreparation.CaptureAsync(SetupPaths.Resolve(root,"bin/ffprobe.exe"),["-version"],token);
        if(!version.Split('\n').Any(line=>line.StartsWith("ffprobe version ",StringComparison.Ordinal)))throw new InvalidOperationException("The matching media analyzer did not report its expected version identity.");
        progress?.Invoke("provider-readback");
        _=await CodecRuntimePackage.InspectAsync(root,SetupPaths.Resolve(package,selected.Path),selected.Sha256,token);
        progress?.Invoke("installed-tools");
        await VerifyFilesAsync(SetupPaths.InstalledRuntimeFiles(initial.Plan.RuntimeFiles),token);
        progress?.Invoke("final-ownership");
        var after=await ReadInitialAsync(sid,token);
        if(after.PlanHash!=initial.PlanHash||after.OwnershipHash!=initial.OwnershipHash||after.ConfigHash!=initial.ConfigHash||after.BundleHash!=initial.BundleHash||after.Service!=initial.Service)throw new IOException("The installation changed during review. Review audio tools again.");
        return new(1,Guid.NewGuid().ToString("N"),sid,package,bundleHash,initial.BundleHash,root,selected.Path,selected.Sha256,initial.PlanHash,initial.OwnershipHash,initial.ConfigHash,files,DateTimeOffset.UtcNow);
    }
    public static async Task<int> ApplyAsync(string path,string expected)
    {
        using var identity=WindowsIdentity.GetCurrent();
        if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))throw new UnauthorizedAccessException("Approve Windows administrator access to add the reviewed audio tools.");
        var plan=await SetupPaths.ReadVerifiedAsync<RuntimeSupplementPlan>(path,expected,CancellationToken.None);RuntimeSupplementInventory.ValidatePlan(plan);
        var package=SetupPaths.CanonicalRoot(Package);
        if(!package.Equals(SetupPaths.CanonicalRoot(plan.PackageRoot),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Use the application package that prepared this audio tools review.");
        var bundle=await SetupPaths.VerifyBundleAsync(package,plan.PackageSha256,CancellationToken.None);
        var reference=bundle.Files.SingleOrDefault(f=>f.Path==plan.ReferencePath)??throw new IOException("The approved provider reference is missing.");
        if(!reference.Sha256.Equals(plan.ReferenceSha256,StringComparison.OrdinalIgnoreCase))throw new IOException("The approved provider reference changed.");
        var inspected=await CodecRuntimePackage.InspectAsync(plan.RuntimeRoot,SetupPaths.Resolve(package,reference.Path),reference.Sha256,CancellationToken.None);
        CodecRuntimePackage.RequireInventory(plan.RuntimeFiles,inspected);
        var lockPath=Path.Combine(ServiceEndpoint.DataRoot,"setup.lock");WindowsInstaller.AssertProtected(lockPath);
        await using var held=await UpdateMaintenanceLock.AcquireAsync(lockPath);
        var initial=await ReadInitialAsync(plan.OperatorSid,CancellationToken.None);
        if(initial.PlanHash!=plan.InitialPlanSha256||initial.OwnershipHash!=plan.OwnershipSha256||initial.ConfigHash!=plan.ConfigurationSha256||initial.BundleHash!=plan.InstalledBundleSha256)throw new IOException("The installation changed after review. Keep the saved operation and inspect its original package.");
        var additions=RuntimeSupplementInventory.Additions(initial.Plan.RuntimeFiles,plan.RuntimeFiles);var full=SetupPaths.InstalledRuntimeFiles(plan.RuntimeFiles);
        var prior=await RuntimeSupplementInventory.ReadReceiptAsync(ReceiptPath,WindowsInstaller.AssertProtected);
        var planHash=RuntimeSupplementInventory.HashPlan(plan);
        if(prior is not null&&!prior.PlanSha256.Equals(planHash,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Another audio tools operation already owns this installation. Resume its reviewed plan.");
        if(prior is null)
        {
            await UpdateSwap.VerifyImageAsync(ServiceEndpoint.InstallRoot,new(initial.BundleHash,SetupPaths.InstalledRuntimeFiles(initial.Plan.RuntimeFiles)));
            if(additions.Length==0)throw new InvalidOperationException("The matching audio tools are already complete.");
        }
        RequireKnownTools(full,additions);
        await VerifyFilesAsync(SetupPaths.InstalledRuntimeFiles(initial.Plan.RuntimeFiles),CancellationToken.None);
        var account=new SecurityIdentifier(initial.Plan.OperatorSid);
        if(new DriveInfo(Path.GetPathRoot(ServiceEndpoint.InstallRoot)!).AvailableFreeSpace<additions.Sum(f=>f.Bytes)+2L*1024*1024*1024)throw new IOException("Audio tools completion needs the added files plus 2 GiB of free space.");
        try
        {
            var copied=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            await RuntimeSupplementTransaction.CompleteAsync(plan,initial.Plan.RuntimeFiles,WriteReceiptAsync,async file=>
            {
                var destination=SetupPaths.Resolve(ServiceEndpoint.InstallRoot,"tools/"+SetupPaths.RuntimeDestination(file.Path));
                // false permits creating a missing file. CopyAsync never overwrites an
                // existing file; only additions outside the immutable original set enter here.
                await WindowsInstaller.CopyAsync(SetupPaths.Resolve(plan.RuntimeRoot,file.Path),destination,file,account,false,copied,2*1024*1024);
            },async()=>
            {
                await VerifyFilesAsync(full,CancellationToken.None);RequireKnownTools(full,additions);
                var after=await ReadInitialAsync(plan.OperatorSid,CancellationToken.None);
                if(after.PlanHash!=initial.PlanHash||after.OwnershipHash!=initial.OwnershipHash||after.ConfigHash!=initial.ConfigHash||after.BundleHash!=initial.BundleHash||after.Service!=initial.Service)throw new IOException("The service or configuration changed during audio tools completion. Read the retained operation before retrying.");
            });
            return 0;
        }
        catch(Exception error)
        {
            var detail=error is InvalidOperationException or ArgumentException?error.Message:"Audio tools copy needs attention. Keep the original provider package and resume this same reviewed operation.";
            await WriteReceiptAsync(new(planHash,plan,"Pending",DateTimeOffset.UtcNow,detail));
            var failures=Path.Combine(ServiceEndpoint.DataRoot,"audio-tools-failures");WindowsInstaller.CreateProtected(failures,account,false);
            await WindowsInstaller.WriteNewAsync(Path.Combine(failures,plan.Id+"-"+Guid.NewGuid().ToString("N")+".json"),new{AtUtc=DateTimeOffset.UtcNow,PlanSha256=planHash,Detail=detail,ErrorType=error.GetType().Name,HResult=error.HResult});
            return 1;
        }
    }
    private static async Task VerifyFilesAsync(SetupFile[] files,CancellationToken token)
    {
        foreach(var file in files)
        {
            var path=SetupPaths.Resolve(ServiceEndpoint.InstallRoot,"tools/"+file.Path);WindowsInstaller.AssertProtected(path);
            if(new FileInfo(path).Length!=file.Bytes||!string.Equals(await SetupPaths.HashAsync(path,token),file.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("An original or added audio tool differs from its approved identity. No codec replacement is permitted.");
        }
    }
    private static void RequireKnownTools(SetupFile[] full,SetupFile[] additions)
    {
        var root=SetupPaths.Resolve(ServiceEndpoint.InstallRoot,"tools");var allowed=new HashSet<string>(full.Select(f=>f.Path),StringComparer.OrdinalIgnoreCase);
        foreach(var file in additions)allowed.Add(SetupPaths.RuntimeDestination(file.Path)+".onair-installing");
        var pending=new Queue<string>();pending.Enqueue(root);var count=0;
        while(pending.TryDequeue(out var directory))foreach(var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if(++count>4096)throw new IOException("The installed audio tools directory exceeds its bound.");SetupPaths.RequireNoLinks(entry);
            if(Directory.Exists(entry)){pending.Enqueue(entry);continue;}
            var relative=Path.GetRelativePath(root,entry).Replace('\\','/');
            if(!allowed.Contains(relative))throw new IOException("The audio tools directory contains an unrelated file. No file was removed.");
        }
    }
    private static async Task WriteReceiptAsync(RuntimeSupplementReceipt receipt)
    {
        SetupPaths.RequireNoLinks(ReceiptPath);if(File.Exists(ReceiptPath))WindowsInstaller.AssertProtected(ReceiptPath);
        var temporary=ReceiptPath+".onair-installing";SetupPaths.RequireNoLinks(temporary);
        if(File.Exists(temporary)){WindowsInstaller.AssertProtected(ServiceEndpoint.DataRoot);File.Delete(temporary);}
        try
        {
            await using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true))
            {await JsonSerializer.SerializeAsync(stream,receipt);await stream.FlushAsync();stream.Flush(true);}WindowsInstaller.SetOwner(temporary);
            for(var attempt=0;;attempt++)
                try{File.Move(temporary,ReceiptPath,true);break;}
                catch(IOException error)when(attempt<20&&((error.HResult&0xffff) is 32 or 33)){await Task.Delay(50);}
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
