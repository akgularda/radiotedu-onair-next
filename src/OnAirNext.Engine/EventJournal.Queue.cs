using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OnAirNext.Engine;

public sealed record QueueLibraryReference(long Revision, string AssetId, long ExpectedVersion, string SourceStamp);
public sealed record StationQueueEntry(string Id, string StationId, long Position, MediaItem Item, string State,
    QueueLibraryReference? Source = null, string? PreviousOccurrenceId = null);
public sealed record StationQueueSnapshot(string StationId, long Revision, IReadOnlyList<StationQueueEntry> Entries);
public enum QueueEdit { MoveUp, MoveDown, Remove }

public sealed partial class EventJournal
{
    public const int MaximumQueuedItems = 128;
    private readonly ConcurrentDictionary<string, StationQueueSnapshot> _queueViews = new(StringComparer.Ordinal);
    public event Action<string>? QueueChanged;

    // Readers on the programme clock only see immutable, committed snapshots.
    public StationQueueSnapshot? PublishedQueue(string station) => _queueViews.GetValueOrDefault(station);

    public async Task<StationQueueSnapshot> ReadStationQueueAsync(string station)
    {
        var view = await TransactionAsync((c, t) =>
        {
            RequireQueueStation(c, t, station);
            return ReadQueue(c, t, station);
        });
        PublishQueue(view); return view;
    }

    // This typed engine boundary accepts already authorized media, not an operator
    // path. It is intentionally not reachable from the browser or service pipe yet.
    public async Task<StationQueueSnapshot> AppendStationQueueAsync(string station, MediaItem item,
        long expectedRevision, string commandId)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 128 ||
            string.IsNullOrWhiteSpace(item.Path) || item.Path.Length > 32768 ||
            item.Artist is null || item.Title is null || item.Artist.Length > 2000 || item.Title.Length > 2000 ||
            !Enum.IsDefined(item.Kind) || item.DurationSeconds is not { } seconds || !double.IsFinite(seconds) || seconds <= 0)
            throw new ArgumentException("Queue media requires validated identity, metadata and duration.");
        var payload = JsonSerializer.Serialize(new { item, expectedRevision });
        var view = await TransactionAsync((c, t) =>
        {
            RequireQueueStation(c, t, station);
            if (!AdmitControl(c, t, station, "station-queue-append", payload, commandId)) return ReadQueue(c, t, station);
            var current = ReadQueue(c, t, station);
            RequireQueueRevision(current, expectedRevision);
            if (current.Entries.Count >= MaximumQueuedItems)
                throw new InvalidOperationException("This channel's queue is full. Wait for an item to finish.");
            using var position = LibraryCommand(c, t, "SELECT COALESCE(MAX(position),0) FROM station_queue WHERE station_id=$station", ("$station", station));
            var next = checked(Convert.ToInt64(position.ExecuteScalar(), CultureInfo.InvariantCulture) + 1);
            using var insert = LibraryCommand(c, t,
                "INSERT INTO station_queue(id,station_id,position,payload,state) VALUES($id,$station,$position,$payload,'Pending')",
                ("$id", commandId), ("$station", station), ("$position", next), ("$payload", JsonSerializer.Serialize(item)));
            insert.ExecuteNonQuery(); AdvanceQueueRevision(c, t, station);
            return ReadQueue(c, t, station);
        });
        PublishQueue(view); return view;
    }

    // Editing is deliberately limited to stopped channels until the live adapter
    // can arbitrate against prepared audio. An unfinished recovery item stays owned.
    public async Task<StationQueueSnapshot> EditStationQueueAsync(string station, string entryId,
        long expectedRevision, QueueEdit edit, string commandId)
        => await EditStationQueueCoreAsync(station, entryId, expectedRevision, edit, commandId, null);

    internal Task<bool> HasStationQueueEditReceiptAsync(string station, string entryId, long expectedRevision,
        QueueEdit edit, string commandId) => TransactionAsync((c,t) =>
    {
        RequireQueueStation(c,t,station);
        var payload=JsonSerializer.Serialize(new { entryId, expectedRevision, edit });
        using var read=LibraryCommand(c,t,"SELECT station_id,kind,request_target FROM engine_commands WHERE id=$id",("$id",commandId));
        using var reader=read.ExecuteReader();
        if (!reader.Read()) return false;
        if (reader.GetString(0)!=station||reader.GetString(1)!="station-queue-edit"||reader.IsDBNull(2)||reader.GetString(2)!=payload)
            throw new InvalidOperationException("This request ID belongs to another operation.");
        return true;
    });

    internal Task<StationQueueSnapshot> EditReservedStationQueueAsync(string station, string entryId,
        long expectedRevision, QueueEdit edit, string commandId, Action authorizeReservation)
        => EditStationQueueCoreAsync(station, entryId, expectedRevision, edit, commandId, authorizeReservation);

    private async Task<StationQueueSnapshot> EditStationQueueCoreAsync(string station, string entryId,
        long expectedRevision, QueueEdit edit, string commandId, Action? authorizeReservation)
    {
        if (string.IsNullOrWhiteSpace(entryId) || entryId.Length > 128 || !Enum.IsDefined(edit))
            throw new ArgumentException("Choose a queued item and an available queue action.");
        var payload = JsonSerializer.Serialize(new { entryId, expectedRevision, edit });
        var view = await TransactionAsync((c, t) =>
        {
            RequireQueueStation(c, t, station);
            if (!AdmitControl(c, t, station, "station-queue-edit", payload, commandId)) return ReadQueue(c, t, station);
            var current = ReadQueue(c, t, station); RequireQueueRevision(current, expectedRevision);
            if (authorizeReservation is null && ReadDesired(c, t, station)!.RequestedRunning)
                throw new InvalidOperationException("Stop this channel before changing saved queue order.");
            authorizeReservation?.Invoke();
            var entries = current.Entries.ToArray(); var index = Array.FindIndex(entries, e => e.Id == entryId);
            if (index < 0) throw new InvalidOperationException("This queued item was consumed or removed. Refresh the queue.");
            using var checkpoint = LibraryCommand(c, t, "SELECT payload FROM playout_checkpoints WHERE station_id=$station", ("$station", station));
            var held = checkpoint.ExecuteScalar() is string saved ? JsonSerializer.Deserialize<PlayoutCheckpoint>(saved)?.QueueEntryId : null;
            var other = edit == QueueEdit.MoveUp ? index - 1 : index + 1;
            if (entries[index].State != "Pending" || held == entryId ||
                edit != QueueEdit.Remove && other >= 0 && other < entries.Length &&
                (entries[other].State != "Pending" || entries[other].Id == held))
                throw new InvalidOperationException("The unfinished item is retained for recovery. It cannot be moved or removed.");
            if (edit == QueueEdit.Remove)
            {
                using var remove = LibraryCommand(c, t, "UPDATE station_queue SET state='Removed' WHERE station_id=$station AND id=$id",
                    ("$station", station), ("$id", entryId)); remove.ExecuteNonQuery();
            }
            else
            {
                if (other < 0 || other >= entries.Length) throw new InvalidOperationException("This item is already at the end of the queue.");
                using var move = LibraryCommand(c, t,
                    "UPDATE station_queue SET position=CASE id WHEN $id THEN $otherPosition ELSE $position END WHERE station_id=$station AND id IN ($id,$other)",
                    ("$id", entryId), ("$other", entries[other].Id), ("$station", station),
                    ("$position", entries[index].Position), ("$otherPosition", entries[other].Position)); move.ExecuteNonQuery();
            }
            AdvanceQueueRevision(c, t, station); return ReadQueue(c, t, station);
        });
        PublishQueue(view); return view;
    }

    private void PublishQueue(StationQueueSnapshot view)
    {
        while (true)
        {
            if (!_queueViews.TryGetValue(view.StationId, out var prior))
            {
                if (!_queueViews.TryAdd(view.StationId, view)) continue;
            }
            else
            {
                if (view.Revision <= prior.Revision) return;
                if (!_queueViews.TryUpdate(view.StationId, view, prior)) continue;
            }
            QueueChanged?.Invoke(view.StationId); return;
        }
    }

    private static void RequireQueueStation(SqliteConnection c, SqliteTransaction t, string station)
    {
        if (string.IsNullOrWhiteSpace(station) || ReadDesired(c, t, station) is null)
            throw new ArgumentException("Choose a configured channel.");
    }
    private static void RequireQueueRevision(StationQueueSnapshot current, long expected)
    {
        if (expected < 0 || expected == long.MaxValue || current.Revision != expected)
            throw new InvalidOperationException("This channel's queue changed. Refresh it before editing.");
    }
    private static void AdvanceQueueRevision(SqliteConnection c, SqliteTransaction t, string station)
    {
        using var change = LibraryCommand(c, t,
            "INSERT INTO station_queue_state VALUES($station,1) ON CONFLICT(station_id) DO UPDATE SET revision=revision+1",
            ("$station", station)); change.ExecuteNonQuery();
    }
    private static StationQueueSnapshot ReadQueue(SqliteConnection c, SqliteTransaction t, string station)
    {
        using var version = LibraryCommand(c, t, "SELECT revision FROM station_queue_state WHERE station_id=$station", ("$station", station));
        var revision = Convert.ToInt64(version.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        using var rows = LibraryCommand(c, t,
            "SELECT id,position,payload,state,library_reference,occurrence_id FROM station_queue WHERE station_id=$station AND state IN ('Pending','Playing') ORDER BY position,id LIMIT 129",
            ("$station", station));
        using var reader = rows.ExecuteReader(); var entries = new List<StationQueueEntry>();
        while (reader.Read())
        {
            if (entries.Count >= MaximumQueuedItems) throw new InvalidDataException("The saved queue exceeds its bounded capacity.");
            var item = JsonSerializer.Deserialize<MediaItem>(reader.GetString(2)) ?? throw new InvalidDataException("The saved queue item is empty.");
            var source = reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<QueueLibraryReference>(reader.GetString(4));
            entries.Add(new(reader.GetString(0), station, reader.GetInt64(1), item, reader.GetString(3), source,
                reader.IsDBNull(5)?null:reader.GetString(5)));
        }
        return new(station, revision, entries.AsReadOnly());
    }

    // Return the post-transaction view before acknowledging the event. Publishing
    // it after FULL-WAL commit and before the returned task completes means the
    // engine cannot release its in-flight exclusion while a stale pending view remains.
    private static StationQueueSnapshot? RecordQueueAttempt(SqliteConnection c, SqliteTransaction t, PlaybackEvent entry)
    {
        if (entry.QueueEntryId is null) return null;
        using var row = LibraryCommand(c, t, "SELECT station_id,payload,state,occurrence_id FROM station_queue WHERE id=$id", ("$id", entry.QueueEntryId));
        string station, state; string? owner; MediaItem item;
        using (var reader = row.ExecuteReader())
        {
            if (!reader.Read()) return null; // Older isolated StudioStore fixtures use their separate prototype table.
            station = reader.GetString(0); state = reader.GetString(2);
            owner = reader.IsDBNull(3) ? null : reader.GetString(3);
            item = JsonSerializer.Deserialize<MediaItem>(reader.GetString(1)) ?? throw new InvalidDataException("The saved queue item is empty.");
        }
        if (station != entry.StationId || item.Id != entry.ItemId || item.Artist != entry.Artist || item.Title != entry.Title || item.Kind != entry.Kind)
            throw new InvalidOperationException("The playback attempt does not match this channel's saved queue item.");
        var next = entry.Type switch
        {
            "started" or "resumed" => "Playing", "ended" or "skipped" => "Completed",
            "failed" or "decode_failed" => "Failed", "interrupted" => "Pending", _ => state
        };
        if (state is "Completed" or "Failed" or "Removed")
        {
            // A decoder can emit failed then decode_failed for the same occurrence.
            // A delayed start must never resurrect consumed content.
            if (next is "Pending" or "Playing" || owner != entry.OccurrenceId)
                throw new InvalidOperationException("A consumed queue entry cannot be restarted or claimed by another attempt.");
            return ReadQueue(c, t, station);
        }
        if (state == "Playing" && owner != entry.OccurrenceId ||
            state == "Pending" && (next == "Completed" || next == "Pending" && entry.Type == "interrupted" ||
                owner is not null && owner == entry.OccurrenceId && next != state))
            throw new InvalidOperationException("The playback attempt no longer owns this queued item.");
        var nextOwner = next == state ? owner : entry.OccurrenceId;
        if (state != next || nextOwner != owner)
        {
            using var update = LibraryCommand(c, t, "UPDATE station_queue SET state=$state,occurrence_id=$owner WHERE id=$id AND station_id=$station",
                ("$state", next), ("$owner", nextOwner), ("$id", entry.QueueEntryId), ("$station", station)); update.ExecuteNonQuery();
            AdvanceQueueRevision(c, t, station);
        }
        return ReadQueue(c, t, station);
    }

    internal static bool HasUnfinishedStationQueue(SqliteConnection c, SqliteTransaction? t)
    {
        using var table = c.CreateCommand(); table.Transaction = t;
        table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='station_queue'";
        if (Convert.ToInt64(table.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return false;
        using var rows = c.CreateCommand(); rows.Transaction = t;
        rows.CommandText = "SELECT EXISTS(SELECT 1 FROM station_queue WHERE state IN ('Pending','Playing'))";
        return Convert.ToInt64(rows.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }
}
