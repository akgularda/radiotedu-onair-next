namespace OnAirNext.Engine;

public sealed record SourceAccessResult(bool Accessible, string Identity, string? IdentitySid, string VolumeKind,
    string? VolumeId, int EntriesExamined, bool LimitReached, bool? SampleReadable, string Detail);
public sealed record SourceAccessCheck(string Id, string StationId, string Path, string State,
    DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt, DateTimeOffset? ExpiresAt,
    SourceAccessResult? Result, string? Error);

public sealed class SourceAccessChecks : IAsyncDisposable
{
    private readonly object _gate = new();
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly Dictionary<string, long> _completed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceAccessCheck> _checks = new(StringComparer.Ordinal);
    private readonly Func<string, Task<SourceAccessResult>> _probe;
    private readonly TimeProvider _clock;
    private Task _running = Task.CompletedTask;
    private bool _busy, _disposed;
    public SourceAccessChecks(Func<string, Task<SourceAccessResult>> probe, TimeProvider? clock = null)
    { _probe = probe; _clock = clock ?? TimeProvider.System; }

    public SourceAccessCheck Start(string station, string path, string command)
    {
        if (string.IsNullOrWhiteSpace(station) || station.Length > 100 || !Guid.TryParse(command, out var id))
            throw new ArgumentException("Choose a station and a valid source-check request.");
        path = Normalize(path); var key = id.ToString("N");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_checks.TryGetValue(key, out var previous))
            {
                if (previous.StationId != station || !previous.Path.Equals(path, PathComparison))
                    throw new ArgumentException("This source-check ID belongs to another station or folder.");
                return Snapshot(previous);
            }
            if (_busy) throw new InvalidOperationException("A source access check is running. Wait for it to finish.");
            if (_checks.Count >= 64)
            { var oldest = _checks.Values.MinBy(x => x.RequestedAt)!.Id; _checks.Remove(oldest); _completed.Remove(oldest); }
            var check = new SourceAccessCheck(key, station, path, "Checking", _clock.GetUtcNow(), null, null, null, null);
            _checks.Add(key, check); _busy = true;
            _running = Task.Run(async () =>
            {
                SourceAccessResult? result = null; string? error = null;
                try { result = await _probe(path); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or TimeoutException or OperationCanceledException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
                { error = "The source check did not complete. Check the drive and service permissions, then retry."; }
                catch (Exception failure)
                { error = "The source check failed (" + failure.GetType().Name + "). Run a new check before assigning the folder."; }
                finally
                {
                    lock (_gate)
                    {
                        var completed = _clock.GetUtcNow();
                        _completed[key] = _clock.GetTimestamp();
                        _checks[key] = check with { State = error is not null ? "Failed" : result?.Accessible == true ? "Accessible" : "Unavailable",
                            CompletedAt = completed, ExpiresAt = completed.AddMinutes(1), Result = result, Error = error };
                        _busy = false;
                    }
                }
            });
            return check;
        }
    }
    public SourceAccessCheck Get(string station, string command)
    {
        if (!Guid.TryParse(command, out var id)) throw new ArgumentException("Choose a valid source-check request.");
        lock (_gate)
        {
            if (!_checks.TryGetValue(id.ToString("N"), out var check) || check.StationId != station)
                throw new InvalidOperationException("This source access check is unavailable for the selected station. Run it again.");
            return Snapshot(check);
        }
    }
    public void RequireReady(string station, string path, string? command)
    {
        var check = Get(station, command ?? "");
        if (check.State != "Accessible" || !check.Path.Equals(Normalize(path), PathComparison))
            throw new InvalidOperationException("Check access to this exact station folder again before assigning it.");
    }
    private SourceAccessCheck Snapshot(SourceAccessCheck check) => _completed.TryGetValue(check.Id, out var completed) && _clock.GetElapsedTime(completed) >= TimeSpan.FromMinutes(1)
        ? check with { State = "Expired" } : check;
    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024) throw new ArgumentException("Choose a shorter music folder path.");
        var normalized = LocalMediaPath.Normalize(path);
        if (Path.TrimEndingDirectorySeparator(Path.GetPathRoot(normalized)!).Equals(normalized, PathComparison))
            throw new ArgumentException("Choose a station music folder, rather than a whole drive.");
        return normalized;
    }
    public async ValueTask DisposeAsync()
    {
        Task running; lock (_gate) { _disposed = true; running = _running; }
        await running;
    }
}
