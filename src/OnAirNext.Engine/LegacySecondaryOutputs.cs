using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OnAirNext.Engine;

internal static class LegacySecondaryOutputs
{
    internal static LegacySavedOutput[] Read(SqliteConnection connection, SqliteTransaction transaction, int stationId,
        LegacySavedOutput? primary)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='system_settings'";
        if (command.ExecuteScalar() is null) return [];
        command.Parameters.AddWithValue("$key", $"station_{stationId}_extra_icecast_outputs");
        command.CommandText = """
            SELECT CASE WHEN value IS NULL OR trim(value)='' THEN 0
                        WHEN length(value)>65536 OR NOT json_valid(value) THEN -1
                        WHEN json_type(value)<>'array' THEN -1
                        ELSE json_array_length(value) END
            FROM system_settings WHERE key=$key
            """;
        var count = (long?)command.ExecuteScalar() ?? 0;
        if (count == 0) return [];
        if (count is < 0 or > 16)
            throw new InvalidDataException($"Station {stationId}: saved secondary outputs are invalid or exceed the preview limit.");

        // Project only public destination fields. Credential values stay in
        // SQLite; the preview receives only whether an override is populated.
        command.CommandText = """
            SELECT type,
                coalesce(json_extract(value,'$.icecast_host'),json_extract(value,'$.host'),''),
                coalesce(json_extract(value,'$.icecast_port'),json_extract(value,'$.port'),''),
                coalesce(nullif(json_extract(value,'$.icecast_mount'),''),json_extract(value,'$.mount'),''),
                coalesce(nullif(json_extract(value,'$.stream_codec_profile'),''),json_extract(value,'$.codec'),''),
                coalesce(json_extract(value,'$.stream_bitrate_kbps'),json_extract(value,'$.bitrate_kbps'),''),
                coalesce(json_extract(value,'$.enabled'),'true'),
                coalesce(json_extract(value,'$.quality'),''),
                CASE WHEN json_type(value,'$.icecast_password') IS NOT NULL
                     THEN length(trim(coalesce(json_extract(value,'$.icecast_password'),'')))>0
                     WHEN json_type(value,'$.password') IS NOT NULL
                     THEN length(trim(coalesce(json_extract(value,'$.password'),'')))>0 END
            FROM (SELECT value AS payload FROM system_settings WHERE key=$key) setting, json_each(setting.payload)
            WHERE json_each.type='object'
            ORDER BY json_each.key
            """;
        using var rows = command.ExecuteReader();
        var outputs = new List<LegacySavedOutput>();
        while (rows.Read())
        {
            var host = rows.GetString(1);
            var port = Parse(rows.GetString(2), primary?.Port ?? 0);
            var mount = rows.GetString(3).Trim();
            if (mount.Length != 0 && !mount.StartsWith('/')) mount = "/" + mount;
            var profile = rows.GetString(4);
            var bitrate = Parse(rows.GetString(5), primary?.BitrateKbps ?? 192);
            var enabled = rows.GetString(6).Trim().ToLowerInvariant() is not ("0" or "false" or "no" or "off" or "private");
            var quality = rows.GetString(7).Trim().ToLowerInvariant();
            if (quality == "high" || mount.EndsWith("-high", StringComparison.OrdinalIgnoreCase))
                (profile, bitrate) = ("aac_low_192", 192);
            else if (quality == "low" || mount.EndsWith("-low", StringComparison.OrdinalIgnoreCase))
                (profile, bitrate) = ("aac_he_v2_64", 64);
            var output = new LegacySavedOutput(host.Length > 0 ? host : primary?.Host ?? "", port, mount,
                profile.Length > 0 ? profile : primary?.Profile ?? "", bitrate, enabled,
                rows.IsDBNull(8) ? primary?.HasCredential == true : rows.GetBoolean(8));
            if (output.Host.Length > 253 || output.Mount.Length > 1024 || output.Profile.Length > 64)
                throw new InvalidDataException($"Station {stationId}: a saved secondary output exceeds its preview size limit.");
            if (output.Enabled && LegacyImportPreview.OutputAddressIssues(output) is { Length: > 0 } issues)
                throw new InvalidDataException($"Station {stationId}: an enabled secondary output has an invalid {string.Join(", ", issues)}.");
            outputs.Add(output);
        }
        if (outputs.Count != count)
            throw new InvalidDataException($"Station {stationId}: a saved secondary output is not an object.");
        return outputs.ToArray();
    }

    private static int Parse(string value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
}
