using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using OnAirNext.Engine;
using OnAirNext.ServiceControl;
using OnAirNext.Web;
using Xunit;

namespace OnAirNext.Tests;

public sealed class PrivateCueDownloadTests
{
    private static async Task Scenario(Func<NamedPipeServerStream,PrivateCueRequest,CancellationToken,Task> write,Func<string,PrivateCueRequest,Task> read,bool waitForClose=true)
    {
        var engine="onair-next-dev-cue-test-"+Guid.NewGuid().ToString("N");
        var request=new PrivateCueRequest(Guid.NewGuid().ToString("N"),"a",1,new string('A',64),0,"16:1");
        await using var server=new NamedPipeServerStream(ServiceEndpoint.Cue(engine),PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var worker=Task.Run(async()=>
        {
            try{await server.WaitForConnectionAsync(deadline.Token);await PipeFrames.ReadAsync(server,4096,deadline.Token);await write(server,request,deadline.Token);if(waitForClose){var ack=new byte[1];if(await server.ReadAsync(ack,deadline.Token)!=0)Assert.Equal(4,ack[0]);}}
            catch(Exception error)when(error is IOException or OperationCanceledException){}
            finally{if(server.IsConnected)server.Disconnect();}
        });
        try{await read(engine,request);}finally{await deadline.CancelAsync();await worker;}
    }
    private static async Task LineAsync<T>(Stream stream,T data,CancellationToken token)
    {await stream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(data),token);await stream.WriteAsync("\n"u8.ToArray(),token);}
    private static async Task HeaderAsync(Stream stream,PrivateCueRequest request,CancellationToken token)
    {await LineAsync(stream,new PrivateCueHeader(true,request.SessionId,request.StartSample,96000),token);var ack=new byte[1];await stream.ReadExactlyAsync(ack,token);Assert.Equal(6,ack[0]);}
    private static async Task PrefixAsync(Stream stream,int bytes,CancellationToken token)
    {var prefix=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(prefix,bytes);await stream.WriteAsync(prefix,token);}
    [Fact]
    public async Task Valid_complete_frames_and_verified_terminal_receipt_are_forwarded()
    {
        await Scenario(async(pipe,request,token)=>
        {await HeaderAsync(pipe,request,token);await PrefixAsync(pipe,16,token);await pipe.WriteAsync(new byte[16],token);await PrefixAsync(pipe,0,token);await LineAsync(pipe,new PrivateCueResult(request.SessionId,true,2),token);},async(engine,request)=>
        {
            await using var cue=await PrivateCueDownload.OpenAsync(engine,request,CancellationToken.None);using var sink=new MemoryStream();await cue.CopyToAsync(sink,CancellationToken.None);
            var bytes=sink.ToArray();var newline=Array.IndexOf(bytes,(byte)10);Assert.True(newline>0);
            Assert.Equal(16,BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(newline+1,4)));
            var terminal=JsonSerializer.Deserialize<PrivateCueResult>(bytes.AsSpan(newline+1+4+16+4));Assert.NotNull(terminal);Assert.True(terminal.Completed);Assert.Equal(2,terminal.Frames);
        });
    }
    [Theory]
    [InlineData("oversize")]
    [InlineData("unaligned")]
    [InlineData("truncated")]
    [InlineData("wrong-receipt")]
    public async Task Corrupt_or_partial_transfer_cannot_be_reported_as_complete(string failure)
    {
        await Scenario(async(pipe,request,token)=>
        {
            await HeaderAsync(pipe,request,token);
            if(failure=="oversize"){await PrefixAsync(pipe,7688,token);return;}
            if(failure=="unaligned"){await PrefixAsync(pipe,9,token);return;}
            await PrefixAsync(pipe,16,token);await pipe.WriteAsync(new byte[failure=="truncated"?8:16],token);
            if(failure=="truncated")return;
            await PrefixAsync(pipe,0,token);await LineAsync(pipe,new PrivateCueResult(request.SessionId,true,3),token);
        },async(engine,request)=>
        {await using var cue=await PrivateCueDownload.OpenAsync(engine,request,CancellationToken.None);using var sink=new MemoryStream();await Assert.ThrowsAnyAsync<IOException>(()=>cue.CopyToAsync(sink,CancellationToken.None));},waitForClose:failure!="truncated");
    }
    [Fact]
    public async Task Wrong_session_header_is_rejected_before_audio_is_forwarded()
    {
        await Scenario((pipe,request,token)=>LineAsync(pipe,new PrivateCueHeader(true,Guid.NewGuid().ToString("N"),0,96000),token),async(engine,request)=>
        {await Assert.ThrowsAnyAsync<IOException>(async()=>{await using var cue=await PrivateCueDownload.OpenAsync(engine,request,CancellationToken.None);});});
    }
    [Fact]
    public async Task Failed_source_receipt_remains_failed_even_after_some_valid_pcm()
    {
        await Scenario(async(pipe,request,token)=>
        {await HeaderAsync(pipe,request,token);await PrefixAsync(pipe,16,token);await pipe.WriteAsync(new byte[16],token);await PrefixAsync(pipe,0,token);await LineAsync(pipe,new PrivateCueResult(request.SessionId,false,0,"Source did not finish."),token);},async(engine,request)=>
        {await using var cue=await PrivateCueDownload.OpenAsync(engine,request,CancellationToken.None);using var sink=new MemoryStream();await cue.CopyToAsync(sink,CancellationToken.None);Assert.Contains("\"Completed\":false",System.Text.Encoding.UTF8.GetString(sink.ToArray()));});
    }
    [Fact]
    public void Cue_lane_is_reserved_for_the_owned_service_and_hidden_from_control_discovery()
    {
        Assert.True(ServiceEndpoint.IsReserved(ServiceEndpoint.Cue(ServiceEndpoint.Pipe)));
        Assert.False(EngineRpc.IsIsolated(ServiceEndpoint.Cue(ServiceEndpoint.Pipe)));
        Assert.Throws<ArgumentException>(()=>ServiceEndpoint.Cue(ServiceEndpoint.ReportPipe));
        Assert.Throws<ArgumentException>(()=>ServiceEndpoint.Cue(ServiceEndpoint.CuePipe));
    }
}
