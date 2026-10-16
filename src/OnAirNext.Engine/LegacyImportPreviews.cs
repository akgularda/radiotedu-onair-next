using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OnAirNext.Engine;

// This lane prepares durable, redacted mappings. It cannot release or claim a stream.
public sealed class LegacyImportPreviews : IAsyncDisposable
{
    private readonly string _folder;
    private readonly HashSet<string> _targets;
    private readonly Func<bool> _isUnarmed;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _operation = Task.CompletedTask;
    private LegacyImportPreview? _current;
    public LegacyImportPreviews(string dataRoot, IEnumerable<string> targets, Func<bool> isUnarmed)
    { _folder = Path.Combine(dataRoot, "migration-previews"); _targets = targets.ToHashSet(StringComparer.Ordinal); _isUnarmed = isUnarmed; }
    public void Initialize()
    {
        RequireNoLinks(_folder); Directory.CreateDirectory(_folder); RequireNoLinks(_folder);
        var latest = Path.Combine(_folder, "latest"); RequireNoLinks(latest);
        if (!File.Exists(latest)) return;
        if (new FileInfo(latest).Length > 64) throw new InvalidDataException("The retained preview pointer is invalid.");
        var id = File.ReadAllText(latest); LegacyImportPreview.ValidateId(id);
        _current = Read(id);
        if (_current.State is "Inspecting" or "Verifying backup")
            Save(_current with { State = "Interrupted", Error = "Preview interrupted. Create a fresh preview; the former program was not changed." });
    }
    public LegacyImportPreview? Snapshot(string? id = null)
    { lock (_gate) return id is null ? _current : Read(id); }
    public LegacyImportPreview Start(string id, string legacyRoot)
    {
        LegacyImportPreview.ValidateId(id);
        if (string.IsNullOrWhiteSpace(legacyRoot) || legacyRoot.Length > 1024 || !Path.IsPathFullyQualified(legacyRoot)) throw new ArgumentException("Choose a local absolute legacy data directory.");
        legacyRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyRoot));
        if (!Path.IsPathFullyQualified(legacyRoot) || legacyRoot.StartsWith(@"\", StringComparison.Ordinal)) throw new ArgumentException("Choose a local legacy data directory.");
        RequireNoLinks(legacyRoot);
        lock (_gate)
        {
            if (File.Exists(PlanPath(id)))
            {
                var saved = Read(id);
                if (!string.Equals(saved.LegacyRoot, legacyRoot, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The request ID belongs to another source directory.");
                return saved;
            }
            if (!_operation.IsCompleted) throw new InvalidOperationException("A migration preview is already running. Read its saved status.");
            if (!_isUnarmed()) throw new InvalidOperationException("Migration preparation requires all new channels stopped with no configured outputs.");
            var preview = new LegacyImportPreview(id, legacyRoot, "Inspecting", DateTimeOffset.UtcNow);
            Save(preview); AtomicText(Path.Combine(_folder, "latest"), id);
            _operation = Task.Factory.StartNew(() => Inspect(preview), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return preview;
        }
    }
    public LegacyImportPreview SaveMappings(string id, int revision, LegacyStationMapping[] mappings)
    {
        lock (_gate)
        {
            var saved = Read(id);
            if (saved.State is not ("Ready for mapping" or "Mapping saved") || saved.Backup is null)
                throw new InvalidOperationException("Wait for a verified backup before saving the mapping.");
            LegacyImportPreview.ValidateMappings(saved, mappings, _targets);
            var ordered = mappings.OrderBy(mapping => mapping.LegacyStationId).ToArray();
            if (saved.Mappings?.SequenceEqual(ordered) == true) return saved;
            if (saved.Revision != revision) throw new InvalidOperationException("The mapping changed. Reload it before saving again.");
            var result = saved with { State = "Mapping saved", Revision = saved.Revision + 1, Mappings = ordered };
            Save(result); return Read(id);
        }
    }
    private void Inspect(LegacyImportPreview preview)
    {
        if (OperatingSystem.IsWindows()) Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        try
        {
            var source = Path.Combine(preview.LegacyRoot, "cleanroom.db"); RequireNoLinks(source);
            if (!File.Exists(source) || new FileInfo(source).Length > 1024L * 1024 * 1024) throw new IOException("Choose the legacy data directory containing cleanroom.db (maximum 1 GiB).");
            var stations = new List<LegacyStationPreview>(); LegacyBackupProof proof; string backupPath;
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2 }.ToString()))
            {
                connection.Open(); using var transaction = connection.BeginTransaction(deferred: true);
                using var names = connection.CreateCommand(); names.Transaction = transaction;
                names.CommandText = "SELECT id,name FROM stations ORDER BY id LIMIT 17";
                var identities = new List<(int Id, string Name)>();
                using (var rows = names.ExecuteReader()) while (rows.Read()) identities.Add((rows.GetInt32(0), rows.GetString(1)));
                if (identities.Count is 0 or > 16) throw new InvalidDataException("The legacy station list is missing or exceeds 16 stations.");
                foreach (var identity in identities)
                {
                    if (identity.Name.Length > 128) throw new InvalidDataException("A station name exceeds the preview limit.");
                    _stop.Token.ThrowIfCancellationRequested();
                    var settings = new Dictionary<string, string>(StringComparer.Ordinal);
                    using var command = connection.CreateCommand(); command.Transaction = transaction;
                    command.CommandText = "SELECT key,value FROM station_settings WHERE station_id=$id AND key IN ('music_library_folder','jingle_library_folder','jingle_folder','sweeper_track_ids','sweeper_enabled','sweeper_interval')";
                    command.Parameters.AddWithValue("$id", identity.Id);
                    using (var rows = command.ExecuteReader()) while (rows.Read())
                    { var value = rows.GetString(1); if (value.Length > 2048) throw new InvalidDataException("A saved folder setting exceeds the preview limit."); settings.Add(rows.GetString(0), value); }
                    LegacySavedOutput? output = null;
                    command.CommandText = "SELECT icecast_host,icecast_port,icecast_mount,stream_codec_profile,stream_bitrate_kbps,icecast_enabled,LENGTH(TRIM(icecast_password))>0 FROM station_outputs WHERE station_id=$id";
                    using (var rows = command.ExecuteReader()) if (rows.Read()) output = new(rows.GetString(0), rows.GetInt32(1), rows.GetString(2), rows.GetString(3), rows.GetInt32(4), rows.GetInt32(5) != 0, rows.GetInt32(6) != 0);
                    if (output is not null && (output.Host.Length > 253 || Uri.CheckHostName(output.Host.Trim('[', ']')) == UriHostNameType.Unknown || output.Port is < 1 or > 65535 || output.Mount.Length is < 1 or > 1024 || !output.Mount.StartsWith('/') || output.Mount.Any(char.IsControl) || output.Profile.Length > 64))
                        throw new InvalidDataException("A saved primary output has an invalid address or exceeds its size limit.");
                    var music = settings.GetValueOrDefault("music_library_folder");
                    var jingle = settings.GetValueOrDefault("jingle_library_folder") ?? settings.GetValueOrDefault("jingle_folder");
                    var warnings = new List<string> { "Secondary outputs and source credentials have not been imported.", "Jingle/sweeper files and cadence require a separate validated import." };
                    if (string.IsNullOrWhiteSpace(music)) warnings.Add("No saved music folder.");
                    if (string.IsNullOrWhiteSpace(jingle)) warnings.Add("No saved jingle folder.");
                    stations.Add(new(identity.Id, identity.Name, music, jingle, settings.GetValueOrDefault("sweeper_track_ids"),
                        settings.GetValueOrDefault("sweeper_enabled") == "true", int.TryParse(settings.GetValueOrDefault("sweeper_interval"), NumberStyles.None, CultureInfo.InvariantCulture, out var interval) ? interval : 0, output, warnings.ToArray()));
                }
                using var backup = connection.CreateCommand(); backup.Transaction = transaction;
                backup.CommandText = "SELECT file_path,sha256,size_bytes,verified_at FROM recovery_points WHERE integrity_status='ok' AND verified_at IS NOT NULL ORDER BY id DESC LIMIT 1";
                using (var rows = backup.ExecuteReader())
                {
                    if (!rows.Read()) throw new InvalidOperationException("Create a verified recovery point in the former program, then prepare a fresh preview.");
                    backupPath = Path.GetFullPath(rows.GetString(0));
                    proof = new(Path.GetFileName(backupPath), rows.GetString(1).ToLowerInvariant(), rows.GetInt64(2), rows.GetString(3));
                }
                transaction.Commit();
            }
            var backupRoot = Path.Combine(preview.LegacyRoot, "recovery-points") + Path.DirectorySeparatorChar;
            if (!backupPath.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase) || proof.Bytes is <= 0 or > 1073741824 || proof.Sha256.Length != 64 || proof.Sha256.Any(c => !char.IsAsciiHexDigit(c)))
                throw new InvalidDataException("The legacy recovery receipt is outside the selected data directory or invalid.");
            RequireNoLinks(backupPath);
            preview = preview with { State = "Verifying backup", Stations = stations.ToArray(),
                SettingsFingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(stations))) };
            Publish(preview);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var input = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            if (input.Length != proof.Bytes) throw new InvalidDataException("The recovery file size differs from its verified receipt.");
            var buffer = new byte[65536]; long checkedBytes = 0; var clock = System.Diagnostics.Stopwatch.StartNew(); long nextProgress = 2097152;
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                _stop.Token.ThrowIfCancellationRequested(); hash.AppendData(buffer.AsSpan(0, count)); checkedBytes += count;
                while (checkedBytes / 2097152d > clock.Elapsed.TotalSeconds)
                    if (_stop.Token.WaitHandle.WaitOne(25)) _stop.Token.ThrowIfCancellationRequested();
                if (checkedBytes >= nextProgress) { Publish(preview with { CheckedBytes = checkedBytes }); nextProgress += 2097152; }
            }
            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != proof.Sha256 || checkedBytes != proof.Bytes)
                throw new InvalidDataException("The recovery file failed its independent SHA-256 check. No import is allowed.");
            Publish(preview with { State = "Ready for mapping", Backup = proof, CheckedBytes = checkedBytes });
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException ? "Preview interrupted; the former program was not changed."
                : ex is SqliteException ? "The legacy database could not be read without changing it. Check the source and service access."
                : ex is UnauthorizedAccessException ? "The engine account cannot read this legacy source. No permissions were changed." : ex.Message;
            Publish(preview with { State = ex is OperationCanceledException ? "Interrupted" : "Failed", Stations = null, Backup = null, Error = message });
        }
    }
    private void Publish(LegacyImportPreview preview) { lock (_gate) Save(preview); }
    private void Save(LegacyImportPreview preview)
    {
        var json = JsonSerializer.Serialize(preview);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 60000) throw new InvalidDataException("The preview exceeds the control-protocol size limit. Choose a smaller legacy station set.");
        AtomicText(PlanPath(preview.Id), json);
        _current = preview;
    }
    private LegacyImportPreview Read(string id)
    {
        var path = PlanPath(id); RequireNoLinks(path);
        if (new FileInfo(path).Length > 1048576) throw new InvalidDataException("The saved preview exceeds its size limit.");
        var preview = JsonSerializer.Deserialize<LegacyImportPreview>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("The saved preview is invalid.");
        if (preview.Id != id || preview.ProductionChanged) throw new InvalidDataException("The saved preview identity is invalid.");
        return preview;
    }
    private string PlanPath(string id) { LegacyImportPreview.ValidateId(id); return Path.Combine(_folder, id + ".json"); }
    private static void AtomicText(string path, string text)
    {
        RequireNoLinks(path); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try { using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(System.Text.Encoding.UTF8.GetBytes(text)); file.Flush(true); } File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void RequireNoLinks(string path)
    {
        for (DirectoryInfo? item = new(Path.GetFullPath(path)); item is not null; item = item.Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Migration paths cannot use folder links.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Migration files cannot be links.");
    }
    public async ValueTask DisposeAsync() { await _stop.CancelAsync(); try { await _operation; } finally { _stop.Dispose(); } }
}
