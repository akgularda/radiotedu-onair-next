using System.Globalization;
using System.Text.Json;

namespace OnAirNext.Engine;

public sealed partial class EventJournal
{
    private static string QueueLibraryPayload(QueueLibraryReference source, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Revision < 1 || source.ExpectedVersion < 0 || string.IsNullOrWhiteSpace(source.AssetId) ||
            source.AssetId.Length > 128 || string.IsNullOrWhiteSpace(source.SourceStamp) || source.SourceStamp.Length > 100)
            throw new ArgumentException("Choose a validated item from the current library.");
        return JsonSerializer.Serialize(new { source, expectedRevision });
    }

    internal Task<bool> HasLibraryQueueReceiptAsync(string station, QueueLibraryReference source,
        long expectedRevision, string id)
    {
        var payload = QueueLibraryPayload(source, expectedRevision);
        return TransactionAsync((c,t) =>
        {
            RequireQueueStation(c,t,station);
            using var command = LibraryCommand(c,t,
                "SELECT station_id,kind,request_target FROM engine_commands WHERE id=$id", ("$id",id));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return false;
            if (reader.GetString(0) != station || reader.GetString(1) != "station-queue-library" ||
                reader.IsDBNull(2) || reader.GetString(2) != payload)
                throw new InvalidOperationException("This request ID belongs to another operation.");
            return true;
        });
    }

    internal async Task<StationQueueSnapshot> EnqueueLibraryItemAsync(string station, QueueLibraryReference source,
        long expectedRevision, string id, Action requireSourceGuard)
    {
        var payload = QueueLibraryPayload(source, expectedRevision);
        var view = await TransactionAsync((c,t) =>
        {
            RequireQueueStation(c,t,station);
            if (!AdmitControl(c,t,station,"station-queue-library",payload,id)) return ReadQueue(c,t,station);
            var queue = ReadQueue(c,t,station); RequireQueueRevision(queue,expectedRevision);
            if (queue.Entries.Count >= MaximumQueuedItems) throw new InvalidOperationException("This channel's queue is full.");
            requireSourceGuard(); // A file handle was admitted outside this writer lane; no disk access here.
            var root = ReadLibraryRoot(c,t,station) ?? throw new InvalidOperationException("Choose a live folder in Library.");
            if (root.Revision != source.Revision) throw new InvalidOperationException("The live folder changed. Refresh Library.");
            LibraryAsset? asset;
            using (var read = LibraryCommand(c,t,AssetSelect+AssetJoin+
                " WHERE a.station_id=$station AND a.revision=$revision AND a.id=$id",
                ("$station",station),("$revision",source.Revision),("$id",source.AssetId)))
            using (var reader = read.ExecuteReader()) asset = reader.Read() ? ReadAsset(reader) : null;
            if (asset is not { State: "Ready" } || asset.EditVersion != source.ExpectedVersion ||
                asset.SourceStamp != source.SourceStamp || !double.IsFinite(asset.Seconds) || asset.Seconds <= 0)
                throw new InvalidOperationException("The item changed or is unavailable. Refresh Library before queueing.");
            var item = new MediaItem(asset.Id,Path.Combine(root.Path,asset.RelativePath),asset.Artist,asset.Title,
                DurationSeconds:asset.Seconds,Loudness:asset.Loudness);
            using var position = LibraryCommand(c,t,"SELECT COALESCE(MAX(position),0) FROM station_queue WHERE station_id=$station",("$station",station));
            var next = checked(Convert.ToInt64(position.ExecuteScalar(),CultureInfo.InvariantCulture)+1);
            using var insert = LibraryCommand(c,t,
                "INSERT INTO station_queue(id,station_id,position,payload,state,library_reference) VALUES($id,$station,$position,$item,'Pending',$source)",
                ("$id",id),("$station",station),("$position",next),("$item",JsonSerializer.Serialize(item)),("$source",JsonSerializer.Serialize(source)));
            insert.ExecuteNonQuery(); AdvanceQueueRevision(c,t,station); return ReadQueue(c,t,station);
        });
        PublishQueue(view); return view;
    }
}
