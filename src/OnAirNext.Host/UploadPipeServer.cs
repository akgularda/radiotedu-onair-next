using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using OnAirNext.Engine;
using OnAirNext.ServiceControl;
using OnAirNext.UploadProtocol;

namespace OnAirNext.Host;

// A separate single-client lane prevents file bytes from occupying station controls.
internal sealed class UploadPipeServer : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly ServicePipePolicy? _policy;
    private readonly UploadInbox _inbox;
    private readonly CancellationTokenSource _stop;
    private readonly Task _worker;
    private string? _error;
    public string? Error => Volatile.Read(ref _error);
    public UploadPipeServer(string control, bool serviceMode, ServicePipePolicy? policy, UploadInbox inbox, CancellationToken token)
    {
        var name=ServiceEndpoint.Files(control);_policy=policy;_inbox=inbox;
        _stop=CancellationTokenSource.CreateLinkedTokenSource(token);
        _pipe=serviceMode && OperatingSystem.IsWindows() ? policy!.CreateServer(name)
            : new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly|PipeOptions.FirstPipeInstance,65536,65536);
        _worker=RunAsync();
    }
    private async Task RunAsync()
    {
        while(!_stop.IsCancellationRequested)
        {
            var connected=false;
            try
            {
                await _pipe.WaitForConnectionAsync(_stop.Token);connected=true;
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromMinutes(2));
                var frame=await PipeFrames.ReadAsync(_pipe,131072,deadline.Token);
                if(frame is null)continue;
                if(_policy is not null && OperatingSystem.IsWindows())_policy.Authorize(_pipe);
                try
                {
                    var request=JsonSerializer.Deserialize<UploadRequest>(frame)??throw new ArgumentException("Missing upload request.");
                    if(request.StationId is null || request.StationId.Length is <1 or >100 || !Guid.TryParse(request.BatchId,out _))
                        throw new ArgumentException("Choose a station and upload ID.");
                    switch(request.Action)
                    {
                        case "begin":
                            var receipt=await _inbox.BeginAsync(request.StationId,request.BatchId,request.Files??[]);
                            await ReplyAsync(new(true,State:receipt.State),deadline.Token);break;
                        case "put":
                            var retained=await _inbox.HasFileAsync(request.StationId,request.BatchId,request.Index,deadline.Token);
                            await ReplyAsync(new(true,Send:!retained),deadline.Token);
                            if(!retained)
                            {
                                await _inbox.ReceiveAsync(request.StationId,request.BatchId,request.Index,_pipe,deadline.Token);
                                await ReplyAsync(new(true),deadline.Token);
                            }
                            break;
                        case "commit":
                            await _inbox.CommitAsync(request.StationId,request.BatchId,deadline.Token);
                            await ReplyAsync(new(true),deadline.Token);break;
                        case "cancel":
                            await _inbox.CancelAsync(request.StationId,request.BatchId);
                            await ReplyAsync(new(true),deadline.Token);break;
                        default: throw new ArgumentException("Unknown upload command.");
                    }
                    Volatile.Write(ref _error,null);
                }
                catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or Microsoft.Data.Sqlite.SqliteException)
                {
                    Volatile.Write(ref _error,"A transfer could not finish. Retry or cancel it in Library.");
                    await ReplyAsync(new(false,Error:ex is ArgumentException or InvalidOperationException ? ex.Message : "The file transfer failed. Retry or cancel it in Library."),deadline.Token);
                }
                // Retain the server instance until the acknowledgement is consumed.
                if(await _pipe.ReadAsync(new byte[1],deadline.Token)!=0)throw new IOException("Unexpected trailing upload data.");
            }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){break;}
            catch(Exception ex) when(ex is IOException or OperationCanceledException or UnauthorizedAccessException or JsonException)
            {Volatile.Write(ref _error,"The transfer connection closed. Retained files can be retried or cancelled in Library.");}
            catch(Exception)
            {
                // Upload failure must never stop healthy station playback.
                Volatile.Write(ref _error,"File transfer is unavailable. Station playback is independent; inspect System before retrying.");
                try{await Task.Delay(TimeSpan.FromSeconds(2),_stop.Token);}catch(OperationCanceledException){break;}
            }
            finally
            {
                if(connected)try{_pipe.Disconnect();}catch(IOException){}
            }
        }
    }
    private Task ReplyAsync(UploadReply reply,CancellationToken token)=>_pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply)+"\n"),token).AsTask();
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try{await _worker;}finally{await _pipe.DisposeAsync();_stop.Dispose();}
    }
}
