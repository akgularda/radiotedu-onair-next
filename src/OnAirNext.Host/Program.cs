using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using OnAirNext.Engine;

var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
if (args.Length != 2 || args[0] != "--config")
{
    Console.Error.WriteLine("Developer host: OnAirNext.Host --config <isolated local configuration>.");
    return 2;
}
var config = JsonSerializer.Deserialize<HostSettings>(await File.ReadAllTextAsync(args[1]), json)
    ?? throw new InvalidDataException("Missing engine configuration.");
if (!config.PipeName.StartsWith("onair-next-dev-", StringComparison.Ordinal) || config.PipeName.Length > 100 ||
    config.PipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("Use an isolated development pipe name.");
await using var journal = new EventJournal(Path.Combine(config.DataRoot, "playback.db"));
await using var coordinator = new StationCoordinator(config.Ffmpeg, config.Stations, journal);
await coordinator.InitializeAsync();
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
Console.WriteLine(JsonSerializer.Serialize(new { state = "controller-ready", config.PipeName, stations = config.Stations.Count, loopbackOnly = true }));
try
{
    while (!stop.IsCancellationRequested)
    {
        await using var pipe = new NamedPipeServerStream(config.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.WaitForConnectionAsync(stop.Token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var frame = await PipeFrames.ReadAsync(pipe, 8192, deadline.Token);
            if (frame is null) continue;
            var request = JsonSerializer.Deserialize<EngineCommand>(frame, json);
            if (request is null || string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 128) continue;
            string result;
            var shutdown = false;
            try
            {
                switch (request.Action)
                {
                    case "status":
                        result = JsonSerializer.Serialize(new { stations = coordinator.Snapshot(),
                            audit = new { journal.PendingEvents, journal.RejectedEvents, journal.Error },
                            children = ChildProcess.ActiveProcesses });
                        break;
                    case "start-station" or "stop-station" when request.StationId is { } targetStation:
                        await coordinator.RequestRunningAsync(targetStation, request.Action == "start-station", request.Id);
                        result = "{\"accepted\":true}"; break;
                    case "skip" when request.StationId is { } skipStation:
                        await coordinator.SkipAsync(skipStation, request.Id);
                        result = "{\"accepted\":true}"; break;
                    case "stop":
                        shutdown = await coordinator.StopAllAsync(request.Id);
                        result = "{\"accepted\":true}"; break;
                    default:
                        result = "{\"accepted\":false,\"error\":\"Unknown command or station.\"}"; break;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or Microsoft.Data.Sqlite.SqliteException)
            {
                result = JsonSerializer.Serialize(new { accepted = false, error = ex.Message });
            }
            try { await pipe.WriteAsync(Encoding.UTF8.GetBytes(result + "\n"), deadline.Token); }
            finally { if (shutdown) stop.Cancel(); }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
        catch (OperationCanceledException) { /* one stalled client cannot retain the command pipe */ }
        catch (IOException) { /* a disconnected UI does not own playout */ }
        catch (JsonException) { /* reject malformed local commands */ }
    }
}
finally { await stop.CancelAsync(); }
return 0;

internal sealed record HostSettings(string Ffmpeg, string DataRoot, string PipeName, List<StationSettings> Stations);
internal sealed record EngineCommand(string Id, string Action, string? StationId);
