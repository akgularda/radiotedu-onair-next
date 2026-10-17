using System.Globalization;

namespace OnAirNext.Engine;

public static class ReportCalendar
{
    public static TimeZoneInfo Istanbul => TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
    public static (DateTimeOffset Start, DateTimeOffset End) DayBounds(DateOnly date)
        => (UtcMidnight(date), UtcMidnight(date.AddDays(1)));
    public static (DateTimeOffset Start, DateTimeOffset End) PeriodBounds(string period, string selection)
    {
        if (string.IsNullOrWhiteSpace(period) || string.IsNullOrWhiteSpace(selection))
            throw new ArgumentException("Choose an Istanbul calendar date or four-digit year.");
        if (period == "day" && DateOnly.TryParseExact(selection, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date) && date.Year < 9999) return DayBounds(date);
        if (period == "year" && selection.Length == 4 && int.TryParse(selection, NumberStyles.None,
                CultureInfo.InvariantCulture, out var year) && year is >= 1 and < 9999)
            return (UtcMidnight(new(year, 1, 1)), UtcMidnight(new(year + 1, 1, 1)));
        throw new ArgumentException("Choose an Istanbul calendar date or four-digit year.");
    }

    private static DateTimeOffset UtcMidnight(DateOnly date)
        => new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), Istanbul));

}
