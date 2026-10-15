using System.Security.Cryptography;
using System.Text.Json;
using OnAirNext.Engine;
using OnAirNext.ServiceControl;
using OnAirNext.UploadProtocol;
using OnAirNext.Web;

namespace OnAirNext.Lab;

internal static class UploadProbe
{
    public static async Task<int> RunAsync()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var original=Path.Combine(ServiceEndpoint.DataRoot,"operator-only-source","selected.wav");
        var before=await File.ReadAllBytesAsync(original,deadline.Token);
        var hash=Convert.ToHexString(SHA256.HashData(before));var id=Guid.NewGuid().ToString("N");
        await UploadRpc.SendAsync(ServiceEndpoint.Pipe,new("begin",id,"service-fixture",[new("selected.wav",before.Length,hash)]),null,deadline.Token);
        bool controlsAvailable;
        await using(var stalled=ServicePipeClient.Create(ServiceEndpoint.FilePipe))
        {
            await stalled.ConnectAsync(deadline.Token);ServicePipeClient.AuthenticateServer(stalled,ServiceEndpoint.FilePipe);
            await stalled.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new UploadRequest("put",id,"service-fixture",Index:0)),deadline.Token);
            await stalled.WriteAsync("\n"u8.ToArray(),deadline.Token);
            var ready=await PipeFrames.ReadAsync(stalled,16384,deadline.Token)??throw new IOException("Missing file handshake.");
            if(JsonSerializer.Deserialize<UploadReply>(ready)?.Send!=true)throw new IOException("Expected an empty retained upload.");
            var live=await EngineRpc.SendAsync(ServiceEndpoint.Pipe,new{Id=Guid.NewGuid().ToString("N"),Action="status"},deadline.Token);
            controlsAvailable=live.GetProperty("controller").GetProperty("mode").GetString()=="windows-service";
            // Closing without bytes exercises partial-file recovery while the control lane stays responsive.
        }
        await using(var input=File.OpenRead(original))await UploadRpc.SendAsync(ServiceEndpoint.Pipe,new("put",id,"service-fixture",Index:0),input,deadline.Token);
        await using(var input=File.OpenRead(original))await UploadRpc.SendAsync(ServiceEndpoint.Pipe,new("put",id,"service-fixture",Index:0),input,deadline.Token);
        var staged=Path.Combine(ServiceEndpoint.EngineData,"upload-inbox",id,"00","selected.wav");
        var verified=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(staged,deadline.Token)))==hash;
        var status=await EngineRpc.SendAsync(ServiceEndpoint.Pipe,new{Id=Guid.NewGuid().ToString("N"),Action="library-status",StationId="service-fixture"},deadline.Token);
        var retained=status.GetProperty("uploads").EnumerateArray().Single().GetProperty("ReceivedFiles").GetInt32()==1;
        var pathImport=await EngineRpc.SendAsync(ServiceEndpoint.Pipe,new{Id=Guid.NewGuid().ToString("N"),Action="library-import",StationId="service-fixture",Paths=new[]{original}},deadline.Token);
        await UploadRpc.SendAsync(ServiceEndpoint.Pipe,new("cancel",id,"service-fixture"),null,deadline.Token);
        var after=await File.ReadAllBytesAsync(original,deadline.Token);
        var preserved=before.SequenceEqual(after);
        Console.WriteLine(JsonSerializer.Serialize(new{verified,retained,preserved,controlsAvailable,discarded=!Directory.Exists(Path.Combine(ServiceEndpoint.EngineData,"upload-inbox",id)),rawPathsRejected=!pathImport.GetProperty("accepted").GetBoolean()}));return 0;
    }
}
