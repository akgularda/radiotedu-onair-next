using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class ProgrammeCalendarTests
{
    private static ProgrammeCalendarRule Rule() => new(Guid.NewGuid().ToString("N"), 1, "29 Ekim",
        ["4"], CalendarRuleKind.SpecialDay, new(2026, 10, 29), new(2026, 10, 29), new(8, 0), new(14, 0), Enabled: true);
    private static DateTimeOffset Local(int year, int month, int day, int hour = 0, int minute = 0)
        => new(year, month, day, hour, minute, 0, TimeSpan.FromHours(3));
    private static ProgrammeForecast Preview(IEnumerable<ProgrammeCalendarRule> rules, DateTimeOffset from)
        => ProgrammeCalendar.Forecast(rules, ["1", "4", "9"], from, TimeSpan.FromHours(24));

    [Fact]
    public void ImportedDisabledRuleCannotStartAndUnknownStationsAreRejected()
    {
        var rule = Rule();
        Assert.Empty(Preview([rule with { Enabled = false }], Local(2026, 10, 29)).Occurrences);
        Assert.Throws<ArgumentException>(() => Preview([rule with { Stations = ["external"] }], Local(2026, 10, 29)));
    }

    [Fact]
    public void EightToFourteenHasSoftBoundariesAndAnExplicitTimedFadeAlternative()
    {
        var item = Assert.Single(Preview([Rule()], Local(2026, 10, 29)).Occurrences);
        Assert.Equal(Local(2026, 10, 29, 8).ToUniversalTime(), item.DueAtUtc);
        Assert.Equal(Local(2026, 10, 29, 14).ToUniversalTime(), item.EndsAtUtc);
        Assert.Equal(Local(2026, 10, 29, 8, 2).ToUniversalTime(), item.EstimateStart(Local(2026, 10, 29, 8, 2)));
        Assert.Equal(Local(2026, 10, 29, 14, 3).ToUniversalTime(), item.EstimateReturn(Local(2026, 10, 29, 14, 3)));
        var fixedTime = item with { StartPolicy = CalendarStartPolicy.FadeAtTime, ReturnPolicy = CalendarReturnPolicy.FadeAtTime };
        Assert.Equal(item.DueAtUtc, fixedTime.EstimateStart(Local(2026, 10, 29, 8, 2)));
        Assert.Equal(item.EndsAtUtc, fixedTime.EstimateReturn(Local(2026, 10, 29, 14, 3)));
        Assert.Null(item.EstimateStart(Local(2026, 10, 29, 14)));
    }

    [Fact]
    public void AnnualDatesUseIstanbulAndOneTimeEventsDoNotRepeat()
    {
        var rule = Rule();
        Assert.Empty(Preview([rule], Local(2027, 10, 29)).Occurrences);
        var occurrence = Assert.Single(Preview([rule with { Annual = true }], Local(2027, 10, 29)).Occurrences);
        Assert.Equal(Local(2027, 10, 29, 8).ToUniversalTime(), occurrence.DueAtUtc);
        Assert.Equal("Europe/Istanbul", Preview([], Local(2027, 10, 29)).TimeZone);
    }

    [Fact]
    public void AllDayAndOvernightWindowsKeepExclusiveEndBoundaries()
    {
        var allDay = Rule() with { StartsAt = TimeOnly.MinValue, EndsAt = TimeOnly.MinValue, AllDay = true };
        var occurrence = Assert.Single(Preview([allDay], Local(2026, 10, 29)).Occurrences);
        Assert.Equal(Local(2026, 10, 30).ToUniversalTime(), occurrence.EndsAtUtc);
        Assert.Empty(Preview([allDay], Local(2026, 10, 30)).Occurrences);
        var overnight = Rule() with { StartsAt = new(22, 0), EndsAt = new(2, 0) };
        var active = Assert.Single(Preview([overnight], Local(2026, 10, 30, 1)).Occurrences);
        Assert.Equal(Local(2026, 10, 29, 22).ToUniversalTime(), active.DueAtUtc);
        Assert.Equal(Local(2026, 10, 30, 2).ToUniversalTime(), active.EndsAtUtc);
    }

    [Fact]
    public void AnnualNewYearRangesWorkAndLeapDayIsNeverSilentlyMoved()
    {
        var rule = Rule() with { FirstDate = new(2026, 12, 31), LastDate = new(2027, 1, 1), Annual = true };
        Assert.Single(Preview([rule], Local(2028, 1, 1)).Occurrences);
        var leap = Rule() with { FirstDate = new(2028, 2, 29), LastDate = new(2028, 2, 29), Annual = true };
        Assert.Empty(Preview([leap], Local(2027, 2, 28)).Occurrences);
        Assert.Single(Preview([leap], Local(2028, 2, 29)).Occurrences);
    }

    [Fact]
    public void TimedSweepersHaveStableKeysAndExpiredSlotsCannotAccumulate()
    {
        var rule = Rule() with { Name = "LÖSEV", Kind = CalendarRuleKind.TimedSweeper,
            StartsAt = new(10, 0), EndsAt = new(13, 0), EveryMinutes = 60 };
        var first = Preview([rule], Local(2026, 10, 29));
        Assert.Equal(3, first.Occurrences.Count);
        Assert.All(first.Occurrences, x => Assert.Equal(TimeSpan.FromMinutes(2), x.EndsAtUtc - x.DueAtUtc));
        Assert.Equal(first.Occurrences.Select(x => x.Key), Preview([rule], Local(2026, 10, 29)).Occurrences.Select(x => x.Key));
        var restarted = Preview([rule], Local(2026, 10, 29, 11, 3));
        Assert.Single(restarted.Occurrences);
        Assert.Equal(Local(2026, 10, 29, 12).ToUniversalTime(), restarted.Occurrences[0].DueAtUtc);
        Assert.Null(first.Occurrences[0].EstimateStart(Local(2026, 10, 29, 10, 3)));
        Assert.NotEqual(first.Occurrences[0].Key, Preview([rule with { Version = 2 }], Local(2026, 10, 29)).Occurrences[0].Key);
    }

    [Fact]
    public void ShortCadenceDoesNotLeaveMultipleLateSlotsEligible()
    {
        var rule = Rule() with { Kind = CalendarRuleKind.TimedSweeper, StartsAt = new(10, 0),
            EndsAt = new(10, 3), EveryMinutes = 1, LateToleranceSeconds = 120 };
        var result = Preview([rule], Local(2026, 10, 29));
        Assert.Equal(3, result.Occurrences.Count);
        Assert.All(result.Occurrences, x => Assert.Equal(TimeSpan.FromMinutes(1), x.EndsAtUtc - x.DueAtUtc));
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void ConflictsAreStationScopedAndEqualPriorityHasNoImplicitWinner()
    {
        var first = Rule();
        var second = Rule() with { Stations = ["4", "9"], StartsAt = new(9, 0), EndsAt = new(15, 0) };
        var result = Preview([first, second], Local(2026, 10, 29));
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("4", conflict.StationId);
        Assert.Equal(CalendarConflictKind.OverlappingProgrammes, conflict.Kind);
        Assert.Null(conflict.PreferredRuleId);
        Assert.Equal(second.Id, Assert.Single(Preview([first, second with { Priority = 1 }], Local(2026, 10, 29)).Conflicts).PreferredRuleId);
    }

    [Fact]
    public void SweeperInsideSpecialDayIsAnInsertionWarningNotAProgrammeWinner()
    {
        var programme = Rule();
        var sweeper = Rule() with { Kind = CalendarRuleKind.TimedSweeper, StartsAt = new(9, 0), EndsAt = new(9, 5), Priority = 10 };
        var conflict = Assert.Single(Preview([programme, sweeper], Local(2026, 10, 29)).Conflicts);
        Assert.Equal(CalendarConflictKind.SweeperInProgrammeWindow, conflict.Kind);
        Assert.Null(conflict.PreferredRuleId);
    }

    [Fact]
    public void InvalidRangesDuplicateIdsAndUnboundedPreviewAreRejected()
    {
        var rule = Rule();
        Assert.Throws<ArgumentException>(() => Preview([rule, rule], Local(2026, 10, 29)));
        Assert.Throws<ArgumentException>(() => Preview([rule with { EndsAt = rule.StartsAt }], Local(2026, 10, 29)));
        Assert.Throws<ArgumentException>(() => Preview([rule with { LastDate = rule.FirstDate.AddDays(-1) }], Local(2026, 10, 29)));
        Assert.Throws<ArgumentException>(() => Preview([rule with { EveryMinutes = 1 }], Local(2026, 10, 29)));
        Assert.Throws<ArgumentException>(() => Preview([rule with { Stations = ["4", "4"] }], Local(2026, 10, 29)));
        Assert.Throws<ArgumentException>(() => ProgrammeCalendar.Forecast([rule], ["4"], Local(2026, 10, 29), TimeSpan.FromHours(25)));
    }

    [Fact]
    public void DenseSchedulesFailExplicitlyInsteadOfTruncatingThePreview()
    {
        var rule = Rule() with { Kind = CalendarRuleKind.TimedSweeper, EveryMinutes = 1,
            StartsAt = TimeOnly.MinValue, EndsAt = TimeOnly.MinValue, AllDay = true };
        var rules = Enumerable.Range(0, 3).Select(_ => rule with { Id = Guid.NewGuid().ToString("N") });
        Assert.Throws<InvalidOperationException>(() => Preview(rules, Local(2026, 10, 29)));
    }

    [Fact]
    public void ActiveWindowsRetainTheirTrueTimesButConflictDisplayStaysInsidePreview()
    {
        var first = Rule();
        var second = Rule();
        var result = ProgrammeCalendar.Forecast([first, second], ["4"], Local(2026, 10, 29, 10), TimeSpan.FromHours(1));
        Assert.All(result.Occurrences, x => Assert.Equal(Local(2026, 10, 29, 8).ToUniversalTime(), x.DueAtUtc));
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(result.FromUtc, conflict.FromUtc);
        Assert.Equal(result.ThroughUtc, conflict.ThroughUtc);
    }
}
