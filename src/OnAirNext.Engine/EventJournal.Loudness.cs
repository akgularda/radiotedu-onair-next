using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OnAirNext.Engine;

public sealed partial class EventJournal
{
    public Task RecoverLoudnessAsync(string station) => TransactionAsync((c, t) =>
    {
        using var query = LibraryCommand(c, t, """
            SELECT p.payload,p.payload_hash FROM programme_measurements p
            WHERE p.station_id=$station AND p.sequence=(SELECT MAX(latest.sequence) FROM programme_measurements latest WHERE latest.window_id=p.window_id)
              AND p.phase NOT IN ('Finished','Interrupted') ORDER BY p.sequence
            """, ("$station", station));
        var open = new List<ProgrammeLoudnessWindow>();
        using (var reader = query.ExecuteReader())
            while (reader.Read())
            {
                var payload = reader.GetString(0);
                if (reader.GetString(1) != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))))
                    throw new InvalidDataException("Measurement history integrity check failed.");
                open.Add(JsonSerializer.Deserialize<ProgrammeLoudnessWindow>(payload)
                    ?? throw new InvalidDataException("Invalid measurement history."));
            }
        foreach (var window in open)
            WriteMeasurement(c, t, window with { Phase = "Interrupted", Reason = "ProcessRestart",
                MissingTail = true, HasMeasurementGap = true });
        return true;
    });

    public Task<bool> SaveMeasurementAsync(ProgrammeLoudnessWindow window) => TransactionAsync((c, t) => WriteMeasurement(c, t, window));

    private static bool WriteMeasurement(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction t,
        ProgrammeLoudnessWindow window)
    {
        if (!Guid.TryParseExact(window.Id, "N", out _) || string.IsNullOrWhiteSpace(window.StationId) ||
            window.StationId.Length > 100 || string.IsNullOrWhiteSpace(window.SessionId) || window.SessionId.Length > 100 ||
            window.StartSample < 0 || window.EndSample < window.StartSample ||
            window.MaximumFrames is < 144000 or > 4147200000L || window.EndSample - window.StartSample > window.MaximumFrames ||
            window.Reason.Length > 100 || window.GapBeforeFrames < 0 ||
            window.Phase is not ("Started" or "Checkpoint" or "Finished" or "Interrupted") ||
            window.EndSample - window.StartSample != (window.Reading?.SampleFrames ?? 0))
            throw new ArgumentException("Invalid measurement-window identity, phase or sample boundaries.");
        var payload = JsonSerializer.Serialize(window);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        using (var identity = LibraryCommand(c, t, "SELECT payload FROM programme_measurements WHERE window_id=$id ORDER BY sequence LIMIT 1", ("$id", window.Id)))
        {
            if (identity.ExecuteScalar() is string first)
            {
                var owner = JsonSerializer.Deserialize<ProgrammeLoudnessWindow>(first) ?? throw new InvalidDataException("Invalid measurement-window owner.");
                if (owner.StationId != window.StationId || owner.SessionId != window.SessionId || owner.StartSample != window.StartSample ||
                    owner.StartedAtUtc != window.StartedAtUtc || owner.MaximumFrames != window.MaximumFrames)
                    throw new InvalidOperationException("A measurement window cannot change its station, session or boundaries.");
            }
        }
        using (var exists = LibraryCommand(c, t, "SELECT payload_hash FROM programme_measurements WHERE window_id=$id AND end_sample=$end AND phase=$phase",
            ("$id", window.Id), ("$end", window.EndSample), ("$phase", window.Phase)))
        {
            if (exists.ExecuteScalar() is string prior)
            {
                if (prior != hash) throw new InvalidOperationException("An immutable measurement snapshot cannot be replaced.");
                return false;
            }
        }
        using var add = LibraryCommand(c, t, "INSERT INTO programme_measurements(window_id,station_id,end_sample,phase,payload,payload_hash) VALUES($id,$station,$end,$phase,$payload,$hash)",
            ("$id", window.Id), ("$station", window.StationId), ("$end", window.EndSample), ("$phase", window.Phase),
            ("$payload", payload), ("$hash", hash));
        add.ExecuteNonQuery(); return true;
    }

    public IReadOnlyList<ProgrammeLoudnessWindow> MeasurementHistory(string station, int offset = 0, int count = 50)
    {
        if (offset < 0 || count is < 1 or > 100 || string.IsNullOrWhiteSpace(station)) throw new ArgumentException("Choose a bounded station history page.");
        using var c = OpenReader(DatabasePath);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT payload,payload_hash FROM programme_measurements WHERE station_id=$station ORDER BY sequence DESC LIMIT $count OFFSET $offset";
        cmd.Parameters.AddWithValue("$station", station); cmd.Parameters.AddWithValue("$count", count); cmd.Parameters.AddWithValue("$offset", offset);
        using var reader = cmd.ExecuteReader(); var rows = new List<ProgrammeLoudnessWindow>();
        while (reader.Read())
        {
            var payload = reader.GetString(0);
            if (reader.GetString(1) != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))))
                throw new InvalidDataException("Measurement history integrity check failed.");
            rows.Add(JsonSerializer.Deserialize<ProgrammeLoudnessWindow>(payload) ?? throw new InvalidDataException("Invalid measurement history."));
        }
        return rows;
    }
}
