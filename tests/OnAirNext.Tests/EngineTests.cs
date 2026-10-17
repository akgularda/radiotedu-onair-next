using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OnAirNext.Engine;
using OnAirNext.ServiceControl;
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
    public void ProductionOutputRequiresTheProtectedCommissioningValidator()
    {
        var information = new IcecastStationInformation("RadioTEDU Test", "Live test stream", "Test", "https://radiotedu.com", true);
        var output = new OutputSettings("energize-main", "stream.radiotedu.com", 11154, "/energize", "fixture-secret",
            CodecProfile.AacLc192, Commissioned: true, Information: information);
        Assert.Throws<ArgumentException>(() => output.Validate());
        output.ValidateCommissioned();
        Assert.Throws<ArgumentException>(() => new IcecastOutput("ffmpeg", output));
    }

    [Theory]
    [InlineData("/en")]
    [InlineData("/FR")]
    [InlineData("/energize\r\nIce-Public: 1")]
    [InlineData("/energize/extra")]
    public void CommissionedOutputRejectsExternalAndInjectedMounts(string mount)
    {
        var output = new OutputSettings("station", "stream.radiotedu.com", 11154, mount, "fixture-secret",
            Commissioned: true, Information: new IcecastStationInformation("Name", "", "", "", false));
        Assert.Throws<ArgumentException>(output.ValidateCommissioned);
    }

    private static ProductionOutputConfiguration ValidProductionProfile()
    {
        var definitions = new (string Id, string Primary)[]
        [
            ("1", "/classic"), ("2", "/lofi"), ("4", "/radio"), ("5", "/cazz"),
            ("8", "/rock"), ("9", "/energize"), ("10", "/situation"), ("11", "/maincharacter")
        ];
        var stations = definitions.Select(station =>
        {
            var information = new IcecastStationInformation($"RadioTEDU {station.Id}", "Continuous music", "Music", "https://radiotedu.com", true);
            OutputSettings Create(string suffix, CodecProfile profile) => new(
                $"station-{station.Id}-{suffix}", "stream.radiotedu.com", 11154,
                suffix == "main" ? station.Primary : station.Primary + (suffix == "flac" ? "-flac" : "-low"),
                "fixture-secret", profile, Commissioned: true, Information: information);
            var outputs = new List<OutputSettings>
            {
                Create("main", CodecProfile.AacLc192), Create("low", CodecProfile.HeAacV2_64)
            };
            if (station.Id is "1" or "5") outputs.Add(Create("flac", CodecProfile.OggFlac));
            return new StationOutputConfiguration(station.Id, outputs);
        }).ToArray();
        return new ProductionOutputConfiguration(1, stations);
    }

    [Fact]
    public void ProductionOutputProfileRequiresTheCompleteEighteenMountRoster()
    {
        var profile = ValidProductionProfile();
        profile.Validate(["1", "2", "4", "5", "8", "9", "10", "11"]);
        var incomplete = profile with
        {
            Stations = profile.Stations.Select(station => station.StationId == "10"
                ? station with { Outputs = station.Outputs.Take(1).ToArray() } : station).ToArray()
        };
        Assert.Throws<ArgumentException>(() => incomplete.Validate(["1", "2", "4", "5", "8", "9", "10", "11"]));
        Assert.Throws<ArgumentException>(() => profile.Validate(["1", "2", "4", "5", "8", "9", "10", "11", "12"]));
    }

    [Fact]
    public void ProductionOutputProfileRejectsUnapprovedIcecastOrigins()
    {
        var profile = ValidProductionProfile();
        var altered = profile with
        {
            Stations = profile.Stations.Select(station => station.StationId == "9"
                ? station with { Outputs = station.Outputs.Select((output, index) => index == 0 ? output with { Host = "elsewhere.example" } : output).ToArray() }
                : station).ToArray()
        };
        Assert.Throws<ArgumentException>(() => altered.Validate(["1", "2", "4", "5", "8", "9", "10", "11"]));
    }

    [Fact]
    public void LegacySourceFenceRequiresStoppedProcessAndDisabledStartup()
    {
        LegacyServiceFence.RequireReleasedState(1, 0, 4);
        Assert.Throws<InvalidOperationException>(() => LegacyServiceFence.RequireReleasedState(4, 1234, 4));
        Assert.Throws<InvalidOperationException>(() => LegacyServiceFence.RequireReleasedState(1, 0, 2));
        Assert.Throws<InvalidOperationException>(() => LegacyServiceFence.RequireReleasedState(1, 0, 3));
    }

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
    public void LiveMixerPreservesOverFullScaleHeadroomAndReturnsToMusicWhenInputDisappears()
    {
        using var live = new LiveInput();
        live.SetEnabled(true);
        using var program = PcmBlock.Rent();
        for (var i = 0; i < 4; i++)
        {
            var mic = PcmBlock.Rent(); MemoryMarshal.Cast<byte, float>(mic.Memory.Span).Fill(0.9f);
            live.Push(mic); MemoryMarshal.Cast<byte, float>(program.Memory.Span).Fill(0.6f);
            live.Mix(program.Memory.Span);
            Assert.All(MemoryMarshal.Cast<byte, float>(program.Memory.Span).ToArray(), x => Assert.InRange(x, 1.04f, 1.51f));
            if (i >= 1) Assert.InRange(MemoryMarshal.Cast<byte, float>(program.Memory.Span)[^1], 1.049f, 1.051f);
        }
        for (var i = 0; i < 12; i++)
        {
            MemoryMarshal.Cast<byte, float>(program.Memory.Span).Fill(0.6f);
            live.Mix(program.Memory.Span);
        }
        Assert.InRange(MemoryMarshal.Cast<byte, float>(program.Memory.Span)[^1], 0.5999f, 0.6001f);
    }

    [Fact]
    public void LiveCaptureQueueIsBounded()
    {
        using var live = new LiveInput(); live.SetEnabled(true);
        for (var i = 0; i < 100; i++)
        { var input = PcmBlock.Rent(); input.Memory.Span.Clear(); live.Push(input); }
        Assert.Equal(95, live.DroppedBlocks);
    }

    [Fact]
    public void MicrophoneKeepsSubSixteenBitSignalAndChannelsSeparate()
    {
        using var live = new LiveInput(); live.SetEnabled(true);
        var mic = PcmBlock.Rent();
        var input = MemoryMarshal.Cast<byte, float>(mic.Memory.Span);
        for (var i = 0; i < input.Length; i += 2) { input[i] = 1f / 1048576; input[i + 1] = -input[i]; }
        var expected = input.ToArray();
        using var program = PcmBlock.Rent(); program.Memory.Span.Clear();
        live.Push(mic); live.Mix(program.Memory.Span);
        Assert.True(expected.AsSpan().SequenceEqual(MemoryMarshal.Cast<byte, float>(program.Memory.Span)));
        Assert.Equal(8, AudioFormat.BytesPerSampleFrame);
        Assert.Equal(7680, AudioFormat.BlockBytes);
    }

    [Fact]
    public void InvalidMicrophonePcmIsRejectedWithoutMutingOrPoisoningMusic()
    {
        using var live = new LiveInput(); live.SetEnabled(true);
        var bad = PcmBlock.Rent(); bad.Memory.Span.Clear();
        MemoryMarshal.Cast<byte, float>(bad.Memory.Span)[9] = float.NaN;
        live.Push(bad);
        var partial = PcmBlock.Rent(); partial.Length -= sizeof(float); live.Push(partial);
        using var program = PcmBlock.Rent(); MemoryMarshal.Cast<byte, float>(program.Memory.Span).Fill(0.1234567f);
        live.Mix(program.Memory.Span);
        Assert.Equal(2, live.RejectedBlocks);
        Assert.All(MemoryMarshal.Cast<byte, float>(program.Memory.Span).ToArray(), value => Assert.Equal(0.1234567f, value));
        Assert.Throws<ObjectDisposedException>(() => bad.Memory);
        Assert.Throws<ObjectDisposedException>(() => partial.Memory);
        Assert.Throws<ArgumentException>(() => live.Mix(new byte[7]));
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
