using System.Diagnostics;

namespace OnAirNext.Engine;

public sealed class StationEngine : IAsyncDisposable
{
    private readonly string _ffmpeg;
    private readonly StationSettings _settings;
    private readonly IReadOnlyList<IProgramOutput> _outputs;
    private readonly EventJournal _journal;
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;
    private MediaDecoder? _decoder, _next;
    private PcmBlock? _decoded;
    private int _decodedOffset, _index, _skip;
    private bool _announced;
    private string _occurrence = Guid.NewGuid().ToString("N");
    private volatile string _state = "Stopped";
    private string? _currentTitle;
    private long _samples, _completed, _skipped, _late, _auditRejected;
    private DateTimeOffset _anchor;
    public LiveInput Live { get; } = new();
    public Task Completion => _run ?? Task.CompletedTask;

    public StationEngine(string ffmpeg, StationSettings settings, EventJournal journal, IReadOnlyList<IProgramOutput>? outputs = null)
    {
        if (string.IsNullOrWhiteSpace(settings.Id)) throw new ArgumentException("Station ID is required.");
        if (settings.Playlist.Count == 0) throw new ArgumentException("An approved nonempty playlist is required.");
        if (settings.Outputs.Select(x => x.Id).Distinct().Count() != settings.Outputs.Count)
            throw new ArgumentException("Output IDs must be unique.");
        if (settings.Outputs.Select(x => (x.Host, x.Port, x.Mount)).Distinct().Count() != settings.Outputs.Count)
            throw new ArgumentException("Duplicate source ownership is prohibited.");
        _ffmpeg = ffmpeg; _settings = settings; _journal = journal;
        _outputs = outputs ?? settings.Outputs.Select(x => (IProgramOutput)new IcecastOutput(ffmpeg, x)).ToArray();
    }

    public async Task StartAsync()
    {
        if (_run is not null) throw new InvalidOperationException("Station already started; construct a new session to resume.");
        await EncoderCapabilities.ValidateAsync(_ffmpeg, _settings.Outputs);
        _state = "Preparing";
        _run = Task.Run(RunAsync);
    }

    public void Skip() => Interlocked.Exchange(ref _skip, 1);
    public StationSnapshot Snapshot() => new(_settings.Id, _state, Interlocked.Read(ref _samples),
        Interlocked.Read(ref _completed), Interlocked.Read(ref _skipped), _currentTitle,
        Interlocked.Read(ref _late), Interlocked.Read(ref _auditRejected), _outputs.Select(x => x.Snapshot()).ToArray());

    private async Task RunAsync()
    {
        Exception? fault = null;
        try
        {
            _decoder = new MediaDecoder(_ffmpeg, _settings.Playlist[0]);
            PrepareNext();
            await _decoder.Ready.WaitAsync(TimeSpan.FromSeconds(15), _stop.Token);
            if (_next is not null) await _next.Ready.WaitAsync(TimeSpan.FromSeconds(15), _stop.Token);
            foreach (var output in _outputs) output.Start();
            var connectDeadline = Stopwatch.StartNew();
            while (_outputs.Count > 0 && !_outputs.Any(x => x.Snapshot().State == "SourceConnected") && connectDeadline.Elapsed < TimeSpan.FromSeconds(1))
                await Task.Delay(10, _stop.Token);
            _anchor = DateTimeOffset.UtcNow;
            var clock = Stopwatch.StartNew();
            _state = "Running";
            while (!_stop.IsCancellationRequested)
            {
                var due = TimeSpan.FromSeconds((double)_samples / AudioFormat.SampleRate);
                var delay = due - clock.Elapsed;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, _stop.Token);
                else if (delay < -AudioFormat.BlockDuration * 2) Interlocked.Increment(ref _late);
                if (Interlocked.Exchange(ref _skip, 0) == 1)
                {
                    if (_announced) { Emit("skipped", _samples); Interlocked.Increment(ref _skipped); }
                    await AdvanceAsync();
                }
                using var frame = PcmBlock.Rent();
                frame.SamplePosition = _samples;
                var filled = await FillAsync(frame.Memory, _stop.Token);
                if (filled == 0) break;
                if (filled < AudioFormat.BlockBytes) frame.Memory.Span[filled..].Clear();
                Live.Mix(frame.Memory.Span);
                foreach (var output in _outputs) output.Publish(frame);
                Interlocked.Add(ref _samples, filled / AudioFormat.BytesPerSampleFrame);
                if (filled < AudioFormat.BlockBytes) break;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            fault = ex;
            if (_announced) Emit("failed", _samples, ex.GetType().Name);
        }
        finally
        {
            if (fault is null && _announced) Emit("interrupted", _samples, "Explicit stop preserves the current item; restart begins a new session.");
            _decoded?.Dispose(); _decoded = null;
            if (_decoder is not null) await _decoder.DisposeAsync();
            if (_next is not null) await _next.DisposeAsync();
            foreach (var output in _outputs) await output.DisposeAsync();
            _state = fault is null ? "Stopped" : "Faulted";
        }
        if (fault is not null) throw new InvalidOperationException("Station engine failed; inspect audit events.", fault);
    }

    private async ValueTask<int> FillAsync(Memory<byte> target, CancellationToken token)
    {
        var offset = 0;
        var failedAssets = 0;
        while (offset < target.Length && _decoder is not null)
        {
            if (_decoded is null)
            {
                try { _decoded = await _decoder.ReadAsync(token); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (_announced)
                    {
                        Emit("failed", _samples + offset / 4, "Decoding stopped during the asset.");
                        _announced = false;
                    }
                    Emit("decode_failed", _samples + offset / 4, "Asset rejected: " + ex.GetType().Name);
                    if (++failedAssets >= _settings.Playlist.Count) throw new InvalidDataException("No approved asset could be decoded.");
                    await AdvanceAsync();
                    continue;
                }
                _decodedOffset = 0;
                if (_decoded is null)
                {
                    if (_announced) { Emit("ended", _samples + offset / 4); Interlocked.Increment(ref _completed); }
                    else if (++failedAssets >= _settings.Playlist.Count) throw new InvalidDataException("Approved playlist contains no audio.");
                    await AdvanceAsync();
                    continue;
                }
                if (!_announced)
                {
                    _announced = true;
                    _currentTitle = _settings.Playlist[_index].Title;
                    Emit("started", _samples + offset / 4);
                }
            }
            var take = Math.Min(target.Length - offset, _decoded.Length - _decodedOffset);
            _decoded.Memory.Slice(_decodedOffset, take).CopyTo(target[offset..]);
            offset += take; _decodedOffset += take;
            if (_decodedOffset == _decoded.Length) { _decoded.Dispose(); _decoded = null; }
        }
        return offset;
    }

    private void PrepareNext()
    {
        var nextIndex = _index + 1;
        if (nextIndex < _settings.Playlist.Count || _settings.Repeat)
            _next = new MediaDecoder(_ffmpeg, _settings.Playlist[nextIndex % _settings.Playlist.Count]);
    }

    private async ValueTask AdvanceAsync()
    {
        _decoded?.Dispose(); _decoded = null;
        if (_decoder is not null) await _decoder.DisposeAsync();
        _decoder = _next; _next = null;
        _announced = false; _currentTitle = null;
        _occurrence = Guid.NewGuid().ToString("N");
        _index = (_index + 1) % _settings.Playlist.Count;
        if (_decoder is not null) PrepareNext();
    }

    private void Emit(string type, long position, string detail = "")
    {
        var item = _settings.Playlist[_index];
        var entry = new PlaybackEvent(Guid.NewGuid().ToString("N"), _occurrence, _settings.Id, type,
            item.Id, item.Artist, item.Title, item.Kind,
            _anchor.AddSeconds((double)position / AudioFormat.SampleRate), position, detail);
        if (!_journal.TryAppend(entry)) Interlocked.Increment(ref _auditRejected);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { if (_run is not null) await _run; }
        finally { Live.Dispose(); _stop.Dispose(); }
    }
}
