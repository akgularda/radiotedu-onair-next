using Microsoft.Data.Sqlite;

namespace OnAirNext.Engine;

public sealed record BrandingCadence(string StationId, long MusicSinceJingle, long MusicSinceSweeper);

public sealed partial class EventJournal
{
    public Task<BrandingCadence> CadenceAsync(string stationId) => TransactionAsync((connection, transaction) =>
    {
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT music_since_jingle,music_since_sweeper FROM station_cadence WHERE station_id=$station";
        query.Parameters.AddWithValue("$station", stationId);
        using var reader = query.ExecuteReader();
        return reader.Read() ? new BrandingCadence(stationId, reader.GetInt64(0), reader.GetInt64(1)) : new(stationId, 0, 0);
    });

    // Counters share the completion/checkpoint/queue transaction. Process restart
    // cannot count a partial attempt as a completed song or reset unrelated branding.
    private static void CompleteCadence(SqliteConnection connection, SqliteTransaction transaction, PlaybackEvent entry)
    {
        using var mark = connection.CreateCommand(); mark.Transaction = transaction;
        mark.CommandText = "INSERT OR IGNORE INTO cadence_completions(station_id,occurrence_id) VALUES($station,$occurrence)";
        mark.Parameters.AddWithValue("$station", entry.StationId); mark.Parameters.AddWithValue("$occurrence", entry.OccurrenceId);
        if (mark.ExecuteNonQuery() == 0) return;
        using var ensure = connection.CreateCommand(); ensure.Transaction = transaction;
        ensure.CommandText = "INSERT OR IGNORE INTO station_cadence(station_id) VALUES($station)";
        ensure.Parameters.AddWithValue("$station", entry.StationId); ensure.ExecuteNonQuery();
        var change = entry.Kind switch
        {
            ContentKind.Music => "music_since_jingle=music_since_jingle+1,music_since_sweeper=music_since_sweeper+1",
            ContentKind.Jingle => "music_since_jingle=0",
            ContentKind.Sweeper => "music_since_sweeper=0",
            _ => null
        };
        if (change is null) return;
        using var update = connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = "UPDATE station_cadence SET " + change + " WHERE station_id=$station";
        update.Parameters.AddWithValue("$station", entry.StationId); update.ExecuteNonQuery();
    }
}
