namespace OnAirNext.Engine;

public sealed record ReportExport(string Id, string Period, string Selection, string? StationId,
    DateTimeOffset RequestedAtUtc, string State = "Queued", int Attempt = 0, long Records = 0,
    long IncompleteRecords = 0, long Bytes = 0, string? Sha256 = null,
    DateTimeOffset? FinishedAtUtc = null, string? Error = null, bool CancelRequested = false, long? SnapshotSequence = null, DateTimeOffset? SnapshotAtUtc = null)
{
    public string Timezone => "Europe/Istanbul";
    public string FileName => $"radiotedu-{Selection}-{Id}-{Attempt}.csv";
    public DateTimeOffset FromUtc => ReportCalendar.PeriodBounds(Period, Selection).Start;
    public DateTimeOffset ToUtc => ReportCalendar.PeriodBounds(Period, Selection).End;
    public static void Validate(string id, string period, string selection, string? station)
    {
        if (!Guid.TryParseExact(id, "N", out var parsed) || parsed.ToString("N") != id)
            throw new ArgumentException("A report requires a lowercase export ID.");
        _ = ReportCalendar.PeriodBounds(period, selection);
        if (station is not null && (station.Length is < 1 or > 100 || station.Any(char.IsControl)))
            throw new ArgumentException("Choose a known station or all stations.");
    }
}
