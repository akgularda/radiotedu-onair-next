using System.Diagnostics;

namespace OnAirNext.Engine;

// Desired state is committed before acknowledgement. Each station reconciles independently;
// a slow start/cleanup never holds a sibling's lifecycle or the status/command boundary.
public sealed class StationCoordinator : IAsyncDisposable
{
    private sealed class Slot(StationSettings settings)
    {
        public StationSettings Settings { get; } = settings;
        public volatile DesiredStationState Desired = new(settings.Id, false, false);
        public volatile IStationSession? Session;
        public volatile StationSnapshot? Last;
        public volatile string? Error;
        public volatile RetryState Retry = new(null, 0);
        public volatile bool ReleasePending, Completed;
        public int ContentAvailable;
        public SemaphoreSlim Wake { get; } = new(0, 1);
    }
    private sealed record RetryState(DateTimeOffset? At, int Failures, long DueTimestamp = 0)
    {
        public TimeSpan Remaining => DueTimestamp == 0 ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Math.Max(0, (DueTimestamp - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency));
    }
    private readonly Dictionary<string, Slot> _slots;
    private readonly EventJournal _journal;
    private readonly Func<StationSettings, IStationSession> _factory;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private Task[]? _workers;
    private readonly object _lifetime = new();
    private Task? _disposal;
    private volatile bool _disposed;

    public StationCoordinator(string ffmpeg, IEnumerable<StationSettings> settings, EventJournal journal,
        Func<StationSettings, IStationSession>? factory = null)
    {
        var stations = settings.ToArray();
        if (stations.Length == 0 || stations.Any(x => string.IsNullOrWhiteSpace(x.Id)) ||
            stations.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != stations.Length)
            throw new ArgumentException("Configure distinct stations.");
        var outputs = stations.SelectMany(x => x.Outputs).ToArray();
        foreach (var output in outputs) output.Validate();
        if (outputs.Select(x => (x.Host, x.Port, x.Mount)).Distinct().Count() != outputs.Length)
            throw new ArgumentException("Each destination must have exactly one source owner.");
        _slots = stations.ToDictionary(x => x.Id, x => new Slot(x), StringComparer.Ordinal);
        _journal = journal;
        _factory = factory ?? (station => new StationEngine(ffmpeg, station, journal));
    }

    public async Task InitializeAsync()
    {
        await _commands.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_workers is not null) throw new InvalidOperationException("Coordinator already initialized.");
            foreach (var slot in _slots.Values) slot.Desired = await _journal.InitializeDesiredStateAsync(slot.Settings);
            _workers = _slots.Values.Select(slot => Task.Run(() => RunStationAsync(slot))).ToArray();
        }
        finally { _commands.Release(); }
    }

    public IReadOnlyList<StationSnapshot> Snapshot() => _slots.Values.Select(slot =>
    {
        var desired = slot.Desired;
        var view = slot.Session?.Snapshot() ?? slot.Last ?? new StationSnapshot(slot.Settings.Id, "Stopped", 0, 0, 0,
            null, 0, 0, slot.Settings.Outputs.Select(x => new OutputSnapshot(x.Id, "Stopped", 0, 0, 0, 0, 0, null, null)).ToArray());
        var state = slot.ReleasePending ? "Releasing" : slot.Session is not null ?
            !desired.RequestedRunning ? "Stopping" : view.State : slot.Completed ? "Completed"
            : desired.Enabled && desired.RequestedRunning ? slot.Error is null ? "Starting" : "Retrying" : "Stopped";
        return view with { State = state, Enabled = desired.Enabled, RequestedRunning = desired.RequestedRunning,
            LastError = slot.Error, RetryAtUtc = slot.Retry.At };
    }).ToArray();

    public async Task<bool> RequestRunningAsync(string station, bool running, string commandId)
    {
        await _commands.WaitAsync();
        try
        {
            var slot = Get(station);
            var admitted = await _journal.RequestRunningIntentAsync(station, running, commandId);
            if (admitted)
            {
                slot.Desired = slot.Desired with { RequestedRunning = running };
                if (running) { slot.Completed = false; slot.Retry = new(null, slot.Retry.Failures); }
                Signal(slot);
            }
            return admitted;
        }
        finally { _commands.Release(); }
    }

    public async Task<bool> StopAllAsync(string commandId)
    {
        await _commands.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var admitted = await _journal.RequestStopAllAsync(_slots.Keys, commandId);
            if (admitted)
                foreach (var slot in _slots.Values) { slot.Desired = slot.Desired with { RequestedRunning = false }; Signal(slot); }
            return admitted;
        }
        finally { _commands.Release(); }
    }

    public async Task SkipAsync(string station, string commandId)
    {
        await _commands.WaitAsync();
        try
        {
            var slot = Get(station);
            if (await _journal.HasObservedSkipReceiptAsync(station, commandId)) return;
            var session = slot.Session;
            if (!slot.Desired.RequestedRunning || slot.ReleasePending || session?.Snapshot().State != "Running")
                throw new InvalidOperationException("The selected station is not running.");
            await session.SkipAsync(commandId);
        }
        finally { _commands.Release(); }
    }

    public void RefreshLibrary(string station, bool available)
    {
        if (_disposed || !_slots.TryGetValue(station, out var slot)) return;
        slot.Session?.RefreshNext();
        if (available) Interlocked.Exchange(ref slot.ContentAvailable, 1);
        Signal(slot);
    }

    private Slot Get(string station)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_workers is null) throw new InvalidOperationException("Coordinator is not initialized.");
        return _slots.TryGetValue(station, out var slot) ? slot : throw new ArgumentException("Unknown station.");
    }
    private static void Signal(Slot slot) { try { slot.Wake.Release(); } catch (SemaphoreFullException) { } }
    private static bool ShouldRun(Slot slot) => slot.Desired.Enabled && slot.Desired.RequestedRunning;
    private static void Failure(Slot slot, Exception error)
    {
        var attempts = Math.Min(slot.Retry.Failures + 1, 7);
        slot.Error = error.GetType().Name + ": " + error.Message;
        var delay = TimeSpan.FromMilliseconds(Math.Min(30000, 250 * (1 << attempts)));
        slot.Retry = new(DateTimeOffset.UtcNow.Add(delay), attempts,
            Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency));
    }

    private async Task RunStationAsync(Slot slot)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (Interlocked.Exchange(ref slot.ContentAvailable, 0) == 1) slot.Completed = false;
                if (slot.ReleasePending || !ShouldRun(slot) && slot.Session is not null)
                {
                    slot.ReleasePending = true;
                    try
                    {
                        var session = slot.Session;
                        if (session is not null) { await session.DisposeAsync(); slot.Last = session.Snapshot(); }
                        slot.Session = null; slot.ReleasePending = false;
                    }
                    catch (Exception ex)
                    {
                        Failure(slot, ex);
                        var remaining = slot.Retry.Remaining;
                        await slot.Wake.WaitAsync(remaining > TimeSpan.FromMilliseconds(20)
                            ? remaining : TimeSpan.FromMilliseconds(20), _stop.Token);
                    }
                    continue;
                }
                if (!ShouldRun(slot) || slot.Completed)
                { await slot.Wake.WaitAsync(_stop.Token); continue; }
                if (slot.Session is null)
                {
                    if (slot.Retry.Remaining is { } remaining && remaining > TimeSpan.Zero)
                    { await slot.Wake.WaitAsync(remaining, _stop.Token); continue; }
                    try
                    {
                        slot.Session = _factory(slot.Settings);
                        await slot.Session.StartAsync();
                        slot.Error = null;
                        slot.Retry = new(null, slot.Retry.Failures);
                    }
                    catch (Exception ex) { Failure(slot, ex); slot.ReleasePending = slot.Session is not null; }
                    continue;
                }
                if (slot.Session.Completion.IsCompleted)
                {
                    try { await slot.Session.Completion; slot.Completed = true; }
                    catch (Exception ex) { Failure(slot, ex); }
                    slot.ReleasePending = true;
                    continue;
                }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var signal = slot.Wake.WaitAsync(wait.Token);
                await Task.WhenAny(signal, slot.Session.Completion);
                await wait.CancelAsync();
                try { await signal; } catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            if (slot.Session is not null) await slot.Session.DisposeAsync();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetime) return new(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await _commands.WaitAsync();
        try { _disposed = true; await _stop.CancelAsync(); }
        finally { _commands.Release(); }
        try { if (_workers is not null) await Task.WhenAll(_workers); }
        finally { _stop.Dispose(); }
    }
}
