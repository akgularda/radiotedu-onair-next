using System.Text;
using System.Text.Json;
using OnAirNext.Engine;
using OnAirNext.ServiceControl;
using OnAirNext.UploadProtocol;

namespace OnAirNext.Web;

internal static class UploadRpc
{
    public static async Task<UploadReply> SendAsync(string engine,UploadRequest request,Stream? bytes,CancellationToken token)
    {
        if(!EngineRpc.IsIsolated(engine))throw new ArgumentException("Choose an isolated engine from System.");
        var name=ServiceEndpoint.Files(engine);
        var frame=JsonSerializer.SerializeToUtf8Bytes(request);
        if(frame.Length>131000)throw new ArgumentException("The upload manifest exceeds the message limit.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromMinutes(2));
        await using var pipe=ServicePipeClient.Create(name);
        await pipe.ConnectAsync(deadline.Token);
        ServicePipeClient.AuthenticateServer(pipe,name);
        await pipe.WriteAsync(frame,deadline.Token);await pipe.WriteAsync("\n"u8.ToArray(),deadline.Token);
        async Task<UploadReply> ReadAsync()
        {
            var response=await PipeFrames.ReadAsync(pipe,16384,deadline.Token)??throw new EndOfStreamException("The engine closed before acknowledging the transfer. Retry the same upload.");
            var reply=JsonSerializer.Deserialize<UploadReply>(response)??throw new IOException("Missing transfer acknowledgement.");
            if(!reply.Accepted)throw new InvalidOperationException(reply.Error??"The engine rejected this transfer.");return reply;
        }
        var result=await ReadAsync();
        if(result.Send)
        {
            if(bytes is null)throw new ArgumentException("The selected file is required.");
            await bytes.CopyToAsync(pipe,65536,deadline.Token);result=await ReadAsync();
        }
        return result;
    }
}
