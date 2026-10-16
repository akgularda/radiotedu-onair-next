using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace OnAirNext.Engine;

public sealed record LibraryWorkerStatus(string State, string? Error, DateTimeOffset? ScannedAtUtc);
public sealed record LibraryPlaybackInventory(bool Configured, int ReadyCount, IReadOnlyList<MediaItem> Items, long Version);

// One bounded import/validation lane across stations. No file operation or SQLite query runs in Select().
public sealed class StationLibrary : IAsyncDisposable
{
    private sealed record Signature(string Path, string Relative, long Bytes, long Modified);
    private sealed record Stable(long Bytes, long Modified, long FirstObserved);
    private sealed record PlaybackSet(bool Configured, MediaItem[] Items, long Version);
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".aif", ".aiff", ".wma", ".mp4" };
    private readonly EventJournal _journal;
    private readonly string _dataRoot;
    private readonly Func<string, CancellationToken, Task<AudioMetadata>> _probe;
    private readonly LibraryAudioProbe? _audioProbe;
    private readonly HashSet<string> _stations;
    private readonly IReadOnlyDictionary<string, ContentKind> _contentKinds;
    private readonly ConcurrentDictionary<string, PlaybackSet> _playback = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LibraryWorkerStatus> _status = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Stable> _observed = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private Task? _disposal;
    private readonly object _lifetime = new();
    private readonly object _publication = new();
    private volatile bool _disposed;
    private long _version;
    public event Action<string, bool>? Changed;

    public StationLibrary(EventJournal journal, string ffmpeg, string dataRoot, IEnumerable<string> stations,
        Func<string, CancellationToken, Task<AudioMetadata>>? probe = null,
        IReadOnlyDictionary<string, ContentKind>? contentKinds = null)
    {
        _journal = journal; _dataRoot = dataRoot; _stations = stations.ToHashSet(StringComparer.Ordinal);
        var kinds = contentKinds is null ? new Dictionary<string, ContentKind>(StringComparer.Ordinal)
            : contentKinds.ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal);
        if(kinds.Any(x=>!_stations.Contains(x.Key)||x.Value is not (ContentKind.Music or ContentKind.Jingle or ContentKind.Sweeper)))
            throw new ArgumentException("Library roles must name configured music, jingle or sweeper inventories.");
        _contentKinds = kinds;
        _audioProbe = probe is null ? new LibraryAudioProbe(ffmpeg) : null;
        _probe = probe ?? _audioProbe!.VerifyAsync;
    }

    public async Task InitializeAsync()
    {
        if (_worker is not null) throw new InvalidOperationException("Library already initialized.");
        await _journal.InitializeLibraryAsync();
        foreach (var root in _journal.LibraryRoots().Where(x => _stations.Contains(x.StationId)))
            _playback[root.StationId] = new(true, [], Interlocked.Increment(ref _version));
        _worker = Task.Run(RunAsync);
    }

    public LibraryWorkerStatus Status(string station) => _status.GetValueOrDefault(station) ?? new("Not configured", null, null);
    // The host builds immutable role pools outside the programme clock. Limit
    // copies without hiding the total, so an oversized pool can be rejected.
    public LibraryPlaybackInventory ReadyInventory(string station, int maximumItems = BrandingRotation.MaximumAssets)
    {
        RequireStation(station);
        if(maximumItems is <1 or >BrandingRotation.MaximumAssets)throw new ArgumentOutOfRangeException(nameof(maximumItems));
        return _playback.TryGetValue(station,out var set)
            ?new(set.Configured,set.Items.Length,Array.AsReadOnly(set.Items.Take(maximumItems).ToArray()),set.Version)
            :new(false,0,Array.Empty<MediaItem>(),0);
    }
    public LibraryPage Page(string station, string query, int offset) { RequireStation(station); return _journal.LibraryPage(station, query, offset); }
    public async Task SetRootAsync(string station, string root, string command, Action? authorizeNewAssignment = null)
    {
        RequireStation(station);
        if (await _journal.SetLibraryRootAsync(station, root, command, authorizeNewAssignment))
        { lock (_publication) { _playback[station] = new(true, [], Interlocked.Increment(ref _version)); Changed?.Invoke(station, false); } Wake(); }
    }
    public async Task SetLegacyMusicRootAsync(string station, string root, string command, LegacyMusicProof proof, Action authorize)
    {
        RequireStation(station);
        if (await _journal.SetLegacyMusicRootAsync(station, root, command, proof, authorize))
        { lock (_publication) { _playback[station] = new(true, [], Interlocked.Increment(ref _version)); Changed?.Invoke(station, false); } Wake(); }
    }
    public async Task ImportAsync(string station, string[] paths, string command, bool uploaded = false)
    {
        RequireStation(station);
        if (await _journal.QueueLibraryImportAsync(station, paths, ManagedRoot(station), command, uploaded)) Wake();
    }
    public Task UseManagedRootAsync(string station, string command) => SetRootAsync(station, ManagedRoot(station), command);
    private string ManagedRoot(string station) => Path.Combine(_dataRoot, "music", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(station)))[..16]);
    public bool CanImportFiles(LibraryRoot? root) => root is null || IsPrivateFolder(root.Path);
    private bool IsPrivateFolder(string root) => Path.GetFullPath(root).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_dataRoot)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }
    public async Task RescanAsync(string station, string command)
    { RequireStation(station); if (await _journal.RequestLibraryRescanAsync(station, command)) Wake(); }
    public async Task<LibraryAsset> EditAssetAsync(string station, long revision, string assetId, long expectedVersion,
        LibraryEditKind kind, string command, string? artist = null, string? title = null)
    {
        RequireStation(station);
        if (await _journal.ChangeLibraryAssetAsync(station, revision, assetId, expectedVersion, kind, command, artist, title))
        {
            var root = _journal.LibraryRoots().SingleOrDefault(x => x.StationId == station);
            if (root is not null) Publish(root, allowNew: false);
            Wake();
        }
        return _journal.LibraryItem(station, revision, assetId) ?? throw new InvalidOperationException("The edited item is no longer in the current library.");
    }
    private void RequireStation(string station)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_stations.Contains(station)) throw new ArgumentException("Unknown station.");
    }

    public IPlayoutPlanner? Planner(StationSettings settings) => settings.Repeat ? new PlannerView(this, settings) : null;
    private sealed class PlannerView(StationLibrary owner, StationSettings settings) : IPlayoutPlanner
    {
        private static readonly int[] Strides = [1, 3, 5, 7, 11, 13, 17, 19];
        public int? AvailableItems => owner._playback.TryGetValue(settings.Id, out var set) && set.Configured ? set.Items.Length : settings.Playlist.Count;
        public PlayoutSelection? SelectPrepared(int rotationIndex,IReadOnlySet<string> excludedQueueEntries)
            => AvailableItems==0?null:Select(rotationIndex);
        public PlayoutSelection Select(int rotationIndex, string? excludeQueueEntryId = null)
        {
            if (!owner._playback.TryGetValue(settings.Id, out var set) || !set.Configured)
            {
                if (settings.Playlist.Count == 0) throw new InvalidOperationException("Choose a live folder or import music in Library.");
                return new(settings.Playlist[rotationIndex % settings.Playlist.Count]);
            }
            if (set.Items.Length == 0) throw new InvalidOperationException("The configured live folder has no validated, available music.");
            // The background publisher orders content hashes. A coprime stride changes
            // each cycle's traversal without sorting or hashing on the programme clock.
            var cycle = rotationIndex / set.Items.Length;
            var stride = 1;
            for (var i = 0; i < Strides.Length; i++)
            {
                var candidate = Strides[(cycle + i) % Strides.Length];
                var a = candidate; var b = set.Items.Length;
                while (b != 0) { var remainder = a % b; a = b; b = remainder; }
                if (a == 1) { stride = candidate; break; }
            }
            var position = (int)((long)(rotationIndex % set.Items.Length) * stride % set.Items.Length);
            return new(set.Items[position]);
        }
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var job = _journal.PendingLibraryImport();
                if (job is not null)
                {
                    try { _stop.Token.ThrowIfCancellationRequested(); if (!_stations.Contains(job.StationId)) throw new ArgumentException("Import station is not configured."); await CopyImportAsync(job, _stop.Token); await _journal.FinishLibraryImportAsync(job.Id, null); }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                    catch (Exception ex) { await _journal.FinishLibraryImportAsync(job.Id, ex.Message); }
                    continue;
                }
                var roots = _journal.LibraryRoots().Where(x => _stations.Contains(x.StationId)).ToArray();
                foreach (var path in _observed.Keys.Where(path => !roots.Any(root => path.StartsWith(root.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToArray())
                    _observed.Remove(path);
                foreach (var root in roots)
                {
                    _stop.Token.ThrowIfCancellationRequested();
                    try { await ScanAsync(root, _stop.Token); }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                    catch (LibraryValidationUnavailableException ex)
                    { _status[root.StationId] = new("Validation paused", ex.Message, DateTimeOffset.UtcNow); }
                    catch (Exception ex)
                    {
                        _status[root.StationId] = new("Unavailable", ex.Message, DateTimeOffset.UtcNow);
                        lock (_publication)
                        {
                            _playback[root.StationId] = new(true, [], Interlocked.Increment(ref _version));
                            Changed?.Invoke(root.StationId, false);
                        }
                        foreach (var path in _observed.Keys.Where(path => path.StartsWith(root.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray())
                            _observed.Remove(path);
                    }
                }
                await _wake.WaitAsync(TimeSpan.FromSeconds(_observed.Count > 0 ? 3 : 30), _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                foreach (var station in _stations) _status[station] = new("Retrying library operation", ex.Message, DateTimeOffset.UtcNow);
                try { await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task CopyImportAsync(LibraryImport job, CancellationToken token)
    {
        var root = _journal.LibraryRoots().SingleOrDefault(x => x.StationId == job.StationId && x.Revision == job.Revision)
            ?? throw new InvalidOperationException("The music folder changed. Import these files again into the current folder.");
        _status[job.StationId] = new("Importing", null, null);
        var writable = IsPrivateFolder(root.Path);
        if (writable)
        {
            // Reject junction/symlink ancestors before creating or writing any destination.
            for (DirectoryInfo? parent = new(root.Path); parent is not null; parent = parent.Parent)
                if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Choose a private music folder without folder links.");
            Directory.CreateDirectory(root.Path);
        }
        else if (!Directory.Exists(root.Path)) throw new DirectoryNotFoundException("The external live folder is unavailable.");
        if ((File.GetAttributes(root.Path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Choose a real folder, rather than a folder link.");
        var errors = new List<string>(); var imported = new List<string>();
        foreach (var source in job.Paths)
        {
            token.ThrowIfCancellationRequested();
            if (!_journal.LibraryRoots().Contains(root)) throw new IOException("The live folder changed during import.");
            if (!Extensions.Contains(Path.GetExtension(source))) { errors.Add(Path.GetFileName(source) + ": unsupported audio file type"); continue; }
            var relative = Path.GetRelativePath(root.Path, source);
            if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            { imported.Add(relative); continue; }
            if (!writable) throw new InvalidOperationException("External folders are watched read-only in this isolated build. Choose the managed music folder before copying files.");
            var temporary = Path.Combine(root.Path, ".onair-import-" + Guid.NewGuid().ToString("N") + ".partial");
            try
            {
                var before = new FileInfo(source); var bytes = before.Length; var modified = before.LastWriteTimeUtc.Ticks;
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                { await input.CopyToAsync(output, token); await output.FlushAsync(token); output.Flush(flushToDisk: true); }
                var after = new FileInfo(source);
                if (after.Length != bytes || after.LastWriteTimeUtc.Ticks != modified) throw new IOException("The source file changed during import.");
                string hash;
                await using (var copied = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                    hash = Convert.ToHexString(await SHA256.HashDataAsync(copied, token));
                // Content-addressed destination makes a crash/retry idempotent, without replacing a different song.
                var stem = Path.GetFileNameWithoutExtension(source);
                stem = stem[..Math.Min(stem.Length, 50)];
                var destination = Path.Combine(root.Path, stem + "--" + hash + Path.GetExtension(source).ToLowerInvariant());
                if (File.Exists(destination))
                {
                    await using var old = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                    if (Convert.ToHexString(await SHA256.HashDataAsync(old, token)) != hash)
                        throw new IOException("An existing imported file changed; it was not overwritten.");
                }
                else
                {
                    if (!_journal.LibraryRoots().Contains(root)) throw new IOException("The live folder changed during import.");
                    File.Move(temporary, destination, overwrite: false);
                }
                imported.Add(Path.GetRelativePath(root.Path, destination));
            }
            catch (IOException ex)
            {
                errors.Add(Path.GetFileName(source) + ": " + ex.Message);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            if (errors.Count > 8) break;
        }
        await ScanAsync(root, token);
        await Task.Delay(2100, token); // second observation is required before validation/publication
        await ScanAsync(root, token);
        var indexed = _journal.LibraryAssets(root).ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var relative in imported)
            if (!indexed.TryGetValue(EventJournal.LibraryAssetId(relative), out var asset) || asset.State != "Ready")
                errors.Add(relative + ": " + (asset?.Error ?? "not stable or not ready"));
        if (errors.Count > 0) throw new IOException(string.Join("; ", errors));
    }

    private async Task ScanAsync(LibraryRoot root, CancellationToken token)
    {
        _status[root.StationId] = new("Scanning", null, null);
        var all = Enumerate(root.Path, token); // failure/partial enumeration must never mark existing songs missing
        var existing = _journal.LibraryAssets(root, raw: true).ToDictionary(x => x.Id, StringComparer.Ordinal);
        var present = all.Select(x => EventJournal.LibraryAssetId(x.Relative)).ToHashSet(StringComparer.Ordinal);
        var missing = existing.Values.Where(x => x.State is not ("Missing" or "Retired") && !present.Contains(x.Id)).Select(x => x.Id).ToArray();
        foreach (var batch in missing.Chunk(100)) await _journal.ApplyLibraryChangesAsync(root, [], batch);
        var changed = all.Select(file => (File: file, Asset: existing.GetValueOrDefault(EventJournal.LibraryAssetId(file.Relative))))
            .Where(x => x.Asset is { State: "Ready" } && (x.Asset.Bytes != x.File.Bytes || x.Asset.ModifiedTicks != x.File.Modified || _audioProbe is not null && x.Asset.Loudness is null))
            .Select(x => x.Asset! with { State = "Invalid", ModifiedTicks = -1, Loudness = null, Error = "File changed or loudness analysis required; validation pending." }).ToArray();
        foreach (var batch in changed.Chunk(100)) await _journal.ApplyLibraryChangesAsync(root, batch, []);
        Publish(root);
        var lastPublish = Stopwatch.StartNew();
        foreach (var file in all)
        {
            token.ThrowIfCancellationRequested();
            var id = EventJournal.LibraryAssetId(file.Relative);
            if (existing.TryGetValue(id, out var prior) && prior.State is "Ready" or "Invalid" or "Retired" && prior.Bytes == file.Bytes && prior.ModifiedTicks == file.Modified && !prior.AnalysisRequested && (_audioProbe is null || prior.Loudness is not null || prior.State != "Ready")) continue;
            if (!_observed.TryGetValue(file.Path, out var observed) || observed.Bytes != file.Bytes || observed.Modified != file.Modified)
            {
                _observed[file.Path] = new(file.Bytes, file.Modified, Stopwatch.GetTimestamp());
                continue;
            }
            if (Stopwatch.GetElapsedTime(observed.FirstObserved) < TimeSpan.FromSeconds(2)) continue;
            _status[root.StationId] = new("Validating " + file.Relative, null, null);
            LibraryAsset asset;
            try
            {
                var metadata = await _probe(file.Path, token);
                var after = new FileInfo(file.Path);
                if (after.Length != file.Bytes || after.LastWriteTimeUtc.Ticks != file.Modified) { _observed.Remove(file.Path); continue; }
                asset = new(id, file.Relative, metadata.Artist, metadata.Title, metadata.Seconds, file.Bytes, file.Modified, "Ready", Loudness: metadata.Loudness);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (LibraryValidationUnavailableException) { throw; }
            catch (Exception ex) { asset = new(id, file.Relative, "", Path.GetFileNameWithoutExtension(file.Relative), 0, file.Bytes, file.Modified, "Invalid", ex.Message); }
            await _journal.ApplyLibraryChangesAsync(root, [asset], []);
            _observed.Remove(file.Path);
            if (lastPublish.Elapsed >= TimeSpan.FromSeconds(5)) { Publish(root); lastPublish.Restart(); }
            await Task.Delay(100, token);
        }
        var paths = all.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _observed.Keys.Where(path => path.StartsWith(root.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !paths.Contains(path)).ToArray()) _observed.Remove(path);
        Publish(root);
        _status[root.StationId] = new(_observed.Keys.Any(paths.Contains) ? "Waiting for stable files" : "Watching", null, DateTimeOffset.UtcNow);
    }

    private void Publish(LibraryRoot root, bool allowNew = true)
    {
        lock (_publication)
        {
            if (!_journal.LibraryRoots().Contains(root)) return;
            _playback.TryGetValue(root.StationId, out var prior);
            // Edits may update/remove already eligible items; only a successful scan can add them.
            var eligible = allowNew ? null : prior?.Items.Select(x => x.Id).ToHashSet(StringComparer.Ordinal) ?? [];
            var items = _journal.LibraryAssets(root).Where(x => x.State == "Ready" && (eligible is null || eligible.Contains(x.Id)))
                .Select(x => new MediaItem(x.Id, Path.Combine(root.Path, x.RelativePath), x.Artist, x.Title,
                    Kind: _contentKinds.GetValueOrDefault(root.StationId, ContentKind.Music), DurationSeconds: x.Seconds, Loudness: x.Loudness))
                .OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
            if (prior is not null && prior.Configured && prior.Items.SequenceEqual(items)) return;
            _playback[root.StationId] = new(true, items, Interlocked.Increment(ref _version));
            Changed?.Invoke(root.StationId, items.Length > 0);
        }
    }

    private static List<Signature> Enumerate(string root, CancellationToken token)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The live folder is unavailable. Existing catalog history is retained.");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Folder links cannot be used as live roots.");
        var files = new List<Signature>(); var directories = new Stack<string>(); directories.Push(root);
        while (directories.TryPop(out var directory))
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested(); var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) { directories.Push(path); continue; }
                if (!Extensions.Contains(Path.GetExtension(path))) continue;
                if (files.Count >= 100000) throw new IOException("Live folder exceeds 100,000 audio files; choose a smaller station folder.");
                var info = new FileInfo(path);
                files.Add(new(path, Path.GetRelativePath(root, path), info.Length, info.LastWriteTimeUtc.Ticks));
            }
        return files;
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetime)
        {
            if (_disposal is null || _disposal.IsFaulted) _disposal = DisposeCoreAsync();
            return new(_disposal);
        }
    }
    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        await _stop.CancelAsync();
        if (_worker is not null) await _worker;
        if (_audioProbe is not null) await _audioProbe.DisposeAsync();
        _stop.Dispose();
    }
}
