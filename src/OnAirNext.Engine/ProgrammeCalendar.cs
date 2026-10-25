using System.Globalization;
using System.Text.Json.Serialization;

namespace OnAirNext.Engine;

[JsonConverter(typeof(JsonStringEnumConverter<CalendarRuleKind>))]
public enum CalendarRuleKind { SpecialDay, TimedSweeper }
[JsonConverter(typeof(JsonStringEnumConverter<CalendarStartPolicy>))]
public enum CalendarStartPolicy { FinishCurrentSong, FadeAtTime }
[JsonConverter(typeof(JsonStringEnumConverter<CalendarReturnPolicy>))]
public enum CalendarReturnPolicy { FinishCurrentItem, FadeAtTime }

// Timing only: media, metadata, permissions and durable execution ownership are
// configured separately. Forecasts must never be recorded as actual plays.
public sealed record ProgrammeCalendarRule(string Id, long Version, string Name,
    IReadOnlyList<string> Stations, CalendarRuleKind Kind, DateOnly FirstDate, DateOnly LastDate,
    TimeOnly StartsAt, TimeOnly EndsAt, bool AllDay = false, bool Annual = false,
    bool Enabled = false, int Priority = 0,
    CalendarStartPolicy StartPolicy = CalendarStartPolicy.FinishCurrentSong,
    CalendarReturnPolicy ReturnPolicy = CalendarReturnPolicy.FinishCurrentItem,
    int? EveryMinutes = null, int LateToleranceSeconds = 120)
{
    public void Validate(IReadOnlySet<string> installedStations)
    {
        ArgumentNullException.ThrowIfNull(installedStations);
        if (!Guid.TryParseExact(Id, "N", out _) || Version < 1 || string.IsNullOrWhiteSpace(Name) ||
            Name.Length > 160 || Name.Any(char.IsControl))
            throw new ArgumentException("Choose a valid event identity, version and name.");
        if (Stations is null || Stations.Count is < 1 or > 8 ||
            Stations.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 64 || x.Contains('/') || x.Any(char.IsControl) || !installedStations.Contains(x)) ||
            Stations.Distinct(StringComparer.Ordinal).Count() != Stations.Count)
            throw new ArgumentException("Choose distinct installed stations for this event.");
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(StartPolicy) || !Enum.IsDefined(ReturnPolicy) ||
            Priority is < 0 or > 1000 || LastDate < FirstDate || LastDate == DateOnly.MaxValue ||
            Annual && (LastDate.DayNumber - FirstDate.DayNumber > 366 || LastDate.Year - FirstDate.Year > 1))
            throw new ArgumentException("Choose a supported event kind, priority and date range.");
        if (AllDay ? StartsAt != TimeOnly.MinValue || EndsAt != TimeOnly.MinValue : StartsAt == EndsAt)
            throw new ArgumentException("Choose a nonempty time window, or midnight boundaries for All Day.");
        if (EveryMinutes is < 1 or > 1440 || LateToleranceSeconds is < 1 or > 3600 ||
            Kind == CalendarRuleKind.SpecialDay && EveryMinutes is not null)
            throw new ArgumentException("Only timed sweepers may repeat every 1–1,440 minutes; late tolerance must be 1–3,600 seconds.");
    }
}

public sealed record CalendarOccurrence(string Key, string RuleId, long RuleVersion, string Name,
    string StationId, CalendarRuleKind Kind, DateTimeOffset DueAtUtc, DateTimeOffset EndsAtUtc,
    int Priority, CalendarStartPolicy StartPolicy, CalendarReturnPolicy ReturnPolicy)
{
    // An estimate for UI/planner use, not a command to interrupt an active item.
    public DateTimeOffset? EstimateStart(DateTimeOffset currentItemEndsAt)
    {
        var start = StartPolicy == CalendarStartPolicy.FadeAtTime || currentItemEndsAt <= DueAtUtc
            ? DueAtUtc : currentItemEndsAt.ToUniversalTime();
        return start < EndsAtUtc ? start : null;
    }

    public DateTimeOffset EstimateReturn(DateTimeOffset currentItemEndsAt)
        => ReturnPolicy == CalendarReturnPolicy.FadeAtTime || currentItemEndsAt <= EndsAtUtc
            ? EndsAtUtc : currentItemEndsAt.ToUniversalTime();
}

public enum CalendarConflictKind { OverlappingProgrammes, SweeperInProgrammeWindow, OverlappingSweeperWindows }
public sealed record CalendarConflict(string StationId, string FirstKey, string SecondKey,
    CalendarConflictKind Kind, DateTimeOffset FromUtc, DateTimeOffset ThroughUtc, string? PreferredRuleId);
public sealed record ProgrammeForecast(DateTimeOffset FromUtc, DateTimeOffset ThroughUtc, string TimeZone,
    IReadOnlyList<CalendarOccurrence> Occurrences, IReadOnlyList<CalendarConflict> Conflicts);

// Expand on a background/control lane, never in the PCM callback. Both execution
// preparation and the 24-hour UI preview must consume this same calendar.
public static class ProgrammeCalendar
{
    public const int MaximumRules = 256;
    public const int MaximumOccurrences = 4096;
    public const int MaximumConflicts = 4096;

    public static ProgrammeForecast Forecast(IEnumerable<ProgrammeCalendarRule> rules,
        IEnumerable<string> installedStations, DateTimeOffset from, TimeSpan horizon)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(installedStations);
        if (horizon <= TimeSpan.Zero || horizon > TimeSpan.FromHours(24) || from.Year is < 2 or > 9998)
            throw new ArgumentException("Preview a positive interval of at most 24 hours within supported calendar years.");
        var known = installedStations.ToHashSet(StringComparer.Ordinal);
        var source = rules.Take(MaximumRules + 1).ToArray();
        if (source.Length > MaximumRules || source.Any(x => x is null) ||
            source.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != source.Length)
            throw new ArgumentException("Choose at most 256 events with distinct identities.");
        // Take an immutable station-target snapshot before validation/expansion.
        source = source.Select(x => x with { Stations = x.Stations is null ? null! : Array.AsReadOnly(x.Stations.ToArray()) }).ToArray();
        foreach (var rule in source) rule.Validate(known);
        var zone = ReportCalendar.Istanbul;
        var begin = from.ToUniversalTime();
        var through = begin + horizon;
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(begin, zone).DateTime).AddDays(-1);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(through, zone).DateTime);
        var result = new List<CalendarOccurrence>();
        foreach (var rule in source.Where(x => x.Enabled))
        {
            for (var day = first; day <= last; day = day.AddDays(1))
            {
                if (!AppliesOn(rule, day)) continue;
                var start = day.ToDateTime(rule.AllDay ? TimeOnly.MinValue : rule.StartsAt);
                var end = day.ToDateTime(rule.AllDay ? TimeOnly.MinValue : rule.EndsAt);
                if (rule.AllDay || end <= start) end = end.AddDays(1);
                var startUtc = Utc(start, zone);
                var endUtc = Utc(end, zone);
                if (startUtc >= through || endUtc <= begin) continue;
                if (rule.Kind == CalendarRuleKind.SpecialDay) Add(rule, startUtc, endUtc);
                else
                {
                    for (var local = start; local < end;)
                    {
                        var due = Utc(local, zone);
                        var tolerance = Math.Min(rule.LateToleranceSeconds, (rule.EveryMinutes ?? 1440) * 60);
                        var expiry = due.AddSeconds(tolerance);
                        if (expiry > endUtc) expiry = endUtc;
                        if (due < through && expiry > begin) Add(rule, due, expiry);
                        if (rule.EveryMinutes is not { } minutes) break;
                        local = local.AddMinutes(minutes);
                    }
                }
            }
        }
        var ordered = result.OrderBy(x => x.DueAtUtc).ThenBy(x => x.StationId, StringComparer.Ordinal)
            .ThenByDescending(x => x.Priority).ThenBy(x => x.Key, StringComparer.Ordinal).ToArray();
        return new(begin, through, zone.Id, Array.AsReadOnly(ordered), Conflicts(ordered, begin, through));

        void Add(ProgrammeCalendarRule rule, DateTimeOffset due, DateTimeOffset end)
        {
            foreach (var station in rule.Stations)
            {
                if (result.Count >= MaximumOccurrences)
                    throw new InvalidOperationException("This preview exceeds 4,096 event occurrences. Narrow the schedule; no entries were silently omitted.");
                var key = string.Join('/', rule.Id.ToLowerInvariant(), rule.Version.ToString(CultureInfo.InvariantCulture),
                    station, due.UtcTicks.ToString(CultureInfo.InvariantCulture));
                result.Add(new(key, rule.Id, rule.Version, rule.Name, station, rule.Kind, due, end,
                    rule.Priority, rule.StartPolicy, rule.ReturnPolicy));
            }
        }
    }

    private static bool AppliesOn(ProgrammeCalendarRule rule, DateOnly day)
    {
        if (!rule.Annual) return day >= rule.FirstDate && day <= rule.LastDate;
        // An annual range may cross New Year. A February 29 boundary is skipped
        // in non-leap years rather than silently shifted to February 28/March 1.
        for (var year = Math.Max(1, day.Year - 1); year <= day.Year; year++)
        {
            var start = AnnualDate(year, rule.FirstDate);
            var end = AnnualDate(year + rule.LastDate.Year - rule.FirstDate.Year, rule.LastDate);
            if (start is { } a && end is { } b && day >= a && day <= b) return true;
        }
        return false;
    }

    private static DateOnly? AnnualDate(int year, DateOnly template)
        => year is < 1 or > 9999 || template.Day > DateTime.DaysInMonth(year, template.Month)
            ? null : new DateOnly(year, template.Month, template.Day);

    private static DateTimeOffset Utc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
            throw new ArgumentException("An event falls in an invalid or ambiguous local-time interval. Review its timing.");
        return new(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    private static IReadOnlyList<CalendarConflict> Conflicts(CalendarOccurrence[] occurrences, DateTimeOffset begin, DateTimeOffset through)
    {
        var conflicts = new List<CalendarConflict>();
        foreach (var group in occurrences.GroupBy(x => x.StationId, StringComparer.Ordinal))
        {
            var active = new List<CalendarOccurrence>();
            foreach (var next in group)
            {
                active.RemoveAll(x => x.EndsAtUtc <= next.DueAtUtc);
                foreach (var prior in active)
                {
                    if (conflicts.Count >= MaximumConflicts)
                        throw new InvalidOperationException("This preview exceeds 4,096 timing conflicts. Simplify the overlapping schedule before publishing.");
                    var programmes = prior.Kind == CalendarRuleKind.SpecialDay && next.Kind == CalendarRuleKind.SpecialDay;
                    var kind = programmes ? CalendarConflictKind.OverlappingProgrammes
                        : prior.Kind != next.Kind ? CalendarConflictKind.SweeperInProgrammeWindow
                        : CalendarConflictKind.OverlappingSweeperWindows;
                    var winner = programmes && prior.Priority != next.Priority
                        ? prior.Priority > next.Priority ? prior.RuleId : next.RuleId : null;
                    var end = prior.EndsAtUtc < next.EndsAtUtc ? prior.EndsAtUtc : next.EndsAtUtc;
                    conflicts.Add(new(next.StationId, prior.Key, next.Key, kind, next.DueAtUtc < begin ? begin : next.DueAtUtc,
                        end < through ? end : through, winner));
                }
                active.Add(next);
            }
        }
        return conflicts.AsReadOnly();
    }
}
