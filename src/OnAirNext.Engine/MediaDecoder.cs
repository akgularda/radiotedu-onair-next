using System.Diagnostics;
using System.Threading.Channels;

namespace OnAirNext.Engine;

// Two seconds of predecoded audio at most, regardless of file length.
public sealed class MediaDecoder : IAsyncDisposable
{
    private readonly Channel<PcmBlock> _blocks = Channel.CreateBounded<PcmBlock>(new BoundedChannelOptions(100)
        { SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop = new();
    private readonly Process _process;
    private readonly CodecPipe _pcm = new(System.IO.Pipes.PipeDirection.In);
    private readonly Task _pump;
    private readonly Task<string> _stderr;
    public Task Ready { get; }
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MediaDecoder(string ffmpeg, MediaItem item, long startSample = 0)
    {
        _process = ChildProcess.Start(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", item.Path,
            "-vn", "-sn", "-dn", "-threads", "1", "-af", "aresample=48000,atrim=start_sample=" + startSample.ToString(System.Globalization.CultureInfo.InvariantCulture), "-f", "s16le", "-acodec", "pcm_s16le", "-ar", "48000", "-ac", "2", _pcm.Path]);
        _stderr = ChildProcess.DrainErrorAsync(_process);
        Ready = _ready.Task;
        _pump = PumpAsync();
    }

    private async Task PumpAsync()
    {
        Exception? failure = null;
        try
        {
            var connected = _pcm.ConnectAsync(_stop.Token);
            var exited = _process.WaitForExitAsync(_stop.Token);
            if (await Task.WhenAny(connected, exited) == exited && !connected.IsCompleted)
                throw new InvalidDataException("Media decoder exited before opening its PCM pipe: " + await _stderr);
            await connected;
            while (!_stop.IsCancellationRequested)
            {
                var block = PcmBlock.Rent();
                var transferred = false;
                try
                {
                    var count = 0;
                    while (count < AudioFormat.BlockBytes)
                    {
                        var read = await _pcm.Stream.ReadAsync(block.Memory[count..], _stop.Token);
                        if (read == 0) break;
                        count += read;
                    }
                    if (count == 0) break;
                    if (count % AudioFormat.BytesPerSampleFrame != 0) throw new InvalidDataException("Unaligned decoded PCM.");
                    block.Length = count;
                    await _blocks.Writer.WriteAsync(block, _stop.Token);
                    transferred = true;
                    _ready.TrySetResult();
                }
                finally { if (!transferred) block.Dispose(); }
            }
            await _process.WaitForExitAsync(_stop.Token);
            var error = await _stderr;
            if (_process.ExitCode != 0) throw new InvalidDataException($"Media decode failed ({_process.ExitCode}): {error[..Math.Min(300, error.Length)]}");
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            _blocks.Writer.TryComplete(failure);
            // Readiness means the preparation attempt completed, not that the asset is valid.
            _ready.TrySetResult();
        }
    }

    public async ValueTask<PcmBlock?> ReadAsync(CancellationToken token)
    {
        if (await _blocks.Reader.WaitToReadAsync(token)) return await _blocks.Reader.ReadAsync(token);
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await ChildProcess.StopAsync(_process);
        try { await _pump; } catch (OperationCanceledException) { }
        while (_blocks.Reader.TryRead(out var block)) block.Dispose();
        _pcm.Dispose();
        _stop.Dispose();
    }
}
