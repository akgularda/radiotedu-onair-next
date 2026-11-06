using System.Diagnostics;

namespace OnAirNext.Engine;

internal interface IFrameDecoder : IAsyncDisposable
{
    Task Ready { get; }
    ValueTask<PcmBlock?> ReadAsync(CancellationToken token);
}

public sealed record DecoderPreparationSnapshot(int Owned, int Active, int Retiring, string? CleanupError,
    double MaximumOpenMilliseconds, double MaximumReleaseMilliseconds);

// The programme clock owns selections; this pool only owns codec lifetimes. File
// opens, process creation and retirement must never run on the publishing loop.
// Four physical decoders preserve the existing current + three warm-source bound.
internal sealed class DecoderPreparation(Func<MediaItem, IFrameDecoder> factory) : IAsyncDisposable
{
    private readonly Func<MediaItem, IFrameDecoder> _factory = factory;
    private readonly SemaphoreSlim _slots = new(4, 4);
    private readonly object _gate = new();
    private readonly HashSet<Source> _owned = [];
    private bool _stopping;
    private int _active;
    private double _openMilliseconds, _releaseMilliseconds;

    internal sealed class Source : IFrameDecoder
    {
        private readonly DecoderPreparation _owner;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task<IFrameDecoder> _creation;
        private readonly object _gate = new();
        private Task? _release;
        private bool _released;
        internal bool Retiring { get { lock (_gate) return _release is not null; } }
        internal string? CleanupError { get { lock (_gate) return _release?.Exception?.GetBaseException().GetType().Name; } }
        public Task Ready { get; }

        internal Source(DecoderPreparation owner, MediaItem item)
        {
            _owner = owner;
            _creation = OpenAsync(item);
            Ready = WaitReadyAsync();
            // A withdrawn source may never be read; retain failures for readers
            // without leaving an unobserved readiness exception behind.
            _ = Ready.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        private async Task<IFrameDecoder> OpenAsync(MediaItem item)
        {
            await _owner._slots.WaitAsync(_stop.Token);
            Interlocked.Increment(ref _owner._active);
            try
            {
                // LongRunning isolates potentially blocking OS/file operations
                // from the pool that services the independent station clocks.
                return await Task.Factory.StartNew(() =>
                {
                    _stop.Token.ThrowIfCancellationRequested();
                    var clock = Stopwatch.StartNew();
                    try { return _owner._factory(item); }
                    finally { lock (_owner._gate) _owner._openMilliseconds = Math.Max(_owner._openMilliseconds, clock.Elapsed.TotalMilliseconds); }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            catch { ReleaseSlot(); throw; }
        }
        private async Task WaitReadyAsync() => await (await _creation).Ready;
        public async ValueTask<PcmBlock?> ReadAsync(CancellationToken token)
            => await (await _creation.WaitAsync(token)).ReadAsync(token);
        private void ReleaseSlot()
        { Interlocked.Decrement(ref _owner._active); _owner._slots.Release(); }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                if (_released) return ValueTask.CompletedTask;
                // A failed release keeps its source/slot owned and can be retried.
                if (_release is null || _release.IsFaulted)
                    _release = Task.Factory.StartNew(ReleaseAsync, CancellationToken.None,
                        TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
                return new(_release);
            }
        }
        private async Task ReleaseAsync()
        {
            var clock = Stopwatch.StartNew();
            await _stop.CancelAsync();
            IFrameDecoder? decoder = null;
            try { decoder = await _creation; }
            catch { /* Failed/cancelled construction already returned its slot. */ }
            if (decoder is not null)
            {
                await decoder.DisposeAsync();
                ReleaseSlot();
            }
            _stop.Dispose();
            lock (_gate) _released = true;
            lock (_owner._gate)
            {
                _owner._releaseMilliseconds = Math.Max(_owner._releaseMilliseconds, clock.Elapsed.TotalMilliseconds);
                _owner._owned.Remove(this);
            }
        }
    }

    public Source Start(MediaItem item)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            // Also bound pending/cancelled requests if the OS cannot retire a
            // worker. Never accumulate unlimited processes or preparation jobs.
            if (_owned.Count >= 16) throw new IOException("Previous decoder workers have not released their bounded preparation slots.");
            var source = new Source(this, item); _owned.Add(source); return source;
        }
    }
    public void Retire(Source source)
    {
        var release = source.DisposeAsync().AsTask();
        _ = release.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    public DecoderPreparationSnapshot Snapshot()
    {
        Source[] sources; double open, release;
        lock (_gate) { sources = _owned.ToArray(); open = _openMilliseconds; release = _releaseMilliseconds; }
        return new(sources.Length, Volatile.Read(ref _active), sources.Count(s => s.Retiring),
            sources.Select(s => s.CleanupError).FirstOrDefault(e => e is not null), open, release);
    }
    public async ValueTask DisposeAsync()
    {
        Source[] sources;
        lock (_gate) { _stopping = true; sources = _owned.ToArray(); }
        await Task.WhenAll(sources.Select(s => s.DisposeAsync().AsTask()));
    }
}
