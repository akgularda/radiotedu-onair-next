using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace OnAirNext.Engine;

// SQLite writes and reports have a dedicated lane; the program never waits for a CSV or network upload.
public sealed partial class EventJournal : IAsyncDisposable
{
    private readonly Channel<Action> _pending = Channel.CreateBounded<Action>(4096);
    private readonly SqliteConnection _connection;
    private readonly FileStream _ownership;
    private readonly Task _writer;
    private long _rejected, _committed;
    private int _transactionEvents;
    private string? _error;
    public string DatabasePath { get; }
    public long RejectedEvents => Interlocked.Read(ref _rejected);
    public long CommittedEvents => Interlocked.Read(ref _committed);
    public int PendingEvents => _pending.Reader.Count;
    public string? Error => _error;

    public EventJournal(string path) : this(path, false) { }

    public static EventJournal OpenIsolatedWindowsService()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The isolated service journal requires Windows.");
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (identity.User?.Value != "S-1-5-19")
            throw new UnauthorizedAccessException("The isolated machine journal requires the LocalService identity.");
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RadioTEDU-OnAirNext-Development", "data");
        if (!Directory.Exists(data)) throw new DirectoryNotFoundException("The guided installer must create the protected service data directory.");
        var path = Path.Combine(data, "playback.db");
        for (DirectoryInfo? parent = new(data); parent is not null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Service journal paths cannot traverse folder links.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The service journal cannot be a file link.");
        return new EventJournal(path, true);
    }

    private EventJournal(string path, bool isolatedMachineService)
    {
        DatabasePath = Path.GetFullPath(path);
        var localRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadioTEDU", "OnAirNext-Dev");
        if (!isolatedMachineService && !DatabasePath.StartsWith(localRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Development databases must be under the local application data directory.");
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _ownership = AcquireWriterOwnership(DatabasePath);
        SqliteConnection? opening = null;
        try
        {
            ValidateExistingSchema(DatabasePath);
            opening = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            opening.Open();
            _connection = opening;
            using var command = _connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=FULL;
                PRAGMA busy_timeout=2000;
                CREATE TABLE IF NOT EXISTS schema_info(version INTEGER NOT NULL);
                INSERT INTO schema_info SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM schema_info);
                CREATE TABLE IF NOT EXISTS playback_events(
                  sequence INTEGER PRIMARY KEY, event_id TEXT NOT NULL UNIQUE,
                  occurrence_id TEXT NOT NULL, station_id TEXT NOT NULL, event_type TEXT NOT NULL,
                  at_utc TEXT NOT NULL, payload TEXT NOT NULL, previous_hash TEXT NOT NULL, entry_hash TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS playback_events_time ON playback_events(at_utc, sequence);
                CREATE INDEX IF NOT EXISTS playback_events_occurrence ON playback_events(occurrence_id, sequence);
                CREATE TABLE IF NOT EXISTS station_desired_state(station_id TEXT PRIMARY KEY,enabled INTEGER NOT NULL,
                  requested_running INTEGER NOT NULL,updated_utc TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS station_cadence(station_id TEXT PRIMARY KEY,
                  music_since_jingle INTEGER NOT NULL DEFAULT 0, music_since_sweeper INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS cadence_completions(station_id TEXT NOT NULL,occurrence_id TEXT NOT NULL,
                  PRIMARY KEY(station_id,occurrence_id));
                CREATE TABLE IF NOT EXISTS playout_checkpoints(station_id TEXT PRIMARY KEY,payload TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS engine_commands(id TEXT PRIMARY KEY,station_id TEXT NOT NULL,kind TEXT NOT NULL,processed INTEGER NOT NULL DEFAULT 0);
                """;
            command.ExecuteNonQuery();
            using var version = _connection.CreateCommand();
            version.CommandText = "SELECT version FROM schema_info";
            if (Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            {
                _connection.Dispose();
                throw new InvalidDataException("Unsupported development journal schema; explicit migration is required.");
            }
            // Additive migration for pre-recovery development journals. Existing accepted commands remain readable.
            using (var columns = _connection.CreateCommand())
            {
                columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('engine_commands') WHERE name='target_occurrence'";
                if (Convert.ToInt32(columns.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                {
                    using var migrate = _connection.CreateCommand();
                    migrate.CommandText = "ALTER TABLE engine_commands ADD COLUMN target_occurrence TEXT";
                    migrate.ExecuteNonQuery();
                }
            }
            using (var columns = _connection.CreateCommand())
            {
                columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('engine_commands') WHERE name='request_target'";
                if (Convert.ToInt32(columns.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                {
                    using var migrate = _connection.CreateCommand();
                    migrate.CommandText = "ALTER TABLE engine_commands ADD COLUMN request_target TEXT; UPDATE engine_commands SET request_target=target_occurrence";
                    migrate.ExecuteNonQuery();
                }
            }
            _writer = Task.Factory.StartNew(WriteLoop, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch
        {
            try { opening?.Dispose(); }
            finally { _ownership.Dispose(); }
            throw;
        }
    }

    public bool TryAppend(PlaybackEvent entry)
    {
        try { AppendAsync(entry).GetAwaiter().GetResult(); return true; }
        catch { Interlocked.Increment(ref _rejected); return false; }
    }

    // An acknowledgement follows FULL-WAL commit. Queue ownership and checkpoint changes
    // share the event transaction; a process kill cannot acknowledge an in-memory-only event.
    public Task AppendAsync(PlaybackEvent entry, PlayoutCheckpoint? checkpoint = null)
        => TransactionAsync((connection, transaction) =>
        {
            if (!WriteEvent(connection, transaction, entry)) return false;
            if (checkpoint is not null) WriteCheckpoint(connection, transaction, checkpoint);
            if (entry.Type == "ended") CompleteCadence(connection, transaction, entry);
            if (entry.Type is "skipped" or "ended" or "failed")
                ConsumeSkips(connection, transaction, entry.StationId, entry.OccurrenceId);
            if (entry.QueueEntryId is not null)
            {
                using var exists = connection.CreateCommand(); exists.Transaction = transaction;
                exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='studio_queue'";
                if (Convert.ToInt32(exists.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                {
                    using var update = connection.CreateCommand(); update.Transaction = transaction;
                    update.CommandText = "UPDATE studio_queue SET state=$state WHERE id=$id";
                    update.Parameters.AddWithValue("$id", entry.QueueEntryId);
                    update.Parameters.AddWithValue("$state", entry.Type is "started" or "resumed" ? "playing" : entry.Type is "ended" or "skipped" or "failed" ? "completed" : "pending");
                    update.ExecuteNonQuery();
                }
            }
            return true;
        });

    public Task<T> TransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task SubmitAsync()
        {
            try
            {
                await _pending.Writer.WriteAsync(() =>
                {
                    try
                    {
                        using var transaction = _connection.BeginTransaction();
                        _transactionEvents = 0;
                        var result = action(_connection, transaction);
                        transaction.Commit();
                        Interlocked.Add(ref _committed, _transactionEvents);
                        completion.TrySetResult(result);
                    }
                    catch (Exception ex) { completion.TrySetException(ex); }
                });
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }
        _ = SubmitAsync();
        return completion.Task;
    }

    public Task<PlayoutCheckpoint?> CheckpointAsync(string stationId) => TransactionAsync((connection, transaction) =>
    {
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT payload FROM playout_checkpoints WHERE station_id=$id";
        query.Parameters.AddWithValue("$id", stationId);
        return query.ExecuteScalar() is string value ? JsonSerializer.Deserialize<PlayoutCheckpoint>(value) : null;
    });

    public Task SaveCheckpointAsync(PlayoutCheckpoint checkpoint) => TransactionAsync((connection, transaction) =>
    { WriteCheckpoint(connection, transaction, checkpoint); return true; });

    public Task<bool> RequestSkipAsync(string station, string id, string? targetOccurrence = null)
        => RequestSkipCoreAsync(station, id, targetOccurrence, targetOccurrence);

    internal Task<bool> RequestObservedSkipAsync(string station, string id, string targetOccurrence)
        => RequestSkipCoreAsync(station, id, targetOccurrence, null);

    private Task<bool> RequestSkipCoreAsync(string station, string id, string? targetOccurrence, string? requestTarget) => TransactionAsync((connection, transaction) =>
    {
        if (string.IsNullOrWhiteSpace(station) || string.IsNullOrWhiteSpace(id) || id.Length > 128)
            throw new ArgumentException("Skip commands require a station and a bounded command ID.");
        if (ExistingSkipReceipt(connection, transaction, station, id, requestTarget)) return false;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO engine_commands(id,station_id,kind,target_occurrence,request_target) VALUES($id,$station,'skip',$target,$request)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$station", station);
        command.Parameters.AddWithValue("$target", (object?)targetOccurrence ?? DBNull.Value);
        command.Parameters.AddWithValue("$request", (object?)requestTarget ?? DBNull.Value);
        return command.ExecuteNonQuery() == 1;
    });

    internal Task<bool> HasObservedSkipReceiptAsync(string station, string id) => TransactionAsync((connection, transaction)
        => ExistingSkipReceipt(connection, transaction, station, id, null));

    private static bool ExistingSkipReceipt(SqliteConnection connection, SqliteTransaction transaction,
        string station, string id, string? requestTarget)
    {
        if (string.IsNullOrWhiteSpace(station) || string.IsNullOrWhiteSpace(id) || id.Length > 128)
            throw new ArgumentException("Skip commands require a station and a bounded command ID.");
        using (var prior = connection.CreateCommand())
        {
            prior.Transaction = transaction;
            prior.CommandText = "SELECT station_id,kind,request_target FROM engine_commands WHERE id=$id";
            prior.Parameters.AddWithValue("$id", id);
            using var reader = prior.ExecuteReader();
            if (reader.Read())
            {
                var target = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (reader.GetString(0) != station || reader.GetString(1) != "skip" || target != requestTarget)
                    throw new InvalidOperationException("Command ID was already used for a different operation.");
                return true;
            }
        }
        return false;
    }

    public Task<bool> HasPendingSkipAsync(string station, string? targetOccurrence = null) => TransactionAsync((connection, transaction)
        => HasPendingSkip(connection, transaction, station, targetOccurrence));

    private static bool HasPendingSkip(SqliteConnection connection, SqliteTransaction transaction, string station, string? occurrence)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM engine_commands WHERE station_id=$station AND kind='skip' AND processed=0 AND (target_occurrence IS NULL OR target_occurrence=$target)";
        command.Parameters.AddWithValue("$station", station);
        command.Parameters.AddWithValue("$target", (object?)occurrence ?? DBNull.Value);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static void ConsumeSkips(SqliteConnection connection, SqliteTransaction transaction, string station, string occurrence)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE engine_commands SET processed=1 WHERE station_id=$station AND kind='skip' AND processed=0 AND (target_occurrence IS NULL OR target_occurrence=$target)";
        command.Parameters.AddWithValue("$station", station); command.Parameters.AddWithValue("$target", occurrence);
        command.ExecuteNonQuery();
    }

    private static void WriteCheckpoint(SqliteConnection connection, SqliteTransaction transaction, PlayoutCheckpoint checkpoint)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO playout_checkpoints(station_id,payload) VALUES($id,$payload) ON CONFLICT(station_id) DO UPDATE SET payload=excluded.payload";
        command.Parameters.AddWithValue("$id", checkpoint.StationId);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(checkpoint)); command.ExecuteNonQuery();
    }

    private bool WriteEvent(SqliteConnection connection, SqliteTransaction transaction, PlaybackEvent entry)
    {
        var payload = JsonSerializer.Serialize(entry);
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT payload FROM playback_events WHERE event_id=$id";
            existing.Parameters.AddWithValue("$id", entry.EventId);
            if (existing.ExecuteScalar() is string prior)
            {
                if (prior != payload) throw new InvalidOperationException("Event ID was already used with a different payload.");
                return false;
            }
        }
        using var previous = connection.CreateCommand(); previous.Transaction = transaction;
        previous.CommandText = "SELECT entry_hash FROM playback_events ORDER BY sequence DESC LIMIT 1";
        var priorHash = previous.ExecuteScalar() as string ?? "";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(priorHash + "\n" + payload)));
        using var insert = connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO playback_events(event_id,occurrence_id,station_id,event_type,at_utc,payload,previous_hash,entry_hash)
            VALUES($id,$occurrence,$station,$type,$at,$payload,$previous,$hash)
            ON CONFLICT(event_id) DO NOTHING
            """;
        insert.Parameters.AddWithValue("$id", entry.EventId); insert.Parameters.AddWithValue("$occurrence", entry.OccurrenceId);
        insert.Parameters.AddWithValue("$station", entry.StationId); insert.Parameters.AddWithValue("$type", entry.Type);
        insert.Parameters.AddWithValue("$at", entry.AtUtc.ToString("O", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$previous", priorHash);
        insert.Parameters.AddWithValue("$hash", hash);
        var inserted = insert.ExecuteNonQuery() == 1;
        if (inserted) _transactionEvents++;
        return inserted;
    }

    private void WriteLoop()
    {
        try
        {
            // SQLite FULL-WAL commits may block in the OS. One dedicated thread
            // keeps that wait outside the shared pool used by audio pacing and UI commands.
            while (_pending.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                while (_pending.Reader.TryRead(out var work)) work();
        }
        catch (Exception ex)
        {
            _error = "Audit writer stopped: " + ex.GetType().Name;
            _pending.Writer.TryComplete(ex);
            throw;
        }
    }

    public static IReadOnlyList<PlaybackEvent> ReadEvents(string path)
    {
        using var connection = OpenReader(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM playback_events ORDER BY sequence";
        using var reader = command.ExecuteReader();
        var result = new List<PlaybackEvent>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<PlaybackEvent>(reader.GetString(0))!);
        return result;
    }

    public static bool Verify(string path)
    {
        using var connection = OpenReader(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload,previous_hash,entry_hash FROM playback_events ORDER BY sequence";
        using var reader = command.ExecuteReader();
        var previous = "";
        while (reader.Read())
        {
            var computed = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(previous + "\n" + reader.GetString(0))));
            if (previous != reader.GetString(1) || computed != reader.GetString(2)) return false;
            previous = computed;
        }
        return true;
    }

    internal static SqliteConnection OpenReader(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        _pending.Writer.TryComplete();
        try { await _writer; }
        finally
        {
            try { await _connection.DisposeAsync(); }
            finally { _ownership.Dispose(); }
        }
    }
}
