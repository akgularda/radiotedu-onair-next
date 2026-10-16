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
    CodecProfile Profile = CodecProfile.AacLc192, int QueueBlocks = 100, int WriteTimeoutMs = 2000)
{
    public void Validate(bool loopbackOnly = true)
    {
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
}

public sealed record StationSettings(string Id, IReadOnlyList<MediaItem> Playlist, IReadOnlyList<OutputSettings> Outputs, bool Repeat = true,
    bool Enabled = true, bool AutoStart = true);
public sealed record PlaybackEvent(string EventId, string OccurrenceId, string StationId, string Type, string ItemId,
    string Artist, string Title, ContentKind Kind, DateTimeOffset AtUtc, long SamplePosition, string Detail = "", string? QueueEntryId = null, string? PreviousOccurrenceId = null, string TimingQuality = "sample_clock",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SourceGain? SourceGain = null);
public sealed record PlayoutSelection(MediaItem Item, string? QueueEntryId = null);
public interface IPlayoutPlanner
{
    int? AvailableItems => null;
    PlayoutSelection Select(int rotationIndex, string? excludeQueueEntryId = null);
    PlayoutSelection? SelectPrepared(int rotationIndex,IReadOnlySet<string> excludedQueueEntries)
    {
        if(excludedQueueEntries.Count>1)throw new NotSupportedException("This planner cannot exclude every in-flight queue entry.");
        return Select(rotationIndex,excludedQueueEntries.FirstOrDefault());
    }
}
public sealed record PlayoutCheckpoint(string StationId, string State, int RotationIndex, MediaItem? Item,
    string OccurrenceId, long ItemSamples, long ProgramSamples, bool Announced, DateTimeOffset SavedAtUtc,
    string? QueueEntryId = null, string? SessionId = null, string? PreviousOccurrenceId = null);
public sealed record OutputSnapshot(string Id, string State, long EncoderStarts, long Connections, long FramesWritten,
    long DroppedBlocks, int QueuedBlocks, int? EncoderPid, string? LastError);
public sealed record PlayoutItemSnapshot(string Id, string Artist, string Title, ContentKind Kind, double? DurationSeconds);
public sealed record StationSnapshot(string Id, string State, long Samples, long CompletedItems, long SkippedItems,
    string? CurrentTitle, long LateBlocks, long AuditRejectedEvents, IReadOnlyList<OutputSnapshot> Outputs,
    string? CurrentAssetId = null, long CurrentItemSamples = 0, bool Recovered = false,
    bool Enabled = true, bool RequestedRunning = true, string? LastError = null, DateTimeOffset? RetryAtUtc = null,
    string? CurrentArtist = null, double? CurrentDurationSeconds = null, PlayoutItemSnapshot? NextItem = null,
    ProgrammeLoudnessSnapshot? Loudness = null, SourceGain? SourceGain = null, LimiterSnapshot? Limiter = null, int PendingAuditCommits = 0);

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
