using System.Text.Json;

namespace OnAirNext.Engine;

public sealed partial class EventJournal
{
    // Recovery is one FULL-WAL transaction. Reopening the same checkpoint cannot
    // manufacture another interruption or mark an unfinished queue item completed.
    public Task<PlayoutCheckpoint?> PrepareRecoveryAsync(string stationId) => TransactionAsync((connection, transaction) =>
    {
        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT payload FROM playout_checkpoints WHERE station_id=$id";
        query.Parameters.AddWithValue("$id", stationId);
        if (query.ExecuteScalar() is not string payload) return null;
        var saved = JsonSerializer.Deserialize<PlayoutCheckpoint>(payload)
            ?? throw new InvalidDataException("The playout checkpoint is empty. Recovery requires inspection.");
        if (saved.StationId != stationId || saved.RotationIndex < 0 || saved.ItemSamples < 0 || saved.ProgramSamples < 0 ||
            saved.ItemSamples > saved.ProgramSamples || saved.State is not ("Playing" or "Stopped" or "BetweenItems" or "RestartPending") ||
            (saved.Announced && (saved.Item is null || string.IsNullOrWhiteSpace(saved.OccurrenceId))))
            throw new InvalidDataException("The playout checkpoint is inconsistent. Recovery requires inspection.");
        if (saved.State is "Playing" or "RestartPending" or "Stopped" && saved.Item is not null && saved.Announced &&
            HasPendingSkip(connection, transaction, stationId, saved.OccurrenceId))
        {
            var item = saved.Item;
            WriteEvent(connection, transaction, new PlaybackEvent(Guid.NewGuid().ToString("N"), saved.OccurrenceId,
                stationId, "skipped", item.Id, item.Artist, item.Title, item.Kind, saved.SavedAtUtc, saved.ProgramSamples,
                "A durably accepted Skip was recovered. Playback after the last checkpoint is unknown.",
                saved.QueueEntryId, saved.PreviousOccurrenceId, "last_committed_checkpoint"));
            ConsumeSkips(connection, transaction, stationId, saved.OccurrenceId);
            if (saved.QueueEntryId is not null)
            {
                using var exists = connection.CreateCommand(); exists.Transaction = transaction;
                exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='studio_queue'";
                if (Convert.ToInt32(exists.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0)
                {
                    using var complete = connection.CreateCommand(); complete.Transaction = transaction;
                    complete.CommandText = "UPDATE studio_queue SET state='completed' WHERE id=$id";
                    complete.Parameters.AddWithValue("$id", saved.QueueEntryId); complete.ExecuteNonQuery();
                }
            }
            var skipped = saved with { State = "BetweenItems", RotationIndex = saved.RotationIndex + (saved.QueueEntryId is null ? 1 : 0),
                Item = null, ItemSamples = 0, Announced = false, QueueEntryId = null };
            WriteCheckpoint(connection, transaction, skipped);
            return skipped;
        }
        if (saved.State != "Playing") return saved;
        if (saved.Announced && saved.Item is not null)
        {
            var item = saved.Item;
            WriteEvent(connection, transaction, new PlaybackEvent(
                Guid.NewGuid().ToString("N"), saved.OccurrenceId, stationId, "interrupted", item.Id,
                item.Artist, item.Title, item.Kind, saved.SavedAtUtc, saved.ProgramSamples,
                "Process interruption detected on restart. Duration is a lower bound from the last committed checkpoint; the uncommitted tail is unknown.",
                saved.QueueEntryId, saved.PreviousOccurrenceId, "last_committed_checkpoint"));
        }
        var prepared = saved with { State = "RestartPending" };
        WriteCheckpoint(connection, transaction, prepared);
        return prepared;
    });
}
