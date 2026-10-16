using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OnAirNext.Engine;

public sealed record LibraryRoot(string StationId, string Path, long Revision);
public sealed record LibraryAsset(string Id, string RelativePath, string Artist, string Title, double Seconds,
    long Bytes, long ModifiedTicks, string State, string? Error = null, long EditVersion = 0, bool HasMetadataOverride = false, string? ValidationState = null, SourceLoudness? Loudness = null, bool AnalysisRequested = false)
{
    public SourceGain? Normalization => State == "Ready" ? SourceNormalization.Decide(Loudness) : null;
}
public sealed record LibraryImport(string Id, string StationId, long Revision, string[] Paths, string State, string? Error);
public sealed record LibraryPage(LibraryRoot? Root, long Total, long Ready, long Missing,
    IReadOnlyList<LibraryAsset> Items, int Offset, LibraryImport? Import, long Retired = 0);

public sealed partial class EventJournal
{
    public Task InitializeLibraryAsync() => TransactionAsync((c, t) =>
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = t;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS library_roots(station_id TEXT PRIMARY KEY,path TEXT NOT NULL,revision INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS library_assets(station_id TEXT NOT NULL,revision INTEGER NOT NULL,id TEXT NOT NULL,
              relative_path TEXT NOT NULL,artist TEXT NOT NULL,title TEXT NOT NULL,seconds REAL NOT NULL,
              bytes INTEGER NOT NULL,modified_ticks INTEGER NOT NULL,state TEXT NOT NULL,error TEXT,
              PRIMARY KEY(station_id,revision,id));
            CREATE TABLE IF NOT EXISTS library_loudness_requests(station_id TEXT NOT NULL,revision INTEGER NOT NULL,id TEXT NOT NULL,
              requested INTEGER NOT NULL DEFAULT 1,PRIMARY KEY(station_id,revision,id));
            CREATE TABLE IF NOT EXISTS library_loudness(station_id TEXT NOT NULL,revision INTEGER NOT NULL,id TEXT NOT NULL,
              payload TEXT NOT NULL,payload_hash TEXT NOT NULL,PRIMARY KEY(station_id,revision,id));
            CREATE INDEX IF NOT EXISTS library_asset_page ON library_assets(station_id,revision,title,id);
            CREATE TABLE IF NOT EXISTS library_asset_edits(station_id TEXT NOT NULL,revision INTEGER NOT NULL,id TEXT NOT NULL,
              artist TEXT,title TEXT,retired INTEGER NOT NULL DEFAULT 0,version INTEGER NOT NULL,PRIMARY KEY(station_id,revision,id));
            INSERT OR IGNORE INTO library_asset_edits(station_id,revision,id,retired,version)
              SELECT station_id,revision,id,1,1 FROM library_assets WHERE state='Retired';
            UPDATE library_assets SET state='Invalid',modified_ticks=-1,error='Restored catalog requires validation.' WHERE state='Retired';
            CREATE TABLE IF NOT EXISTS library_uploads(id TEXT PRIMARY KEY,station_id TEXT NOT NULL,revision INTEGER NOT NULL,payload TEXT NOT NULL,state TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS library_imports(id TEXT PRIMARY KEY,station_id TEXT NOT NULL,revision INTEGER NOT NULL,
              payload TEXT NOT NULL,state TEXT NOT NULL,error TEXT,created_utc TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery(); return true;
    });

    // Pure path normalization; checking a removable/network drive belongs to the background worker.
    public static string NormalizeLibraryPath(string path) => LocalMediaPath.Normalize(path);

    public Task<bool> SetLibraryRootAsync(string station, string path, string id, Action? authorizeNewAssignment = null)
    {
        path = NormalizeLibraryPath(path);
        if (Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!).Equals(path, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a station music subfolder, rather than a whole drive.");
        return TransactionAsync((c, t) =>
        {
            if (ReadDesired(c, t, station) is null) throw new ArgumentException("Unknown station.");
            if (!AdmitControl(c, t, station, "library-root", path, id)) return false;
            authorizeNewAssignment?.Invoke(); // Fast in-memory gate only; durable retries do not need a fresh transient proof.
            using var cmd = LibraryCommand(c, t, """
                INSERT INTO library_roots VALUES($station,$path,1)
                ON CONFLICT(station_id) DO UPDATE SET path=excluded.path,revision=revision+1
                """, ("$station", station), ("$path", path));
            cmd.ExecuteNonQuery(); return true;
        });
    }

    public Task<bool> QueueLibraryImportAsync(string station, IReadOnlyList<string> paths, string defaultRoot, string id, bool uploaded = false)
    {
        if (paths.Count is < 1 or > 64) throw new ArgumentException("Choose between 1 and 64 files per import.");
        var normalized = paths.Select(NormalizeLibraryPath).ToArray();
        defaultRoot = NormalizeLibraryPath(defaultRoot);
        var payload = JsonSerializer.Serialize(normalized);
        return TransactionAsync((c, t) =>
        {
            if (ReadDesired(c, t, station) is null) throw new ArgumentException("Unknown station.");
            if (!AdmitControl(c, t, station, "library-import", payload, id)) return false;
            if (uploaded)
            {
                var batch = ReadUpload(c,t,id) ?? throw new InvalidOperationException("Upload is unavailable.");
                if (batch.StationId != station || batch.State != "Receiving" || (ReadLibraryRoot(c,t,station)?.Revision ?? 0) != batch.Revision)
                    throw new InvalidOperationException("The upload or live folder changed. Start a new import for the current folder.");
            }
            using (var queued = LibraryCommand(c, t, "SELECT COUNT(*) FROM library_imports WHERE state='Pending'"))
                if (Convert.ToInt64(queued.ExecuteScalar(), CultureInfo.InvariantCulture) >= 16)
                    throw new InvalidOperationException("The import queue is full. Wait for an import to finish before adding another batch.");
            using (var root = LibraryCommand(c, t, "INSERT OR IGNORE INTO library_roots VALUES($station,$path,1)",
                ("$station", station), ("$path", defaultRoot))) root.ExecuteNonQuery();
            var current = ReadLibraryRoot(c, t, station)!;
            using var job = LibraryCommand(c, t, "INSERT INTO library_imports VALUES($id,$station,$revision,$payload,'Pending',NULL,$at)",
                ("$id", id), ("$station", station), ("$revision", current.Revision), ("$payload", payload),
                ("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
            job.ExecuteNonQuery();
            if (uploaded) { using var mark = LibraryCommand(c,t,"UPDATE library_uploads SET state='Queued' WHERE id=$id",("$id",id));mark.ExecuteNonQuery(); }
            return true;
        });
    }

    public IReadOnlyList<LibraryRoot> LibraryRoots()
    {
        using var c = OpenReader(DatabasePath); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT station_id,path,revision FROM library_roots ORDER BY station_id";
        using var r = cmd.ExecuteReader(); var rows = new List<LibraryRoot>();
        while (r.Read()) rows.Add(new(r.GetString(0), r.GetString(1), r.GetInt64(2)));
        return rows;
    }

    public LibraryImport? PendingLibraryImport()
    {
        using var c = OpenReader(DatabasePath); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,station_id,revision,payload,state,error FROM library_imports WHERE state='Pending' ORDER BY created_utc,id LIMIT 1";
        using var r = cmd.ExecuteReader(); return r.Read() ? ReadImport(r) : null;
    }

    public Task FinishLibraryImportAsync(string id, string? error) => TransactionAsync((c, t) =>
    {
        using var cmd = LibraryCommand(c, t, "UPDATE library_imports SET state=$state,error=$error WHERE id=$id AND state='Pending'",
            ("$id", id), ("$state", error is null ? "Completed" : "Failed"), ("$error", error));
        cmd.ExecuteNonQuery(); return true;
    });

    public Task<bool> RequestLibraryRescanAsync(string station, string id) => TransactionAsync((c, t) =>
    {
        if (!AdmitControl(c, t, station, "library-rescan", "", id)) return false;
        var root = ReadLibraryRoot(c, t, station) ?? throw new InvalidOperationException("Choose a music folder before requesting a rescan.");
        using var cmd = LibraryCommand(c, t, "UPDATE library_assets SET modified_ticks=-1,error='Validation requested.' WHERE station_id=$station AND revision=$revision AND state='Invalid'",
            ("$station", station), ("$revision", root.Revision));
        cmd.ExecuteNonQuery(); return true;
    });

    public Task<bool> ApplyLibraryChangesAsync(LibraryRoot root, IReadOnlyList<LibraryAsset> updated,
        IReadOnlyList<string> missing) => TransactionAsync((c, t) =>
    {
        if (updated.Count > 100 || missing.Count > 100) throw new ArgumentException("Library changes must use bounded batches.");
        if (ReadLibraryRoot(c, t, root.StationId) != root) return false; // stale scan may not change a replacement root
        foreach (var asset in updated)
        {
            if (Path.IsPathRooted(asset.RelativePath) || asset.RelativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }).Any(x => x is ".." or ".") ||
                string.IsNullOrWhiteSpace(asset.RelativePath) || asset.State is not ("Ready" or "Invalid") ||
                !double.IsFinite(asset.Seconds) || asset.Seconds < 0 || asset.State == "Ready" && asset.Seconds == 0)
                throw new ArgumentException("Invalid indexed music asset.");
            if (asset.Loudness is { } measurement)
            {
                measurement.Validate();
                if (measurement.Bytes != asset.Bytes || measurement.ModifiedTicks != asset.ModifiedTicks || measurement.Seconds != asset.Seconds)
                    throw new ArgumentException("Loudness measurement does not belong to this source revision.");
            }
            using var cmd = LibraryCommand(c, t, """
                INSERT INTO library_assets VALUES($station,$revision,$id,$path,$artist,$title,$seconds,$bytes,$modified,$state,$error)
                ON CONFLICT(station_id,revision,id) DO UPDATE SET relative_path=excluded.relative_path,artist=excluded.artist,
                  title=excluded.title,seconds=excluded.seconds,bytes=excluded.bytes,modified_ticks=excluded.modified_ticks,
                  state=excluded.state,error=excluded.error
                """, ("$station", root.StationId), ("$revision", root.Revision), ("$id", asset.Id), ("$path", asset.RelativePath),
                ("$artist", asset.Artist), ("$title", asset.Title), ("$seconds", asset.Seconds), ("$bytes", asset.Bytes),
                ("$modified", asset.ModifiedTicks), ("$state", asset.State), ("$error", asset.Error));
            cmd.ExecuteNonQuery();
            using var finishAnalysis = LibraryCommand(c, t, "DELETE FROM library_loudness_requests WHERE station_id=$station AND revision=$revision AND id=$id",
                ("$station", root.StationId), ("$revision", root.Revision), ("$id", asset.Id));
            finishAnalysis.ExecuteNonQuery();
            using var clearMeasurement = LibraryCommand(c, t, "DELETE FROM library_loudness WHERE station_id=$station AND revision=$revision AND id=$id",
                ("$station", root.StationId), ("$revision", root.Revision), ("$id", asset.Id));
            clearMeasurement.ExecuteNonQuery();
            if (asset.Loudness is not null)
            {
                var payload = JsonSerializer.Serialize(asset.Loudness);
                using var measured = LibraryCommand(c, t, "INSERT INTO library_loudness VALUES($station,$revision,$id,$payload,$hash)",
                    ("$station", root.StationId), ("$revision", root.Revision), ("$id", asset.Id), ("$payload", payload),
                    ("$hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))));
                measured.ExecuteNonQuery();
            }
        }
        foreach (var id in missing)
        {
            using var cmd = LibraryCommand(c, t, "UPDATE library_assets SET state='Missing' WHERE station_id=$station AND revision=$revision AND id=$id",
                ("$station", root.StationId), ("$revision", root.Revision), ("$id", id));
            cmd.ExecuteNonQuery();
        }
        return true;
    });

    public LibraryPage LibraryPage(string station, string query = "", int offset = 0)
    {
        if (query.Length > 200 || offset is < 0 or > 10000000) throw new ArgumentException("Invalid library page.");
        using var c = OpenReader(DatabasePath); using var t = c.BeginTransaction(deferred: true);
        var root = ReadLibraryRoot(c, t, station);
        if (root is null) return new(null, 0, 0, 0, [], offset, null);
        using var count = LibraryCommand(c, t, "SELECT COUNT(*),COALESCE(SUM(a.state='Ready' AND COALESCE(e.retired,0)=0),0),COALESCE(SUM(a.state='Missing' AND COALESCE(e.retired,0)=0),0),COALESCE(SUM(e.retired=1),0) " + AssetJoin + " WHERE a.station_id=$station AND a.revision=$revision",
            ("$station", station), ("$revision", root.Revision));
        long total, ready, missing, retired;
        using (var r = count.ExecuteReader()) { r.Read(); total = r.GetInt64(0); ready = r.GetInt64(1); missing = r.GetInt64(2); retired = r.GetInt64(3); }
        using var rows = LibraryCommand(c, t, AssetSelect + AssetJoin + """
            WHERE a.station_id=$station AND a.revision=$revision AND
              ($query='' OR instr(lower(COALESCE(e.title,a.title) || ' ' || COALESCE(e.artist,a.artist) || ' ' || a.relative_path),lower($query))>0)
            ORDER BY COALESCE(e.title,a.title) COLLATE NOCASE,a.id LIMIT 50 OFFSET $offset
            """, ("$station", station), ("$revision", root.Revision), ("$query", query), ("$offset", offset));
        var assets = new List<LibraryAsset>();
        using (var r = rows.ExecuteReader()) while (r.Read()) assets.Add(ReadAsset(r));
        using var job = LibraryCommand(c, t, "SELECT id,station_id,revision,payload,state,error FROM library_imports WHERE station_id=$station ORDER BY created_utc DESC,id DESC LIMIT 1", ("$station", station));
        LibraryImport? import; using (var r = job.ExecuteReader()) import = r.Read() ? ReadImport(r) : null;
        return new(root, total, ready, missing, assets, offset, import, retired);
    }

    public IReadOnlyList<LibraryAsset> LibraryAssets(LibraryRoot root, bool raw = false)
    {
        using var c = OpenReader(DatabasePath); using var cmd = c.CreateCommand();
        cmd.CommandText = raw
            ? "SELECT a.id,a.relative_path,a.artist,a.title,a.seconds,a.bytes,a.modified_ticks,a.state,a.error,0,0,a.state,l.payload,l.payload_hash,COALESCE(q.requested,0) FROM library_assets a " + LoudnessJoin + " WHERE a.station_id=$station AND a.revision=$revision"
            : AssetSelect + AssetJoin + " WHERE a.station_id=$station AND a.revision=$revision";
        cmd.Parameters.AddWithValue("$station", root.StationId); cmd.Parameters.AddWithValue("$revision", root.Revision);
        using var r = cmd.ExecuteReader(); var assets = new List<LibraryAsset>();
        while (r.Read()) assets.Add(ReadAsset(r)); return assets;
    }

    private const string AssetSelect = """
        SELECT a.id,a.relative_path,COALESCE(e.artist,a.artist),COALESCE(e.title,a.title),a.seconds,a.bytes,a.modified_ticks,
          CASE WHEN e.retired=1 THEN 'Retired' ELSE a.state END,a.error,COALESCE(e.version,0),
          (e.artist IS NOT NULL OR e.title IS NOT NULL),a.state,l.payload,l.payload_hash,COALESCE(q.requested,0)
        """;
    private const string LoudnessJoin = " LEFT JOIN library_loudness l ON l.station_id=a.station_id AND l.revision=a.revision AND l.id=a.id LEFT JOIN library_loudness_requests q ON q.station_id=a.station_id AND q.revision=a.revision AND q.id=a.id ";
    private const string AssetJoin = " FROM library_assets a LEFT JOIN library_asset_edits e ON e.station_id=a.station_id AND e.revision=a.revision AND e.id=a.id " + LoudnessJoin;

    public static string LibraryAssetId(string relativePath) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relativePath.ToUpperInvariant())));
    private static LibraryAsset ReadAsset(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.GetDouble(4), r.GetInt64(5), r.GetInt64(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
        r.FieldCount > 9 ? r.GetInt64(9) : 0, r.FieldCount > 10 && r.GetBoolean(10), r.FieldCount > 11 ? r.GetString(11) : r.GetString(7), ReadLoudness(r), r.FieldCount > 14 && r.GetBoolean(14));
    private static SourceLoudness? ReadLoudness(SqliteDataReader r)
    {
        if (r.FieldCount < 14 || r.IsDBNull(12) || r.GetString(11) != "Ready") return null;
        var payload = r.GetString(12);
        if (r.IsDBNull(13) || Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))) != r.GetString(13))
            throw new InvalidDataException("Stored source measurement integrity check failed; revalidate the affected source.");
        var result = JsonSerializer.Deserialize<SourceLoudness>(payload) ?? throw new InvalidDataException("Stored source measurement is missing.");
        result.Validate();
        if (result.Bytes != r.GetInt64(5) || result.ModifiedTicks != r.GetInt64(6) || result.Seconds != r.GetDouble(4))
            throw new InvalidDataException("Stored source measurement does not match this file revision.");
        return result;
    }
    private static LibraryImport ReadImport(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetInt64(2),
        JsonSerializer.Deserialize<string[]>(r.GetString(3))!, r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5));
    private static LibraryRoot? ReadLibraryRoot(SqliteConnection c, SqliteTransaction t, string station)
    {
        using var cmd = LibraryCommand(c, t, "SELECT path,revision FROM library_roots WHERE station_id=$station", ("$station", station));
        using var r = cmd.ExecuteReader(); return r.Read() ? new(station, r.GetString(0), r.GetInt64(1)) : null;
    }
    private static SqliteCommand LibraryCommand(SqliteConnection c, SqliteTransaction t, string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
}
