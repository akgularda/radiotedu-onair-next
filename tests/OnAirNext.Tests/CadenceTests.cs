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
            Assert.Equal(new BrandingCadence("station", 0, 6), await journal.CadenceAsync("station"));
        }
        await using (var journal = new EventJournal(db))
        {
            Assert.Equal(new BrandingCadence("station", 0, 6), await journal.CadenceAsync("station"));
            await Complete(journal, ContentKind.Sweeper, "sweeper");
            Assert.Equal(new BrandingCadence("station", 0, 0), await journal.CadenceAsync("station"));
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
}
