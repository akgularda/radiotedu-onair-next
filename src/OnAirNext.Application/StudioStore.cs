using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OnAirNext.Engine;

namespace OnAirNext.Application;

public sealed class StudioStore : IPlayoutPlanner
{
    private readonly EventJournal _journal;
    public string Root { get; }
    public StudioStore(EventJournal journal, string root)
    {
        _journal = journal; Root = root;
        Execute((c, t) =>
        {
            Sql(c, t, """
                CREATE TABLE IF NOT EXISTS studio_assets(id TEXT PRIMARY KEY,hash TEXT NOT NULL UNIQUE,path TEXT NOT NULL,artist TEXT NOT NULL,title TEXT NOT NULL,kind TEXT NOT NULL,seconds REAL NOT NULL,retired INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS studio_playlists(id TEXT PRIMARY KEY,name TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS studio_playlist_items(playlist_id TEXT NOT NULL,asset_id TEXT NOT NULL,position INTEGER NOT NULL,PRIMARY KEY(playlist_id,asset_id));
                CREATE TABLE IF NOT EXISTS studio_settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS studio_queue(id TEXT PRIMARY KEY,asset_id TEXT NOT NULL,position INTEGER NOT NULL,state TEXT NOT NULL DEFAULT 'pending',origin TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS studio_schedules(id TEXT PRIMARY KEY,asset_id TEXT NOT NULL,at_utc TEXT NOT NULL,mode TEXT NOT NULL,repeat_rule TEXT NOT NULL,state TEXT NOT NULL DEFAULT 'scheduled');
                CREATE TABLE IF NOT EXISTS studio_receipts(id TEXT PRIMARY KEY,fingerprint TEXT NOT NULL,result TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS studio_queue_order ON studio_queue(state,position);
                CREATE INDEX IF NOT EXISTS studio_schedule_due ON studio_schedules(state,at_utc);
                """);
            return true;
        });
    }

    public T Execute<T>(Func<SqliteConnection, SqliteTransaction, T> action) => _journal.TransactionAsync(action).GetAwaiter().GetResult();
    public static void Sql(SqliteConnection c, SqliteTransaction t, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(c, t, sql, parameters); command.ExecuteNonQuery(); }
    public static SqliteCommand Command(SqliteConnection c, SqliteTransaction t, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = c.CreateCommand(); command.Transaction = t; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    public string Get(string key, string fallback = "") => Execute((c, t) => Get(c, t, key, fallback));
    private static string Get(SqliteConnection c, SqliteTransaction t, string key, string fallback)
    { using var command = Command(c, t, "SELECT value FROM studio_settings WHERE key=$key", ("$key", key)); return command.ExecuteScalar() as string ?? fallback; }
    public void Set(string key, string value) => Execute((c, t) => { Set(c, t, key, value); return true; });
    private static void Set(SqliteConnection c, SqliteTransaction t, string key, string value)
        => Sql(c, t, "INSERT INTO studio_settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$key", key), ("$value", value));

    public string Once(string commandId, string fingerprint, Func<SqliteConnection, SqliteTransaction, object> mutation) => Execute((c, t) =>
    {
        using (var prior = Command(c, t, "SELECT fingerprint,result FROM studio_receipts WHERE id=$id", ("$id", commandId)))
        using (var reader = prior.ExecuteReader())
            if (reader.Read())
            {
                if (reader.GetString(0) != fingerprint) throw new InvalidOperationException("This command ID belongs to a different operation.");
                return reader.GetString(1);
            }
        var result = JsonSerializer.Serialize(mutation(c, t), StudioJson.Options);
        Sql(c, t, "INSERT INTO studio_receipts(id,fingerprint,result) VALUES($id,$fingerprint,$result)", ("$id", commandId), ("$fingerprint", fingerprint), ("$result", result));
        return result;
    });

    public Asset ImportMetadata(string path, string hash, string artist, string title, string kind, double seconds) => Execute((c, t) =>
    {
        if (seconds <= 0 || !double.IsFinite(seconds)) throw new ArgumentException("Audio must have a valid duration.");
        if (!Enum.TryParse<ContentKind>(kind, out _)) throw new ArgumentException("Invalid content category.");
        using var existing = Command(c, t, "SELECT id FROM studio_assets WHERE hash=$hash", ("$hash", hash));
        var id = existing.ExecuteScalar() as string ?? Guid.NewGuid().ToString("N");
        Sql(c, t, "INSERT INTO studio_assets(id,hash,path,artist,title,kind,seconds) VALUES($id,$hash,$path,$artist,$title,$kind,$seconds) ON CONFLICT(hash) DO UPDATE SET retired=0",
            ("$id", id), ("$hash", hash), ("$path", path), ("$artist", artist), ("$title", title), ("$kind", kind), ("$seconds", seconds));
        return new Asset(id, artist, title, kind, seconds, false, Path.GetFileName(path));
    });

    public IReadOnlyList<Asset> Assets(bool includeRetired = false) => Execute((c, t) =>
    {
        using var cmd = Command(c, t, "SELECT id,artist,title,kind,seconds,retired,path FROM studio_assets WHERE ($all=1 OR retired=0) ORDER BY artist COLLATE NOCASE,title COLLATE NOCASE LIMIT 5000", ("$all", includeRetired ? 1 : 0));
        using var reader = cmd.ExecuteReader(); var rows = new List<Asset>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDouble(4), reader.GetInt64(5) == 1, Path.GetFileName(reader.GetString(6))));
        return (IReadOnlyList<Asset>)rows;
    });

    public IReadOnlyList<QueueEntry> Queue() => Execute((c, t) =>
    {
        using var cmd = Command(c, t, "SELECT q.id,a.id,a.artist,a.title,a.kind,a.seconds,q.state,q.position,q.origin FROM studio_queue q JOIN studio_assets a ON a.id=q.asset_id WHERE q.state IN ('pending','playing') ORDER BY CASE q.state WHEN 'playing' THEN 0 ELSE 1 END,q.position");
        using var reader = cmd.ExecuteReader(); var rows = new List<QueueEntry>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetDouble(5), reader.GetString(6), reader.GetInt64(7), reader.GetString(8)));
        return (IReadOnlyList<QueueEntry>)rows;
    });

    public IReadOnlyList<Playlist> Playlists() => Execute((c, t) =>
    {
        using var cmd = Command(c, t, "SELECT p.id,p.name,COUNT(i.asset_id) FROM studio_playlists p LEFT JOIN studio_playlist_items i ON i.playlist_id=p.id GROUP BY p.id,p.name ORDER BY p.name");
        using var reader = cmd.ExecuteReader(); var rows = new List<Playlist>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        return (IReadOnlyList<Playlist>)rows;
    });

    public IReadOnlyList<ScheduleEntry> Schedules() => Execute((c, t) =>
    {
        using var cmd = Command(c, t, "SELECT s.id,a.id,a.artist,a.title,s.at_utc,s.mode,s.repeat_rule,s.state FROM studio_schedules s JOIN studio_assets a ON a.id=s.asset_id WHERE s.state<>'cancelled' ORDER BY s.at_utc LIMIT 1000");
        using var reader = cmd.ExecuteReader(); var rows = new List<ScheduleEntry>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), PlayReports.Istanbul).ToString("dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        return (IReadOnlyList<ScheduleEntry>)rows;
    });

    public PlayoutSelection Select(int rotationIndex, string? excludeQueueEntryId = null)
        => SelectPrepared(rotationIndex,excludeQueueEntryId is null?new HashSet<string>():new HashSet<string>{excludeQueueEntryId})
           ?? throw new InvalidOperationException("Import songs or choose a playlist before starting automation.");

    public PlayoutSelection? SelectPrepared(int rotationIndex,IReadOnlySet<string> excludedQueueEntries) => Execute((c, t) =>
    {
        using var pending = Command(c, t, "SELECT a.id,a.path,a.artist,a.title,a.kind,q.id FROM studio_queue q JOIN studio_assets a ON a.id=q.asset_id WHERE q.state='pending' AND a.retired=0 ORDER BY q.position");
        using (var reader = pending.ExecuteReader()) while (reader.Read()) if(!excludedQueueEntries.Contains(reader.GetString(5))) return Selection(reader);
        var playlist = Get(c, t, "playlist", "");
        using var rotation = Command(c, t, "SELECT a.id,a.path,a.artist,a.title,a.kind,NULL FROM studio_assets a LEFT JOIN studio_playlist_items i ON a.id=i.asset_id AND i.playlist_id=$playlist WHERE a.retired=0 AND (($playlist<>'' AND i.asset_id IS NOT NULL) OR ($playlist='' AND a.kind='Music')) ORDER BY COALESCE(i.position,0),a.title,a.id", ("$playlist", playlist));
        using var rows = rotation.ExecuteReader(); var items = new List<PlayoutSelection>();
        while (rows.Read()) items.Add(Selection(rows));
        if (items.Count == 0) return (PlayoutSelection?)null;
        return items[rotationIndex % items.Count];
    });

    private static PlayoutSelection Selection(SqliteDataReader r) => new(new MediaItem(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Enum.Parse<ContentKind>(r.GetString(4))), r.IsDBNull(5) ? null : r.GetString(5));

    public OutputSettings Output()
    {
        var password = Get("output.password");
        return new("studio", Get("output.host", "127.0.0.1"), int.Parse(Get("output.port", "18980"), CultureInfo.InvariantCulture),
            Get("output.mount", "/next-studio"), password == "" ? Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(password), null, DataProtectionScope.CurrentUser)),
            Enum.Parse<CodecProfile>(Get("output.profile", "AacLc192")));
    }

    public void SaveOutput(OutputSettings settings)
    {
        settings.Validate(); Execute((c, t) =>
        {
            Set(c, t, "output.host", settings.Host); Set(c, t, "output.port", settings.Port.ToString(CultureInfo.InvariantCulture));
            Set(c, t, "output.mount", settings.Mount); Set(c, t, "output.profile", settings.Profile.ToString());
            Set(c, t, "output.password", Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(settings.SourcePassword), null, DataProtectionScope.CurrentUser)));
            return true;
        });
    }

    public bool ClaimDue(DateTimeOffset now) => Execute((c, t) =>
    {
        var due = new List<(string Id, string Asset, string Mode, string Rule, DateTimeOffset At)>();
        using (var cmd = Command(c, t, "SELECT id,asset_id,mode,repeat_rule,at_utc FROM studio_schedules WHERE state='scheduled' AND at_utc<=$now ORDER BY at_utc", ("$now", now.ToUniversalTime().ToString("O"))))
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) due.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
        var interrupt = false;
        foreach (var row in due)
        {
            var occurrenceId = "schedule-" + row.Id + "-" + row.At.UtcTicks;
            Sql(c, t, "INSERT OR IGNORE INTO studio_queue(id,asset_id,position,origin) VALUES($id,$asset,$position,'schedule')", ("$id", occurrenceId), ("$asset", row.Asset), ("$position", row.Mode == "Exact time" ? -now.UtcTicks : now.UtcTicks));
            if (row.Rule == "Daily")
            {
                var next = row.At; do { next = next.AddDays(1); } while (next <= now);
                Sql(c, t, "UPDATE studio_schedules SET at_utc=$next WHERE id=$id", ("$next", next.ToUniversalTime().ToString("O")), ("$id", row.Id));
            }
            else Sql(c, t, "UPDATE studio_schedules SET state='queued' WHERE id=$id", ("$id", row.Id));
            interrupt |= row.Mode == "Exact time";
        }
        return interrupt;
    });
}
