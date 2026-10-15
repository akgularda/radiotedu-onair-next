using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OnAirNext.Installation;
using OnAirNext.ServiceControl;

namespace OnAirNext.Web;

internal sealed class SetupCoordinator : IAsyncDisposable
{
    private readonly string _folder;
    private readonly string _package;
    private readonly string _executable;
    private readonly object _gate=new();
    private readonly CancellationTokenSource _stop=new();
    private Task _operation=Task.CompletedTask;
    private SetupPlan? _plan;
    private string? _planPath,_planHash;
    private string _phase="Not prepared",_detail="Choose the local audio runtime, then review the separate installation.";
    public SetupCoordinator(string dataRoot)
    {
        _folder=Path.Combine(dataRoot,"setup");_package=Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.FullName;
        _executable=Path.Combine(_package,"setup","OnAirNext.Setup.exe");
        if(OperatingSystem.IsWindows())
        {
            var pointer=Path.Combine(_folder,"latest.json");
            try
            {
                if(File.Exists(pointer)&&new FileInfo(pointer).Length<256)
                {
                    var id=File.ReadAllText(pointer).Trim();if(Guid.TryParseExact(id,"N",out _))
                    {
                        _planPath=Path.Combine(_folder,id+".json");SetupPaths.RequireNoLinks(_planPath);
                        if(new FileInfo(_planPath).Length<524288){var bytes=File.ReadAllBytes(_planPath);_plan=JsonSerializer.Deserialize<SetupPlan>(bytes);_planHash=Convert.ToHexString(SHA256.HashData(bytes));_phase="Review";}
                    }
                }
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException){_detail="A saved plan could not be opened. Reopen its original installation package.";}
        }
    }
    public object Status()
    {
        SetupReceipt? receipt=null;SetupServiceState? service=null;string? systemError=null;
        if(OperatingSystem.IsWindows())
        {
            try
            {
                var path=Path.Combine(ServiceEndpoint.DataRoot,"setup-status.json");SetupPaths.RequireNoLinks(path);
                if(File.Exists(path)&&new FileInfo(path).Length<=32768)receipt=JsonSerializer.Deserialize<SetupReceipt>(File.ReadAllBytes(path));
                service=SetupService.Read();if(service is not null)SetupService.RequireOwned(service);
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or Win32Exception or InvalidOperationException){systemError="Windows installation status could not be verified. Existing services were left unchanged.";}
        }
        if(_plan is null&&OperatingSystem.IsWindows())
        {
            try
            {
                var saved=Path.Combine(ServiceEndpoint.DataRoot,"setup-plan.json");SetupPaths.RequireNoLinks(saved);
                if(File.Exists(saved)&&new FileInfo(saved).Length<524288)
                {
                    var bytes=File.ReadAllBytes(saved);var recovered=JsonSerializer.Deserialize<SetupPlan>(bytes);
                    if(recovered is not null&&Guid.TryParseExact(recovered.Id,"N",out _)){SetupPaths.ValidateFiles(recovered.RuntimeFiles,false);lock(_gate){if(_plan is null){_plan=recovered;_planPath=saved;_planHash=Convert.ToHexString(SHA256.HashData(bytes));_phase="Review";}}}
                }
            }
            catch(Exception error)when(error is IOException or JsonException or UnauthorizedAccessException or ArgumentException){systemError="The retained setup plan could not be recovered. Keep the installation and inspect System before continuing.";}
        }
        lock(_gate)return new{supported=OperatingSystem.IsWindows(),packageAvailable=File.Exists(_executable)&&File.Exists(Path.Combine(_package,"bundle.json")),busy=!_operation.IsCompleted,phase=_phase,detail=_detail,
            plan=_plan is null?null:new{_plan.Id,_plan.OperatorSid,_plan.RuntimeRoot,_plan.StartService,bytes=_plan.RuntimeFiles.Sum(f=>f.Bytes),bundle=_plan.BundleSha256},receipt,service,systemError,
            installRoot=ServiceEndpoint.InstallRoot,dataRoot=ServiceEndpoint.DataRoot};
    }
    public void Prepare(SetupRequest request)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Windows service setup is available in the Windows package.");
        if(!File.Exists(_executable)||!File.Exists(Path.Combine(_package,"bundle.json")))throw new InvalidOperationException("Open the complete Windows installation package to prepare setup.");
        SetupPaths.CanonicalRoot(request.RuntimeFolder);
        lock(_gate)
        {
            if(!_operation.IsCompleted)throw new InvalidOperationException("Wait for the current setup operation.");
            if(Directory.Exists(ServiceEndpoint.DataRoot)||SetupService.Read() is not null)throw new InvalidOperationException("An installation or recovery receipt already exists. Resume its reviewed plan instead of preparing another installation.");
            _phase="Checking";_detail="Verifying package bytes and inspecting the runtime as the current operator. No service is installed at this step.";
            _operation=Task.Run(()=>PrepareCoreAsync(request));
        }
    }
    private async Task PrepareCoreAsync(SetupRequest request)
    {
        try
        {
            Directory.CreateDirectory(_folder);SetupPaths.RequireNoLinks(_folder);
            var requestPath=Path.Combine(_folder,"request-"+Guid.NewGuid().ToString("N")+".json");
            var bytes=JsonSerializer.SerializeToUtf8Bytes(request);await File.WriteAllBytesAsync(requestPath,bytes,_stop.Token);
            try
            {
                var start=new ProcessStartInfo(_executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                start.ArgumentList.Add("--prepare");start.ArgumentList.Add(requestPath);start.ArgumentList.Add(Convert.ToHexString(SHA256.HashData(bytes)));
                SetupPaths.RequireNoLinks(_executable);using var child=Process.Start(start)??throw new IOException("Setup inspection did not start.");
                try{child.PriorityClass=ProcessPriorityClass.BelowNormal;}catch(Win32Exception){}
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);deadline.CancelAfter(TimeSpan.FromMinutes(4));
                async Task<string> ReadAsync(StreamReader reader){var builder=new StringBuilder();var buffer=new char[4096];int count;while((count=await reader.ReadAsync(buffer,deadline.Token))>0){if(builder.Length+count>524288)throw new IOException("Setup inspection output exceeded its limit.");builder.Append(buffer,0,count);}return builder.ToString();}
                var output=ReadAsync(child.StandardOutput);var errors=ReadAsync(child.StandardError);
                try
                {
                    await child.WaitForExitAsync(deadline.Token);var results=await Task.WhenAll(output,errors);
                    using var document=JsonDocument.Parse(results[0]);
                    if(child.ExitCode!=0)throw new InvalidOperationException(document.RootElement.TryGetProperty("error",out var error)?error.GetString():"Runtime inspection failed.");
                    var plan=JsonSerializer.Deserialize<SetupPlan>(results[0])??throw new IOException("Setup returned no plan.");
                    if(!Guid.TryParseExact(plan.Id,"N",out _))throw new IOException("Setup returned an invalid plan ID.");
                    SetupPaths.ValidateFiles(plan.RuntimeFiles,false);
                    var path=Path.Combine(_folder,plan.Id+".json");var saved=JsonSerializer.SerializeToUtf8Bytes(plan);
                    await using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true)){await file.WriteAsync(saved,_stop.Token);await file.FlushAsync(_stop.Token);file.Flush(true);}
                    var pointer=Path.Combine(_folder,"latest.json");var temporary=pointer+".partial";SetupPaths.RequireNoLinks(temporary);
                    await using(var file=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None,65536,true)){await file.WriteAsync(Encoding.UTF8.GetBytes(plan.Id),_stop.Token);await file.FlushAsync(_stop.Token);file.Flush(true);}File.Move(temporary,pointer,true);
                    lock(_gate){_plan=plan;_planPath=path;_planHash=Convert.ToHexString(SHA256.HashData(saved));_phase="Review";_detail="Package files match their manifest. libfdk_aac and FLAC are listed; real codec and loudness qualification remains required. Review before administrator approval.";}
                }
                finally
                {
                    if(!child.HasExited){child.Kill(entireProcessTree:true);using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));await child.WaitForExitAsync(cleanup.Token);}
                    try{await Task.WhenAll(output,errors);}catch(Exception)when(deadline.IsCancellationRequested||output.IsFaulted||errors.IsFaulted){}
                }
            }
            finally{SetupPaths.RequireNoLinks(requestPath);if(File.Exists(requestPath))File.Delete(requestPath);}
        }
        catch(Exception error){lock(_gate){_phase="Check failed";_detail=error is InvalidOperationException or ArgumentException?error.Message:"Setup inspection failed. Check the package, selected runtime, permissions and available disk space.";}}
    }
    public void Install(string id)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Use the Windows installation package.");
        lock(_gate)
        {
            if(!_operation.IsCompleted)throw new InvalidOperationException("Wait for the current setup operation.");
            if(_plan?.Id!=id||_planPath is null||_planHash is null)throw new InvalidOperationException("Review the prepared installation again.");
            _phase="Administrator approval";_detail="Approve Windows setup for the separate RadioTEDU OnAir Next service. Existing broadcasts are not released.";
            var path=_planPath;var hash=_planHash;_operation=Task.Run(()=>InstallCoreAsync(path,hash));
        }
    }
    private async Task InstallCoreAsync(string path,string hash)
    {
        try
        {
            var start=new ProcessStartInfo(_executable){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};start.ArgumentList.Add("--install");start.ArgumentList.Add(path);start.ArgumentList.Add(hash);
            SetupPaths.RequireNoLinks(_executable);using var child=Process.Start(start)??throw new IOException("Windows setup did not start.");
            await child.WaitForExitAsync(_stop.Token);
            lock(_gate){_phase=child.ExitCode==0?"Installed":"Installation needs attention";_detail=child.ExitCode==0?"Read the installation receipt below, then connect the isolated engine.":"Read the installation receipt and retry the same reviewed plan. No production channel was taken over.";}
        }
        catch(Win32Exception error)when(error.NativeErrorCode==1223){lock(_gate){_phase="Approval cancelled";_detail="Windows administrator approval was cancelled. The reviewed plan is retained.";}}
        catch(Exception){lock(_gate){_phase="Installation needs attention";_detail="Read the saved installation receipt. An approved installer may continue if this console closes; reopen System to read its result.";}}
    }
    public async ValueTask DisposeAsync(){await _stop.CancelAsync();try{await _operation;}finally{_stop.Dispose();}}
}
