using System.Text.Json;

namespace OnAirNext.Lab;

// An optional guard for a single off-air rehearsal on a broadcasting PC.
// It reads the independent observer only and can cancel only this lab run.
internal sealed class LiveWorkloadGuard(string observer)
{
    private readonly Dictionary<int, int> _workers = new();
    private DateTimeOffset _lastSample;
    private int _highCpuSamples;

    public void Check()
    {
        using var stream = new FileStream(observer, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(Math.Max(0, stream.Length - 65536), SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        var tail = reader.ReadToEnd();
        var end = tail.LastIndexOf('\n');
        if (end <= 0) throw new InvalidDataException("The live resource observer has no complete sample.");
        var start = tail.LastIndexOf('\n', end - 1) + 1;
        using var document = JsonDocument.Parse(tail[start..end]);
        var row = document.RootElement;
        var at = row.GetProperty("at").GetDateTimeOffset();
        if ((DateTimeOffset.UtcNow - at).TotalSeconds is > 20 or < -5)
            throw new InvalidOperationException("The live resource observer is stale. Rehearsal cancelled.");
        if (at <= _lastSample) return;
        _lastSample = at;
        if (row.GetProperty("freeMiB").GetInt32() < 3200)
            throw new InvalidOperationException("Available memory fell below the on-air reserve. Rehearsal cancelled.");
        _highCpuSamples = row.GetProperty("cpuPercent").GetDouble() >= 86 ? _highCpuSamples + 1 : 0;
        if (_highCpuSamples >= 3)
            throw new InvalidOperationException("Sustained host load reached the on-air limit. Rehearsal cancelled.");
        var workers = row.GetProperty("workers").EnumerateArray().ToArray();
        if (workers.Length != 8) throw new InvalidOperationException("A live worker is missing. Rehearsal cancelled.");
        var current = new Dictionary<int, int>();
        foreach (var worker in workers)
        {
            var id = worker.GetProperty("id").GetInt32();
            var pid = worker.GetProperty("pid").GetInt32();
            if (pid <= 0 || worker.GetProperty("stalled").GetBoolean() ||
                worker.GetProperty("heartbeatAge").GetDouble() > 10 ||
                _workers.Count > 0 && (!_workers.TryGetValue(id, out var prior) || prior != pid))
                throw new InvalidOperationException("A live worker needs attention. Rehearsal cancelled.");
            current.Add(id, pid);
        }
        if (_workers.Count == 0)
            foreach (var (id, pid) in current) _workers.Add(id, pid);
    }
}
