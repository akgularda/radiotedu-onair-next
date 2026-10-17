using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using OnAirNext.Engine;
using OnAirNext.ServiceControl;

namespace OnAirNext.Installation;

// Read-only preparation; no service/configuration/data mutation is permitted here.
[SupportedOSPlatform("windows")]
internal static class AppUpdatePreparation
{
    public static async Task<AppUpdateReview> PrepareAsync(string package,CancellationToken token)
    {
        package=SetupPaths.CanonicalRoot(package);var installed=SetupPaths.CanonicalRoot(ServiceEndpoint.InstallRoot);
        var installParent=Path.GetDirectoryName(installed)!;var packageName=Path.GetFileName(package);
        if(package.Equals(installed,StringComparison.OrdinalIgnoreCase)||package.StartsWith(installed+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
            package.Equals(ServiceEndpoint.DataRoot,StringComparison.OrdinalIgnoreCase)||package.StartsWith(ServiceEndpoint.DataRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
            Path.GetDirectoryName(package)!.Equals(installParent,StringComparison.OrdinalIgnoreCase)&&(packageName.StartsWith(Path.GetFileName(installed)+".candidate-",StringComparison.OrdinalIgnoreCase)||packageName.StartsWith(Path.GetFileName(installed)+".previous-",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Open a complete new package outside the installed application to review an update.");
        var service=SetupService.Read()??throw new InvalidOperationException("Install the isolated service before reviewing an application update.");
        RequireRunningOwned(service);
        using var process=Process.GetProcessById(checked((int)service.ProcessId));_=process.SafeHandle;
        var started=new DateTimeOffset(process.StartTime.ToUniversalTime());
        // The SCM-owned binary path is already checked by RequireOwned. Reading
        // MainModule requires elevation on some Windows builds even for a
        // read-only review; do not turn that diagnostic into an admin-only gate.
        foreach(var path in new[]{ServiceEndpoint.DataRoot,installed,ServiceEndpoint.ConfigPath,Path.Combine(ServiceEndpoint.DataRoot,"setup-plan.json"),Path.Combine(ServiceEndpoint.DataRoot,"setup-ownership.json")})WindowsInstaller.AssertProtected(path);
        using var identity=WindowsIdentity.GetCurrent();var sid=identity.User?.Value??throw new InvalidOperationException("The Windows operator is unavailable.");
        var initialPath=Path.Combine(ServiceEndpoint.DataRoot,"setup-plan.json");var initialHash=await SetupPaths.HashAsync(initialPath,token);
        var initial=await SetupPaths.ReadVerifiedAsync<SetupPlan>(initialPath,initialHash,token);
        if(initial.OperatorSid!=sid||!Guid.TryParseExact(initial.Id,"N",out _))throw new InvalidOperationException("Review updates from the original Windows operator account.");
        var ownershipPath=Path.Combine(ServiceEndpoint.DataRoot,"setup-ownership.json");var ownershipHash=await SetupPaths.HashAsync(ownershipPath,token);
        var ownership=await SetupPaths.ReadVerifiedAsync<JsonElement>(ownershipPath,ownershipHash,token);
        var claim=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(initial)));
        if(ownership.GetProperty("PlanId").GetString()!=initial.Id||!string.Equals(ownership.GetProperty("PlanSha256").GetString(),claim,StringComparison.OrdinalIgnoreCase))
            throw new IOException("The retained installation plan does not match its ownership receipt.");
        var configHash=await SetupPaths.HashAsync(ServiceEndpoint.ConfigPath,token);
        var config=await SetupPaths.ReadVerifiedAsync<JsonElement>(ServiceEndpoint.ConfigPath,configHash,token);
        UpdateAdmission.RequireUnarmedConfiguration(config,sid);
        var initialStatus=await StatusAsync(token);UpdateAdmission.RequireUnarmedStatus(initialStatus);
        var runtime=await RuntimeSupplementInventory.EffectiveAsync(initial,initialHash,Path.Combine(ServiceEndpoint.DataRoot,RuntimeSupplementInventory.ReceiptName),installed,WindowsInstaller.AssertProtected,token);
        var original=new UpdateImage(await SetupPaths.HashAsync(Path.Combine(installed,"bundle.json"),token),runtime);
        var currentBundle=await UpdateSwap.VerifyImageAsync(installed,original,token);
        foreach(var file in currentBundle.Files)WindowsInstaller.AssertProtected(SetupPaths.Resolve(installed,file.Path));
        foreach(var file in original.RuntimeFiles)WindowsInstaller.AssertProtected(SetupPaths.Resolve(installed,"tools/"+file.Path));
        WindowsInstaller.AssertProtected(Path.Combine(installed,"bundle.json"));
        var nextHash=await SetupPaths.HashAsync(Path.Combine(package,"bundle.json"),token);
        var nextBundle=await SetupPaths.VerifyBundleAsync(package,nextHash,token);
        var contractFile=nextBundle.Files.SingleOrDefault(f=>f.Path=="setup/maintenance-contract.json")??throw new IOException("The new package has no maintenance compatibility contract.");
        var contract=await SetupPaths.ReadVerifiedAsync<MaintenanceContract>(SetupPaths.Resolve(package,contractFile.Path),contractFile.Sha256,token);
        if(contract.ContractVersion!=1||contract.JournalSchemaVersion!=1||!contract.RetainsCurrentDataOnRollback)throw new InvalidOperationException("This package requires an explicit journal migration outside application-only maintenance.");
        var replacement=new UpdateImage(nextHash,original.RuntimeFiles.ToArray());
        var files=new UpdateSwapReview(Guid.NewGuid().ToString("N"),original,replacement,DateTimeOffset.UtcNow);
        _=new UpdateSwap(installed,files.Id,original,replacement);
        // Verification may take time on a small PC. Read the authenticated status again
        // and bind the review to the same process/configuration/installation identity.
        var finalStatus=await StatusAsync(token);UpdateAdmission.RequireUnarmedStatus(finalStatus);
        if(process.HasExited||SetupService.Read()!=service||!configHash.Equals(await SetupPaths.HashAsync(ServiceEndpoint.ConfigPath,token),StringComparison.OrdinalIgnoreCase)||
            !ownershipHash.Equals(await SetupPaths.HashAsync(ownershipPath,token),StringComparison.OrdinalIgnoreCase)||
            !initialHash.Equals(await SetupPaths.HashAsync(initialPath,token),StringComparison.OrdinalIgnoreCase))
            throw new IOException("The service or installation changed during review. Prepare a fresh review.");
        var statusHash=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(finalStatus.GetRawText())));
        return new(files,sid,package,configHash,ownershipHash,initialHash,service.ProcessId,started,1,currentBundle.Source,nextBundle.Source,statusHash);
    }
    private static void RequireRunningOwned(SetupServiceState service)
    {
        SetupService.RequireOwned(service);
        if(service.State!=4||service.ProcessId==0||service.StartType!=2||!service.Delayed||!service.FailureFlag||service.RecoveryActions!=3)
            throw new InvalidOperationException("Connect the healthy installed service with its verified automatic startup and recovery settings before reviewing an update.");
    }
    internal static async Task<JsonElement> StatusAsync(CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(4));
        await using var pipe=ServicePipeClient.Create(ServiceEndpoint.Pipe);
        await pipe.ConnectAsync(deadline.Token);ServicePipeClient.AuthenticateServer(pipe,ServiceEndpoint.Pipe);
        var request=JsonSerializer.SerializeToUtf8Bytes(new{Id=Guid.NewGuid().ToString("N"),Action="status"});
        await pipe.WriteAsync(request,deadline.Token);await pipe.WriteAsync("\n"u8.ToArray(),deadline.Token);
        var response=await PipeFrames.ReadAsync(pipe,524288,deadline.Token)??throw new EndOfStreamException("The service closed before acknowledging update review.");
        using var document=JsonDocument.Parse(response);return document.RootElement.Clone();
    }
}
