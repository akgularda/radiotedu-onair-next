using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OnAirNext.Engine;

public sealed record DesiredStationState(string StationId, bool Enabled, bool RequestedRunning);

public sealed partial class EventJournal
{
    public Task<DesiredStationState> InitializeDesiredStateAsync(StationSettings settings) => TransactionAsync((c, t) =>
    {
        using var insert = c.CreateCommand(); insert.Transaction = t;
        insert.CommandText = "INSERT OR IGNORE INTO station_desired_state VALUES($station,$enabled,$running,$at)";
        insert.Parameters.AddWithValue("$station", settings.Id);
        insert.Parameters.AddWithValue("$enabled", settings.Enabled ? 1 : 0);
        insert.Parameters.AddWithValue("$running", settings.Enabled && settings.AutoStart ? 1 : 0);
        insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        insert.ExecuteNonQuery();
        return ReadDesired(c, t, settings.Id)!;
    });

    public Task<DesiredStationState?> DesiredStateAsync(string station) => TransactionAsync((c, t) => ReadDesired(c, t, station));

    public Task<bool> SetDesiredStateAsync(string station, bool enabled, bool running, string commandId)
    {
        if (!enabled && running) throw new ArgumentException("A disabled station cannot be requested to run.");
        return TransactionAsync((c, t) =>
        {
            var payload = (enabled ? "1" : "0") + "," + (running ? "1" : "0");
            if (!AdmitControl(c, t, station, "desired-state", payload, commandId)) return false;
            WriteDesired(c, t, station, enabled, running);
            return true;
        });
    }

    // Shutdown intent and its receipt share one commit for every configured station.
    // Replayed acknowledgements never overwrite a later operator decision.
    public Task<bool> RequestStopAllAsync(IEnumerable<string> configuredStations, string commandId)
    {
        var stations = configuredStations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (stations.Length == 0 || stations.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Specify configured stations.");
        var payload = System.Text.Json.JsonSerializer.Serialize(stations);
        return TransactionAsync((c, t) =>
        {
            if (!AdmitControl(c, t, "*", "stop-all", payload, commandId)) return false;
            foreach (var station in stations)
            {
                var current = ReadDesired(c, t, station) ?? throw new InvalidOperationException("Station startup state is not initialized.");
                WriteDesired(c, t, station, current.Enabled, false);
            }
            return true;
        });
    }

    private static DesiredStationState? ReadDesired(SqliteConnection c, SqliteTransaction t, string station)
    {
        using var command = c.CreateCommand(); command.Transaction = t;
        command.CommandText = "SELECT enabled,requested_running FROM station_desired_state WHERE station_id=$station";
        command.Parameters.AddWithValue("$station", station);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(station, reader.GetInt64(0) == 1, reader.GetInt64(1) == 1) : null;
    }

    private static void WriteDesired(SqliteConnection c, SqliteTransaction t, string station, bool enabled, bool running)
    {
        using var command = c.CreateCommand(); command.Transaction = t;
        command.CommandText = "INSERT INTO station_desired_state VALUES($station,$enabled,$running,$at) ON CONFLICT(station_id) DO UPDATE SET enabled=excluded.enabled,requested_running=excluded.requested_running,updated_utc=excluded.updated_utc";
        command.Parameters.AddWithValue("$station", station);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$running", running ? 1 : 0);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static bool AdmitControl(SqliteConnection c, SqliteTransaction t, string station, string kind, string payload, string id)
    {
        if (string.IsNullOrWhiteSpace(station) || string.IsNullOrWhiteSpace(id) || id.Length > 128)
            throw new ArgumentException("Control commands require a station and bounded command ID.");
        using (var prior = c.CreateCommand())
        {
            prior.Transaction = t;
            prior.CommandText = "SELECT station_id,kind,request_target FROM engine_commands WHERE id=$id";
            prior.Parameters.AddWithValue("$id", id);
            using var reader = prior.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetString(0) != station || reader.GetString(1) != kind || reader.IsDBNull(2) || reader.GetString(2) != payload)
                    throw new InvalidOperationException("Command ID was already used for a different operation.");
                return false;
            }
        }
        using var insert = c.CreateCommand(); insert.Transaction = t;
        insert.CommandText = "INSERT INTO engine_commands(id,station_id,kind,processed,request_target) VALUES($id,$station,$kind,1,$payload)";
        insert.Parameters.AddWithValue("$id", id); insert.Parameters.AddWithValue("$station", station);
        insert.Parameters.AddWithValue("$kind", kind); insert.Parameters.AddWithValue("$payload", payload);
        insert.ExecuteNonQuery(); return true;
    }
}
