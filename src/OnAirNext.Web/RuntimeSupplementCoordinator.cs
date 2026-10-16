using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using OnAirNext.Installation;
using OnAirNext.ServiceControl;

namespace OnAirNext.Web;

internal sealed class RuntimeSupplementCoordinator:IAsyncDisposable
{
    private readonly string _folder,_package,_helper;
    private readonly string? _packageHash;
    private readonly object _gate=new();private readonly CancellationTokenSource _stop=new();
    private Task _operation=Task.CompletedTask;private RuntimeSupplementPlan? _plan;
    private string _phase="Not reviewed",_detail="Review the original complete audio tools package to add missing files while retaining the installed codec binaries.";
    public RuntimeSupplementCoordinator(string consoleData)
    {
        _folder=Path.Combine(consoleData,"audio-tools");_package=Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.FullName;_helper=Path.Combine(_package,"setup","OnAirNext.Setup.exe");
        var manifest=Path.Combine(_package,"bundle.json");SetupPaths.RequireNoLinks(manifest);
        if(File.Exists(manifest)&&new FileInfo(manifest).Length<=524288)_packageHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest)));
        try
        {
            var pointer=Path.Combine(_folder,"latest");SetupPaths.RequireNoLinks(pointer);
            if(File.Exists(pointer)&&new FileInfo(pointer).Length<=64)
            {
                var id=File.ReadAllText(pointer).Trim();if(!Guid.TryParseExact(id,"N",out _))throw new IOException("Invalid saved tools review.");
                var path=SetupPaths.Resolve(_folder,id+".json");if(new FileInfo(path).Length>524288)throw new IOException("Saved tools review exceeds its bound.");
                _plan=JsonSerializer.Deserialize<RuntimeSupplementPlan>(File.ReadAllBytes(path))??throw new IOException("Saved tools review missing.");RuntimeSupplementInventory.ValidatePlan(_plan);
                if(_plan.Id!=id)throw new IOException("Saved tools review identity mismatch.");_phase="Saved review";_detail="Read the saved review and protected completion receipt before resuming this operation.";
            }
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {_plan=null;_phase="Review needs attention";_detail="The saved tools review could not be recovered. Keep the original package and read the installation receipt.";}
    }
    private bool Available=>OperatingSystem.IsWindows()&&_packageHash is not null&&File.Exists(_helper);
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertProtected(string path)
    {
        SetupPaths.RequireNoLinks(path);
        FileSystemSecurity security=Directory.Exists(path)?new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner|AccessControlSections.Access):new FileInfo(path).GetAccessControl(AccessControlSections.Owner|AccessControlSections.Access);
        ServiceFileProtection.AssertProtected(security);
    }
    public async Task<object> StatusAsync(CancellationToken token)
    {
        SetupPlan? initial=null;RuntimeSupplementReceipt? receipt=null;SetupFile[]? additions=null;string? candidate=null,readError=null;bool complete=false,maintenanceBusy=false;
        if(OperatingSystem.IsWindows())
        {
            try
            {
                var path=Path.Combine(ServiceEndpoint.DataRoot,"setup-plan.json");SetupPaths.RequireNoLinks(path);
                if(File.Exists(path))
                {
                    AssertProtected(ServiceEndpoint.DataRoot);AssertProtected(path);var initialHash=await SetupPaths.HashAsync(path,token);initial=await SetupPaths.ReadVerifiedAsync<SetupPlan>(path,initialHash,token);_=SetupPaths.InstalledRuntimeFiles(initial.RuntimeFiles);
                    using var identity=WindowsIdentity.GetCurrent();if(initial.OperatorSid!=identity.User?.Value)throw new InvalidOperationException("Open the console from the original Windows operator account.");
                    var ownerPath=Path.Combine(ServiceEndpoint.DataRoot,"setup-ownership.json");AssertProtected(ownerPath);var owner=await SetupPaths.ReadAsync<JsonElement>(ownerPath,32768,token);
                    var claim=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(initial)));
                    if(owner.GetProperty("PlanId").GetString()!=initial.Id||!string.Equals(owner.GetProperty("PlanSha256").GetString(),claim,StringComparison.OrdinalIgnoreCase))throw new IOException("The original installation claim changed.");
                    receipt=await RuntimeSupplementInventory.ReadReceiptAsync(Path.Combine(ServiceEndpoint.DataRoot,RuntimeSupplementInventory.ReceiptName),AssertProtected,token);
                    if(receipt is not null&&receipt.Plan.InitialPlanSha256!=initialHash)throw new IOException("The tools receipt belongs to a different installation.");
                    if(receipt is not null){lock(_gate){_plan=receipt.Plan;}}
                    candidate=Path.GetFileName(initial.RuntimeRoot).Equals("bin",StringComparison.OrdinalIgnoreCase)?Path.GetDirectoryName(initial.RuntimeRoot):initial.RuntimeRoot;
                    var effective=receipt?.Phase=="Pending"?SetupPaths.InstalledRuntimeFiles(initial.RuntimeFiles):await RuntimeSupplementInventory.EffectiveAsync(initial,initialHash,Path.Combine(ServiceEndpoint.DataRoot,RuntimeSupplementInventory.ReceiptName),ServiceEndpoint.InstallRoot,AssertProtected,token);
                    complete=effective.Any(f=>f.Path=="ffprobe.exe")&&effective.Any(f=>f.Path=="runtime-manifest.json")&&effective.All(f=>File.Exists(SetupPaths.Resolve(ServiceEndpoint.InstallRoot,"tools/"+f.Path)));
                    maintenanceBusy=UpdateMaintenanceLock.IsHeld(Path.Combine(ServiceEndpoint.DataRoot,"setup.lock"));
                    lock(_gate){if(_plan is not null)additions=RuntimeSupplementInventory.Additions(initial.RuntimeFiles,_plan.RuntimeFiles);}
                }
            }
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or KeyNotFoundException)
            {initial=null;additions=null;complete=false;readError=error is InvalidOperationException or ArgumentException?error.Message:"The protected tools inventory could not be read. Keep the installation and original provider package.";}
        }
        lock(_gate)
        {
            var busy=!_operation.IsCompleted||maintenanceBusy;var match=_plan is not null&&_plan.PackageSha256.Equals(_packageHash,StringComparison.OrdinalIgnoreCase)&&_plan.PackageRoot.Equals(_package,StringComparison.OrdinalIgnoreCase);
            return new{supported=OperatingSystem.IsWindows(),packageAvailable=Available,installed=initial is not null,busy,
                phase=maintenanceBusy?"Installation operation in progress":!_operation.IsCompleted?_phase:complete?"Complete":_phase,
                detail=maintenanceBusy?"Wait for the operation holding the installation lock.":!_operation.IsCompleted?_detail:receipt?.Detail??(complete?"The initial installer recorded a complete tools inventory. No additions are required.":_detail),readError,complete,candidateRoot=candidate,
                review=_plan is null?null:new{id=_plan.Id,root=_plan.RuntimeRoot,package=_plan.PackageRoot,reference=_plan.ReferenceSha256,operatorSid=_plan.OperatorSid,
                    preserved=initial?.RuntimeFiles.Length,files=additions?.Select(f=>new{path=SetupPaths.RuntimeDestination(f.Path),bytes=f.Bytes}).ToArray()},
                receipt=receipt is null?null:new{phase=receipt.Phase,at=receipt.AtUtc,detail=receipt.Detail,id=receipt.Plan.Id},
                canReview=Available&&initial is not null&&!busy&&receipt is null&&!complete&&readError is null,
                canApply=Available&&match&&!busy&&!complete&&additions is{Length:>0}&&_phase!="Review failed"&&readError is null};
        }
    }
    public void Prepare(RuntimeSupplementRequest request)
    {
        if(!Available)throw new InvalidOperationException("Open a complete Windows package to review its audio tools.");SetupPaths.CanonicalRoot(request.RuntimeFolder);
        lock(_gate)
        {
            if(!_operation.IsCompleted)throw new InvalidOperationException("Wait for the current audio tools operation.");
            _phase="Checking";_detail="Checking the original installation, complete provider and media analyzer under this operator account. No files are installed during review.";
            _operation=Task.Run(()=>PrepareCoreAsync(request));
        }
    }
    private async Task PrepareCoreAsync(RuntimeSupplementRequest request)
    {
        try
        {
            Directory.CreateDirectory(_folder);SetupPaths.RequireNoLinks(_folder);var requestPath=SetupPaths.Resolve(_folder,"request-"+Guid.NewGuid().ToString("N")+".json");var bytes=JsonSerializer.SerializeToUtf8Bytes(request);
            await SaveAsync(requestPath,bytes,false,_stop.Token);
            try
            {
                var start=new ProcessStartInfo(_helper){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=_package};
                foreach(var arg in new[]{"--prepare-audio-tools",requestPath,Convert.ToHexString(SHA256.HashData(bytes))})start.ArgumentList.Add(arg);
                SetupPaths.RequireNoLinks(_helper);using var process=Process.Start(start)??throw new IOException("The tools review did not start.");try{process.PriorityClass=ProcessPriorityClass.BelowNormal;}catch(Win32Exception){}
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);deadline.CancelAfter(RuntimeSupplementReviewProgress.ConsoleTimeout);
                string? lastStage=null;
                var progress=new RuntimeSupplementReviewProgress.Reader(stage=>
                {lastStage=stage;lock(_gate){_detail=RuntimeSupplementReviewProgress.Describe(stage)+". No files are installed during review.";}});
                var output=DrainAsync(process.StandardOutput,deadline.Token);var errors=DrainProgressAsync(process.StandardError,progress,deadline.Token);
                try
                {
                    await process.WaitForExitAsync(deadline.Token);var result=await Task.WhenAll(output,errors);using var json=JsonDocument.Parse(result[0]);
                    if(process.ExitCode!=0)throw new InvalidOperationException(json.RootElement.TryGetProperty("error",out var error)?error.GetString():"The tools review failed.");
                    var plan=JsonSerializer.Deserialize<RuntimeSupplementPlan>(result[0])??throw new IOException("The tools review is missing.");RuntimeSupplementInventory.ValidatePlan(plan);
                    if(plan.PackageSha256!=_packageHash||!plan.PackageRoot.Equals(_package,StringComparison.OrdinalIgnoreCase))throw new IOException("The tools review belongs to another package.");
                    await SaveAsync(SetupPaths.Resolve(_folder,plan.Id+".json"),JsonSerializer.SerializeToUtf8Bytes(plan),false,_stop.Token);
                    await SaveAsync(Path.Combine(_folder,"latest"),Encoding.ASCII.GetBytes(plan.Id),true,_stop.Token);
                    lock(_gate){_plan=plan;_phase="Review";_detail="Review the missing files below. Windows approval will add them without replacing the original codec binaries or restarting the service.";}
                }
                catch(OperationCanceledException error)when(!_stop.IsCancellationRequested)
                {throw new InvalidOperationException(RuntimeSupplementReviewProgress.FailureMessage(error,lastStage),error);}
                finally
                {
                    if(!process.HasExited){process.Kill(entireProcessTree:true);using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));await process.WaitForExitAsync(cleanup.Token);}
                    try{await Task.WhenAll(output,errors);}catch(Exception)when(deadline.IsCancellationRequested||output.IsFaulted||errors.IsFaulted){}
                }
            }
            finally{if(File.Exists(requestPath))File.Delete(requestPath);}
        }
        catch(Exception error){lock(_gate){_phase="Review failed";_detail=error is InvalidOperationException or ArgumentException?error.Message:"Audio tools review failed. Check the original provider, operator account, permissions and disk space.";}}
    }
    public void Execute(string id)
    {
        if(!Available)throw new InvalidOperationException("Use the complete package that prepared this tools review.");
        lock(_gate)
        {
            if(!_operation.IsCompleted)throw new InvalidOperationException("Wait for the current audio tools operation.");
            if(_plan?.Id!=id||_phase=="Review failed"||_plan.PackageSha256!=_packageHash||!_plan.PackageRoot.Equals(_package,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Read the matching package's saved tools review before continuing.");
            var plan=_plan;_phase="Administrator approval";_detail="Approve adding the reviewed missing audio tools. Original codec files and both broadcasting services keep their existing state.";
            _operation=Task.Run(()=>ExecuteCoreAsync(plan));
        }
    }
    private async Task ExecuteCoreAsync(RuntimeSupplementPlan plan)
    {
        try
        {
            Directory.CreateDirectory(_folder);SetupPaths.RequireNoLinks(_folder);var path=SetupPaths.Resolve(_folder,plan.Id+".json");var bytes=JsonSerializer.SerializeToUtf8Bytes(plan);
            if(File.Exists(path))await SetupPaths.ReadVerifiedAsync<RuntimeSupplementPlan>(path,RuntimeSupplementInventory.HashPlan(plan),_stop.Token);else await SaveAsync(path,bytes,false,_stop.Token);
            var start=new ProcessStartInfo(_helper){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=_package};
            foreach(var arg in new[]{"--complete-audio-tools",path,RuntimeSupplementInventory.HashPlan(plan)})start.ArgumentList.Add(arg);
            SetupPaths.RequireNoLinks(_helper);using var process=Process.Start(start)??throw new IOException("Windows audio tools completion did not start.");await process.WaitForExitAsync(_stop.Token);
            lock(_gate){_phase=process.ExitCode==0?"Complete":"Needs attention";_detail=process.ExitCode==0?"Read the protected completion receipt. The service was not restarted and original codec files were retained.":"Read the saved receipt and retry this same reviewed operation. Keep its original provider folder.";}
        }
        catch(Win32Exception error)when(error.NativeErrorCode==1223){lock(_gate){_phase="Approval cancelled";_detail="Windows approval was cancelled. The saved review is retained.";}}
        catch(Exception){lock(_gate){_phase="Needs attention";_detail="An approved helper may continue after console closure. Read the protected receipt and resume the same operation.";}}
    }
    private static async Task<string> DrainAsync(StreamReader reader,CancellationToken token)
    {var text=new StringBuilder();var buffer=new char[4096];int count;while((count=await reader.ReadAsync(buffer,token))>0){if(text.Length+count>524288)throw new IOException("Tools review output exceeded its bound.");text.Append(buffer,0,count);}return text.ToString();}
    private static async Task<string> DrainProgressAsync(StreamReader reader,RuntimeSupplementReviewProgress.Reader progress,CancellationToken token)
    {var buffer=new char[4096];int count;while((count=await reader.ReadAsync(buffer,token))>0)progress.Append(buffer.AsSpan(0,count));return string.Empty;}
    private static async Task SaveAsync(string path,byte[] bytes,bool replace,CancellationToken token)
    {
        SetupPaths.RequireNoLinks(path);var temporary=path+".partial-"+Guid.NewGuid().ToString("N");SetupPaths.RequireNoLinks(temporary);
        try{await using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true)){await file.WriteAsync(bytes,token);await file.FlushAsync(token);file.Flush(true);}File.Move(temporary,path,replace);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public async ValueTask DisposeAsync(){await _stop.CancelAsync();try{await _operation;}finally{_stop.Dispose();}}
}
