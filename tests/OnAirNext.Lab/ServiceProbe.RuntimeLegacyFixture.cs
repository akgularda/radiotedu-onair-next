using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using OnAirNext.Installation;
using OnAirNext.ServiceControl;

namespace OnAirNext.Lab;

internal static partial class ServiceProbe
{
    // Disposable software-fixture layout simulation, never a product repair command.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static async Task<int> RuntimeLegacyFixtureAsync()
    {
        using var identity=WindowsIdentity.GetCurrent();
        if(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")!="true"||Environment.GetEnvironmentVariable("ONAIR_SETUP_TEST_RUNTIME")!="1"||
            !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)||Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RadioTEDU","OnAir")))
            throw new InvalidOperationException("Disposable Windows setup fixture only. Use the guided UI on the broadcasting PC.");
        var initialPath=Path.Combine(ServiceEndpoint.DataRoot,"setup-plan.json");var initial=await SetupPaths.ReadAsync<SetupPlan>(initialPath);
        var expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RadioTEDU","OnAirNext-Dev");
        var fixture=SetupPaths.CanonicalRoot(initial.BundleRoot);
        if(!Path.GetDirectoryName(fixture)!.Equals(expected,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(fixture).StartsWith("setup-fixture-",StringComparison.Ordinal)||!Guid.TryParseExact(Path.GetFileName(fixture)[14..],"N",out _))
            throw new InvalidOperationException("The initial package is not this disposable setup fixture.");
        var service=SetupService.Read()??throw new IOException("Fixture service missing.");SetupService.RequireOwned(service);
        UpdateAdmission.RequireUnarmedStatus(await RequestAsync(new{Id=Guid.NewGuid().ToString("N"),Action="status"}));
        var original=initial.RuntimeFiles.Where(f=>f.Path.StartsWith("bin/",StringComparison.Ordinal)&&f.Path!="bin/ffprobe.exe").Select(f=>f with{Path=SetupPaths.RuntimeDestination(f.Path)}).ToArray();
        if(original.Length!=8)throw new InvalidOperationException("Expected exactly eight synthetic original binary files.");
        var legacy=initial with{RuntimeRoot=Path.Combine(initial.RuntimeRoot,"bin"),RuntimeFiles=original};
        var additions=RuntimeSupplementInventory.Additions(legacy.RuntimeFiles,initial.RuntimeFiles);
        if(additions.Length!=12||File.Exists(Path.Combine(ServiceEndpoint.DataRoot,RuntimeSupplementInventory.ReceiptName)))throw new InvalidOperationException("Fixture provider shape or receipt is unexpected.");
        // Verify every target before removing any fixture-only additions. No recursion.
        foreach(var file in additions)
        {
            var path=SetupPaths.Resolve(ServiceEndpoint.InstallRoot,"tools/"+SetupPaths.RuntimeDestination(file.Path));
            if(new FileInfo(path).Length!=file.Bytes||!string.Equals(await SetupPaths.HashAsync(path),file.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("The fixture addition changed.");
        }
        foreach(var file in additions)File.Delete(SetupPaths.Resolve(ServiceEndpoint.InstallRoot,"tools/"+SetupPaths.RuntimeDestination(file.Path)));
        var bytes=JsonSerializer.SerializeToUtf8Bytes(legacy);
        await using(var writer=new FileStream(initialPath,FileMode.Truncate,FileAccess.Write,FileShare.None)){await writer.WriteAsync(bytes);writer.Flush(true);}
        var owner=JsonSerializer.SerializeToUtf8Bytes(new{PlanId=legacy.Id,PlanSha256=Convert.ToHexString(SHA256.HashData(bytes))});
        await using(var writer=new FileStream(Path.Combine(ServiceEndpoint.DataRoot,"setup-ownership.json"),FileMode.Truncate,FileAccess.Write,FileShare.None)){await writer.WriteAsync(owner);writer.Flush(true);}
        UpdateAdmission.RequireUnarmedStatus(await RequestAsync(new{Id=Guid.NewGuid().ToString("N"),Action="status"}));
        if(SetupService.Read()!=service)throw new IOException("The fixture service changed during layout simulation.");
        Console.WriteLine(JsonSerializer.Serialize(legacy));return 0;
    }
}
