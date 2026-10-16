using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace OnAirNext.Engine;

public sealed record ProgrammeLoudnessWindow(string Id, string StationId, string SessionId, long StartSample,
    long EndSample, DateTimeOffset StartedAtUtc, string Phase, string Reason, long MaximumFrames,
    bool HasMeasurementGap, long GapBeforeFrames, bool MissingTail, LoudnessReading? Reading)
{
    public DateTimeOffset KnownEndAtUtc => StartedAtUtc.AddSeconds((EndSample - StartSample) / 48000d);
    public string Scope => "pre_encoder_final_mix";
}

public sealed record ProgrammeLoudnessSnapshot(string State, DateTimeOffset UpdatedAtUtc,
    ProgrammeLoudnessWindow? Current, ProgrammeLoudnessWindow? LastClosed,
    long DroppedBlocks, long DroppedFrames, long InvalidBlocks, long Gaps,
    long HistoryFailures, string? Error, string? HistoryError, int QueuedBlocks = 0, int PendingHistoryWrites = 0)
{
    public bool Continuous => State != "Faulted" && DroppedBlocks == 0 && InvalidBlocks == 0 && Gaps == 0;
}

/// <summary>
/// One programme producer, one measurement worker. Publish never waits for measurement, disk or history.
/// PCM ownership is retained only within a bounded queue. Lost coverage is explicit, never reconstructed.
/// </summary>
public sealed class ProgrammeLoudnessMonitor : IAsyncDisposable
{
    private readonly Channel<PcmBlock> _queue;
    private readonly CancellationTokenSource _abort = new();
    private readonly string _station, _session;
    private readonly DateTimeOffset _anchor;
    private readonly TimeSpan _windowDuration;
    private readonly long _maximumFrames;
    private readonly Func<ProgrammeLoudnessWindow, Task>? _history;
    private readonly Task _worker;
    private readonly Channel<ProgrammeLoudnessWindow> _historyQueue;
    private readonly Task _historyWorker, _completion;
    private int _pendingHistoryWrites;
    private StereoLoudnessMeter48k? _meter;
    private ProgrammeLoudnessWindow? _window, _lastClosed;
    private ProgrammeLoudnessSnapshot _published = new("Waiting", DateTimeOffset.UtcNow, null, null, 0, 0, 0, 0, 0, null, null);
    private long _frames, _expectedEnd, _lastSourceEnd, _nextLevels, _nextStatistics, _nextCheckpoint;
    private long _drops, _droppedFrames, _invalid, _gaps, _historyFailures;
    private long _gapBefore;
    private string? _error, _historyError;
    private int _accepting = 1, _disposed;

    public ProgrammeLoudnessMonitor(string station, string session, DateTimeOffset sampleClockAnchor,
        long initialSample = 0, TimeSpan? maximumWindow = null, int queueBlocks = 100,
        Func<ProgrammeLoudnessWindow, Task>? history = null)
    {
        if (string.IsNullOrWhiteSpace(station) || station.Length > 100 || string.IsNullOrWhiteSpace(session) || session.Length > 100)
            throw new ArgumentException("Measurement requires bounded station and session identities.");
        _windowDuration = maximumWindow ?? TimeSpan.FromHours(1);
        if (_windowDuration < TimeSpan.FromSeconds(3) || _windowDuration > TimeSpan.FromHours(24) || initialSample < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumWindow));
        if (queueBlocks is < 2 or > 500) throw new ArgumentOutOfRangeException(nameof(queueBlocks));
        _station = station; _session = session; _anchor = sampleClockAnchor; _history = history;
        _maximumFrames = checked((long)(_windowDuration.TotalSeconds * AudioFormat.SampleRate));
        _expectedEnd = _lastSourceEnd = initialSample;
        _queue = Channel.CreateBounded<PcmBlock>(new BoundedChannelOptions(queueBlocks)
            { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        _historyQueue = Channel.CreateBounded<ProgrammeLoudnessWindow>(new BoundedChannelOptions(64)
            { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        _historyWorker = Task.Run(RunHistoryAsync);
        _worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _completion = Task.WhenAll(_worker, _historyWorker);
    }

    public Task Completion => _completion;
    public ProgrammeLoudnessSnapshot Snapshot()
    {
        var view = Volatile.Read(ref _published);
        return view with { DroppedBlocks = Interlocked.Read(ref _drops), DroppedFrames = Interlocked.Read(ref _droppedFrames),
            InvalidBlocks = Interlocked.Read(ref _invalid), Gaps = Interlocked.Read(ref _gaps),
            HistoryFailures = Interlocked.Read(ref _historyFailures), QueuedBlocks = _queue.Reader.Count,
            PendingHistoryWrites = Volatile.Read(ref _pendingHistoryWrites) };
    }

    public bool Publish(PcmBlock block)
    {
        if (Volatile.Read(ref _accepting) == 0) return false;
        if (block.Length is <= 0 or > AudioFormat.BlockBytes || block.Length % AudioFormat.BytesPerSampleFrame != 0 ||
            block.SamplePosition < 0 || block.SamplePosition > long.MaxValue - AudioFormat.SamplesPerBlock)
        { Interlocked.Increment(ref _invalid); return false; }
        var frames = block.Length / AudioFormat.BytesPerSampleFrame;
        Volatile.Write(ref _lastSourceEnd, block.SamplePosition + frames);
        block.AddReference();
        if (_queue.Writer.TryWrite(block)) return true;
        block.Dispose(); Interlocked.Increment(ref _drops); Interlocked.Add(ref _droppedFrames, frames);
        return false;
    }

    public void Complete()
    {
        if (Interlocked.Exchange(ref _accepting, 0) == 0) return;
        _queue.Writer.TryComplete();
        _abort.CancelAfter(TimeSpan.FromSeconds(2));
    }

    private void Run()
    {
        try
        {
            while (_queue.Reader.WaitToReadAsync(_abort.Token).AsTask().GetAwaiter().GetResult())
            while (_queue.Reader.TryRead(out var block))
            {
                using (block)
                {
                    if (block.SamplePosition != _expectedEnd)
                    {
                        Interlocked.Increment(ref _gaps);
                        _gapBefore = Math.Max(0, block.SamplePosition - _expectedEnd);
                        CloseWindow("SourceGap", true);
                    }
                    try { ValidateSamples(block); Consume(block); _error = null; }
                    catch (ArgumentException)
                    {
                        Interlocked.Increment(ref _invalid); Interlocked.Increment(ref _gaps);
                        _error = "Invalid programme PCM was excluded from measurement.";
                        CloseWindow("InvalidPcm", true);
                        _gapBefore = block.Length / AudioFormat.BytesPerSampleFrame;
                    }
                    _expectedEnd = block.SamplePosition + block.Length / AudioFormat.BytesPerSampleFrame;
                }
            }
            var missingTail = Volatile.Read(ref _lastSourceEnd) != _expectedEnd;
            if (missingTail) Interlocked.Increment(ref _gaps);
            CloseWindow(missingTail ? "UnmeasuredTail" : "SessionEnded", missingTail);
            PublishView("Stopped");
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
            _error = "Measurement shutdown ended before all queued samples were measured.";
            Interlocked.Increment(ref _gaps);
            if (_window is not null && _meter is not null)
            {
                _meter.Finish();
                _window = _window with { EndSample = _window.StartSample + _frames, Phase = "Interrupted",
                    Reason = "MeasurementShutdownTimeout", HasMeasurementGap = true, MissingTail = true, Reading = _meter.Read() };
                _lastClosed = _window;
            }
            PublishView("Stopped");
        }
        catch (Exception ex)
        {
            _error = "Measurement worker stopped: " + ex.GetType().Name;
            PublishView("Faulted");
        }
        finally
        {
            Interlocked.Exchange(ref _accepting, 0); _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out var block))
            { Interlocked.Increment(ref _drops); Interlocked.Add(ref _droppedFrames, block.Length / AudioFormat.BytesPerSampleFrame); block.Dispose(); }
            _historyQueue.Writer.TryComplete();
        }
    }

    private void Consume(PcmBlock block)
    {
        var remaining = block.Length / AudioFormat.BytesPerSampleFrame; var offset = 0;
        while (remaining > 0)
        {
            _abort.Token.ThrowIfCancellationRequested();
            if (_meter is null)
            {
                _meter = new(_windowDuration); _frames = 0;
                _nextLevels = 4800; _nextStatistics = 48000; _nextCheckpoint = 60L * 48000;
                var firstSample = block.SamplePosition + offset;
                _window = new(Guid.NewGuid().ToString("N"), _station, _session, firstSample, firstSample,
                    _anchor.AddSeconds(firstSample / 48000d), "Started", "ContinuousWindow", _maximumFrames,
                    false, _gapBefore, false, null);
                _gapBefore = 0;
                PublishView("Measuring"); QueueHistory(_window);
            }
            var take = (int)Math.Min(remaining, _maximumFrames - _frames);
            FeedPart(block, offset, take); _frames += take; offset += take; remaining -= take;
            if (_frames == _maximumFrames) { CloseWindow("Duration", false); continue; }
            if (_frames >= _nextLevels)
            {
                var reading = _frames >= _nextStatistics ? _meter.Read() : _meter.ReadLevels();
                if (_frames >= _nextStatistics) _nextStatistics = (_frames / 48000 + 1) * 48000;
                _nextLevels = (_frames / 4800 + 1) * 4800;
                _window = _window! with { Phase = "Live", EndSample = _window!.StartSample + _frames, Reading = reading };
                PublishView("Measuring");
            }
            if (_frames >= _nextCheckpoint)
            {
                _nextCheckpoint = (_frames / (60L * 48000) + 1) * 60L * 48000;
                QueueHistory(_window! with { Phase = "Checkpoint", EndSample = _window!.StartSample + _frames, Reading = _meter.Read() });
            }
        }
    }

    private void FeedPart(PcmBlock block, int offsetFrames, int frames)
        => _meter!.Feed(MemoryMarshal.Cast<byte, float>(block.Memory.Span.Slice(
            offsetFrames * AudioFormat.BytesPerSampleFrame, frames * AudioFormat.BytesPerSampleFrame)));

    private static void ValidateSamples(PcmBlock block)
    {
        foreach (var value in MemoryMarshal.Cast<byte, float>(block.Memory.Span))
            if (!float.IsFinite(value)) throw new ArgumentException("Invalid programme PCM.");
    }

    private void CloseWindow(string reason, bool gap)
    {
        if (_meter is null || _window is null) return;
        _meter.Finish();
        _window = _window with { EndSample = _window.StartSample + _frames, Phase = "Finished", Reason = reason,
            HasMeasurementGap = gap, MissingTail = gap, Reading = _meter.Read() };
        _lastClosed = _window; _meter = null;
        PublishView("WindowFinished"); QueueHistory(_window);
    }

    private void QueueHistory(ProgrammeLoudnessWindow window)
    {
        if (_history is null) return;
        Interlocked.Increment(ref _pendingHistoryWrites);
        if (!_historyQueue.Writer.TryWrite(window))
        {
            Interlocked.Decrement(ref _pendingHistoryWrites);
            HistoryFailed();
        }
    }

    private async Task RunHistoryAsync()
    {
        try
        {
            await foreach (var window in _historyQueue.Reader.ReadAllAsync(_abort.Token))
            {
                Task? write = null;
                var ownsDecrement = true;
                try
                {
                    write = _history!(window);
                    try { await write.WaitAsync(TimeSpan.FromSeconds(2), _abort.Token); }
                    catch (TimeoutException)
                    {
                        HistoryFailed();
                        // A slow disk can recover without restarting measurement. Keep exactly
                        // this one write in flight; later immutable records stay in the bounded queue.
                        await write.WaitAsync(_abort.Token);
                    }
                }
                catch (Exception)
                {
                    HistoryFailed();
                    // Keep at most one unconfirmed storage operation. Never build an unbounded set
                    // of timed-out writes while continuing to measure real programme PCM.
                    if (write is { IsCompleted: false })
                    {
                        _ = write.ContinueWith(static (task, state) =>
                        {
                            _ = task.Exception;
                            Interlocked.Decrement(ref ((ProgrammeLoudnessMonitor)state!)._pendingHistoryWrites);
                        }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        ownsDecrement = false;
                        break;
                    }
                }
                finally
                {
                    // A timed-out in-flight task owns its decrement until it actually settles.
                    if (ownsDecrement) Interlocked.Decrement(ref _pendingHistoryWrites);
                }
            }
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested) { }
        finally
        {
            _historyQueue.Writer.TryComplete();
            while (_historyQueue.Reader.TryRead(out _))
            {
                Interlocked.Decrement(ref _pendingHistoryWrites);
                HistoryFailed();
            }
        }
    }
    private void HistoryFailed()
    {
        Interlocked.Increment(ref _historyFailures); _historyError = "Measurement history is incomplete or not yet confirmed.";
        PublishView(_published.State);
    }
    private void PublishView(string state) => Volatile.Write(ref _published, new(state, DateTimeOffset.UtcNow,
        _window, _lastClosed, Interlocked.Read(ref _drops), Interlocked.Read(ref _droppedFrames),
        Interlocked.Read(ref _invalid), Interlocked.Read(ref _gaps), Interlocked.Read(ref _historyFailures), _error, _historyError));

    public async ValueTask DisposeAsync()
    {
        Complete(); await _completion;
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _abort.Dispose();
    }
}
