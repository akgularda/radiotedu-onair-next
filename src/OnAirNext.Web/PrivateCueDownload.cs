using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using OnAirNext.Engine;
using OnAirNext.ServiceControl;

namespace OnAirNext.Web;

internal sealed class PrivateCueDownload:IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;private readonly CancellationTokenSource _deadline;
    public PrivateCueHeader Header {get;}
    private PrivateCueDownload(NamedPipeClientStream pipe,CancellationTokenSource deadline,PrivateCueHeader header)
    {_pipe=pipe;_deadline=deadline;Header=header;}
    public static async Task<PrivateCueDownload> OpenAsync(string engine,PrivateCueRequest request,CancellationToken cancellation)
    {
        if(!EngineRpc.IsIsolated(engine))throw new ArgumentException("Choose a connected engine from System.");request.Validate();
        var name=ServiceEndpoint.Cue(engine);var pipe=ServicePipeClient.Create(name);
        var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            await pipe.ConnectAsync(deadline.Token);ServicePipeClient.AuthenticateServer(pipe,name);
            await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(request),deadline.Token);await pipe.WriteAsync("\n"u8.ToArray(),deadline.Token);
            var line=await PipeFrames.ReadAsync(pipe,4096,deadline.Token)??throw new IOException("Private cue did not confirm its source.");
            var header=JsonSerializer.Deserialize<PrivateCueHeader>(line)??throw new IOException("Invalid private cue format.");
            if(!header.Accepted)throw new InvalidOperationException(header.Error??"Private cue is not available.");
            if(header.SessionId!=request.SessionId||header.StartSample!=request.StartSample||header.TotalSamples<=header.StartSample||
                header.SampleRate!=48000||header.Channels!=2||header.Format!="f32le")throw new IOException("Private cue returned an unexpected source or audio format.");
            await pipe.WriteAsync(new byte[]{6},deadline.Token);return new(pipe,deadline,header);
        }
        catch{await pipe.DisposeAsync();deadline.Dispose();throw;}
    }
    public async Task CopyToAsync(Stream destination,CancellationToken cancellation)
    {
        await LineAsync(Header);var prefix=new byte[4];var pcm=new byte[7680];long frames=0;
        while(true)
        {
            cancellation.ThrowIfCancellationRequested();_deadline.CancelAfter(TimeSpan.FromSeconds(8));
            await _pipe.ReadExactlyAsync(prefix,_deadline.Token);var count=BinaryPrimitives.ReadInt32LittleEndian(prefix);
            if(count==0)
            {
                var line=await PipeFrames.ReadAsync(_pipe,4096,_deadline.Token)??throw new IOException("Private cue ended without a completion receipt.");
                var result=JsonSerializer.Deserialize<PrivateCueResult>(line)??throw new IOException("Invalid cue completion receipt.");
                if(result.SessionId!=Header.SessionId||result.Completed&&(result.Frames!=frames||frames==0))throw new IOException("Private cue frame accounting did not match its receipt.");
                await _pipe.WriteAsync(new byte[]{4},_deadline.Token);await destination.WriteAsync(prefix,cancellation);await LineAsync(result);return;
            }
            if(count<8||count>pcm.Length||count%8!=0)throw new IOException("Private cue exceeded its stereo block limit.");
            await _pipe.ReadExactlyAsync(pcm.AsMemory(0,count),_deadline.Token);frames+=count/8;
            if(frames>48000L*86400*7)throw new IOException("Private cue exceeded its session duration limit.");
            await destination.WriteAsync(prefix,cancellation);await destination.WriteAsync(pcm.AsMemory(0,count),cancellation);await destination.FlushAsync(cancellation);
        }
        async Task LineAsync<T>(T value)
        {await destination.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(value),cancellation);await destination.WriteAsync("\n"u8.ToArray(),cancellation);await destination.FlushAsync(cancellation);}
    }
    public async ValueTask DisposeAsync(){try{await _pipe.DisposeAsync();}finally{_deadline.Dispose();}}
}
internal sealed record PrivateCueOpen(string Engine,PrivateCueRequest Cue);
internal sealed record PrivateCueStop(string Engine,string SessionId);
