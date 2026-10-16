using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class CadenceTests
{
    private static string Database()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RadioTEDU", "OnAirNext-Dev", "unit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return Path.Combine(root, "cadence.db");
    }
    private static async Task Complete(EventJournal journal, ContentKind kind, string occurrence, string terminal = "ended")
    {
        var start = new PlaybackEvent(Guid.NewGuid().ToString("N"), occurrence, "station", "started", "asset-" + occurrence,
            "Fixture", occurrence, kind, DateTimeOffset.UtcNow, 0);
        await journal.AppendAsync(start);
        await journal.AppendAsync(start with { EventId = Guid.NewGuid().ToString("N"), Type = terminal,
            SamplePosition = 48000, AtUtc = start.AtUtc.AddSeconds(1) });
    }

    [Fact]
    public async Task DistinctBrandingCountersSurviveReopenAndResetOnlyTheirOwnCategory()
    {
        var db = Database();
        await using (var journal = new EventJournal(db))
        {
            for (var i = 0; i < 6; i++) await Complete(journal, ContentKind.Music, "music-" + i);
            await Complete(journal, ContentKind.Advertisement, "advert");
            Assert.Equal(new BrandingCadence("station", 6, 6), await journal.CadenceAsync("station"));
            await Complete(journal, ContentKind.Jingle, "jingle");
            Assert.Equal(new BrandingCadence("station", 0, 6, 1, 0), await journal.CadenceAsync("station"));
        }
        await using (var journal = new EventJournal(db))
        {
            Assert.Equal(new BrandingCadence("station", 0, 6, 1, 0), await journal.CadenceAsync("station"));
            await Complete(journal, ContentKind.Sweeper, "sweeper");
            Assert.Equal(new BrandingCadence("station", 0, 0, 1, 1), await journal.CadenceAsync("station"));
        }
        Assert.True(EventJournal.Verify(db));
    }

    [Theory]
    [InlineData("skipped")]
    [InlineData("failed")]
    [InlineData("interrupted")]
    public async Task IncompleteAttemptsCannotAdvanceTheSongCadence(string terminal)
    {
        await using var journal = new EventJournal(Database());
        await Complete(journal, ContentKind.Music, "partial", terminal);
        Assert.Equal(new BrandingCadence("station", 0, 0), await journal.CadenceAsync("station"));
        await Complete(journal, ContentKind.Music, "restart");
        Assert.Equal(new BrandingCadence("station", 1, 1), await journal.CadenceAsync("station"));
    }

    [Fact]
    public async Task LostCompletionAcknowledgementCannotAdvanceCountersTwice()
    {
        var db = Database();
        await using var journal = new EventJournal(db);
        await Complete(journal, ContentKind.Music, "one");
        var end = EventJournal.ReadEvents(db).Single(x => x.Type == "ended");
        await journal.AppendAsync(end);
        Assert.Equal(new BrandingCadence("station", 1, 1), await journal.CadenceAsync("station"));
    }

    [Theory]
    [InlineData(ContentKind.Jingle)]
    [InlineData(ContentKind.Sweeper)]
    public async Task BrandingOrdinalsCountCompletionsRatherThanAttemptsOrRetries(ContentKind kind)
    {
        var db = Database();
        await using (var journal = new EventJournal(db))
        {
            await Complete(journal, kind, "skipped", "skipped");
            await Complete(journal, kind, "failed", "failed");
            Assert.Equal(0, (await journal.CadenceAsync("station")).CompletedJingles);
            Assert.Equal(0, (await journal.CadenceAsync("station")).CompletedSweepers);
            await Complete(journal, kind, "complete-one");
            var end = EventJournal.ReadEvents(db).Last(x => x.Type == "ended");
            await journal.AppendAsync(end);
            await Complete(journal, kind, "complete-two");
        }
        await using (var journal = new EventJournal(db))
        {
            var saved = await journal.CadenceAsync("station");
            Assert.Equal(kind == ContentKind.Jingle ? 2 : 0, saved.CompletedJingles);
            Assert.Equal(kind == ContentKind.Sweeper ? 2 : 0, saved.CompletedSweepers);
        }
        Assert.True(EventJournal.Verify(db));
    }

    [Fact]
    public async Task OldDevelopmentCadenceMigrationPreservesDebtAndStartsNewOrdinalEpochOnce()
    {
        var db = Database();
        await using (var journal = new EventJournal(db))
            for (var i = 0; i < 6; i++) await Complete(journal, ContentKind.Music, "old-" + i);
        // This is a fresh isolated fixture, never an installed/production database.
        using (var old = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + db))
        {
            old.Open(); using var command = old.CreateCommand();
            command.CommandText = """
                DROP TRIGGER cadence_jingle_completed; DROP TRIGGER cadence_sweeper_completed;
                ALTER TABLE station_cadence DROP COLUMN completed_jingles;
                ALTER TABLE station_cadence DROP COLUMN completed_sweepers;
                """;
            command.ExecuteNonQuery();
        }
        await using (var journal = new EventJournal(db))
        {
            Assert.Equal(new BrandingCadence("station", 6, 6), await journal.CadenceAsync("station"));
            await Complete(journal, ContentKind.Jingle, "new-jingle");
            await Complete(journal, ContentKind.Sweeper, "new-sweeper");
        }
        await using (var journal = new EventJournal(db))
            Assert.Equal(new BrandingCadence("station", 0, 0, 1, 1), await journal.CadenceAsync("station"));
        Assert.True(EventJournal.Verify(db));
    }

    [Fact]
    public async Task FailedTerminalTransactionCannotConsumeTheBrandingOrdinalOrDeduplicationMark()
    {
        var db = Database();
        await using (var journal = new EventJournal(db))
        {
            var start = new PlaybackEvent(Guid.NewGuid().ToString("N"), "retryable", "station", "started", "jingle",
                "Fixture", "Retryable jingle", ContentKind.Jingle, DateTimeOffset.UtcNow, 0);
            var end = start with { EventId = Guid.NewGuid().ToString("N"), Type = "ended", SamplePosition = 48000,
                AtUtc = start.AtUtc.AddSeconds(1) };
            await journal.AppendAsync(start);
            // Inject a rollback only into this fresh disposable fixture.
            using (var fault = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + db))
            {
                fault.Open(); using var command = fault.CreateCommand();
                command.CommandText = """
                    CREATE TRIGGER fixture_branding_failure BEFORE UPDATE OF completed_jingles ON station_cadence
                    BEGIN SELECT RAISE(ABORT, 'fixture terminal failure'); END;
                    """;
                command.ExecuteNonQuery();
            }
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => journal.AppendAsync(end));
            Assert.Equal(new BrandingCadence("station", 0, 0), await journal.CadenceAsync("station"));
            Assert.DoesNotContain(EventJournal.ReadEvents(db), x => x.EventId == end.EventId);
            using (var fault = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + db))
            {
                fault.Open(); using var command = fault.CreateCommand();
                command.CommandText = "DROP TRIGGER fixture_branding_failure"; command.ExecuteNonQuery();
            }
            await journal.AppendAsync(end); await journal.AppendAsync(end);
            Assert.Equal(new BrandingCadence("station", 0, 0, 1, 0), await journal.CadenceAsync("station"));
        }
        await using (var reopened = new EventJournal(db))
            Assert.Equal(new BrandingCadence("station", 0, 0, 1, 0), await reopened.CadenceAsync("station"));
        Assert.True(EventJournal.Verify(db));
    }
}
