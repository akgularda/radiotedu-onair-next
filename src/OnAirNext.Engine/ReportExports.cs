using System.Diagnostics;
using System.Threading.Channels;

namespace OnAirNext.Engine;

public sealed class ReportExports : IAsyncDisposable
{
    private readonly EventJournal _journal;
    private readonly HashSet<string> _stations;
    private readonly string _root;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly HashSet<string> _scheduled = [];
    private readonly Dictionary<string, CancellationTokenSource> _running = [];
    private Task _worker = Task.CompletedTask;
    private int _initialized;
    private string? _error;
    public string? Error => Volatile.Read(ref _error);

    public ReportExports(EventJournal journal, IEnumerable<string> stations)
    {
        _journal = journal; _stations = stations.ToHashSet(StringComparer.Ordinal);
        _root = Path.Combine(Path.GetDirectoryName(journal.DatabasePath)!, "reports");
    }
    public async Task InitializeAsync()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) throw new InvalidOperationException("Reports are already initialized.");
        RequireNoLinks(_root); Directory.CreateDirectory(_root); RequireNoLinks(_root);
        await _journal.InterruptReportsAsync(); _worker = RunAsync();
    }
    public IReadOnlyList<ReportExport> Snapshot(int offset = 0) => _journal.Reports(offset);
    public async Task<ReportExport> StartAsync(string id, string period, string selection, string? station = null)
    {
        if (Volatile.Read(ref _initialized) == 0 || _stop.IsCancellationRequested || _worker.IsCompleted) throw new InvalidOperationException("Reports are not available.");
        ReportExport.Validate(id, period, selection, station);
        if (station is not null && !_stations.Contains(station)) throw new ArgumentException("Choose a known station or all stations.");
        var saved = await _journal.RequestReportAsync(new(id, period, selection, station, DateTimeOffset.UtcNow));
        if (saved.State == "Queued")
        {
            lock (_gate)
            {
                if (_scheduled.Add(saved.Id) && !_queue.Writer.TryWrite(saved.Id))
                { _scheduled.Remove(saved.Id); throw new InvalidOperationException("The export request was saved. Retry the same request after the current export finishes."); }
            }
        }
        return saved;
    }
    public async Task<ReportExport> CancelAsync(string id)
    {
        var saved = await _journal.CancelReportAsync(id);
        lock (_gate) if (_running.TryGetValue(id, out var running)) running.Cancel();
        return saved;
    }
    public (ReportExport Receipt, FileStream File) OpenDownload(string id)
    {
        var receipt = _journal.Report(id) ?? throw new ArgumentException("Report export not found.");
        if (receipt.State != "Completed" || receipt.Sha256?.Length != 64 || receipt.Bytes <= 0)
            throw new InvalidOperationException("This report has no confirmed file. Wait for completion or retry it.");
        var path = ReportPath(receipt); RequireNoLinks(path);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length != receipt.Bytes) { file.Dispose(); throw new InvalidDataException("The report file no longer matches its receipt. Create a fresh export."); }
        return (receipt, file);
    }
    private async Task RunAsync()
    {
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(_stop.Token))
            {
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                lock (_gate) _running.Add(id, cancel);
                try
                {
                    var started = await _journal.BeginReportAsync(id);
                    if (started.CancelRequested) cancel.Cancel();
                    var completed = await Task.Factory.StartNew(() => Generate(started, cancel.Token), cancel.Token,
                        TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    if (!await _journal.SaveReportProgressAsync(completed)) throw new InvalidOperationException("The export receipt changed before confirmation.");
                    Volatile.Write(ref _error, null);
                }
                catch (Exception failure)
                {
                    var current = _journal.Report(id);
                    if (current is { State: "Running" })
                    {
                        var cancelled = failure is OperationCanceledException || cancel.IsCancellationRequested;
                        var message = cancelled ? "Export cancelled. Unconfirmed files are not offered for download."
                            : failure is ReportStorageException ? failure.Message
                            : "Export failed. Check report storage and retry the saved request. Playback continues independently.";
                        await _journal.SaveReportProgressAsync(current with { State = cancelled ? "Cancelled" : "Failed",
                            FinishedAtUtc = DateTimeOffset.UtcNow, Error = message });
                        Volatile.Write(ref _error, cancelled ? null : message);
                    }
                }
                finally
                {
                    lock (_gate) { _running.Remove(id); _scheduled.Remove(id); }
                    if (!_stop.IsCancellationRequested && _journal.Report(id)?.State == "Queued")
                        lock (_gate)
                            if (_scheduled.Add(id) && !_queue.Writer.TryWrite(id)) _scheduled.Remove(id);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception)
        { Volatile.Write(ref _error, "The report worker stopped. Existing broadcasts are independent; restart the new engine before retrying reports."); }
    }
    private ReportExport Generate(ReportExport report, CancellationToken cancellation)
    {
        if (OperatingSystem.IsWindows()) Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        cancellation.ThrowIfCancellationRequested();
        var path = ReportPath(report); RequireNoLinks(path);
        if (File.Exists(path)) throw new IOException("An unconfirmed artifact already uses this export attempt.");
        var progressClock = Stopwatch.StartNew();
        long artifactBytes = 0, snapshotSequence = 0; string digest = ""; DateTimeOffset snapshotAt = default;
        var result = PlayReports.ExportRange(_journal.DatabasePath, report.FromUtc, report.ToUtc, path,
            stationId: report.StationId, cancellation: cancellation, bytesPerSecond: 1024 * 1024,
            artifactWritten: (bytes, sha256) => { artifactBytes = bytes; digest = sha256; },
            snapshotObserved: (sequence, at) => { snapshotSequence = sequence; snapshotAt = at; },
            progress: (records, incomplete) =>
            {
                if (progressClock.Elapsed < TimeSpan.FromSeconds(1)) return;
                cancellation.ThrowIfCancellationRequested();
                _journal.SaveReportProgressAsync(report with { Records = records, IncompleteRecords = incomplete })
                    .WaitAsync(cancellation).GetAwaiter().GetResult();
                progressClock.Restart();
            });
        cancellation.ThrowIfCancellationRequested();
        return report with { State = "Completed", Records = result.Records, IncompleteRecords = result.Incomplete,
            Bytes = artifactBytes, Sha256 = digest, FinishedAtUtc = DateTimeOffset.UtcNow,
            SnapshotSequence = snapshotSequence, SnapshotAtUtc = snapshotAt };
    }
    private string ReportPath(ReportExport report)
    {
        ReportExport.Validate(report.Id, report.Period, report.Selection, report.StationId);
        if (report.Attempt <= 0) throw new InvalidOperationException("The export attempt has not started.");
        return Path.Combine(_root, report.FileName);
    }
    private static void RequireNoLinks(string path)
    {
        for (var item = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!); item is not null; item = item.Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Report storage cannot traverse folder links.");
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Report storage cannot use a file or folder link.");
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync(); _queue.Writer.TryComplete();
        try { await _worker; } finally { _stop.Dispose(); }
    }
}
