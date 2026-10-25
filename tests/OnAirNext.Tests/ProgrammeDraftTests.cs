using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class ProgrammeDraftTests
{
    private static readonly IReadOnlySet<string> Stations = new HashSet<string>(["pop", "energize"], StringComparer.Ordinal);
    private static string Database()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RadioTEDU", "OnAirNext-Dev", "programme-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); return Path.Combine(directory, "playback.db");
    }
    private static string Command() => Guid.NewGuid().ToString("N");
    private static ProgrammeDraft Draft(string? id = null, long version = 1) => new(new(id ?? Command(), version,
        "29 October", ["pop"], CalendarRuleKind.SpecialDay, new(2026, 10, 29), new(2026, 10, 29),
        new(8, 0), new(14, 0)), Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Republic Day")), DisplayText: "29 Ekim Özel");

    [Fact]
    public async Task DraftSettingsAndActorSurviveReopenWithoutArmingAnyStation()
    {
        var database = Database(); var draft = Draft();
        await using (var journal = new EventJournal(database))
        {
            await journal.InitializeProgrammeDraftsAsync();
            var saved = await journal.SaveProgrammeDraftAsync(draft, 0, Command(), "operator-a", Stations);
            Assert.Equal("operator-a", saved.Actor); Assert.False(saved.Draft.Rule.Enabled);
            Assert.True(saved.Draft.PreservesStationBranding);
        }
        await using var reopened = new EventJournal(database); await reopened.InitializeProgrammeDraftsAsync();
        var item = Assert.Single((await reopened.ProgrammeDraftsAsync(Stations, "pop")).Items);
        Assert.Equal(draft.DisplayText, item.Draft.DisplayText); Assert.Equal(draft.SourceFolder, item.Draft.SourceFolder);
        Assert.Empty((await reopened.ProgrammeDraftsAsync(Stations, "energize")).Items);
        Assert.Empty(EventJournal.ReadEvents(database));
    }

    [Fact]
    public async Task RetryReturnsOriginalReceiptAfterLaterEditsAndRejectsReusedCommand()
    {
        await using var journal = new EventJournal(Database()); await journal.InitializeProgrammeDraftsAsync();
        var draft = Draft(); var command = Command();
        var first = await journal.SaveProgrammeDraftAsync(draft, 0, command, "operator-a", Stations);
        var changed = draft with { Rule = draft.Rule with { Version = 2, Name = "Updated title" } };
        await journal.SaveProgrammeDraftAsync(changed, 1, Command(), "operator-a", Stations);
        var retry = await journal.SaveProgrammeDraftAsync(draft, 0, command, "operator-a", Stations);
        Assert.Equal(first.AtUtc, retry.AtUtc); Assert.Equal(first.Version, retry.Version); Assert.Equal(first.Draft.DisplayText, retry.Draft.DisplayText);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SaveProgrammeDraftAsync(draft with { DisplayText = "Changed" }, 0, command, "operator-a", Stations));
        Assert.Equal(2, (await journal.ProgrammeDraftHistoryAsync(draft.Rule.Id, 2, Stations)).Total);
    }

    [Fact]
    public async Task ConcurrentEditorsCannotOverwriteEachOtherAndRejectedCommandCanBeRetriedAfterReview()
    {
        await using var journal = new EventJournal(Database()); await journal.InitializeProgrammeDraftsAsync();
        var draft = Draft(); await journal.SaveProgrammeDraftAsync(draft, 0, Command(), "operator-a", Stations);
        var next = draft with { Rule = draft.Rule with { Version = 2 } }; var one = Command(); var two = Command();
        async Task<bool> Save(string command, string text)
        { try { await journal.SaveProgrammeDraftAsync(next with { DisplayText = text }, 1, command, "operator-a", Stations); return true; } catch (InvalidOperationException) { return false; } }
        var result = await Task.WhenAll(Save(one, "First"), Save(two, "Second"));
        Assert.Single(result, x => x);
        var loser = result[0] ? two : one;
        await journal.SaveProgrammeDraftAsync(next with { Rule = next.Rule with { Version = 3 } }, 2, loser, "operator-a", Stations);
        Assert.Equal(3, Assert.Single((await journal.ProgrammeDraftsAsync(Stations)).Items).Version);
    }

    [Fact]
    public async Task ArchiveAndRestoreAppendHistoryAndKeepOriginalVersionAndBranding()
    {
        await using var journal = new EventJournal(Database()); await journal.InitializeProgrammeDraftsAsync();
        var draft = Draft(); await journal.SaveProgrammeDraftAsync(draft, 0, Command(), "operator-a", Stations);
        await journal.SaveProgrammeDraftAsync(draft with { Rule = draft.Rule with { Version = 2 }, DisplayText = null }, 1, Command(), "operator-b", Stations);
        var archiveCommand = Command();
        var archived = await journal.ArchiveProgrammeDraftAsync(draft.Rule.Id, 2, archiveCommand, "operator-b", Stations);
        Assert.True(archived.Archived); Assert.Empty((await journal.ProgrammeDraftsAsync(Stations)).Items);
        Assert.Single((await journal.ProgrammeDraftsAsync(Stations, archived: true)).Items);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SaveProgrammeDraftAsync(draft with { Rule = draft.Rule with { Version = 4 } }, 3, Command(), "operator-b", Stations));
        var restored = await journal.RestoreProgrammeDraftAsync(draft.Rule.Id, 3, 1, Command(), "operator-b", Stations);
        Assert.Equal(4, restored.Version); Assert.False(restored.Archived); Assert.False(restored.Draft.Rule.Enabled);
        Assert.Equal("29 Ekim Özel", restored.Draft.DisplayText); Assert.Equal(1L, restored.RestoredFromVersion);
        Assert.True(restored.Draft.PreservesStationBranding);
        var archiveRetry = await journal.ArchiveProgrammeDraftAsync(draft.Rule.Id, 2, archiveCommand, "operator-b", Stations);
        Assert.Equal(archived.AtUtc, archiveRetry.AtUtc); Assert.Equal(archived.Version, archiveRetry.Version); Assert.True(archiveRetry.Archived);
        var history = await journal.ProgrammeDraftHistoryAsync(draft.Rule.Id, 4, Stations);
        Assert.Equal(new long[] { 4, 3, 2, 1 }, history.Items.Select(x => x.Version));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.ProgrammeDraftHistoryAsync(draft.Rule.Id, 3, Stations));
    }

    [Fact]
    public async Task HypotheticalPreviewDoesNotSaveOrEnableUnsavedChanges()
    {
        await using var journal = new EventJournal(Database()); await journal.InitializeProgrammeDraftsAsync();
        var draft = Draft(); await journal.SaveProgrammeDraftAsync(draft, 0, Command(), "operator-a", Stations);
        var changed = draft with { Rule = draft.Rule with { Version = 2, StartsAt = new(9, 0) } };
        var forecast = await journal.PreviewProgrammeDraftsAsync(Stations, ProgrammeCalendar.ParseIstanbulMinute("2026-10-29T00:00"), "pop", changed);
        var occurrence = Assert.Single(forecast.Occurrences);
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T06:00:00Z"), occurrence.DueAtUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T11:00:00Z"), occurrence.EndsAtUtc);
        var actual = Assert.Single((await journal.ProgrammeDraftsAsync(Stations)).Items);
        Assert.Equal(1, actual.Version); Assert.Equal(new TimeOnly(8, 0), actual.Draft.Rule.StartsAt); Assert.False(actual.Draft.Rule.Enabled);
        Assert.Empty(EventJournal.ReadEvents(journal.DatabasePath));
    }

    [Fact]
    public async Task PagesAreBoundedAndArchiveDoesNotAppearInPreview()
    {
        await using var journal = new EventJournal(Database()); await journal.InitializeProgrammeDraftsAsync();
        ProgrammeDraft? last = null;
        for (var i = 0; i < 6; i++) { last = Draft(); await journal.SaveProgrammeDraftAsync(last, 0, Command(), "operator-a", Stations); }
        var page = await journal.ProgrammeDraftsAsync(Stations); Assert.Equal(6, page.Total); Assert.Equal(4, page.Items.Count);
        Assert.Equal(2, (await journal.ProgrammeDraftsAsync(Stations, offset: 4)).Items.Count);
        await journal.ArchiveProgrammeDraftAsync(last!.Rule.Id, 1, Command(), "operator-a", Stations);
        var forecast = await journal.PreviewProgrammeDraftsAsync(Stations, ProgrammeCalendar.ParseIstanbulMinute("2026-10-29T00:00"));
        Assert.Equal(5, forecast.Occurrences.Count); Assert.DoesNotContain(forecast.Occurrences, x => x.RuleId == last.Rule.Id);
    }

    [Fact]
    public void UnsafeOrArmedDraftsAreRejectedAndNormalDisplayCanFollowSongs()
    {
        var draft = Draft();
        Assert.Throws<ArgumentException>(() => (draft with { Rule = draft.Rule with { Enabled = true } }).Normalize(Stations));
        Assert.Throws<ArgumentException>(() => (draft with { Rule = draft.Rule with { Stations = ["missing"] } }).Normalize(Stations));
        Assert.Throws<ArgumentException>(() => (draft with { SourceFolder = @"\\server\music" }).Normalize(Stations));
        Assert.Throws<ArgumentException>(() => (draft with { SourceFile = "../sweep.mp3" }).Normalize(Stations));
        Assert.Throws<ArgumentException>(() => (draft with { DisplayText = "line\nbreak" }).Normalize(Stations));
        Assert.Throws<ArgumentException>(() => (draft with { TransitionSeconds = double.NaN }).Normalize(Stations));
        Assert.Null((draft with { DisplayText = null }).Normalize(Stations).DisplayText);
        var sweeper = draft with { Rule = draft.Rule with { Kind = CalendarRuleKind.TimedSweeper }, SourceFile = "LÖSEV.wav" };
        Assert.Equal("LÖSEV.wav", sweeper.Normalize(Stations).SourceFile);
    }

    [Theory]
    [InlineData("2026-02-30T08:00")]
    [InlineData("2026-10-29T08:00Z")]
    [InlineData("2026-10-29T08:00:01")]
    public void PreviewRequiresAnExplicitValidIstanbulMinute(string text)
        => Assert.Throws<ArgumentException>(() => ProgrammeCalendar.ParseIstanbulMinute(text));
}
