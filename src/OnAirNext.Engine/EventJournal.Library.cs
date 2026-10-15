using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OnAirNext.Engine;

public sealed record LibraryRoot(string StationId, string Path, long Revision);
public sealed record LibraryAsset(string Id, string RelativePath, string Artist, string Title, double Seconds,
    long Bytes, long ModifiedTicks, string State, string? Error = null);
public sealed record LibraryImport(string Id, string StationId, long Revision, string[] Paths, string State, string? Error);
public sealed record LibraryPage(LibraryRoot? Root, long Total, long Ready, long Missing,
    IReadOnlyList<LibraryAsset> Items, int Offset, LibraryImport? Import);

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
            CREATE INDEX IF NOT EXISTS library_asset_page ON library_assets(station_id,revision,title,id);
            CREATE TABLE IF NOT EXISTS library_imports(id TEXT PRIMARY KEY,station_id TEXT NOT NULL,revision INTEGER NOT NULL,
              payload TEXT NOT NULL,state TEXT NOT NULL,error TEXT,created_utc TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery(); return true;
    });

    // Pure path normalization; checking a removable/network drive belongs to the background worker.
    public static string NormalizeLibraryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 500 || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Choose a local, absolute music folder or file path.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new ArgumentException("Choose a local drive for the live music folder.");
        return full;
    }

    public Task<bool> SetLibraryRootAsync(string station, string path, string id)
    {
        path = NormalizeLibraryPath(path);
        return TransactionAsync((c, t) =>
        {
            if (ReadDesired(c, t, station) is null) throw new ArgumentException("Unknown station.");
            if (!AdmitControl(c, t, station, "library-root", path, id)) return false;
            using var cmd = LibraryCommand(c, t, """
                INSERT INTO library_roots VALUES($station,$path,1)
                ON CONFLICT(station_id) DO UPDATE SET path=excluded.path,revision=revision+1
                """, ("$station", station), ("$path", path));
            cmd.ExecuteNonQuery(); return true;
        });
    }

    public Task<bool> QueueLibraryImportAsync(string station, IReadOnlyList<string> paths, string defaultRoot, string id)
    {
        if (paths.Count is < 1 or > 64) throw new ArgumentException("Choose between 1 and 64 files per import.");
        var normalized = paths.Select(NormalizeLibraryPath).ToArray();
        defaultRoot = NormalizeLibraryPath(defaultRoot);
        var payload = JsonSerializer.Serialize(normalized);
        return TransactionAsync((c, t) =>
        {
            if (ReadDesired(c, t, station) is null) throw new ArgumentException("Unknown station.");
            if (!AdmitControl(c, t, station, "library-import", payload, id)) return false;
            using (var queued = LibraryCommand(c, t, "SELECT COUNT(*) FROM library_imports WHERE state='Pending'"))
                if (Convert.ToInt64(queued.ExecuteScalar(), CultureInfo.InvariantCulture) >= 16)
                    throw new InvalidOperationException("The import queue is full. Wait for an import to finish before adding another batch.");
            using (var root = LibraryCommand(c, t, "INSERT OR IGNORE INTO library_roots VALUES($station,$path,1)",
                ("$station", station), ("$path", defaultRoot))) root.ExecuteNonQuery();
            var current = ReadLibraryRoot(c, t, station)!;
            using var job = LibraryCommand(c, t, "INSERT INTO library_imports VALUES($id,$station,$revision,$payload,'Pending',NULL,$at)",
                ("$id", id), ("$station", station), ("$revision", current.Revision), ("$payload", payload),
                ("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
            job.ExecuteNonQuery(); return true;
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
            using var cmd = LibraryCommand(c, t, """
                INSERT INTO library_assets VALUES($station,$revision,$id,$path,$artist,$title,$seconds,$bytes,$modified,$state,$error)
                ON CONFLICT(station_id,revision,id) DO UPDATE SET relative_path=excluded.relative_path,artist=excluded.artist,
                  title=excluded.title,seconds=excluded.seconds,bytes=excluded.bytes,modified_ticks=excluded.modified_ticks,
                  state=CASE WHEN library_assets.state='Retired' THEN 'Retired' ELSE excluded.state END,error=excluded.error
                """, ("$station", root.StationId), ("$revision", root.Revision), ("$id", asset.Id), ("$path", asset.RelativePath),
                ("$artist", asset.Artist), ("$title", asset.Title), ("$seconds", asset.Seconds), ("$bytes", asset.Bytes),
                ("$modified", asset.ModifiedTicks), ("$state", asset.State), ("$error", asset.Error));
            cmd.ExecuteNonQuery();
        }
        foreach (var id in missing)
        {
            using var cmd = LibraryCommand(c, t, "UPDATE library_assets SET state='Missing' WHERE station_id=$station AND revision=$revision AND id=$id AND state<>'Retired'",
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
        using var count = LibraryCommand(c, t, "SELECT COUNT(*),COALESCE(SUM(state='Ready'),0),COALESCE(SUM(state='Missing'),0) FROM library_assets WHERE station_id=$station AND revision=$revision",
            ("$station", station), ("$revision", root.Revision));
        long total, ready, missing;
        using (var r = count.ExecuteReader()) { r.Read(); total = r.GetInt64(0); ready = r.GetInt64(1); missing = r.GetInt64(2); }
        using var rows = LibraryCommand(c, t, """
            SELECT id,relative_path,artist,title,seconds,bytes,modified_ticks,state,error FROM library_assets
            WHERE station_id=$station AND revision=$revision AND
              ($query='' OR instr(lower(title || ' ' || artist || ' ' || relative_path),lower($query))>0)
            ORDER BY title COLLATE NOCASE,id LIMIT 50 OFFSET $offset
            """, ("$station", station), ("$revision", root.Revision), ("$query", query), ("$offset", offset));
        var assets = new List<LibraryAsset>();
        using (var r = rows.ExecuteReader()) while (r.Read()) assets.Add(ReadAsset(r));
        using var job = LibraryCommand(c, t, "SELECT id,station_id,revision,payload,state,error FROM library_imports WHERE station_id=$station ORDER BY created_utc DESC,id DESC LIMIT 1", ("$station", station));
        LibraryImport? import; using (var r = job.ExecuteReader()) import = r.Read() ? ReadImport(r) : null;
        return new(root, total, ready, missing, assets, offset, import);
    }

    public IReadOnlyList<LibraryAsset> LibraryAssets(LibraryRoot root)
    {
        using var c = OpenReader(DatabasePath); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,relative_path,artist,title,seconds,bytes,modified_ticks,state,error FROM library_assets WHERE station_id=$station AND revision=$revision";
        cmd.Parameters.AddWithValue("$station", root.StationId); cmd.Parameters.AddWithValue("$revision", root.Revision);
        using var r = cmd.ExecuteReader(); var assets = new List<LibraryAsset>();
        while (r.Read()) assets.Add(ReadAsset(r)); return assets;
    }

    public static string LibraryAssetId(string relativePath) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relativePath.ToUpperInvariant())));
    private static LibraryAsset ReadAsset(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.GetDouble(4), r.GetInt64(5), r.GetInt64(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8));
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
