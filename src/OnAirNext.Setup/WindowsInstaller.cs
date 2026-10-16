using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using OnAirNext.ServiceControl;

namespace OnAirNext.Installation;

[SupportedOSPlatform("windows")]
internal static class WindowsInstaller
{
    private sealed record Ownership(string PlanId,string PlanSha256);
    public static async Task<int> InstallAsync(string planPath,string expected)
    {
        using var identity=WindowsIdentity.GetCurrent();
        if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))throw new UnauthorizedAccessException("Approve the Windows administrator prompt to install the isolated service.");
        var plan=await SetupPaths.ReadVerifiedAsync<SetupPlan>(planPath,expected,CancellationToken.None);
        if(!Guid.TryParseExact(plan.Id,"N",out _)||plan.BundleSha256 is null||plan.BundleSha256.Length!=64||!plan.BundleSha256.All(Uri.IsHexDigit))throw new ArgumentException("Prepare a new installation plan in System.");
        var claimHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan)));
        var account=new SecurityIdentifier(plan.OperatorSid);if(!account.IsAccountSid()&&!account.Value.StartsWith("S-1-12-1-",StringComparison.Ordinal))throw new ArgumentException("Choose an individual Windows operator.");
        SetupPaths.ValidateFiles(plan.RuntimeFiles,false);
        var bundle=await SetupPaths.VerifyBundleAsync(plan.BundleRoot,plan.BundleSha256,CancellationToken.None);
        var runtime=SetupPaths.CanonicalRoot(plan.RuntimeRoot);
        var service=SetupService.Read();if(service is not null)SetupService.RequireOwned(service);
        var root=ServiceEndpoint.DataRoot;var install=ServiceEndpoint.InstallRoot;
        ServiceEndpoint.RequirePath(root,root);ServiceEndpoint.RequirePath(install,install);
        var marker=Path.Combine(root,"setup-ownership.json");
        if(Directory.Exists(root))
        {
            AssertProtected(root);
            if(!File.Exists(marker)&&Directory.EnumerateFileSystemEntries(root).Any(p=>Path.GetFileName(p)!="setup.lock"))throw new InvalidOperationException("The new data folder already contains unrelated files. No files were replaced.");
        }
        if(Directory.Exists(install))
        {
            AssertProtected(install);
            if(!File.Exists(marker)&&Directory.EnumerateFileSystemEntries(install).Any())throw new InvalidOperationException("The new installation folder already contains unrelated files. No files were replaced.");
        }
        CreateProtected(root,account,false);
        var lockPath=Path.Combine(root,"setup.lock");SetupPaths.RequireNoLinks(lockPath);
        if(!File.Exists(lockPath)){await using(var created=new FileStream(lockPath,FileMode.CreateNew,FileAccess.Write,FileShare.None)){created.Flush(true);}SetOwner(lockPath);}
        await using var held=new FileStream(lockPath,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        if(File.Exists(marker))
        {
            AssertProtected(marker);var owner=await SetupPaths.ReadAsync<Ownership>(marker);
            if(owner.PlanId!=plan.Id||!owner.PlanSha256.Equals(claimHash,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("This installation belongs to a different reviewed plan. Its files and service were left unchanged.");
        }
        else
        {
            if(service is not null)throw new InvalidOperationException("An existing service has no matching installation receipt. No service was changed.");
            await WriteNewAsync(marker,new Ownership(plan.Id,claimHash));
        }
        var stage="package copy";
        try
        {
            await WriteNewAsync(Path.Combine(root,"setup-plan.json"),plan);
            var required=bundle.Files.Sum(f=>f.Bytes)+plan.RuntimeFiles.Sum(f=>f.Bytes)+2L*1024*1024*1024;
            if(new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace<required)throw new IOException("Setup needs the package size plus 2 GiB of free space.");
            await ReceiptAsync(new(plan.Id,"Installing","Copying the reviewed package. Existing broadcasts stay with the former installation.",bundle.Source));
            CreateProtected(install,account,false);
            var running=service is not null&&service.State!=1;
            var installed=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(var file in bundle.Files)await CopyAsync(SetupPaths.Resolve(plan.BundleRoot,file.Path),SetupPaths.Resolve(install,file.Path),file,account,running,installed);
            stage="runtime copy";
            foreach(var file in plan.RuntimeFiles)await CopyAsync(SetupPaths.Resolve(runtime,file.Path),SetupPaths.Resolve(install,"tools/"+file.Path),file,account,running,installed);
            await CopyAsync(SetupPaths.Resolve(plan.BundleRoot,"bundle.json"),Path.Combine(install,"bundle.json"),new("bundle.json",new FileInfo(Path.Combine(plan.BundleRoot,"bundle.json")).Length,plan.BundleSha256),account,running,installed);
            stage="initial stopped channels";
            CreateProtected(ServiceEndpoint.EngineData,account,true);
            var config=new{Ffmpeg=ServiceEndpoint.Ffmpeg,DataRoot=ServiceEndpoint.EngineData,PipeName=ServiceEndpoint.Pipe,OperatorSid=account.Value,
                Stations=SetupPaths.StationIds.Select(id=>new{Id=id,Playlist=Array.Empty<object>(),Outputs=Array.Empty<object>(),Repeat=true,Enabled=true,AutoStart=false}).ToArray()};
            await WriteNewAsync(ServiceEndpoint.ConfigPath,config);
            // Protecting the exact installation is also checked independently by the service on every boot.
            ServiceFileProtection.BootstrapInstalled(ServiceEndpoint.ConfigPath,Path.Combine(install,"bin"));
            stage="Windows service registration";
            await ReceiptAsync(new(plan.Id,"Installing","Verifying isolated service startup and recovery settings. All eight channels are initially stopped.",bundle.Source));
            await SetupService.InstallAsync(plan.StartService,CancellationToken.None);
            var after=SetupService.Read()??throw new IOException("Service readback failed.");SetupService.RequireOwned(after);
            await ReceiptAsync(new(plan.Id,"Installed","Installation files and Windows service settings were read back. Connect the engine and complete media/output qualification before migration.",bundle.Source,after.State==4));return 0;
        }
        catch(Exception error)
        {
            var detail=error is ArgumentException or InvalidOperationException ? error.Message : error is Win32Exception native ? "Windows setup operation failed (code "+native.NativeErrorCode+")." : "Check free space, permissions and unchanged package/runtime files, then retry this reviewed plan.";
            await ReceiptAsync(new(plan.Id,"Failed","Setup stopped during "+stage+". "+detail,bundle.Source));return 1;
        }
    }
    internal static void AssertProtected(string path)
    {
        SetupPaths.RequireNoLinks(path);
        FileSystemSecurity security=Directory.Exists(path)?new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access|AccessControlSections.Owner):new FileInfo(path).GetAccessControl(AccessControlSections.Access|AccessControlSections.Owner);
        ServiceFileProtection.AssertProtected(security);
    }
    private static DirectorySecurity Security(SecurityIdentifier account,bool data)
    {
        var security=new DirectorySecurity();security.SetAccessRuleProtection(true,false);
        var admin=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);security.SetOwner(admin);
        foreach(var sid in new[]{admin,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})security.AddAccessRule(new(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalServiceSid,null),data?FileSystemRights.Modify:FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        security.AddAccessRule(new(account,FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));return security;
    }
    private static void CreateProtected(string directory,SecurityIdentifier account,bool data)
    {
        SetupPaths.RequireNoLinks(directory);
        if(Directory.Exists(directory)){if(!data)AssertProtected(directory);return;}
        new DirectoryInfo(directory).Create(Security(account,data));
    }
    private static async Task CopyAsync(string source,string destination,SetupFile file,SecurityIdentifier account,bool running,Dictionary<string,string> installed)
    {
        SetupPaths.RequireNoLinks(source);SetupPaths.RequireNoLinks(destination);
        if(File.Exists(destination))
        {
            AssertProtected(destination);
            if(new FileInfo(destination).Length!=file.Bytes||!string.Equals(await SetupPaths.HashAsync(destination),file.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("An installed file differs from this reviewed package. No file was overwritten.");installed[file.Sha256+":"+file.Bytes]=destination;return;
        }
        if(running)throw new InvalidOperationException("The isolated service is active and its installation is incomplete. No active program files were replaced.");
        var parent=Path.GetDirectoryName(destination)!;
        var missing=new Stack<string>();for(var item=parent;!Directory.Exists(item);item=Path.GetDirectoryName(item)!)missing.Push(item);
        while(missing.TryPop(out var folder))CreateProtected(folder,account,false);
        var key=file.Sha256+":"+file.Bytes;
        if(installed.TryGetValue(key,out var identical))
        {
            SetupPaths.RequireNoLinks(identical);AssertProtected(identical);
            if(CreateHardLinkW(destination,identical,0))return;
            // Unsupported filesystems retain the verified-copy behavior; audio quality is unaffected.
        }
        var temporary=destination+".onair-installing";SetupPaths.RequireNoLinks(temporary);
        // Only this manifest-owned partial path is replaced; there is no recursive cleanup.
        if(File.Exists(temporary)){AssertProtected(Path.GetDirectoryName(temporary)!);File.Delete(temporary);}
        try
        {
            await using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan))
            await using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true))
            {if(input.Length!=file.Bytes)throw new IOException("A source package file changed.");await input.CopyToAsync(output,65536);await output.FlushAsync();output.Flush(true);}
            SetOwner(temporary);
            if(!string.Equals(await SetupPaths.HashAsync(temporary),file.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("Copied package bytes do not match the reviewed file.");
            File.Move(temporary,destination,false);installed[key]=destination;
        }
        finally{SetupPaths.RequireNoLinks(temporary);if(File.Exists(temporary))File.Delete(temporary);}
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool CreateHardLinkW(string path,string existing,nint security);
    private static void SetOwner(string path)
    {
        SetupPaths.RequireNoLinks(path);var file=new FileInfo(path);var security=file.GetAccessControl(AccessControlSections.Owner);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null));file.SetAccessControl(security);
    }
    private static async Task WriteNewAsync<T>(string path,T value)
    {
        SetupPaths.RequireNoLinks(path);var bytes=JsonSerializer.SerializeToUtf8Bytes(value);
        if(File.Exists(path))
        {
            AssertProtected(path);var existing=await File.ReadAllBytesAsync(path);if(!bytes.AsSpan().SequenceEqual(existing))throw new IOException("An existing setup file differs. No configuration was overwritten.");return;
        }
        var temporary=path+".onair-installing";SetupPaths.RequireNoLinks(temporary);if(File.Exists(temporary)){AssertProtected(Path.GetDirectoryName(temporary)!);File.Delete(temporary);}
        await using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true)){await stream.WriteAsync(bytes);await stream.FlushAsync();stream.Flush(true);}SetOwner(temporary);File.Move(temporary,path,false);
    }
    private static async Task ReceiptAsync(SetupReceipt receipt)
    {
        var path=Path.Combine(ServiceEndpoint.DataRoot,"setup-status.json");SetupPaths.RequireNoLinks(path);var temporary=path+".onair-installing";SetupPaths.RequireNoLinks(temporary);
        if(File.Exists(temporary)){AssertProtected(Path.GetDirectoryName(temporary)!);File.Delete(temporary);}
        await using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true)){await JsonSerializer.SerializeAsync(stream,receipt);await stream.FlushAsync();stream.Flush(true);}SetOwner(temporary);File.Move(temporary,path,true);
    }
}
