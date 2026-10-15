using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using OnAirNext.Engine;

namespace OnAirNext.Web;

internal static class EngineRpc
{
    public static bool IsIsolated(string? name) => name is not null && name.StartsWith("onair-next-dev-", StringComparison.Ordinal)
        && name.Length <= 100 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    public static string[] Discover() => Directory.GetFiles(@"\\.\pipe\").Select(Path.GetFileName)
        .OfType<string>().Where(IsIsolated).Order(StringComparer.Ordinal).ToArray();

    public static async Task<JsonElement> SendAsync(string engine, object command, CancellationToken token)
    {
        if (!IsIsolated(engine)) throw new ArgumentException("Choose an isolated engine from System.");
        var request = JsonSerializer.SerializeToUtf8Bytes(command);
        if (request.Length > 65500) throw new ArgumentException("The command exceeds the local message limit.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(4));
        await using var pipe = new NamedPipeClientStream(".", engine, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(deadline.Token);
        await pipe.WriteAsync(request, deadline.Token); await pipe.WriteAsync("\n"u8.ToArray(), deadline.Token);
        var response = await PipeFrames.ReadAsync(pipe, 524288, deadline.Token) ?? throw new EndOfStreamException("Engine closed before acknowledging the request.");
        using var document = JsonDocument.Parse(response); return document.RootElement.Clone();
    }
}
