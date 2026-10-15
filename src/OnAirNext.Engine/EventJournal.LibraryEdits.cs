using System.Text.Json;

namespace OnAirNext.Engine;

public enum LibraryEditKind { Metadata, ResetMetadata, Retire, Restore }

public sealed partial class EventJournal
{
    public LibraryAsset? LibraryItem(string station, long revision, string assetId)
    {
        using var c = OpenReader(DatabasePath); using var t = c.BeginTransaction(deferred: true);
        if (ReadLibraryRoot(c, t, station)?.Revision != revision)
            throw new InvalidOperationException("The live folder changed. Reload the library.");
        using var cmd = LibraryCommand(c, t, AssetSelect + AssetJoin + " WHERE a.station_id=$station AND a.revision=$revision AND a.id=$id",
            ("$station", station), ("$revision", revision), ("$id", assetId));
        using var r = cmd.ExecuteReader(); return r.Read() ? ReadAsset(r) : null;
    }

    // Operator overrides are separate from decoder metadata. Rescans never rewrite them or historic plays.
    public Task<bool> ChangeLibraryAssetAsync(string station, long revision, string assetId, long expectedVersion,
        LibraryEditKind kind, string command, string? artist = null, string? title = null)
    {
        if (revision < 1 || expectedVersion < 0 || expectedVersion == long.MaxValue ||
            string.IsNullOrWhiteSpace(assetId) || assetId.Length > 128 || assetId.Any(char.IsControl) || !Enum.IsDefined(kind))
            throw new ArgumentException("Choose a current library item before editing.");
        if (kind == LibraryEditKind.Metadata)
        {
            if (artist is null || artist.Length > 128 || artist.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(title) || title.Length > 256 || title.Any(char.IsControl))
                throw new ArgumentException("Enter a title of up to 256 characters and an artist of up to 128 characters, without control characters.");
        }
        else if (artist is not null || title is not null) throw new ArgumentException("This library action does not accept metadata.");
        var payload = JsonSerializer.Serialize(new { revision, assetId, expectedVersion, artist, title });
        return TransactionAsync((c, t) =>
        {
            if (!AdmitControl(c, t, station, "library-asset-" + kind.ToString(), payload, command)) return false;
            var root = ReadLibraryRoot(c, t, station);
            if (root?.Revision != revision) throw new InvalidOperationException("The live folder changed. Reload the library before editing.");
            string? manualArtist, manualTitle; bool retired; long version;
            using (var read = LibraryCommand(c, t, """
                SELECT e.artist,e.title,COALESCE(e.retired,0),COALESCE(e.version,0)
                FROM library_assets a LEFT JOIN library_asset_edits e
                  ON e.station_id=a.station_id AND e.revision=a.revision AND e.id=a.id
                WHERE a.station_id=$station AND a.revision=$revision AND a.id=$id
                """, ("$station", station), ("$revision", revision), ("$id", assetId)))
            using (var r = read.ExecuteReader())
            {
                if (!r.Read()) throw new InvalidOperationException("This library item is no longer available. Reload the library.");
                manualArtist = r.IsDBNull(0) ? null : r.GetString(0); manualTitle = r.IsDBNull(1) ? null : r.GetString(1);
                retired = r.GetBoolean(2); version = r.GetInt64(3);
            }
            if (version != expectedVersion) throw new InvalidOperationException("This item was edited in another window. Reload it before saving.");
            switch (kind)
            {
                case LibraryEditKind.Metadata: manualArtist = artist; manualTitle = title; break;
                case LibraryEditKind.ResetMetadata: manualArtist = null; manualTitle = null; break;
                case LibraryEditKind.Retire:
                    if (retired) throw new InvalidOperationException("This item is already removed from rotation.");
                    retired = true; break;
                case LibraryEditKind.Restore:
                    if (!retired) throw new InvalidOperationException("This item is already in the library.");
                    retired = false;
                    using (var invalidate = LibraryCommand(c, t, """
                        UPDATE library_assets SET state='Invalid',modified_ticks=-1,error='Restored; validation pending.'
                        WHERE station_id=$station AND revision=$revision AND id=$id
                        """, ("$station", station), ("$revision", revision), ("$id", assetId))) invalidate.ExecuteNonQuery();
                    break;
            }
            using var update = LibraryCommand(c, t, """
                INSERT INTO library_asset_edits(station_id,revision,id,artist,title,retired,version)
                VALUES($station,$revision,$id,$artist,$title,$retired,$version)
                ON CONFLICT(station_id,revision,id) DO UPDATE SET artist=excluded.artist,title=excluded.title,
                  retired=excluded.retired,version=excluded.version
                """, ("$station", station), ("$revision", revision), ("$id", assetId),
                ("$artist", manualArtist), ("$title", manualTitle), ("$retired", retired ? 1 : 0), ("$version", version + 1));
            update.ExecuteNonQuery(); return true;
        });
    }
}
