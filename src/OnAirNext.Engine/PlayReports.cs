using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OnAirNext.Engine;

public sealed record ReportManifest(int SchemaVersion, string LocalDate, string Timezone, long Records,
    long IncompleteRecords, string Sha256, string FileName, DateTimeOffset GeneratedAtUtc);

public static class PlayReports
{
    public static TimeZoneInfo Istanbul => ReportCalendar.Istanbul;
    public static (DateTimeOffset Start, DateTimeOffset End) DayBounds(DateOnly date) => ReportCalendar.DayBounds(date);
    public static (DateTimeOffset Start, DateTimeOffset End) PeriodBounds(string period, string selection) => ReportCalendar.PeriodBounds(period, selection);

    public static string CsvField(string value)
    {
        var trimmed = value.TrimStart();
        if (trimmed.Length > 0 && (trimmed[0] is '=' or '+' or '-' or '@' || value[0] is '\t' or '\r' or '\n')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public static ReportManifest ExportDaily(string database, DateOnly date, string directory)
    {
        var bounds = DayBounds(date);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv");
        var hours = new long[24];
        string digest = "";
        var (count, incomplete) = ExportRange(database, bounds.Start, bounds.End, path, hours,
            artifactWritten: (_, sha256) => digest = sha256);
        var hourPath = Path.Combine(directory, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "-hours.csv");
        AtomicText(hourPath, writer =>
        {
            writer.WriteLine("local_date,hour,occurrences_started");
            for (var hour = 0; hour < 24; hour++) writer.WriteLine($"{date:yyyy-MM-dd},{hour:D2}:00,{hours[hour]}");
        });
        var manifest = new ReportManifest(1, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "Europe/Istanbul",
            count, incomplete, digest, Path.GetFileName(path), DateTimeOffset.UtcNow);
        AtomicText(Path.ChangeExtension(path, ".manifest.json"), writer => writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true })));
        return manifest;
    }

    // Query results are streamed. UI page sizes cannot truncate a daily or annual export.
    public static (long Records, long Incomplete) ExportRange(string database, DateTimeOffset from, DateTimeOffset to,
        string destination, long[]? hourCounts = null, string? stationId = null,
        CancellationToken cancellation = default, Action<long, long>? progress = null, int bytesPerSecond = 0,
        Action<long, string>? artifactWritten = null, Action<long, DateTimeOffset>? snapshotObserved = null)
    {
        if (to <= from) throw new ArgumentException("Report end must follow start.");
        cancellation.ThrowIfCancellationRequested();
        if (hourCounts is not null && hourCounts.Length != 24) throw new ArgumentException("Hourly counts require 24 slots.");
        long count = 0, incomplete = 0;
        using var connection = EventJournal.OpenReader(database);
        using var transaction = connection.BeginTransaction();
        using var snapshot = connection.CreateCommand(); snapshot.Transaction = transaction;
        snapshot.CommandText = "SELECT COALESCE(MAX(sequence),0) FROM playback_events";
        var snapshotSequence = (long)snapshot.ExecuteScalar()!;
        snapshotObserved?.Invoke(snapshotSequence, DateTimeOffset.UtcNow);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.payload,
              (SELECT e.payload FROM playback_events e WHERE e.occurrence_id=s.occurrence_id
                 AND e.event_type IN ('ended','skipped','interrupted','failed','decode_failed') ORDER BY e.sequence DESC LIMIT 1)
            FROM playback_events s WHERE s.event_type='started' AND s.at_utc >= $from AND s.at_utc < $to
              AND ($station IS NULL OR s.station_id=$station)
            ORDER BY s.at_utc, s.sequence
            """;
        command.Parameters.AddWithValue("$from", from.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", to.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$station", (object?)stationId ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var artifact = AtomicText(destination, writer =>
        {
            writer.WriteLine("occurrence_id,station_id,content_type,artist,title,start_utc,end_utc,start_local,end_local,local_hour,played_seconds,status,previous_occurrence_id,timing_quality");
            while (reader.Read())
            {
                cancellation.ThrowIfCancellationRequested();
                var start = JsonSerializer.Deserialize<PlaybackEvent>(reader.GetString(0))!;
                var end = reader.IsDBNull(1) ? null : JsonSerializer.Deserialize<PlaybackEvent>(reader.GetString(1));
                var local = TimeZoneInfo.ConvertTime(start.AtUtc, Istanbul);
                var localEnd = end is null ? "" : TimeZoneInfo.ConvertTime(end.AtUtc, Istanbul).ToString("O", CultureInfo.InvariantCulture);
                var duration = end is null ? "" : (Math.Max(0, end.SamplePosition - start.SamplePosition) / (double)AudioFormat.SampleRate).ToString("F3", CultureInfo.InvariantCulture);
                writer.WriteLine(string.Join(',', new[] { start.OccurrenceId, start.StationId, start.Kind.ToString(), start.Artist,
                    start.Title, start.AtUtc.ToString("O", CultureInfo.InvariantCulture), end?.AtUtc.ToString("O", CultureInfo.InvariantCulture) ?? "",
                    local.ToString("O", CultureInfo.InvariantCulture), localEnd, local.Hour.ToString("D2", CultureInfo.InvariantCulture), duration,
                    end?.Type ?? "incomplete", start.PreviousOccurrenceId ?? "", end?.TimingQuality ?? start.TimingQuality }.Select(CsvField)));
                count++;
                if (end is null) incomplete++;
                if (hourCounts is not null) hourCounts[local.Hour]++;
                if (count % 256 == 0) progress?.Invoke(count, incomplete);
            }
        }, cancellation, bytesPerSecond);
        cancellation.ThrowIfCancellationRequested();
        progress?.Invoke(count, incomplete);
        transaction.Commit();
        artifactWritten?.Invoke(artifact.Bytes, artifact.Sha256);
        return (count, incomplete);
    }

    private static (long Bytes, string Sha256) AtomicText(string destination, Action<StreamWriter> write, CancellationToken cancellation = default, int bytesPerSecond = 0)
    {
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        long bytes; string sha256;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var limited = new ReportWriteStream(file, bytesPerSecond, cancellation);
                using (var writer = new StreamWriter(limited, new UTF8Encoding(true), leaveOpen: true)) { write(writer); writer.Flush(); }
                file.Flush(flushToDisk: true);
                bytes = file.Length; sha256 = limited.Sha256;
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            return (bytes, sha256);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
