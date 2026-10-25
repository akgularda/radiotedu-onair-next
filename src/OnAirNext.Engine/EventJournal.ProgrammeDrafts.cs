using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OnAirNext.Engine;

public sealed partial class EventJournal
{
    public Task InitializeProgrammeDraftsAsync() => TransactionAsync((c, t) =>
    {
        using var command = ProgrammeDraftCommand(c, t, """
            CREATE TABLE IF NOT EXISTS programme_draft_heads(id TEXT PRIMARY KEY,version INTEGER NOT NULL,archived INTEGER NOT NULL,updated_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS programme_draft_versions(id TEXT NOT NULL,version INTEGER NOT NULL,payload TEXT NOT NULL,previous_hash TEXT NOT NULL,entry_hash TEXT NOT NULL,PRIMARY KEY(id,version));
            CREATE TABLE IF NOT EXISTS programme_draft_stations(id TEXT NOT NULL,station TEXT NOT NULL,PRIMARY KEY(id,station));
            CREATE INDEX IF NOT EXISTS programme_draft_station ON programme_draft_stations(station,id);
            CREATE TABLE IF NOT EXISTS programme_draft_commands(id TEXT PRIMARY KEY,request TEXT NOT NULL,draft_id TEXT NOT NULL,version INTEGER NOT NULL);
            CREATE TRIGGER IF NOT EXISTS programme_draft_version_no_update BEFORE UPDATE ON programme_draft_versions BEGIN SELECT RAISE(ABORT,'Programme draft history is immutable'); END;
            CREATE TRIGGER IF NOT EXISTS programme_draft_version_no_delete BEFORE DELETE ON programme_draft_versions BEGIN SELECT RAISE(ABORT,'Programme draft history is immutable'); END;
            CREATE TRIGGER IF NOT EXISTS programme_draft_command_no_update BEFORE UPDATE ON programme_draft_commands BEGIN SELECT RAISE(ABORT,'Programme draft receipts are immutable'); END;
            CREATE TRIGGER IF NOT EXISTS programme_draft_command_no_delete BEFORE DELETE ON programme_draft_commands BEGIN SELECT RAISE(ABORT,'Programme draft receipts are immutable'); END;
            """);
        command.ExecuteNonQuery(); return true;
    });

    public Task<ProgrammeDraftRevision> SaveProgrammeDraftAsync(ProgrammeDraft proposed, long expectedVersion,
        string commandId, string actor, IReadOnlySet<string> installedStations)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        var known = installedStations.ToHashSet(StringComparer.Ordinal);
        var draft = proposed.Normalize(known);
        var id = ProgrammeCommandId(commandId); RequireProgrammeActor(actor);
        if (expectedVersion < 0 || expectedVersion == long.MaxValue || draft.Rule.Version != expectedVersion + 1)
            throw new ArgumentException("Review the current programme version before saving its next revision.");
        var request = JsonSerializer.Serialize(new { Action = "save", Draft = draft, ExpectedVersion = expectedVersion, Actor = actor });
        return TransactionAsync((c, t) =>
        {
            if (ProgrammeReceipt(c, t, id, request, known) is { } prior) return prior;
            var current = ProgrammeHead(c, t, draft.Rule.Id, known);
            if ((current?.Version ?? 0) != expectedVersion) throw new InvalidOperationException("This programme changed. Reload its saved version before editing again.");
            if (current?.Archived == true) throw new InvalidOperationException("Restore the archived programme explicitly before editing it.");
            return WriteProgrammeRevision(c, t, new(draft, false, actor, DateTimeOffset.UtcNow, "Saved"), current, id, request, known);
        });
    }

    public Task<ProgrammeDraftRevision> ArchiveProgrammeDraftAsync(string draftId, long expectedVersion,
        string commandId, string actor, IReadOnlySet<string> installedStations)
        => ChangeProgrammeDraftAsync(draftId, expectedVersion, null, commandId, actor, installedStations);

    public Task<ProgrammeDraftRevision> RestoreProgrammeDraftAsync(string draftId, long expectedVersion, long sourceVersion,
        string commandId, string actor, IReadOnlySet<string> installedStations)
        => ChangeProgrammeDraftAsync(draftId, expectedVersion, sourceVersion, commandId, actor, installedStations);

    private Task<ProgrammeDraftRevision> ChangeProgrammeDraftAsync(string draftId, long expectedVersion, long? sourceVersion,
        string commandId, string actor, IReadOnlySet<string> installedStations)
    {
        var draftKey = ProgrammeId(draftId); var id = ProgrammeCommandId(commandId); RequireProgrammeActor(actor);
        var known = installedStations.ToHashSet(StringComparer.Ordinal);
        if (expectedVersion is < 1 or long.MaxValue || sourceVersion is < 1)
            throw new ArgumentException("Choose a reviewed programme version.");
        var request = JsonSerializer.Serialize(new { Action = sourceVersion is null ? "archive" : "restore", DraftId = draftKey,
            ExpectedVersion = expectedVersion, SourceVersion = sourceVersion, Actor = actor });
        return TransactionAsync((c, t) =>
        {
            if (ProgrammeReceipt(c, t, id, request, known) is { } prior) return prior;
            var current = ProgrammeHead(c, t, draftKey, known) ?? throw new InvalidOperationException("This programme is unavailable.");
            if (current.Version != expectedVersion) throw new InvalidOperationException("This programme changed. Reload before changing its saved state.");
            if (sourceVersion is null && current.Archived) throw new InvalidOperationException("This programme is already archived.");
            var source = sourceVersion is { } version ? ReadProgrammeRevision(c, t, draftKey, version, known) : current;
            var next = source.Draft with { Rule = source.Draft.Rule with { Version = checked(expectedVersion + 1), Enabled = false } };
            return WriteProgrammeRevision(c, t, new(next, sourceVersion is null, actor, DateTimeOffset.UtcNow,
                sourceVersion is null ? "Archived" : "Restored as draft", sourceVersion), current, id, request, known);
        });
    }

    public Task<ProgrammeDraftPage> ProgrammeDraftsAsync(IReadOnlySet<string> installedStations, string? station = null,
        bool archived = false, int offset = 0)
    {
        var known = installedStations.ToHashSet(StringComparer.Ordinal); RequireProgrammePage(known, station, offset);
        return TransactionAsync((c, t) =>
        {
            var where = "h.archived=$archived AND ($station='' OR EXISTS(SELECT 1 FROM programme_draft_stations s WHERE s.id=h.id AND s.station=$station))";
            using var count = ProgrammeDraftCommand(c, t, "SELECT COUNT(*) FROM programme_draft_heads h WHERE " + where,
                ("$archived", archived ? 1 : 0), ("$station", station ?? ""));
            var total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
            using var query = ProgrammeDraftCommand(c, t, "SELECT h.id,h.version FROM programme_draft_heads h WHERE " + where +
                " ORDER BY h.updated_utc DESC,h.id LIMIT 4 OFFSET $offset", ("$archived", archived ? 1 : 0),
                ("$station", station ?? ""), ("$offset", offset));
            var keys = new List<(string Id, long Version)>();
            using (var reader = query.ExecuteReader()) while (reader.Read()) keys.Add((reader.GetString(0), reader.GetInt64(1)));
            return new ProgrammeDraftPage(keys.Select(x => ReadProgrammeRevision(c, t, x.Id, x.Version, known)).ToArray(), total, offset);
        });
    }

    public Task<ProgrammeDraftHistory> ProgrammeDraftHistoryAsync(string draftId, long expectedVersion,
        IReadOnlySet<string> installedStations, int offset = 0)
    {
        var key = ProgrammeId(draftId); var known = installedStations.ToHashSet(StringComparer.Ordinal);
        RequireProgrammePage(known, null, offset);
        return TransactionAsync((c, t) =>
        {
            var head = ProgrammeHead(c, t, key, known) ?? throw new InvalidOperationException("This programme is unavailable.");
            if (head.Version != expectedVersion) throw new InvalidOperationException("The programme changed. Reload its history before continuing.");
            using var query = ProgrammeDraftCommand(c, t, "SELECT version FROM programme_draft_versions WHERE id=$id ORDER BY version DESC LIMIT 4 OFFSET $offset",
                ("$id", key), ("$offset", offset));
            var versions = new List<long>(); using (var reader = query.ExecuteReader()) while (reader.Read()) versions.Add(reader.GetInt64(0));
            return new ProgrammeDraftHistory(key, head.Version, versions.Select(x => ReadProgrammeRevision(c, t, key, x, known)).ToArray(),
                checked((int)head.Version), offset);
        });
    }

    public Task<ProgrammeForecast> PreviewProgrammeDraftsAsync(IReadOnlySet<string> installedStations, DateTimeOffset from,
        string? station = null, ProgrammeDraft? unsaved = null)
    {
        var known = installedStations.ToHashSet(StringComparer.Ordinal); RequireProgrammePage(known, station, 0);
        var reviewed = unsaved?.Normalize(known);
        return TransactionAsync((c, t) =>
        {
            using var query = ProgrammeDraftCommand(c, t, "SELECT id,version FROM programme_draft_heads WHERE archived=0 LIMIT 257");
            var keys = new List<(string Id, long Version)>();
            using (var reader = query.ExecuteReader()) while (reader.Read()) keys.Add((reader.GetString(0), reader.GetInt64(1)));
            if (keys.Count > ProgrammeCalendar.MaximumRules) throw new InvalidDataException("Too many active programme drafts.");
            var drafts = keys.Select(x => ReadProgrammeRevision(c, t, x.Id, x.Version, known).Draft).ToList();
            if (reviewed is not null) { drafts.RemoveAll(x => x.Rule.Id == reviewed.Rule.Id); drafts.Add(reviewed); }
            var rules = drafts.Where(x => station is null || x.Rule.Stations.Contains(station, StringComparer.Ordinal))
                .Select(x => x.Rule with { Enabled = true, Stations = station is null ? x.Rule.Stations : new[] { station } }).ToArray();
            return ProgrammeCalendar.Forecast(rules, known, from, TimeSpan.FromHours(24));
        });
    }

    private static ProgrammeDraftRevision WriteProgrammeRevision(SqliteConnection c, SqliteTransaction t, ProgrammeDraftRevision value,
        ProgrammeDraftRevision? current, string commandId, string request, IReadOnlySet<string> known)
    {
        _ = value.Draft.Normalize(known);
        if (current is null)
        {
            using var total = ProgrammeDraftCommand(c, t, "SELECT COUNT(*) FROM programme_draft_heads");
            if (Convert.ToInt32(total.ExecuteScalar(), CultureInfo.InvariantCulture) >= 4096)
                throw new InvalidOperationException("The programme archive has reached its 4,096-event bound. Export and review retention before adding more.");
        }
        if (!value.Archived && (current is null || current.Archived))
        {
            using var active = ProgrammeDraftCommand(c, t, "SELECT COUNT(*) FROM programme_draft_heads WHERE archived=0");
            if (Convert.ToInt32(active.ExecuteScalar(), CultureInfo.InvariantCulture) >= ProgrammeCalendar.MaximumRules)
                throw new InvalidOperationException("Keep at most 256 current programme drafts. Archive an unused event first.");
        }
        var previous = "";
        if (current is not null)
        {
            using var hash = ProgrammeDraftCommand(c, t, "SELECT entry_hash FROM programme_draft_versions WHERE id=$id AND version=$version",
                ("$id", current.Id), ("$version", current.Version)); previous = (string)hash.ExecuteScalar()!;
        }
        var payload = JsonSerializer.Serialize(value);
        if (Encoding.UTF8.GetByteCount(payload) > 8192) throw new ArgumentException("This programme exceeds the bounded revision size. Shorten its text or folder path.");
        using (var insert = ProgrammeDraftCommand(c, t, "INSERT INTO programme_draft_versions(id,version,payload,previous_hash,entry_hash) VALUES($id,$version,$payload,$previous,$hash)",
            ("$id", value.Id), ("$version", value.Version), ("$payload", payload), ("$previous", previous), ("$hash", ProgrammeDraftHash(previous, payload)))) insert.ExecuteNonQuery();
        using (var head = ProgrammeDraftCommand(c, t, "INSERT INTO programme_draft_heads(id,version,archived,updated_utc) VALUES($id,$version,$archived,$at) ON CONFLICT(id) DO UPDATE SET version=excluded.version,archived=excluded.archived,updated_utc=excluded.updated_utc",
            ("$id", value.Id), ("$version", value.Version), ("$archived", value.Archived ? 1 : 0), ("$at", value.AtUtc.ToString("O", CultureInfo.InvariantCulture)))) head.ExecuteNonQuery();
        using (var clear = ProgrammeDraftCommand(c, t, "DELETE FROM programme_draft_stations WHERE id=$id", ("$id", value.Id))) clear.ExecuteNonQuery();
        foreach (var station in value.Draft.Rule.Stations)
        {
            using var add = ProgrammeDraftCommand(c, t, "INSERT INTO programme_draft_stations(id,station) VALUES($id,$station)", ("$id", value.Id), ("$station", station)); add.ExecuteNonQuery();
        }
        using var receipt = ProgrammeDraftCommand(c, t, "INSERT INTO programme_draft_commands(id,request,draft_id,version) VALUES($command,$request,$id,$version)",
            ("$command", commandId), ("$request", request), ("$id", value.Id), ("$version", value.Version)); receipt.ExecuteNonQuery();
        return value;
    }

    private static ProgrammeDraftRevision? ProgrammeHead(SqliteConnection c, SqliteTransaction t, string id, IReadOnlySet<string> known)
    {
        using var head = ProgrammeDraftCommand(c, t, "SELECT version,archived FROM programme_draft_heads WHERE id=$id", ("$id", id));
        long version; bool archived;
        using (var reader = head.ExecuteReader()) { if (!reader.Read()) return null; version = reader.GetInt64(0); archived = reader.GetInt64(1) != 0; }
        var value = ReadProgrammeRevision(c, t, id, version, known);
        if (value.Archived != archived) throw new InvalidDataException("The programme head does not match its immutable revision.");
        return value;
    }

    private static ProgrammeDraftRevision ReadProgrammeRevision(SqliteConnection c, SqliteTransaction t, string id, long version, IReadOnlySet<string> known)
    {
        using var query = ProgrammeDraftCommand(c, t, "SELECT payload,previous_hash,entry_hash FROM programme_draft_versions WHERE id=$id AND version=$version", ("$id", id), ("$version", version));
        using var reader = query.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("A programme revision is missing.");
        var payload = reader.GetString(0);
        if (payload.Length > 8192 || ProgrammeDraftHash(reader.GetString(1), payload) != reader.GetString(2)) throw new InvalidDataException("The programme revision failed its integrity check.");
        var value = JsonSerializer.Deserialize<ProgrammeDraftRevision>(payload) ?? throw new InvalidDataException("The programme revision is empty.");
        _ = value.Draft.Normalize(known);
        if (value.Id != id || value.Version != version) throw new InvalidDataException("The programme revision identity does not match.");
        return value;
    }

    private static ProgrammeDraftRevision? ProgrammeReceipt(SqliteConnection c, SqliteTransaction t, string commandId, string request, IReadOnlySet<string> known)
    {
        using var query = ProgrammeDraftCommand(c, t, "SELECT request,draft_id,version FROM programme_draft_commands WHERE id=$id", ("$id", commandId));
        string draft; long version;
        using (var reader = query.ExecuteReader())
        {
            if (!reader.Read()) return null;
            if (reader.GetString(0) != request) throw new InvalidOperationException("This command ID belongs to a different programme change.");
            draft = reader.GetString(1); version = reader.GetInt64(2);
        }
        return ReadProgrammeRevision(c, t, draft, version, known);
    }

    private static string ProgrammeDraftHash(string previous, string payload)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(previous + "\n" + payload)));
    private static string ProgrammeId(string id) => Guid.TryParseExact(id, "N", out var parsed) ? parsed.ToString("N") : throw new ArgumentException("Choose a valid programme identity.");
    private static string ProgrammeCommandId(string id) => Guid.TryParse(id, out var parsed) ? parsed.ToString("N") : throw new ArgumentException("Choose a valid programme command identity.");
    private static void RequireProgrammeActor(string actor)
    { if (string.IsNullOrWhiteSpace(actor) || actor.Length > 128 || actor.Any(char.IsControl)) throw new ArgumentException("An authenticated programme editor is required."); }
    private static void RequireProgrammePage(IReadOnlySet<string> known, string? station, int offset)
    { if (offset is < 0 or > 65536 || station is not null && !known.Contains(station)) throw new ArgumentException("Choose a known channel and a bounded programme page."); }
    private static SqliteCommand ProgrammeDraftCommand(SqliteConnection c, SqliteTransaction t, string sql, params (string Key, object Value)[] values)
    {
        var command = c.CreateCommand(); command.Transaction = t; command.CommandText = sql;
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value);
        return command;
    }
}
