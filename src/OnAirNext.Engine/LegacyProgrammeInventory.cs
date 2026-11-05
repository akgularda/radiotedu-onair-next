using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OnAirNext.Engine;

// Read the schedule identity and timing, never start a programme or read its files.
internal static class LegacyProgrammeInventory
{
    internal static LegacyProgrammeSummary[] Read(SqliteConnection connection, SqliteTransaction transaction, int stationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", stationId);
        var programmes = new List<LegacyProgrammeSummary>();
        if (HasTable("special_event_programs"))
        {
            command.CommandText = """
                SELECT CASE WHEN length(payload_json)<=1048576 AND json_valid(payload_json) THEN
                    json_object('name',json_extract(payload_json,'$.name'),'mode',json_extract(payload_json,'$.mode'),
                    'enabled',json_extract(payload_json,'$.enabled'),'annual',json_extract(payload_json,'$.annual'),
                    'starts_on',json_extract(payload_json,'$.starts_on'),'ends_on',json_extract(payload_json,'$.ends_on'),
                    'local_start',json_extract(payload_json,'$.local_start'),'local_end',json_extract(payload_json,'$.local_end'),
                    'timezone',json_extract(payload_json,'$.timezone'),'order',json_extract(payload_json,'$.order'),
                    'start_policy',json_extract(payload_json,'$.start_policy'),'metadata_title',json_extract(payload_json,'$.metadata_title'),
                    'priority',json_extract(payload_json,'$.priority'),'count',json_array_length(payload_json,'$.tracks')) END
                FROM special_event_programs WHERE station_id=$id ORDER BY id LIMIT 17
                """;
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                if (rows.IsDBNull(0)) throw new InvalidDataException($"Station {stationId}: invalid saved special programme.");
                using var document = JsonDocument.Parse(rows.GetString(0));
                var r = document.RootElement;
                string Get(string key) => r.GetProperty(key).GetString() ?? "";
                int Number(string key) => r.GetProperty(key) is { ValueKind: JsonValueKind.Number } value &&
                    value.TryGetInt32(out var n) ? n : 0;
                bool Enabled(string key) => r.GetProperty(key).ValueKind == JsonValueKind.True ||
                    r.GetProperty(key).ValueKind == JsonValueKind.Number && Number(key) == 1;
                programmes.Add(new(Get("name"), "special-" + Get("mode"), Enabled("enabled"), Enabled("annual"),
                    Get("starts_on"), Get("ends_on"), Get("local_start"), Get("local_end"), Get("timezone"),
                    null, null, Number("count"), Get("order"), Get("start_policy"), Get("metadata_title"), Number("priority")));
            }
        }
        if (HasTable("broadcast_plans") && HasTable("broadcast_plan_targets"))
        {
            command.CommandText = """
                SELECT p.name,p.plan_type,p.cadence_mode,p.starts_on,p.ends_on,p.local_start,p.local_end,p.timezone,
                    p.enabled AND t.enabled,p.repeat_every_songs,p.repeat_every_minutes,t.track_id,p.weekdays_json,p.priority
                FROM broadcast_plans p JOIN broadcast_plan_targets t ON t.plan_id=p.id
                WHERE t.station_id=$id ORDER BY p.id LIMIT 17
                """;
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                var rawDays = rows.GetString(12);
                if (rawDays.Length > 128) throw new InvalidDataException($"Station {stationId}: invalid saved programme weekdays.");
                var days = JsonSerializer.Deserialize<int[]>(rawDays) ?? [];
                if (days.Any(day => day is < 1 or > 7)) throw new InvalidDataException($"Station {stationId}: invalid saved programme weekdays.");
                programmes.Add(new(rows.GetString(0), rows.GetString(1) + "-" + rows.GetString(2), rows.GetBoolean(8), false,
                    rows.GetString(3), rows.GetString(4), rows.GetString(5), rows.GetString(6), rows.GetString(7),
                    rows.IsDBNull(9) ? null : rows.GetInt32(9), rows.IsDBNull(10) ? null : rows.GetInt32(10),
                    rows.IsDBNull(11) ? 0 : 1, Priority: rows.GetInt32(13), Weekdays: days));
            }
        }
        if (programmes.Count > 16 || programmes.Any(p => p.Name.Length > 160 || p.Kind.Length > 64 ||
            p.TimeZone.Length > 128 || p.StartsOn.Length > 16 || p.EndsOn.Length > 16 ||
            p.LocalStart.Length > 16 || p.LocalEnd.Length > 16 || p.MetadataTitle?.Length > 512 ||
            p.Order?.Length > 32 || p.Entry?.Length > 32))
            throw new InvalidDataException($"Station {stationId}: saved programme inventory exceeds the preview limits.");
        return programmes.ToArray();

        bool HasTable(string table)
        {
            using var schema = connection.CreateCommand();
            schema.Transaction = transaction;
            schema.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name";
            schema.Parameters.AddWithValue("$name", table);
            return schema.ExecuteScalar() is not null;
        }
    }
}
