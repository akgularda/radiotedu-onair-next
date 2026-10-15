using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace OnAirNext.Desktop;

// This first desktop milestone can only reach isolated development engines.
// There is no discovery of production credentials or production services here.
internal static class EngineConnection
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    internal static bool IsIsolatedPipe(string name) => name.StartsWith("onair-next-dev-", StringComparison.Ordinal)
        && name.Length <= 100 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    internal static string[] Discover() => Directory.GetFiles(@"\\.\pipe\")
        .Select(Path.GetFileName).OfType<string>().Where(IsIsolatedPipe).Order(StringComparer.Ordinal).ToArray();

    internal static async Task<StatusReply> ReadStatusAsync(string name, CancellationToken token)
    {
        var response = await SendAsync(name, new(Guid.NewGuid().ToString("N"), "status", null), token);
        using var document = JsonDocument.Parse(response);
        if (!document.RootElement.TryGetProperty("stations", out _)) throw new InvalidDataException("Engine did not return station status.");
        var status = JsonSerializer.Deserialize<StatusReply>(response, Json) ?? throw new InvalidDataException("Empty engine status.");
        if (status.Stations is null || status.Audit is null || status.Stations.Length > 32 ||
            status.Stations.Any(x => x is null || x.Outputs is null || x.Outputs.Length > 16))
            throw new InvalidDataException("Engine status exceeds the desktop station limit.");
        return status;
    }

    internal static async Task SkipAsync(string name, string station, string id, CancellationToken token)
    {
        var response = await SendAsync(name, new(id, "skip", station), token);
        using var reply = JsonDocument.Parse(response);
        if (!reply.RootElement.TryGetProperty("accepted", out var accepted) || !accepted.GetBoolean())
            throw new InvalidDataException(reply.RootElement.TryGetProperty("error", out var error)
                ? error.GetString() : "Engine did not acknowledge the command.");
    }

    private static async Task<byte[]> SendAsync(string name, Command request, CancellationToken token)
    {
        if (!IsIsolatedPipe(name)) throw new ArgumentException("Choose an isolated engine from the list.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n"), timeout.Token);
        // One bounded line, without a StreamWriter whose disposal can flush after the peer closes.
        using var response = new MemoryStream();
        var bytes = new byte[4096];
        while (response.Length < 131072)
        {
            var read = await pipe.ReadAsync(bytes, timeout.Token);
            if (read == 0) throw new EndOfStreamException("Engine closed before acknowledging the request.");
            var newline = Array.IndexOf(bytes, (byte)'\n', 0, read);
            response.Write(bytes, 0, newline < 0 ? read : newline);
            if (newline >= 0) return response.ToArray();
        }
        throw new InvalidDataException("Engine response exceeds 128 KiB.");
    }

    private sealed record Command(string Id, string Action, string? StationId);
}

internal sealed record StatusReply(StationView[] Stations, AuditView Audit);
internal sealed record AuditView(long PendingEvents, long RejectedEvents, string? Error);
internal sealed record StationView(string Id, string State, long Samples, long CompletedItems, long SkippedItems,
    string? CurrentTitle, long LateBlocks, long AuditRejectedEvents, OutputView[] Outputs,
    string? CurrentAssetId, long CurrentItemSamples, bool Recovered)
{
    public string Elapsed => TimeSpan.FromSeconds(CurrentItemSamples / 48000d).ToString(@"hh\:mm\:ss");
    public string ProgrammeTime
    {
        get
        {
            var time = TimeSpan.FromSeconds(Samples / 48000d);
            return $"{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
        }
    }
    public string DisplayTitle => string.IsNullOrWhiteSpace(CurrentTitle) ? "No item announced" : CurrentTitle;
}
internal sealed record OutputView(string Id, string State, long EncoderStarts, long Connections, long FramesWritten,
    long DroppedBlocks, int QueuedBlocks, int? EncoderPid, string? LastError);
