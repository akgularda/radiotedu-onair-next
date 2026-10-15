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

    internal static async Task CommandAsync(string name, string action, string station, string id, CancellationToken token)
    {
        if (action is not ("skip" or "start-station" or "stop-station")) throw new ArgumentException("Unknown station action.");
        await MutateAsync(name, new(id, action, station), token);
    }

    internal static async Task<LibraryReply> ReadLibraryAsync(string name, string station, string query, int offset, CancellationToken token)
    {
        var response = await SendAsync(name, new(Guid.NewGuid().ToString("N"), "library-status", station, Query: query, Offset: offset), token);
        using var document = JsonDocument.Parse(response);
        if (!document.RootElement.TryGetProperty("page", out _)) throw new InvalidOperationException(
            document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : "Library status is unavailable.");
        var reply = JsonSerializer.Deserialize<LibraryReply>(response, Json) ?? throw new InvalidDataException("Empty library response.");
        if (reply.Page?.Items is null || reply.Page.Items.Length > 50 || reply.Worker is null) throw new InvalidDataException("Invalid library page.");
        return reply;
    }

    internal static Task SetLibraryRootAsync(string name, string station, string path, CancellationToken token)
        => MutateAsync(name, new(Guid.NewGuid().ToString("N"), "library-root", station, Path: path), token);
    internal static Task ImportFilesAsync(string name, string station, string[] paths, CancellationToken token)
        => MutateAsync(name, new(Guid.NewGuid().ToString("N"), "library-import", station, Paths: paths), token);
    internal static Task RescanLibraryAsync(string name, string station, CancellationToken token)
        => MutateAsync(name, new(Guid.NewGuid().ToString("N"), "library-rescan", station), token);
    internal static Task UseManagedLibraryAsync(string name, string station, CancellationToken token)
        => MutateAsync(name, new(Guid.NewGuid().ToString("N"), "library-managed", station), token);

    private static async Task MutateAsync(string name, Command request, CancellationToken token)
    {
        byte[] response;
        try { response = await SendAsync(name, request, token); }
        catch (Exception ex) when ((ex is IOException or OperationCanceledException) && !token.IsCancellationRequested)
        {
            // The exact same ID/station/action is retried; a lost acknowledgement cannot
            // turn one Skip into two or replay an old Start over a later Stop decision.
            await Task.Delay(100, token);
            response = await SendAsync(name, request, token);
        }
        using var reply = JsonDocument.Parse(response);
        if (!reply.RootElement.TryGetProperty("accepted", out var accepted) || !accepted.GetBoolean())
            throw new InvalidOperationException(reply.RootElement.TryGetProperty("error", out var error)
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
        while (response.Length < 524288)
        {
            var read = await pipe.ReadAsync(bytes, timeout.Token);
            if (read == 0) throw new EndOfStreamException("Engine closed before acknowledging the request.");
            var newline = Array.IndexOf(bytes, (byte)'\n', 0, read);
            if (response.Length + (newline < 0 ? read : newline) > 524288) throw new InvalidDataException("Engine response exceeds 512 KiB.");
            response.Write(bytes, 0, newline < 0 ? read : newline);
            if (newline >= 0) return response.ToArray();
        }
        throw new InvalidDataException("Engine response exceeds 512 KiB.");
    }

    private sealed record Command(string Id, string Action, string? StationId, string? Path = null,
        string[]? Paths = null, string? Query = null, int Offset = 0);
}

internal sealed record LibraryReply(LibraryPageView Page, LibraryWorkerView Worker, bool CanImportFiles = false);
internal sealed record LibraryRootView(string StationId, string Path, long Revision);
internal sealed record LibraryAssetView(string Id, string RelativePath, string Artist, string Title, double Seconds, string State, string? Error)
{
    public string Duration => TimeSpan.FromSeconds(Seconds).ToString(@"hh\:mm\:ss");
}
internal sealed record LibraryImportView(string Id, string State, string? Error);
internal sealed record LibraryPageView(LibraryRootView? Root, long Total, long Ready, long Missing,
    LibraryAssetView[] Items, int Offset, LibraryImportView? Import);
internal sealed record LibraryWorkerView(string State, string? Error, DateTimeOffset? ScannedAtUtc);

internal sealed record StatusReply(StationView[] Stations, AuditView Audit);
internal sealed record AuditView(long PendingEvents, long RejectedEvents, string? Error);
internal sealed record StationView(string Id, string State, long Samples, long CompletedItems, long SkippedItems,
    string? CurrentTitle, long LateBlocks, long AuditRejectedEvents, OutputView[] Outputs,
    string? CurrentAssetId, long CurrentItemSamples, bool Recovered,
    bool Enabled = true, bool RequestedRunning = true, string? LastError = null, DateTimeOffset? RetryAtUtc = null)
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
