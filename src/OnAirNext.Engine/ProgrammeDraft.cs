using System.Text.Json.Serialization;

namespace OnAirNext.Engine;

[JsonConverter(typeof(JsonStringEnumConverter<ProgrammeOrder>))]
public enum ProgrammeOrder { Shuffle, FolderOrder }

public sealed record ProgrammeDraft(ProgrammeCalendarRule Rule, string SourceFolder,
    string? SourceFile = null, string? DisplayText = null, ProgrammeOrder Order = ProgrammeOrder.Shuffle,
    double TransitionSeconds = 3)
{
    public bool PreservesStationBranding => true;

    public ProgrammeDraft Normalize(IReadOnlySet<string> stations)
    {
        ArgumentNullException.ThrowIfNull(Rule);
        Rule.Validate(stations);
        if (Rule.Enabled) throw new ArgumentException("Save this programme as a draft. Publishing requires the qualified playback workflow.");
        if (!Enum.IsDefined(Order) || !double.IsFinite(TransitionSeconds) || TransitionSeconds is < 0.1 or > 15)
            throw new ArgumentException("Choose a supported order and a smooth transition between 0.1 and 15 seconds.");
        if (DisplayText is not null && (string.IsNullOrWhiteSpace(DisplayText) || DisplayText.Length > 512 || DisplayText.Any(char.IsControl)))
            throw new ArgumentException("Choose up to 512 characters of broadcast display text without control characters, or follow song metadata.");
        if (SourceFile is not null && (Rule.Kind != CalendarRuleKind.TimedSweeper || SourceFile.Length is < 1 or > 256 ||
            SourceFile is "." or ".." || SourceFile.IndexOfAny(['/', '\\', ':']) >= 0 || SourceFile.Any(char.IsControl) ||
            !new[] { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".aif", ".aiff", ".wma", ".mp4" }
                .Contains(Path.GetExtension(SourceFile), StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("Choose a supported audio filename inside the selected sweeper folder.");
        return this with { SourceFolder = LocalMediaPath.Normalize(SourceFolder), Rule = Rule with
        { Id = Rule.Id.ToLowerInvariant(), Stations = Array.AsReadOnly(Rule.Stations.Order(StringComparer.Ordinal).ToArray()) } };
    }
}

public sealed record ProgrammeDraftRevision(ProgrammeDraft Draft, bool Archived, string Actor,
    DateTimeOffset AtUtc, string Change, long? RestoredFromVersion = null)
{
    public string Id => Draft.Rule.Id;
    public long Version => Draft.Rule.Version;
}
public sealed record ProgrammeDraftPage(IReadOnlyList<ProgrammeDraftRevision> Items, int Total, int Offset, int PageSize = 4);
public sealed record ProgrammeDraftHistory(string Id, long HeadVersion, IReadOnlyList<ProgrammeDraftRevision> Items,
    int Total, int Offset, int PageSize = 4);
public sealed record ProgrammeDraftForecastPage(DateTimeOffset FromUtc, DateTimeOffset ThroughUtc, string TimeZone,
    IReadOnlyList<CalendarOccurrence> Occurrences, int OccurrenceCount, IReadOnlyList<CalendarConflict> Conflicts,
    int ConflictCount, int Offset, int PageSize = 12)
{
    public static ProgrammeDraftForecastPage From(ProgrammeForecast value, int offset)
    {
        if (offset is < 0 or > 65536) throw new ArgumentException("Choose a bounded forecast page.");
        return new(value.FromUtc, value.ThroughUtc, value.TimeZone, value.Occurrences.Skip(offset).Take(12).ToArray(),
            value.Occurrences.Count, value.Conflicts.Skip(offset).Take(12).ToArray(), value.Conflicts.Count, offset);
    }
}
