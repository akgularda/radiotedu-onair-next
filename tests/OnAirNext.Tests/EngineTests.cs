using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class EngineTests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RadioTEDU", "OnAirNext-Dev", "unit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    private static PlaybackEvent Entry(string occurrence, string type, DateTimeOffset at, long sample = 0)
        => new(Guid.NewGuid().ToString("N"), occurrence, "station-a", type, "asset-1", "Şebnem Ferah", "Yağmurlar",
            ContentKind.Music, at, sample);

    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("localhost")]
    [InlineData("example.com")]
    public void ProductionDestinationsCannotBeUsedByThisMilestone(string host)
        => Assert.Throws<ArgumentException>(() => new OutputSettings("x", host, 18980, "/next-test", new string('x', 32)).Validate());

    [Theory]
    [InlineData("/radiotedu")]
    [InlineData("/en")]
    [InlineData("/fr")]
    [InlineData("/next-test\r\nAuthorization: injected")]
    [InlineData("/next-a/b")]
    public void ProtectedAndMalformedMountsAreRejected(string mount)
        => Assert.Throws<ArgumentException>(() => new OutputSettings("x", "127.0.0.1", 18980, mount, new string('x', 32)).Validate());

    [Fact]
    public void SharedPcmRemainsAliveUntilTheLastConsumerReleasesIt()
    {
        var block = PcmBlock.Rent();
        block.Memory.Span[0] = 23;
        block.AddReference(); block.Dispose();
        Assert.Equal(23, block.Memory.Span[0]);
        block.Dispose();
        Assert.Throws<ObjectDisposedException>(() => block.Memory);
    }

    [Fact]
    public void LiveMixerLimitsSamplesAndReturnsToFullMusicWhenInputDisappears()
    {
        using var live = new LiveInput();
        live.SetEnabled(true);
        using var program = PcmBlock.Rent();
        for (var i = 0; i < 4; i++)
        {
            var mic = PcmBlock.Rent(); MemoryMarshal.Cast<byte, short>(mic.Memory.Span).Fill(30000);
            live.Push(mic); MemoryMarshal.Cast<byte, short>(program.Memory.Span).Fill(20000);
            live.Mix(program.Memory.Span);
            Assert.All(MemoryMarshal.Cast<byte, short>(program.Memory.Span).ToArray(), x => Assert.InRange(x, (short)0, short.MaxValue));
            Assert.Equal(short.MaxValue, MemoryMarshal.Cast<byte, short>(program.Memory.Span)[^1]);
        }
        for (var i = 0; i < 12; i++)
        {
            MemoryMarshal.Cast<byte, short>(program.Memory.Span).Fill(20000);
            live.Mix(program.Memory.Span);
        }
        Assert.InRange(MemoryMarshal.Cast<byte, short>(program.Memory.Span)[^1], 19990, 20000);
    }

    [Fact]
    public void LiveCaptureQueueIsBounded()
    {
        using var live = new LiveInput(); live.SetEnabled(true);
        for (var i = 0; i < 100; i++) live.Push(PcmBlock.Rent());
        Assert.Equal(95, live.DroppedBlocks);
    }

    [Fact]
    public void IstanbulMidnightUsesThePreviousUtcDate()
    {
        var bounds = PlayReports.DayBounds(new DateOnly(2026, 10, 15));
        Assert.Equal(DateTimeOffset.Parse("2026-10-14T21:00:00Z"), bounds.Start);
        Assert.Equal(TimeSpan.FromHours(24), bounds.End - bounds.Start);
    }

    [Theory]
    [InlineData("=2+2")]
    [InlineData(" @SUM(1)")]
    [InlineData("+123")]
    [InlineData("\tformula")]
    public void CsvProtectsSpreadsheetFormulaFields(string text) => Assert.StartsWith("\"'", PlayReports.CsvField(text));

    [Fact]
    public void CsvPreservesQuotesCommasAndTurkishCharacters()
        => Assert.Equal("\"Şebnem, \"\"Yağmur\"\"\"", PlayReports.CsvField("Şebnem, \"Yağmur\""));

    [Fact]
    public async Task DuplicateAcknowledgementDoesNotCreateAnotherEventOrBreakTheChain()
    {
        var database = Path.Combine(NewRoot(), "events.db");
        var entry = Entry("occurrence-1", "started", DateTimeOffset.UtcNow);
        await using (var journal = new EventJournal(database))
        {
            Assert.True(journal.TryAppend(entry)); Assert.True(journal.TryAppend(entry));
            Assert.True(journal.TryAppend(Entry("occurrence-1", "ended", entry.AtUtc.AddSeconds(3), 144000)));
        }
        Assert.Equal(2, EventJournal.ReadEvents(database).Count);
        Assert.True(EventJournal.Verify(database));
    }

    [Fact]
    public async Task JournalDetectsTamperedHistory()
    {
        var database = Path.Combine(NewRoot(), "events.db");
        await using (var journal = new EventJournal(database))
            Assert.True(journal.TryAppend(Entry("a", "started", DateTimeOffset.UtcNow)));
        using var connection = new SqliteConnection($"Data Source={database}"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "UPDATE playback_events SET payload='{}'"; command.ExecuteNonQuery();
        Assert.False(EventJournal.Verify(database));
    }

    [Fact]
    public async Task DailyReportUsesStartTimeAndDoesNotDoubleCountOutputVariants()
    {
        var root = NewRoot(); var database = Path.Combine(root, "events.db");
        var at = DateTimeOffset.Parse("2026-10-14T21:59:59Z");
        await using (var journal = new EventJournal(database))
        {
            journal.TryAppend(Entry("a", "started", at, 0));
            journal.TryAppend(Entry("a", "ended", at.AddSeconds(3), 144000));
            journal.TryAppend(Entry("b", "started", DateTimeOffset.Parse("2026-10-15T21:00:00Z")));
        }
        var report = PlayReports.ExportDaily(database, new DateOnly(2026, 10, 15), root);
        Assert.Equal(1, report.Records); Assert.Equal(0, report.IncompleteRecords);
        var lines = File.ReadAllLines(Path.Combine(root, report.FileName));
        Assert.Contains("Şebnem Ferah", lines[1]); Assert.Contains("00:59:59", lines[1]); Assert.Contains("\"3.000\"", lines[1]);
        Assert.Equal(25, File.ReadAllLines(Path.Combine(root, "2026-10-15-hours.csv")).Length);
    }

    [Fact]
    public async Task CrashWithoutEndIsVisibleAsIncomplete()
    {
        var root = NewRoot(); var database = Path.Combine(root, "events.db");
        await using (var journal = new EventJournal(database))
            journal.TryAppend(Entry("a", "started", DateTimeOffset.Parse("2026-10-15T08:00:00Z")));
        var report = PlayReports.ExportDaily(database, new DateOnly(2026, 10, 15), root);
        Assert.Equal(1, report.IncompleteRecords);
        Assert.Contains("incomplete", File.ReadAllText(Path.Combine(root, report.FileName)));
    }

    [Fact]
    public async Task ExportStreamsMoreThanTenThousandPlays()
    {
        var root = NewRoot(); var database = Path.Combine(root, "events.db");
        await using (var journal = new EventJournal(database)) { }
        // Bulk fixture insertion tests export scale independently of real-time journal throughput.
        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            connection.Open(); using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO playback_events(event_id,occurrence_id,station_id,event_type,at_utc,payload,previous_hash,entry_hash) VALUES($id,$occ,'station-a','started',$at,$payload,'','')";
            var id = insert.Parameters.Add("$id", SqliteType.Text); var occurrence = insert.Parameters.Add("$occ", SqliteType.Text);
            var at = insert.Parameters.Add("$at", SqliteType.Text); var payload = insert.Parameters.Add("$payload", SqliteType.Text);
            for (var i = 0; i < 10025; i++)
            {
                var entry = Entry(i.ToString(), "started", DateTimeOffset.Parse("2026-10-15T08:00:00Z").AddMilliseconds(i));
                id.Value = entry.EventId; occurrence.Value = entry.OccurrenceId; at.Value = entry.AtUtc.ToString("O"); payload.Value = JsonSerializer.Serialize(entry);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        var result = PlayReports.ExportRange(database, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DateTimeOffset.Parse("2027-01-01T00:00:00Z"), Path.Combine(root, "year.csv"));
        Assert.Equal(10025, result.Records);
        Assert.Equal(10026, File.ReadLines(Path.Combine(root, "year.csv")).Count());
    }

    [Fact]
    public void JournalRejectsSharedOrLegacyDatabaseLocations()
        => Assert.Throws<ArgumentException>(() => new EventJournal(Path.Combine(Path.GetTempPath(), "not-a-dev-database.db")));

    [Fact]
    public async Task UnknownJournalSchemaRequiresAnExplicitMigration()
    {
        var database = Path.Combine(NewRoot(), "events.db");
        await using (var journal = new EventJournal(database)) { }
        using var connection = new SqliteConnection($"Data Source={database}"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "UPDATE schema_info SET version=999"; command.ExecuteNonQuery();
        Assert.Throws<InvalidDataException>(() => new EventJournal(database));
    }
}
