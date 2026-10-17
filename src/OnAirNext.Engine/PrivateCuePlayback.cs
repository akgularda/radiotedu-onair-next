using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OnAirNext.Engine;

// No programme engine, event writer, recording sink or output transport is reachable from this lane.
public sealed class PrivateCuePlayback(StationLibrary library,string ffmpeg)
{
    private readonly PrivateCueGate _gate=new();
    public PrivateCueSnapshot Snapshot()=>_gate.Snapshot();
    public void RequestStop(string session)=>_gate.RequestStop(session);
    public async Task<PrivateCueResult> PlayAsync(PrivateCueRequest request,Func<PrivateCueHeader,CancellationToken,Task> confirm,
        Func<ReadOnlyMemory<byte>,CancellationToken,Task> send,CancellationToken ownerStop)
    {
        var lease=_gate.Acquire(request);PrivateCueSource? source=null;MediaDecoder? decoder=null;long sent=0;
        try
        {
            source=library.OpenPrivateCue(request);
            using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(ownerStop,lease.Token);lifetime.CancelAfter(TimeSpan.FromDays(7));
            using var idle=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);idle.CancelAfter(TimeSpan.FromSeconds(5));
            decoder=new(ffmpeg,source.Item,request.StartSample,role:"private-cue",fastSeek:true);decoder.PreferBackgroundScheduling();
            await decoder.Ready.WaitAsync(idle.Token);
            await confirm(new(true,request.SessionId,request.StartSample,source.TotalSamples),idle.Token);
            var limiter=new TruePeakLimiter48k();var output=new byte[AudioFormat.BlockBytes];var clock=Stopwatch.StartNew();
            while(true)
            {
                idle.CancelAfter(TimeSpan.FromSeconds(5));using var block=await decoder.ReadAsync(idle.Token);if(block is null)break;
                var frames=Limit(limiter,block.Memory.Span,output);if(frames>0)await SendAsync(frames);
            }
            limiter.Finish();int tail;while((tail=Flush(limiter,output))>0)await SendAsync(tail);
            if(sent==0)throw new InvalidDataException("The cue item produced no audio.");
            async Task SendAsync(int frames)
            {
                idle.CancelAfter(TimeSpan.FromSeconds(5));await send(output.AsMemory(0,frames*AudioFormat.BytesPerSampleFrame),idle.Token);
                sent+=frames;var delay=sent/48000d-clock.Elapsed.TotalSeconds-.25;
                if(delay>0)await Task.Delay(TimeSpan.FromSeconds(delay),lifetime.Token);
            }
        }
        finally
        {
            if(decoder is not null)
            {
                while(true)
                {
                    try{await decoder.DisposeAsync();break;}
                    catch(Exception cleanup)when(cleanup is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
                    {
                        _gate.CleanupFailed(lease);
                        if(ownerStop.IsCancellationRequested)throw;
                        await Task.Delay(TimeSpan.FromSeconds(2),ownerStop);
                    }
                }
            }
            source?.Dispose();_gate.Complete(lease);
        }
        return new(request.SessionId,true,sent);
    }
    private static int Limit(TruePeakLimiter48k limiter,ReadOnlySpan<byte> input,Span<byte> output)=>limiter.Process(MemoryMarshal.Cast<byte,float>(input),MemoryMarshal.Cast<byte,float>(output));
    private static int Flush(TruePeakLimiter48k limiter,Span<byte> output)=>limiter.Flush(MemoryMarshal.Cast<byte,float>(output));
}
