using System.Text.Json;
using OnAirNext.Engine;

namespace OnAirNext.Application;

public sealed record Asset(string Id, string Artist, string Title, string Kind, double Seconds, bool Retired, string FileName);
public sealed record QueueEntry(string Id, string AssetId, string Artist, string Title, string Kind, double Seconds, string State, long Position, string Origin);
public sealed record Playlist(string Id, string Name, int Tracks);
public sealed record ScheduleEntry(string Id, string AssetId, string Artist, string Title, string LocalTime, string Mode, string Repeat, string State);
public sealed record OutputView(string Host, int Port, string Mount, string Profile, bool HasPassword);
public sealed record StudioStatus(StationSnapshot? Engine, string StationName, bool Enabled, string? PlaylistId,
    IReadOnlyList<QueueEntry> Queue, IReadOnlyList<Asset> Library, IReadOnlyList<Playlist> Playlists,
    IReadOnlyList<ScheduleEntry> Schedule, OutputView Output, string? Error, string Root, bool TestOriginRunning,
    IReadOnlyList<PlaybackEvent> RecentEvents, PlayoutCheckpoint? Checkpoint, bool AuditHealthy);
public sealed record StudioRequest(string Id, string Action, JsonElement Payload);
public sealed record StudioReply(bool Ok, JsonElement? Data = null, string? Error = null);
public static class StudioJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    public static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, Options);
}
