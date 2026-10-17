using System.Buffers;
using System.Text.Json.Serialization;

namespace OnAirNext.Engine;

public static class AudioFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    // Engine/codec-pipe contract: interleaved little-endian IEEE float32, stereo, 48 kHz.
    public const int BytesPerSample = sizeof(float);
    public const float MaximumSourceSample = 64;
    public const int BytesPerSampleFrame = Channels * BytesPerSample;
    public const int SamplesPerBlock = 960;
    public const int BlockBytes = SamplesPerBlock * BytesPerSampleFrame;
    public static readonly TimeSpan BlockDuration = TimeSpan.FromMilliseconds(20);
}

// A block becomes immutable before publication; each consumer owns one reference.
public sealed class PcmBlock : IDisposable
{
    private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(AudioFormat.BlockBytes);
    private int _references = 1;
    public int Length { get; set; } = AudioFormat.BlockBytes;
    public long SamplePosition { get; set; }
    public Memory<byte> Memory => (_buffer ?? throw new ObjectDisposedException(nameof(PcmBlock))).AsMemory(0, Length);
    public static PcmBlock Rent() => new();
    public void AddReference()
    {
        if (Interlocked.Increment(ref _references) <= 1) throw new ObjectDisposedException(nameof(PcmBlock));
    }
    public void Dispose()
    {
        if (Interlocked.Decrement(ref _references) == 0)
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<ContentKind>))]
public enum ContentKind { Music, Advertisement, Sweeper, Program, Jingle }

public sealed record MediaItem(string Id, string Path, string Artist, string Title, ContentKind Kind = ContentKind.Music, double? DurationSeconds = null, SourceLoudness? Loudness = null);

[JsonConverter(typeof(JsonStringEnumConverter<CodecProfile>))]
public enum CodecProfile { AacLc192, OggFlac, HeAacV2_64 }

public sealed record OutputSettings(string Id, string Host, int Port, string Mount, string SourcePassword,
    CodecProfile Profile = CodecProfile.AacLc192, int QueueBlocks = 100, int WriteTimeoutMs = 2000,
    bool Commissioned = false, IcecastStationInformation? Information = null, string SourceUsername = "source")
{
    public void Validate(bool loopbackOnly = true)
    {
        if (Commissioned) throw new ArgumentException("Commissioned outputs require the protected service commissioning path.");
        if (string.IsNullOrWhiteSpace(Id)) throw new ArgumentException("Output ID is required.");
        if (loopbackOnly && Host != "127.0.0.1" && Host != "::1")
            throw new ArgumentException("This engine milestone permits literal loopback destinations only.");
        if (Port is < 1024 or > 65535) throw new ArgumentException("Use an unprivileged test port.");
        if (!Mount.StartsWith("/next-", StringComparison.Ordinal) || Mount.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '/' and not '-' and not '_') || Mount[1..].Contains('/'))
            throw new ArgumentException("Test mounts must begin /next- and contain only letters, numbers, dash and underscore.");
        if (SourcePassword.Length < 16 || SourcePassword.Any(c => char.IsControl(c)))
            throw new ArgumentException("A generated test source credential is required.");
        if (QueueBlocks is < 2 or > 500 || WriteTimeoutMs is < 100 or > 10000)
            throw new ArgumentException("Output buffering and timeout must be bounded.");
    }

    public void ValidateCommissioned()
    {
        if (!Commissioned) { Validate(); return; }
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 128 || Id.Any(char.IsControl))
            throw new ArgumentException("Choose a bounded output ID.");
        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253 ||
            Uri.CheckHostName(Host) == UriHostNameType.Unknown || Host.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new ArgumentException("Use a host name or literal address without a URL scheme.");
        if (Port is < 1 or > 65535) throw new ArgumentException("Choose a valid Icecast port.");
        if (string.IsNullOrWhiteSpace(Mount) || Mount.Length > 128 || Mount[0] != '/' || Mount.Length == 1 ||
            Mount[1..].Contains('/') || Mount.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '/' and not '-' and not '_' and not '.') ||
            Mount.Equals("/en", StringComparison.OrdinalIgnoreCase) || Mount.Equals("/fr", StringComparison.OrdinalIgnoreCase) ||
            Mount.StartsWith("/en-", StringComparison.OrdinalIgnoreCase) || Mount.StartsWith("/fr-", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose an explicitly owned RadioTEDU mount; external EN/FR outputs are protected.");
        if (string.IsNullOrEmpty(SourcePassword) || SourcePassword.Length > 1024 || SourcePassword.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(SourceUsername) || SourceUsername.Length > 128 || SourceUsername.Any(c => char.IsControl(c) || c == ':'))
            throw new ArgumentException("A valid source credential is required.");
        if (!Enum.IsDefined(Profile) || QueueBlocks is < 2 or > 500 || WriteTimeoutMs is < 100 or > 10000)
            throw new ArgumentException("Choose a supported codec and bounded output buffering.");
        (Information ?? throw new ArgumentException("Import the station's Icecast information before commissioning.")).Validate();
    }
}

public sealed record IcecastStationInformation(string Name, string Description, string Genre, string Url, bool Public)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || new[] { Name, Description, Genre, Url }.Any(value => value is null || value.Length > 1024 || value.Any(char.IsControl)))
            throw new ArgumentException("Icecast station information must contain bounded text without control characters.");
        if (Url.Length > 0 && (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme is not "http" and not "https"))
            throw new ArgumentException("The station website must be an HTTP or HTTPS URL.");
    }
}

public sealed record StationOutputConfiguration(string StationId, IReadOnlyList<OutputSettings> Outputs);

// A complete fixed RadioTEDU commissioning roster is committed at once, while every
// station remains disarmed. The service applies it only after validating its full map.
public sealed record ProductionOutputConfiguration(int SchemaVersion, IReadOnlyList<StationOutputConfiguration> Stations)
{
    private static readonly string[] StationIds = ["1", "2", "4", "5", "8", "9", "10", "11"];
    private const string OriginHost = "stream.radiotedu.com";
    private const int OriginPort = 11154;
    private static readonly Dictionary<string, string> PrimaryMounts = new(StringComparer.Ordinal)
    {
        ["1"] = "/classic", ["2"] = "/lofi", ["4"] = "/radio", ["5"] = "/cazz",
        ["8"] = "/rock", ["9"] = "/energize", ["10"] = "/situation", ["11"] = "/maincharacter"
    };

    public ProductionOutputConfiguration Validate(IEnumerable<string> installedStations)
    {
        if (SchemaVersion != 1) throw new ArgumentException("Unsupported production-output configuration version.");
        if (Stations is null) throw new ArgumentException("Provide all eight RadioTEDU output groups.");
        var installed = installedStations.Order(StringComparer.Ordinal).ToArray();
        if (!installed.SequenceEqual(StationIds.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new ArgumentException("Commission only the eight owned RadioTEDU stations; leave external stations alone.");
        if (Stations.Count != StationIds.Length || Stations.Select(x => x.StationId).Distinct(StringComparer.Ordinal).Count() != StationIds.Length ||
            !Stations.Select(x => x.StationId).Order(StringComparer.Ordinal).SequenceEqual(StationIds.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new ArgumentException("Provide an output group for every one of the eight RadioTEDU stations.");
        if (Stations.Any(station => station is null || station.Outputs is null))
            throw new ArgumentException("Each RadioTEDU channel needs a complete output group.");
        var outputs = Stations.SelectMany(station => station.Outputs.Select(output => (station.StationId, Output: output))).ToArray();
        if (outputs.Length != 18) throw new ArgumentException("The production roster must contain all eighteen codec outputs.");
        foreach (var (stationId, output) in outputs)
        {
            output.ValidateCommissioned();
            if (!output.Host.Equals(OriginHost, StringComparison.OrdinalIgnoreCase) || output.Port != OriginPort)
                throw new ArgumentException("All production outputs must use the configured RadioTEDU Icecast origin.");
        }
        if (outputs.Select(x => x.Output.Id).Distinct(StringComparer.Ordinal).Count() != outputs.Length ||
            outputs.Select(x => (x.Output.Host.ToLowerInvariant(), x.Output.Port, x.Output.Mount.ToLowerInvariant())).Distinct().Count() != outputs.Length)
            throw new ArgumentException("Each output ID and destination must have exactly one owner.");
        foreach (var id in StationIds)
        {
            var group = Stations.Single(x => x.StationId == id).Outputs;
            var primary = PrimaryMounts[id];
            var expected = new Dictionary<string, CodecProfile>(StringComparer.Ordinal)
            {
                [primary] = CodecProfile.AacLc192,
                [primary + "-low"] = CodecProfile.HeAacV2_64
            };
            if (id is "1" or "5") expected[primary + "-flac"] = CodecProfile.OggFlac;
            if (group.Count != expected.Count || group.Any(output => !expected.TryGetValue(output.Mount, out var profile) || output.Profile != profile))
                throw new ArgumentException("A station output group does not match its approved AAC-LC, HE-AACv2 and FLAC mount roster.");
            if (group.Select(x => x.Host.ToLowerInvariant() + ":" + x.Port).Distinct(StringComparer.Ordinal).Count() != 1)
                throw new ArgumentException("A station's codec variants must share its verified Icecast origin.");
            if (group.Select(x => x.Information).Distinct().Count() != 1)
                throw new ArgumentException("A station's codec variants must share its Icecast station information.");
        }
        return this;
    }
}

public sealed record StationSettings(string Id, IReadOnlyList<MediaItem> Playlist, IReadOnlyList<OutputSettings> Outputs, bool Repeat = true,
    bool Enabled = true, bool AutoStart = true);
public sealed record PlaybackEvent(string EventId, string OccurrenceId, string StationId, string Type, string ItemId,
    string Artist, string Title, ContentKind Kind, DateTimeOffset AtUtc, long SamplePosition, string Detail = "", string? QueueEntryId = null, string? PreviousOccurrenceId = null, string TimingQuality = "sample_clock",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SourceGain? SourceGain = null);
public sealed record PlayoutSelection(MediaItem Item, string? QueueEntryId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool HoldsRotation = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PreviousOccurrenceId = null)
{
    [JsonIgnore] public bool AdvancesRotation => QueueEntryId is null && !HoldsRotation;
}
public interface IPlayoutPlanner
{
    int? AvailableItems => null;
    void RetainQueueReservations(IReadOnlySet<string> entries) { }
    void ReleaseQueueReservations() { }
    PlayoutSelection? ResolveQueueRecovery(PlayoutSelection selection) => selection;
    PlayoutSelection Select(int rotationIndex, string? excludeQueueEntryId = null);
    PlayoutSelection? SelectPrepared(int rotationIndex,IReadOnlySet<string> excludedQueueEntries)
    {
        if(excludedQueueEntries.Count>1)throw new NotSupportedException("This planner cannot exclude every in-flight queue entry.");
        return Select(rotationIndex,excludedQueueEntries.FirstOrDefault());
    }
}
public sealed record PlayoutCheckpoint(string StationId, string State, int RotationIndex, MediaItem? Item,
    string OccurrenceId, long ItemSamples, long ProgramSamples, bool Announced, DateTimeOffset SavedAtUtc,
    string? QueueEntryId = null, string? SessionId = null, string? PreviousOccurrenceId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool HoldsRotation = false);
public sealed record OutputSnapshot(string Id, string State, long EncoderStarts, long Connections, long FramesWritten,
    long DroppedBlocks, int QueuedBlocks, int? EncoderPid, string? LastError);
public sealed record PlayoutItemSnapshot(string Id, string Artist, string Title, ContentKind Kind, double? DurationSeconds);
public sealed record StationSnapshot(string Id, string State, long Samples, long CompletedItems, long SkippedItems,
    string? CurrentTitle, long LateBlocks, long AuditRejectedEvents, IReadOnlyList<OutputSnapshot> Outputs,
    string? CurrentAssetId = null, long CurrentItemSamples = 0, bool Recovered = false,
    bool Enabled = true, bool RequestedRunning = true, string? LastError = null, DateTimeOffset? RetryAtUtc = null,
    string? CurrentArtist = null, double? CurrentDurationSeconds = null, PlayoutItemSnapshot? NextItem = null,
    ProgrammeLoudnessSnapshot? Loudness = null, SourceGain? SourceGain = null, LimiterSnapshot? Limiter = null, int PendingAuditCommits = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CurrentQueueEntryId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextQueueEntryId = null);

public interface IStationSession : IAsyncDisposable
{
    Task StartAsync();
    Task Completion { get; }
    StationSnapshot Snapshot();
    Task SkipAsync(string commandId);
    void RefreshNext() { }
}

public interface IProgramOutput : IAsyncDisposable
{
    void Start();
    bool Publish(PcmBlock block);
    OutputSnapshot Snapshot();
}
