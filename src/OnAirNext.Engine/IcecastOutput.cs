using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace OnAirNext.Engine;

// Exactly one supervisor owns the encoder, transport and reconnect policy for this output.
public sealed class IcecastOutput : IProgramOutput
{
    private readonly string _ffmpeg;
    private readonly OutputSettings _settings;
    private readonly Channel<PcmBlock> _queue;
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _session;
    private Task? _run;
    private volatile string _state = "Stopped";
    private string? _lastError;
    private long _starts, _connections, _written, _drops;
    private int _pid;

    public IcecastOutput(string ffmpeg, OutputSettings settings)
    {
        settings.Validate();
        _ffmpeg = ffmpeg; _settings = settings;
        _queue = Channel.CreateBounded<PcmBlock>(new BoundedChannelOptions(settings.QueueBlocks)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    }

    public void Start()
    {
        if (_run is not null) throw new InvalidOperationException("Output supervisor already started.");
        _run = RunAsync();
    }

    public bool Publish(PcmBlock block)
    {
        if (_stop.IsCancellationRequested) return false;
        block.AddReference();
        if (_queue.Writer.TryWrite(block)) return true;
        block.Dispose();
        Interlocked.Increment(ref _drops);
        // No common producer waits for this output. A full queue invalidates its stale session.
        try { Volatile.Read(ref _session)?.Cancel(); } catch (ObjectDisposedException) { }
        return false;
    }

    public OutputSnapshot Snapshot() => new(_settings.Id, _state, Interlocked.Read(ref _starts),
        Interlocked.Read(ref _connections), Interlocked.Read(ref _written), Interlocked.Read(ref _drops),
        _queue.Reader.Count, Volatile.Read(ref _pid) is var pid && pid != 0 ? pid : null, _lastError);

    private async Task RunAsync()
    {
        var failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            DrainStale();
            using var session = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            Volatile.Write(ref _session, session);
            Process? encoder = null;
            Task? feed = null, drain = null;
            using var client = new TcpClient { NoDelay = true, SendBufferSize = 8192 };
            using var pcm = new CodecPipe(System.IO.Pipes.PipeDirection.Out);
            using var encoded = new CodecPipe(System.IO.Pipes.PipeDirection.In);
            try
            {
                _state = "Connecting";
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(session.Token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(3));
                    await client.ConnectAsync(_settings.Host, _settings.Port, deadline.Token);
                    await HandshakeAsync(client.GetStream(), deadline.Token);
                }
                Interlocked.Increment(ref _connections);
                encoder = StartEncoder(pcm.Path, encoded.Path);
                Volatile.Write(ref _pid, encoder.Id);
                Interlocked.Increment(ref _starts);
                var errors = ChildProcess.DrainErrorAsync(encoder);
                _state = "SourceConnected";
                _lastError = null;
                feed = FeedEncoderAsync(pcm, session.Token);
                drain = SendEncodedAsync(encoded, client.GetStream(), session.Token);
                var exit = encoder.WaitForExitAsync(session.Token);
                await await Task.WhenAny(feed, drain, exit);
                if (!session.IsCancellationRequested) throw new IOException("Owned encoder or source transport ended.");
                _ = errors; // Output is drained to prevent a blocked stderr pipe; never expose source credentials.
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _lastError = ex is OperationCanceledException ? "Output stalled or its bounded queue overflowed." : ex.Message;
                failures = Math.Min(failures + 1, 5);
            }
            finally
            {
                Volatile.Write(ref _pid, 0);
                await session.CancelAsync();
                client.Close();
                if (encoder is not null) await ChildProcess.StopAsync(encoder);
                foreach (var task in new[] { feed, drain })
                    if (task is not null) { try { await task; } catch (Exception) { /* supervised session failure */ } }
                Volatile.Write(ref _session, null);
            }
            if (_stop.IsCancellationRequested) break;
            _state = "Reconnecting";
            try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(3000, 200 * (1 << failures))), _stop.Token); }
            catch (OperationCanceledException) { break; }
        }
        DrainStale();
        _state = "Stopped";
    }

    private async Task HandshakeAsync(NetworkStream stream, CancellationToken token)
    {
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes("source:" + _settings.SourcePassword));
        var contentType = _settings.Profile == CodecProfile.OggFlac ? "application/ogg" : "audio/aac";
        // Icecast waits for source bytes before its final 200 response. Negotiate the
        // interim 100 response so header acceptance cannot deadlock encoder startup.
        var request = $"PUT {_settings.Mount} HTTP/1.1\r\nHost: localhost:{_settings.Port}\r\nAuthorization: Basic {auth}\r\nContent-Type: {contentType}\r\nExpect: 100-continue\r\nIce-Name: OnAir Next isolated test\r\nIce-Public: 0\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), token);
        var header = new List<byte>(512);
        var one = new byte[1];
        while (header.Count < 8192)
        {
            if (await stream.ReadAsync(one, token) == 0) throw new IOException("Icecast closed the source handshake.");
            header.Add(one[0]);
            if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
        }
        var status = Encoding.ASCII.GetString(header.ToArray()).Split('\n')[0].Trim();
        if (!(status.StartsWith("HTTP/1.0 200", StringComparison.Ordinal) || status.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) || status.StartsWith("HTTP/1.1 100", StringComparison.Ordinal)))
            throw new IOException("Icecast source rejected: " + status);
    }

    private Process StartEncoder(string inputPath, string outputPath)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "s16le", "-ar", "48000", "-ac", "2", "-i", inputPath, "-threads", "1" };
        args.AddRange(_settings.Profile switch
        {
            CodecProfile.AacLc192 => ["-c:a", "aac", "-b:a", "192k", "-f", "adts"],
            CodecProfile.OggFlac => ["-c:a", "flac", "-compression_level", "3", "-f", "ogg", "-page_duration", "20000"],
            CodecProfile.HeAacV2_64 => ["-c:a", "libfdk_aac", "-profile:a", "aac_he_v2", "-b:a", "64k", "-f", "adts"],
            _ => throw new ArgumentOutOfRangeException()
        });
        args.AddRange(["-flush_packets", "1", outputPath]);
        return ChildProcess.Start(_ffmpeg, args);
    }

    private async Task FeedEncoderAsync(CodecPipe pcm, CancellationToken token)
    {
        await pcm.ConnectAsync(token);
        await foreach (var block in _queue.Reader.ReadAllAsync(token))
        {
            using (block)
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(_settings.WriteTimeoutMs);
                await pcm.Stream.WriteAsync(block.Memory, deadline.Token);
                Interlocked.Increment(ref _written);
            }
        }
    }

    private async Task SendEncodedAsync(CodecPipe encoded, NetworkStream stream, CancellationToken token)
    {
        await encoded.ConnectAsync(token);
        var buffer = new byte[16384];
        while (true)
        {
            var length = await encoded.Stream.ReadAsync(buffer, token);
            if (length == 0) return;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(_settings.WriteTimeoutMs);
            await stream.WriteAsync(buffer.AsMemory(0, length), deadline.Token);
        }
    }

    private void DrainStale()
    {
        while (_queue.Reader.TryRead(out var block)) { block.Dispose(); Interlocked.Increment(ref _drops); }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _queue.Writer.TryComplete();
        if (_run is not null) await _run;
        else DrainStale();
        _stop.Dispose();
    }
}
