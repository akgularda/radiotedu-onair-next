using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using OnAirNext.Engine;
using OnAirNext.ServiceControl;

namespace OnAirNext.Host;

// A private lane with one decoder; programme/control/report/upload lanes never wait for its stream.
internal sealed class PrivateCuePipeServer:IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;private readonly ServicePipePolicy? _policy;
    private readonly PrivateCuePlayback _playback;private readonly CancellationTokenSource _stop;private readonly Task _worker;
    public PrivateCueSnapshot Snapshot()=>_playback.Snapshot();
    public void RequestStop(string session)=>_playback.RequestStop(session);
    public PrivateCuePipeServer(string control,bool serviceMode,ServicePipePolicy? policy,StationLibrary library,string ffmpeg,CancellationToken token)
    {
        _playback=new(library,ffmpeg);_policy=policy;_stop=CancellationTokenSource.CreateLinkedTokenSource(token);
        var name=ServiceEndpoint.Cue(control);
        _pipe=serviceMode&&OperatingSystem.IsWindows()?policy!.CreateServer(name):new NamedPipeServerStream(name,PipeDirection.InOut,1,
            PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly|PipeOptions.FirstPipeInstance,65536,65536);
        _worker=RunAsync();
    }
    private Task ReplyAsync<T>(T value,CancellationToken token)=>_pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(value).Concat(new byte[]{10}).ToArray(),token).AsTask();
    private async Task RunAsync()
    {
        while(!_stop.IsCancellationRequested)
        {
            var connected=false;var authorized=false;var started=false;var writingFrame=false;PrivateCueRequest? request=null;
            try
            {
                await _pipe.WaitForConnectionAsync(_stop.Token);connected=true;
                using var admission=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);admission.CancelAfter(TimeSpan.FromSeconds(5));
                var frame=await PipeFrames.ReadAsync(_pipe,4096,admission.Token);if(frame is null)continue;
                if(_policy is not null&&OperatingSystem.IsWindows())_policy.Authorize(_pipe);authorized=true;
                request=JsonSerializer.Deserialize<PrivateCueRequest>(frame)??throw new ArgumentException("Choose a current cue item.");
                var prefix=new byte[4];
                var result=await _playback.PlayAsync(request,async(header,token)=>{await ReplyAsync(header,token);var ready=new byte[1];await _pipe.ReadExactlyAsync(ready,token);if(ready[0]!=6)throw new IOException("The private cue receiver did not accept its format.");started=true;},
                    async(pcm,token)=>{writingFrame=true;BinaryPrimitives.WriteInt32LittleEndian(prefix,pcm.Length);await _pipe.WriteAsync(prefix,token);await _pipe.WriteAsync(pcm,token);writingFrame=false;},_stop.Token);
                using var end=new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _pipe.WriteAsync(new byte[4],end.Token);await ReplyAsync(result,end.Token);await EndAckAsync(end.Token);
            }
            catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
            catch(Exception error)when(error is IOException or OperationCanceledException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                if(connected&&authorized&&!writingFrame)
                {
                    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    try
                    {
                        const string message="Private cue did not complete. Refresh Library, check its drive and validation state, then retry.";
                        if(!started){await ReplyAsync(new PrivateCueHeader(false,Error:message),timeout.Token);await _pipe.ReadAsync(new byte[1],timeout.Token);}
                        else{await _pipe.WriteAsync(new byte[4],timeout.Token);await ReplyAsync(new PrivateCueResult(request?.SessionId??"",false,0,message),timeout.Token);await EndAckAsync(timeout.Token);}
                    }
                    catch(Exception reply)when(reply is IOException or OperationCanceledException){}
                }
            }
            catch(Exception){try{await Task.Delay(TimeSpan.FromSeconds(1),_stop.Token);}catch(OperationCanceledException){}}
            finally{if(connected)try{_pipe.Disconnect();}catch(IOException){}}
        }
    }
    private async Task EndAckAsync(CancellationToken token)
    {var ack=new byte[1];await _pipe.ReadExactlyAsync(ack,token);if(ack[0]!=4)throw new IOException("The private cue receiver did not acknowledge its completion receipt.");}
    public async ValueTask DisposeAsync()
    {await _stop.CancelAsync();try{await _worker;}finally{await _pipe.DisposeAsync();_stop.Dispose();}}
}
