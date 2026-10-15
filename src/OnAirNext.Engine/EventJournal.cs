using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace OnAirNext.Engine;

// SQLite writes and reports have a dedicated lane; the program never waits for a CSV or network upload.
public sealed class EventJournal : IAsyncDisposable
{
    private readonly Channel<PlaybackEvent> _pending = Channel.CreateBounded<PlaybackEvent>(4096);
    private readonly SqliteConnection _connection;
    private readonly Task _writer;
    private long _rejected, _committed;
    private string? _error;
    public string DatabasePath { get; }
    public long RejectedEvents => Interlocked.Read(ref _rejected);
    public long CommittedEvents => Interlocked.Read(ref _committed);
    public int PendingEvents => _pending.Reader.Count;
    public string? Error => _error;

    public EventJournal(string path)
    {
        DatabasePath = Path.GetFullPath(path);
        var localRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadioTEDU", "OnAirNext-Dev");
        if (!DatabasePath.StartsWith(localRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Development databases must be under the local application data directory.");
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        _connection.Open();
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
            """;
        command.ExecuteNonQuery();
        using var version = _connection.CreateCommand();
        version.CommandText = "SELECT version FROM schema_info";
        if (Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
        {
            _connection.Dispose();
            throw new InvalidDataException("Unsupported development journal schema; explicit migration is required.");
        }
        _writer = Task.Run(WriteAsync);
    }

    public bool TryAppend(PlaybackEvent entry)
    {
        if (_pending.Writer.TryWrite(entry)) return true;
        Interlocked.Increment(ref _rejected);
        return false;
    }

    private async Task WriteAsync()
    {
        try
        {
            await foreach (var entry in _pending.Reader.ReadAllAsync())
            {
                using var transaction = _connection.BeginTransaction();
                using var previous = _connection.CreateCommand();
                previous.Transaction = transaction;
                previous.CommandText = "SELECT entry_hash FROM playback_events ORDER BY sequence DESC LIMIT 1";
                var priorHash = previous.ExecuteScalar() as string ?? "";
                var payload = JsonSerializer.Serialize(entry);
                var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(priorHash + "\n" + payload)));
                using var insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO playback_events(event_id,occurrence_id,station_id,event_type,at_utc,payload,previous_hash,entry_hash)
                    VALUES($id,$occurrence,$station,$type,$at,$payload,$previous,$hash)
                    ON CONFLICT(event_id) DO NOTHING
                    """;
                insert.Parameters.AddWithValue("$id", entry.EventId);
                insert.Parameters.AddWithValue("$occurrence", entry.OccurrenceId);
                insert.Parameters.AddWithValue("$station", entry.StationId);
                insert.Parameters.AddWithValue("$type", entry.Type);
                insert.Parameters.AddWithValue("$at", entry.AtUtc.ToString("O", CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$payload", payload);
                insert.Parameters.AddWithValue("$previous", priorHash);
                insert.Parameters.AddWithValue("$hash", hash);
                var inserted = insert.ExecuteNonQuery();
                transaction.Commit();
                Interlocked.Add(ref _committed, inserted);
            }
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
        finally { await _connection.DisposeAsync(); }
    }
}
