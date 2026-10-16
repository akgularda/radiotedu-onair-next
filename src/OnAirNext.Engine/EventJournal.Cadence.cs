using Microsoft.Data.Sqlite;

namespace OnAirNext.Engine;

public sealed record BrandingCadence(string StationId, long MusicSinceJingle, long MusicSinceSweeper,
    long CompletedJingles = 0, long CompletedSweepers = 0);

public sealed partial class EventJournal
{
    public Task<BrandingProgress> BrandingProgressAsync(string stationId) => TransactionAsync((connection, transaction) =>
    {
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT music_since_jingle,music_since_sweeper,completed_jingles,completed_sweepers,completed_music,last_jingle_attempt_music,last_sweeper_attempt_music FROM station_cadence WHERE station_id=$station";
        query.Parameters.AddWithValue("$station", stationId);
        using var reader = query.ExecuteReader();
        return reader.Read() ? new BrandingProgress(new(stationId, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6)).Validate() : new(new(stationId, 0, 0));
    });

    public Task<BrandingCadence> CadenceAsync(string stationId) => TransactionAsync((connection, transaction) =>
    {
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT music_since_jingle,music_since_sweeper,completed_jingles,completed_sweepers FROM station_cadence WHERE station_id=$station";
        query.Parameters.AddWithValue("$station", stationId);
        using var reader = query.ExecuteReader();
        return reader.Read() ? new BrandingCadence(stationId, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)) : new(stationId, 0, 0);
    });

    private static void InitializeBrandingOrdinals(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var name in new[] { "completed_jingles", "completed_sweepers", "completed_music", "last_jingle_attempt_music", "last_sweeper_attempt_music" })
        {
            using var query = connection.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT COUNT(*) FROM pragma_table_info('station_cadence') WHERE name=$name";
            query.Parameters.AddWithValue("$name", name);
            if (Convert.ToInt32(query.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0) continue;
            using var add = connection.CreateCommand(); add.Transaction = transaction;
            // Names above are fixed source constants, never operator input.
            add.CommandText = "ALTER TABLE station_cadence ADD COLUMN " + name + " INTEGER NOT NULL DEFAULT " + (name.StartsWith("last_", StringComparison.Ordinal) ? "-1" : "0");
            add.ExecuteNonQuery();
        }
        using var triggers = connection.CreateCommand(); triggers.Transaction = transaction;
        // Completion already deduplicates the occurrence before resetting exactly
        // its own category. Tracking that existing update also retains ordinals if
        // an older development binary temporarily owns this additive schema.
        // No history scan or audio-clock database read is needed at startup.
        triggers.CommandText = """
            CREATE TABLE IF NOT EXISTS branding_attempts(station_id TEXT NOT NULL,occurrence_id TEXT NOT NULL,
              PRIMARY KEY(station_id,occurrence_id));
            DROP TRIGGER IF EXISTS cadence_jingle_completed;
            DROP TRIGGER IF EXISTS cadence_sweeper_completed;
            CREATE TRIGGER cadence_jingle_completed
              AFTER UPDATE OF music_since_jingle ON station_cadence WHEN NEW.music_since_jingle=0
            BEGIN
              UPDATE station_cadence SET completed_jingles=completed_jingles+1,last_jingle_attempt_music=completed_music WHERE station_id=NEW.station_id;
            END;
            CREATE TRIGGER cadence_sweeper_completed
              AFTER UPDATE OF music_since_sweeper ON station_cadence WHEN NEW.music_since_sweeper=0
            BEGIN
              UPDATE station_cadence SET completed_sweepers=completed_sweepers+1,last_sweeper_attempt_music=completed_music WHERE station_id=NEW.station_id;
            END;
            CREATE TRIGGER IF NOT EXISTS cadence_music_completed
              AFTER UPDATE OF music_since_jingle ON station_cadence WHEN NEW.music_since_jingle>OLD.music_since_jingle
            BEGIN
              UPDATE station_cadence SET completed_music=completed_music+1 WHERE station_id=NEW.station_id;
            END;
            """;
        triggers.ExecuteNonQuery(); transaction.Commit();
    }

    private static void RecordBrandingAttempt(SqliteConnection connection, SqliteTransaction transaction, PlaybackEvent entry)
    {
        if (entry.Kind is not (ContentKind.Jingle or ContentKind.Sweeper) ||
            entry.Type is not ("ended" or "skipped" or "failed" or "decode_failed")) return;
        using var mark = connection.CreateCommand(); mark.Transaction = transaction;
        mark.CommandText = "INSERT OR IGNORE INTO branding_attempts(station_id,occurrence_id) VALUES($station,$occurrence)";
        mark.Parameters.AddWithValue("$station", entry.StationId); mark.Parameters.AddWithValue("$occurrence", entry.OccurrenceId);
        if (mark.ExecuteNonQuery() == 0) return;
        using var ensure = connection.CreateCommand(); ensure.Transaction = transaction;
        ensure.CommandText = "INSERT OR IGNORE INTO station_cadence(station_id) VALUES($station)";
        ensure.Parameters.AddWithValue("$station", entry.StationId); ensure.ExecuteNonQuery();
        using var update = connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = "UPDATE station_cadence SET " +
            (entry.Kind == ContentKind.Jingle ? "last_jingle_attempt_music" : "last_sweeper_attempt_music") + "=completed_music WHERE station_id=$station";
        update.Parameters.AddWithValue("$station", entry.StationId); update.ExecuteNonQuery();
    }

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
