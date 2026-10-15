using System.IO.Pipes;
using System.Text.Json;
using OnAirNext.Engine;

var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
if (args.Length != 2 || args[0] != "--config")
{
    Console.Error.WriteLine("Developer host: OnAirNext.Host --config <isolated local configuration>. Operator UI follows in a later milestone.");
    return 2;
}
var config = JsonSerializer.Deserialize<HostSettings>(await File.ReadAllTextAsync(args[1]), json)
    ?? throw new InvalidDataException("Missing engine configuration.");
if (!config.PipeName.StartsWith("onair-next-dev-", StringComparison.Ordinal) || config.PipeName.Length > 100 ||
    config.PipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("Use an isolated development pipe name.");
if (config.Stations.Count == 0 || config.Stations.Select(x => x.Id).Distinct().Count() != config.Stations.Count)
    throw new ArgumentException("Configure distinct test stations.");
var allOutputs = config.Stations.SelectMany(x => x.Outputs).ToArray();
if (allOutputs.Select(x => (x.Host, x.Port, x.Mount)).Distinct().Count() != allOutputs.Length)
    throw new ArgumentException("A test mount must have exactly one source owner.");
foreach (var output in allOutputs) output.Validate();
await EncoderCapabilities.ValidateAsync(config.Ffmpeg, allOutputs);
await using var journal = new EventJournal(Path.Combine(config.DataRoot, "playback.db"));
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
var stations = config.Stations.Select(x => new StationEngine(config.Ffmpeg, x, journal)).ToArray();
var commands = new Dictionary<string, (string Action, string? Station, string Result)>(StringComparer.Ordinal);
var server = Task.Run(async () =>
{
    while (!stop.IsCancellationRequested)
    {
        await using var pipe = new NamedPipeServerStream(config.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.WaitForConnectionAsync(stop.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            var line = await reader.ReadLineAsync(stop.Token);
            if (line is null || line.Length > 8192) continue;
            var request = JsonSerializer.Deserialize<EngineCommand>(line, json);
            if (request is null || string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 128) continue;
            if (commands.TryGetValue(request.Id, out var prior))
            {
                await writer.WriteLineAsync(prior.Action == request.Action && prior.Station == request.StationId
                    ? prior.Result : "{\"accepted\":false,\"error\":\"Command ID was already used for a different operation.\"}");
                continue;
            }
            var station = stations.SingleOrDefault(x => x.Snapshot().Id == request.StationId);
            string result;
            switch (request.Action)
            {
                case "status": result = JsonSerializer.Serialize(new { stations = stations.Select(x => x.Snapshot()), audit = new { journal.PendingEvents, journal.RejectedEvents, journal.Error } }); break;
                case "skip" when station is not null: station.Skip(request.Id); result = "{\"accepted\":true}"; break;
                case "stop": result = "{\"accepted\":true}"; break;
                default: result = "{\"accepted\":false,\"error\":\"Unknown command or station.\"}"; break;
            }
            if (request.Action != "status")
            {
                if (commands.Count >= 256) commands.Remove(commands.Keys.First());
                commands[request.Id] = (request.Action, request.StationId, result);
            }
            await writer.WriteLineAsync(result);
            if (request.Action == "stop") stop.Cancel();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
        catch (IOException) { /* a disconnected UI does not own playout */ }
        catch (JsonException) { /* reject malformed local commands */ }
    }
});
try
{
    foreach (var station in stations) await station.StartAsync();
    Console.WriteLine(JsonSerializer.Serialize(new { state = "started", config.PipeName, stations = stations.Length, loopbackOnly = true }));
    await Task.WhenAny(Task.Delay(Timeout.Infinite, stop.Token), Task.WhenAll(stations.Select(x => x.Completion)));
}
finally
{
    await stop.CancelAsync();
    var failures = new List<Exception>();
    foreach (var station in stations)
    {
        try { await station.DisposeAsync(); } catch (Exception ex) { failures.Add(ex); }
    }
    try { await server; } catch (Exception ex) { failures.Add(ex); }
    if (failures.Count > 0) throw new AggregateException("Engine shutdown failed.", failures);
}
return 0;

internal sealed record HostSettings(string Ffmpeg, string DataRoot, string PipeName, List<StationSettings> Stations);
internal sealed record EngineCommand(string Id, string Action, string? StationId);
