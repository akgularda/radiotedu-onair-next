using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OnAirNext.Installation;
using OnAirNext.ServiceControl;

namespace OnAirNext.Web;

internal sealed class AppUpdateCoordinator:IAsyncDisposable
{
    private readonly string _folder,_package,_helper;
    private readonly string? _packageIdentity;
    private readonly object _gate=new();private readonly CancellationTokenSource _stop=new();
    private Task _operation=Task.CompletedTask;private AppUpdateReview? _review;private string? _path,_hash;
    private string _phase="Not reviewed",_detail="Open a complete new Windows package, then review its application update. All new channels must be stopped with no outputs.";
    public AppUpdateCoordinator(string consoleData)
    {
        _folder=Path.Combine(consoleData,"app-updates");_package=Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.FullName;
        _helper=Path.Combine(_package,"setup","OnAirNext.Setup.exe");
        var manifest=Path.Combine(_package,"bundle.json");SetupPaths.RequireNoLinks(manifest);
        if(File.Exists(manifest)&&new FileInfo(manifest).Length<=524288)_packageIdentity=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest)));
        try
        {
            var pointer=Path.Combine(_folder,"latest");SetupPaths.RequireNoLinks(pointer);
            if(File.Exists(pointer)&&new FileInfo(pointer).Length<=64)
            {
                var id=File.ReadAllText(pointer).Trim();if(!Guid.TryParseExact(id,"N",out _))throw new IOException("The saved update pointer is invalid.");
                var path=SetupPaths.Resolve(_folder,id+".json");if(new FileInfo(path).Length>524288)throw new IOException("The saved update review is too large.");
                var bytes=File.ReadAllBytes(path);var review=JsonSerializer.Deserialize<AppUpdateReview>(bytes)??throw new IOException("Saved review missing.");
                if(review.Files.Id!=id)throw new IOException("Saved review identity mismatch.");
                _review=review;_path=path;_hash=Convert.ToHexString(SHA256.HashData(bytes));_phase="Saved review";_detail="A saved review is available. Read its retained progress before resuming or rolling back.";
            }
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {_phase="Review needs attention";_detail="A saved review could not be recovered. Keep the package and retained update records, then review again.";}
    }
    private bool Available=>OperatingSystem.IsWindows()&&File.Exists(_helper)&&File.Exists(Path.Combine(_package,"bundle.json"))&&!_package.Equals(ServiceEndpoint.InstallRoot,StringComparison.OrdinalIgnoreCase);
    public async Task<object> StatusAsync(CancellationToken token)
    {
        AppUpdateReview? review;string phase,detail;bool busy;lock(_gate){review=_review;phase=_phase;detail=_detail;busy=!_operation.IsCompleted;}
        UpdateIntent? intent=null;AppUpdateCompletion? completion=null;object? failure=null;string? readError=null;var maintenanceBusy=false;
        if(review is not null)
        {
            try
            {
                maintenanceBusy=UpdateMaintenanceLock.IsHeld(Path.Combine(ServiceEndpoint.DataRoot,"setup.lock"));
                var folder=Path.Combine(ServiceEndpoint.DataRoot,"updates",review.Files.Id);SetupPaths.RequireNoLinks(folder);
                if(Directory.Exists(folder))
                {
                    var logicalHash=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(review)));
                    var protectedReview=Path.Combine(folder,"app-review.json");
                    if(File.Exists(protectedReview))await SetupPaths.ReadVerifiedAsync<AppUpdateReview>(protectedReview,logicalHash,token);
                    var store=new UpdateOperationStore(ServiceEndpoint.InstallRoot,folder);
                    var fileHash=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(review.Files)));
                    intent=await store.ReadIntentAsync(review.Files.Id,fileHash,token);
                    var receipt=Path.Combine(folder,intent?.Direction==UpdateDirection.Rollback?"rollback.completed.json":"apply.completed.json");
                    if(File.Exists(receipt))
                    {
                        var saved=await SetupPaths.ReadAsync<AppUpdateCompletion>(receipt,32768,token);
                        if(saved.ReviewSha256!=logicalHash)throw new IOException("The retained completion belongs to a different review.");completion=saved;
                    }
                    var files=new DirectoryInfo(folder).EnumerateFiles("failure-*.json").Take(101).ToArray();
                    if(files.Length>100)throw new IOException("More than 100 update failures are retained. Keep the records and review this operation before retrying.");
                    var recent=files.OrderByDescending(f=>f.LastWriteTimeUtc).FirstOrDefault();
                    if(recent is not null)failure=await SetupPaths.ReadAsync<JsonElement>(recent.FullName,32768,token);
                }
            }
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {readError="Retained update progress could not be verified. Keep all application images and retry reading this same operation.";}
        }
        if(maintenanceBusy){busy=true;phase="Maintenance in progress";detail="Another operation owns the installation lock. Read its saved progress and wait for completion before resuming or rolling back.";}
        return new{supported=OperatingSystem.IsWindows(),packageAvailable=Available,busy,phase,detail,readError,
            review=review is null?null:new{id=review.Files.Id,from=review.OriginalSource,to=review.ReplacementSource,package=_package,reviewedPackage=review.PackageRoot,schema=review.JournalSchemaVersion,reviewedAt=review.Files.CreatedAtUtc},
            intent=intent is null?null:new{direction=intent.Direction.ToString(),phase=intent.Phase,revision=intent.Revision,at=intent.UpdatedAtUtc},
            completion=completion is null?null:new{direction=completion.Direction.ToString(),at=completion.VerifiedAtUtc,image=completion.ImageSha256},failure,
            canApply=Available&&!busy&&review is not null&&review.Files.Replacement.BundleSha256.Equals(_packageIdentity,StringComparison.OrdinalIgnoreCase)&&phase!="Review failed"&&intent?.Direction!=UpdateDirection.Rollback&&readError is null,
            canRollback=Available&&!busy&&review is not null&&review.Files.Replacement.BundleSha256.Equals(_packageIdentity,StringComparison.OrdinalIgnoreCase)&&intent is not null&&readError is null};
    }
    public void Prepare()
    {
        if(!Available)throw new InvalidOperationException("Open the complete new Windows package to review an application update. The installed console continues to control broadcasts.");
        lock(_gate)
        {
            if(!_operation.IsCompleted)throw new InvalidOperationException("Wait for the current application update operation.");
            _phase="Checking";_detail="Reading package, ownership, stopped channel states and unchanged codec runtime. Windows may request administrator approval to verify the exact service process. No service is changed during review.";
            _operation=Task.Run(PrepareCoreAsync);
        }
    }
    private async Task PrepareCoreAsync()
    {
        try
        {
            var start=new ProcessStartInfo(_helper){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=_package};start.ArgumentList.Add("--prepare-update");
            SetupPaths.RequireNoLinks(_helper);using var child=Process.Start(start)??throw new IOException("Update review did not start.");
            try{child.PriorityClass=ProcessPriorityClass.BelowNormal;}catch(Win32Exception){}
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);deadline.CancelAfter(TimeSpan.FromMinutes(4));
            var output=ReadBoundedAsync(child.StandardOutput,deadline.Token);var errors=ReadBoundedAsync(child.StandardError,deadline.Token);
            try
            {
                await child.WaitForExitAsync(deadline.Token);var results=await Task.WhenAll(output,errors);using var document=JsonDocument.Parse(results[0]);
                if(child.ExitCode!=0)throw new InvalidOperationException(document.RootElement.TryGetProperty("error",out var error)?error.GetString():"The update review was rejected.");
                var review=JsonSerializer.Deserialize<AppUpdateReview>(results[0])??throw new IOException("The review result is missing.");
                if(!Guid.TryParseExact(review.Files.Id,"N",out _)||!SetupPaths.CanonicalRoot(review.PackageRoot).Equals(SetupPaths.CanonicalRoot(_package),StringComparison.OrdinalIgnoreCase))throw new IOException("The reviewed package identity is invalid.");
                _=new UpdateSwap(ServiceEndpoint.InstallRoot,review.Files.Id,review.Files.Original,review.Files.Replacement);
                Directory.CreateDirectory(_folder);SetupPaths.RequireNoLinks(_folder);var path=SetupPaths.Resolve(_folder,review.Files.Id+".json");
                var bytes=JsonSerializer.SerializeToUtf8Bytes(review);await WriteAsync(path,bytes,false,_stop.Token);
                await WriteAsync(Path.Combine(_folder,"latest"),Encoding.ASCII.GetBytes(review.Files.Id),true,_stop.Token);
                lock(_gate){_review=review;_path=path;_hash=Convert.ToHexString(SHA256.HashData(bytes));_phase="Review";_detail="Review the current and new builds. Apply uses Windows administrator approval, retains both images and backs up only the new application's metadata. The former broadcaster is not changed.";}
            }
            finally
            {
                if(!child.HasExited){child.Kill(entireProcessTree:true);using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));await child.WaitForExitAsync(cleanup.Token);}
                try{await Task.WhenAll(output,errors);}catch(Exception)when(deadline.IsCancellationRequested||output.IsFaulted||errors.IsFaulted){}
            }
        }
        catch(Exception error){lock(_gate){_phase="Review failed";_detail=error is InvalidOperationException or ArgumentException?error.Message:"Update review failed. Check the complete package, original operator account, permissions and available disk space.";}}
    }
    public void Execute(string id,UpdateDirection direction)
    {
        if(!Available||!Enum.IsDefined(direction))throw new InvalidOperationException("Use the external Windows package that prepared this update review.");
        lock(_gate)
        {
            if(!_operation.IsCompleted)throw new InvalidOperationException("Wait for the current application update operation.");
            if(_review?.Files.Id!=id||_path is null||_hash is null||_phase=="Review failed"||!_review.Files.Replacement.BundleSha256.Equals(_packageIdentity,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Read the matching package's saved update review before continuing.");
            var review=_review;var path=_path;var hash=_hash;_phase="Verifying package";_detail="Rechecking the reviewed files before Windows administrator approval.";
            _operation=Task.Run(()=>ExecuteCoreAsync(review,path,hash,direction));
        }
    }
    private void ApprovalDetail(UpdateDirection direction)
    {
        lock(_gate){_phase="Administrator approval";_detail=direction==UpdateDirection.Apply?"Approve the reviewed update of the stopped, unarmed new service. The former broadcasts keep their existing owner.":"Approve application rollback. Current library, queue and journal data will be retained.";}
    }
    private async Task ExecuteCoreAsync(AppUpdateReview review,string path,string hash,UpdateDirection direction)
    {
        try
        {
            await SetupPaths.ReadVerifiedAsync<AppUpdateReview>(path,hash,_stop.Token);
            await SetupPaths.VerifyBundleAsync(_package,review.Files.Replacement.BundleSha256,_stop.Token);
            ApprovalDetail(direction);
            var start=new ProcessStartInfo(_helper){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=_package};
            start.ArgumentList.Add(direction==UpdateDirection.Apply?"--apply-update":"--rollback-update");start.ArgumentList.Add(path);start.ArgumentList.Add(hash);
            SetupPaths.RequireNoLinks(_helper);using var child=Process.Start(start)??throw new IOException("Windows application maintenance did not start.");
            // An elevated helper survives console closure; no UI timeout kills it.
            await child.WaitForExitAsync(_stop.Token);
            lock(_gate){_phase=child.ExitCode==0?(direction==UpdateDirection.Apply?"Updated":"Rolled back"):"Update needs attention";_detail=child.ExitCode==0?"Read the verified completion receipt below, then reconnect the engine. Current data is retained.":"Read the retained progress and failure below. Resume this same operation or use its targeted rollback.";}
        }
        catch(Win32Exception error)when(error.NativeErrorCode==1223){lock(_gate){_phase="Approval cancelled";_detail="Windows administrator approval was cancelled. The review is retained.";}}
        catch(Exception){lock(_gate){_phase="Update needs attention";_detail="Read the retained operation. An approved helper may continue after this console closes; reopen this package to resume or read the result.";}}
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader,CancellationToken token)
    {
        var text=new StringBuilder();var buffer=new char[4096];int count;
        while((count=await reader.ReadAsync(buffer,token))>0){if(text.Length+count>524288)throw new IOException("Update review exceeded its output bound.");text.Append(buffer,0,count);}return text.ToString();
    }
    private static async Task WriteAsync(string path,byte[] bytes,bool replace,CancellationToken token)
    {
        SetupPaths.RequireNoLinks(path);var partial=path+".partial-"+Guid.NewGuid().ToString("N");SetupPaths.RequireNoLinks(partial);
        await using(var file=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true)){await file.WriteAsync(bytes,token);await file.FlushAsync(token);file.Flush(true);}File.Move(partial,path,replace);
    }
    public async ValueTask DisposeAsync(){await _stop.CancelAsync();try{await _operation;}finally{_stop.Dispose();}}
}
