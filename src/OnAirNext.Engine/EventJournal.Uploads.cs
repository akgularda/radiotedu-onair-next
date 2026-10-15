using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OnAirNext.Engine;

public sealed record LibraryUpload(string Id, string StationId, long Revision, string Payload, string State);
public sealed partial class EventJournal
{
    public Task<LibraryUpload> RegisterLibraryUploadAsync(string station, string id, string payload, long expectedRevision) => TransactionAsync((c, t) =>
    {
        if (payload.Length > 100000) throw new ArgumentException("The upload manifest exceeds its size limit.");
        if (ReadDesired(c, t, station) is null) throw new ArgumentException("Unknown station.");
        var previous = ReadUpload(c, t, id);
        if (previous is not null)
        {
            if (previous.StationId != station || previous.Payload != payload)
                throw new InvalidOperationException("This upload ID belongs to different files or another station.");
            return previous;
        }
        using (var count = LibraryCommand(c, t, "SELECT COUNT(*) FROM library_uploads WHERE state NOT IN ('Completed','Cancelled')"))
            if (Convert.ToInt64(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) >= 4)
                throw new InvalidOperationException("Four uploads are already retained. Finish or cancel an upload in Library.");
        var revision = ReadLibraryRoot(c, t, station)?.Revision ?? 0;
        if (revision != expectedRevision) throw new InvalidOperationException("The live folder changed while preparing the transfer. Select the files again.");
        using var insert = LibraryCommand(c, t, "INSERT INTO library_uploads VALUES($id,$station,$revision,$payload,'Receiving')",
            ("$id", id), ("$station", station), ("$revision", revision), ("$payload", payload));
        insert.ExecuteNonQuery(); return new LibraryUpload(id, station, revision, payload, "Receiving");
    });
    public LibraryUpload? LibraryUpload(string id)
    {
        using var c = OpenReader(DatabasePath); using var t = c.BeginTransaction(deferred: true);
        return ReadUpload(c, t, id);
    }
    private static LibraryUpload? ReadUpload(SqliteConnection c, SqliteTransaction t, string id)
    {
        using var cmd = LibraryCommand(c, t, "SELECT id,station_id,revision,payload,state FROM library_uploads WHERE id=$id", ("$id", id));
        using var r = cmd.ExecuteReader(); return r.Read() ? new(r.GetString(0),r.GetString(1),r.GetInt64(2),r.GetString(3),r.GetString(4)) : null;
    }
    public IReadOnlyList<LibraryUpload> ActiveLibraryUploads()
    {
        using var c = OpenReader(DatabasePath); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,station_id,revision,payload,state FROM library_uploads WHERE state NOT IN ('Completed','Cancelled') ORDER BY id LIMIT 4";
        using var r = cmd.ExecuteReader(); var rows = new List<LibraryUpload>();
        while (r.Read()) rows.Add(new(r.GetString(0),r.GetString(1),r.GetInt64(2),r.GetString(3),r.GetString(4))); return rows;
    }
    public string? LibraryImportState(string id)
    {
        using var c = OpenReader(DatabasePath); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT state FROM library_imports WHERE id=$id";cmd.Parameters.AddWithValue("$id",id);
        return cmd.ExecuteScalar() as string;
    }
    public Task<bool> FinishLibraryUploadAsync(string id, string station, bool cancel, bool filesRemoved = false) => TransactionAsync((c,t) =>
    {
        var upload = ReadUpload(c,t,id) ?? throw new InvalidOperationException("Upload is unavailable.");
        if (upload.StationId != station) throw new InvalidOperationException("Upload belongs to another station.");
        using var job = LibraryCommand(c,t,"SELECT state FROM library_imports WHERE id=$id",("$id",id));
        var state = job.ExecuteScalar() as string;
        if (state == "Pending") throw new InvalidOperationException("This import is copying or validating. Wait for it to finish.");
        if (!cancel && state != "Completed") throw new InvalidOperationException("Only a completed import can be cleaned up automatically.");
        using var update = LibraryCommand(c,t,"UPDATE library_uploads SET state=$state WHERE id=$id",("$id",id),("$state",cancel ? filesRemoved ? "Cancelled" : "Cancelling" : "Completed"));
        update.ExecuteNonQuery();return true;
    });
}
