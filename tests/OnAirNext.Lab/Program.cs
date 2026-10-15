using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.IO.Pipes;
using OnAirNext.Engine;
using OnAirNext.Lab;

if (args.Length == 2 && args[0] == "--service-probe") return await ServiceProbe.CommandAsync(args[1]);
if (args.Length == 2 && args[0] == "--service-child" && args[1] == "leaf") return await ServiceProbe.OwnedChildFixtureAsync(true);
if (args.SequenceEqual(new[] { "-hide_banner", "-encoders" }) && Path.GetFileName(Environment.ProcessPath)=="ffmpeg.exe")
    return await ServiceProbe.OwnedChildFixtureAsync(false);

var arguments = args.Chunk(2).ToDictionary(x => x[0], x => x.Length == 2 ? x[1] : throw new ArgumentException("Arguments require values."));
var localRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadioTEDU", "OnAirNext-Dev");
var ffmpeg = arguments.GetValueOrDefault("--ffmpeg") ?? "ffmpeg";
var icecastRoot = arguments.GetValueOrDefault("--icecast-root") ?? Path.Combine(localRoot, "tools", "icecast");
var scenario = arguments.GetValueOrDefault("--scenario") ?? "all";
var duration = int.Parse(arguments.GetValueOrDefault("--duration-seconds") ?? "12", System.Globalization.CultureInfo.InvariantCulture);
if (duration is < 6 or > 259200) throw new ArgumentException("Choose 6 seconds through 72 hours.");
if (scenario is not ("all" or "baseline" or "recovery" or "durable" or "lifecycle" or "finite" or "offline" or "fleet" or "live" or "soak" or "backpressure" or "corrupt" or "host")) throw new ArgumentException("Unknown lab scenario.");
var runRoot = Path.Combine(localRoot, "runs", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(runRoot);
var fixtures = Sandbox.Fixtures(Path.Combine(runRoot, "fixtures"));
var results = new List<CaseResult>();
foreach (var name in scenario == "all" ? new[] { "baseline", "recovery", "durable", "lifecycle", "finite", "offline", "fleet", "live", "backpressure", "corrupt", "host" } : new[] { scenario })
{
    var result = new CaseResult(name);
    Console.WriteLine($"Starting {name}; all origins and mounts are loopback test resources.");
    var clock = Stopwatch.StartNew();
    try
    {
        switch (name)
        {
            case "baseline": await BaselineAsync(result); break;
            case "recovery": await RecoveryAsync(result); break;
            case "durable": await DurableAsync(result); break;
            case "lifecycle": await LifecycleAsync(result); break;
            case "finite": await FiniteAsync(result); break;
            case "offline": await OfflineAsync(result); break;
            case "fleet": case "soak": await FleetAsync(result); break;
            case "live": await LiveAsync(result); break;
            case "backpressure": await BackpressureAsync(result); break;
            case "corrupt": await CorruptAsync(result); break;
            case "host": await HostAsync(result); break;
        }
        result.Passed = true;
    }
    catch (Exception ex) { result.Error = ex.ToString(); Console.Error.WriteLine($"{name} failed: {ex.Message}"); }
    result.DurationSeconds = clock.Elapsed.TotalSeconds;
    results.Add(result);
    Console.WriteLine($"{name}: {(result.Passed ? "PASS" : "FAIL")} ({result.Checks.Count} checks)");
    await File.WriteAllTextAsync(Path.Combine(runRoot, "results.json"), JsonSerializer.Serialize(new
    {
        SchemaVersion = 1, CreatedAtUtc = DateTimeOffset.UtcNow, Machine = Environment.MachineName,
        OS = RuntimeInformation.OSDescription, Runtime = RuntimeInformation.FrameworkDescription,
        Icecast = "2.5.0", TestProfiles = new[] { "AAC-LC 192 kbps", "Ogg FLAC" },
        HeAacTested = false, Source = "New independent implementation; no production configuration or services accessed.",
        Results = results, RemainingOwnedChildren = ChildProcess.ActiveProcesses
    }, new JsonSerializerOptions { WriteIndented = true }));
}
Console.WriteLine("Evidence: " + Path.Combine(runRoot, "results.json"));
return results.All(x => x.Passed) && ChildProcess.ActiveProcesses.Count == 0 ? 0 : 1;

async Task WaitAsync(Func<bool> condition, string description, int seconds = 12)
{
    var wait = Stopwatch.StartNew();
    while (!condition())
    {
        if (wait.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(description);
        await Task.Delay(50);
    }
}

void Check(CaseResult result, bool condition, string description)
{
    result.Checks.Add(new { Description = description, Passed = condition });
    if (!condition) throw new InvalidOperationException(description);
}

void ValidateHistory(CaseResult result, string database)
{
    var events = EventJournal.ReadEvents(database);
    Check(result, EventJournal.Verify(database), "Playback journal hash chain is valid.");
    Check(result, events.Select(x => x.EventId).Distinct().Count() == events.Count, "Event acknowledgements do not duplicate journal rows.");
    var starts = events.Where(x => x.Type == "started").ToArray();
    var terminals = events.Where(x => x.Type is "ended" or "skipped" or "interrupted" or "failed").ToDictionary(x => x.OccurrenceId);
    var lengths = new Dictionary<string, int> { ["song-a"] = 97776, ["advert"] = 34224, ["sweeper"] = 60048, ["song-b"] = 96528 };
    Check(result, starts.Length >= 3, "Music, advertisements and sweepers produce separate occurrence records.");
    Check(result, starts.All(x => terminals.ContainsKey(x.OccurrenceId)), "Orderly stop leaves a terminal record for every started occurrence.");
    Check(result, starts.All(x => !terminals.TryGetValue(x.OccurrenceId, out var end) || end.Type != "ended" || end.SamplePosition - x.SamplePosition == lengths[x.ItemId]),
        "Completed assets retain their exact sample counts, including boundaries inside a 20 ms block.");
    foreach (var station in starts.GroupBy(x => x.StationId))
    {
        var stationStarts = station.ToArray();
        Check(result, stationStarts.Zip(stationStarts.Skip(1)).All(pair => pair.Second.SamplePosition > pair.First.SamplePosition),
            "Program sample positions never reset during output recovery: " + station.Key);
        var order = new[] { "song-a", "advert", "sweeper", "song-b" };
        Check(result, stationStarts.Select((x, index) => x.ItemId == order[index % order.Length]).All(x => x),
            "No track is repeated or advanced by transport recovery: " + station.Key);
    }
    var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(starts[0].AtUtc, PlayReports.Istanbul).DateTime);
    var report = PlayReports.ExportDaily(database, date, Path.Combine(Path.GetDirectoryName(database)!, "reports"));
    Check(result, report.Records == starts.Length && report.IncompleteRecords == 0, "Daily local report contains one record per station occurrence, independent of output variants.");
    result.Details["report"] = report;
}

void ValidateProbes(CaseResult result, IEnumerable<Probe> probes, double minimum, bool checkToneOrder = true)
{
    var snapshots = probes.Select(x => x.Snapshot()).ToArray();
    result.Details["decodedListeners"] = snapshots;
    foreach (var probe in snapshots)
    {
        Check(result, probe.Error is null, "No decoder error on " + probe.Mount);
        Check(result, probe.DecodedSeconds >= minimum, "Decoded listener audio exceeds " + minimum + " seconds on " + probe.Mount);
        Check(result, probe.LongestSilentRun == 0, "No low-RMS 20 ms windows after initial decoder priming on " + probe.Mount);
        if (checkToneOrder)
        {
            Check(result, probe.ToneTransitions >= 3, "Listener hears multiple actual content transitions on " + probe.Mount);
            Check(result, probe.UnexpectedToneTransitions == 0, "Decoded tone order has no skips or repeats on " + probe.Mount);
        }
    }
}

async Task BaselineAsync(CaseResult result)
{
    var root = Path.Combine(runRoot, result.Name);
    await using var origin = new Sandbox(icecastRoot, root); await origin.StartAsync();
    var database = Path.Combine(root, "playback.db");
    await using (var journal = new EventJournal(database))
    {
        await using var station = new StationEngine(ffmpeg, new StationSettings("baseline", fixtures,
            [origin.Output("baseline-aac"), origin.Output("baseline-flac", CodecProfile.OggFlac)]), journal);
        await station.StartAsync();
        await WaitAsync(() => station.Snapshot().Outputs.All(x => x.State == "SourceConnected"), "Both source sessions must connect.");
        await Task.Delay(400);
        await using var aac = new Probe(ffmpeg, origin.Port, "/next-baseline-aac");
        await using var flac = new Probe(ffmpeg, origin.Port, "/next-baseline-flac");
        await Task.Delay(TimeSpan.FromSeconds(duration));
        var snapshot = station.Snapshot(); result.Details["station"] = snapshot;
        Check(result, snapshot.State == "Running" && snapshot.CompletedItems >= 4, "The program continues across a full song/ad/sweeper cycle.");
        Check(result, snapshot.Outputs.All(x => x.EncoderStarts == 1 && x.Connections == 1), "Both encoders and source sessions remain persistent across content boundaries.");
        Check(result, snapshot.Outputs.All(x => x.DroppedBlocks == 0), "Healthy outputs lose no PCM blocks.");
        Check(result, snapshot.AuditRejectedEvents == 0 && journal.Error is null, "Audit work does not overflow or fail.");
        ValidateProbes(result, [aac, flac], Math.Max(3, duration - 4));
    }
    ValidateHistory(result, database);
}

async Task RecoveryAsync(CaseResult result)
{
    var root = Path.Combine(runRoot, result.Name);
    await using var origin = new Sandbox(icecastRoot, root); await origin.StartAsync();
    var database = Path.Combine(root, "playback.db");
    await using (var journal = new EventJournal(database))
    {
        await using var station = new StationEngine(ffmpeg, new StationSettings("recovery", fixtures,
            [origin.Output("recovery-main"), origin.Output("recovery-peer")]), journal);
        await station.StartAsync();
        await WaitAsync(() => station.Snapshot().Outputs.All(x => x.State == "SourceConnected"), "Recovery test source sessions must connect.");
        await using var survivor = new Probe(ffmpeg, origin.Port, "/next-recovery-main");
        await Task.Delay(3500);
        var initial = station.Snapshot(); var main = initial.Outputs[0]; var peer = initial.Outputs[1];
        Check(result, peer.EncoderPid.HasValue && ChildProcess.ActiveProcesses.ContainsKey(peer.EncoderPid.Value), "Fault injection targets an owned encoder only.");
        using (var process = Process.GetProcessById(peer.EncoderPid!.Value)) process.Kill(entireProcessTree: true);
        await WaitAsync(() => station.Snapshot().Outputs[1].EncoderStarts > 1 && station.Snapshot().Outputs[1].State == "SourceConnected", "Failed peer encoder must recover.");
        await Task.Delay(3000);
        var isolated = station.Snapshot();
        Check(result, isolated.Outputs[0].EncoderPid == main.EncoderPid && isolated.Outputs[0].Connections == 1,
            "Killing a peer encoder leaves the healthy encoder and source connection unchanged.");
        ValidateProbes(result, [survivor], 3);
        var beforeOutage = isolated.Samples;
        await origin.StopAsync(); await Task.Delay(1800); await origin.StartAsync();
        await WaitAsync(() => station.Snapshot().Outputs[0].Connections >= 2 && station.Snapshot().Outputs.All(x => x.State == "SourceConnected"),
            "Both outputs reconnect after restarting the owned Icecast origin.", 18);
        await using var recovered = new Probe(ffmpeg, origin.Port, "/next-recovery-main");
        await Task.Delay(4000);
        Check(result, recovered.Snapshot().DecodedSeconds >= 1.5 && recovered.Snapshot().Error is null, "Recovered source delivers newly decoded listener audio.");
        var final = station.Snapshot(); result.Details["station"] = final;
        Check(result, final.State == "Running" && final.Samples > beforeOutage + 48000, "Origin outage never restarts or rewinds the program timeline.");
    }
    ValidateHistory(result, database);
}

async Task OfflineAsync(CaseResult result)
{
    var root = Path.Combine(runRoot, result.Name);
    await using var origin = new Sandbox(icecastRoot, root);
    var database = Path.Combine(root, "playback.db");
    await using (var journal = new EventJournal(database))
    {
        await using var station = new StationEngine(ffmpeg, new StationSettings("offline", fixtures, [origin.Output("offline")]), journal);
        await station.StartAsync();
        await WaitAsync(() => station.Snapshot().State == "Running", "Offline boot must enter program playback.");
        await Task.Delay(3000); var before = station.Snapshot().Samples;
        Check(result, before > 48000, "Program clock advances while the origin is absent.");
        await origin.StartAsync();
        await WaitAsync(() => station.Snapshot().Outputs[0].State == "SourceConnected", "Offline output reconnects when the origin appears.", 18);
        await using var probe = new Probe(ffmpeg, origin.Port, "/next-offline");
        await Task.Delay(4500);
        Check(result, probe.Snapshot().DecodedSeconds >= 2 && probe.Snapshot().Error is null, "Offline-start recovery is verified with decoded listener audio.");
        Check(result, station.Snapshot().Samples > before, "Offline recovery does not reset the timeline.");
        result.Details["station"] = station.Snapshot(); result.Details["listener"] = probe.Snapshot();
    }
    ValidateHistory(result, database);
}

async Task FleetAsync(CaseResult result)
{
    var root = Path.Combine(runRoot, result.Name);
    await using var origin = new Sandbox(icecastRoot, root); await origin.StartAsync();
    var database = Path.Combine(root, "playback.db");
    await using var resources = new Resources();
    await using (var journal = new EventJournal(database))
    {
        var stations = Enumerable.Range(1, 8).Select(i => new StationEngine(ffmpeg,
            new StationSettings("fleet-" + i, fixtures, [origin.Output($"fleet-{i}-aac"), origin.Output($"fleet-{i}-flac", CodecProfile.OggFlac)]), journal)).ToArray();
        var probes = new List<Probe>();
        try
        {
            foreach (var station in stations) await station.StartAsync();
            await WaitAsync(() => stations.All(s => s.Snapshot().Outputs.All(o => o.State == "SourceConnected")), "Sixteen loopback outputs must connect.", 25);
            await Task.Delay(500);
            foreach (var station in stations)
                foreach (var output in station.Snapshot().Outputs) probes.Add(new Probe(ffmpeg, origin.Port, "/next-" + output.Id));
            var period = Stopwatch.StartNew();
            while (period.Elapsed < TimeSpan.FromSeconds(duration))
            {
                await Task.Delay(1000);
                if (duration > 60 && (int)period.Elapsed.TotalSeconds % 30 == 0)
                    Console.WriteLine($"Soak {period.Elapsed.TotalMinutes:F1} minutes; {probes.Count} decoded listeners; pending audit {journal.PendingEvents}.");
            }
            var snapshots = stations.Select(x => x.Snapshot()).ToArray();
            result.Details["stations"] = snapshots; result.Details["resources"] = resources.Snapshot();
            Check(result, snapshots.All(x => x.State == "Running" && x.CompletedItems >= 4), "All eight station programs remain active.");
            Check(result, snapshots.SelectMany(x => x.Outputs).All(x => x.EncoderStarts == 1 && x.Connections == 1 && x.DroppedBlocks == 0),
                "All sixteen healthy outputs retain one encoder/session with zero PCM drops.");
            Check(result, snapshots.All(x => x.AuditRejectedEvents == 0) && journal.Error is null, "Concurrent station histories remain writable.");
            ValidateProbes(result, probes, Math.Max(3, duration - 5));
        }
        finally
        {
            foreach (var probe in probes) await probe.DisposeAsync();
            foreach (var station in stations) await station.DisposeAsync();
        }
    }
    ValidateHistory(result, database);
}

async Task LiveAsync(CaseResult result)
{
    var root = Path.Combine(runRoot, result.Name);
    await using var origin = new Sandbox(icecastRoot, root); await origin.StartAsync();
    var database = Path.Combine(root, "playback.db");
    await using (var journal = new EventJournal(database))
    {
        await using var station = new StationEngine(ffmpeg, new StationSettings("live", fixtures, [origin.Output("live")]), journal);
        await station.StartAsync();
        await WaitAsync(() => station.Snapshot().Outputs[0].State == "SourceConnected", "Live test source connects.");
        await using var probe = new Probe(ffmpeg, origin.Port, "/next-live", analyzeLive: true);
        await Task.Delay(2500); var before = station.Snapshot().Outputs[0].EncoderPid;
        station.Live.SetEnabled(true);
        long position = 0;
        for (var frame = 0; frame < 75; frame++)
        {
            var input = PcmBlock.Rent();
            var samples = MemoryMarshal.Cast<byte, short>(input.Memory.Span);
            for (var i = 0; i < 960; i++)
            {
                var value = (short)(9000 * Math.Sin(2 * Math.PI * 1760 * position++ / 48000));
                samples[i * 2] = value; samples[i * 2 + 1] = value;
            }
            station.Live.Push(input); await Task.Delay(20);
        }
        // Simulate audio-agent loss while armed: the engine must restore music gain without a restart.
        await Task.Delay(2500); station.Live.SetEnabled(false); await Task.Delay(1200);
        var snapshot = station.Snapshot(); result.Details["station"] = snapshot;
        Check(result, snapshot.State == "Running" && snapshot.Outputs[0].EncoderPid == before && snapshot.Outputs[0].Connections == 1,
            "Synthetic live PCM takeover, input loss and return keep one encoder/source session.");
        Check(result, probe.Snapshot().LiveToneWindows >= 30, "The listener actually receives at least 600 ms of the distinct synthetic microphone tone.");
        ValidateProbes(result, [probe], 4, checkToneOrder: false);
        result.Details["limitation"] = "Synthetic audio-agent input only; physical WASAPI microphone capture and listening QA remain future work.";
    }
    ValidateHistory(result, database);
}

async Task BackpressureAsync(CaseResult result)
{
    var root = Path.Combine(runRoot, result.Name);
    await using var origin = new Sandbox(icecastRoot, root); await origin.StartAsync();
    await using var blocked = new BlockedOrigin();
    var database = Path.Combine(root, "playback.db");
    await using (var journal = new EventJournal(database))
    {
        var bad = origin.Output("blocked") with { Port = blocked.Port, WriteTimeoutMs = 500, QueueBlocks = 25 };
        await using var station = new StationEngine(ffmpeg, new StationSettings("backpressure", fixtures, [origin.Output("survivor"), bad]), journal);
        await station.StartAsync();
        await WaitAsync(() => station.Snapshot().Outputs[0].State == "SourceConnected", "Healthy source starts beside a blocked transport.");
        await using var listener = new Probe(ffmpeg, origin.Port, "/next-survivor");
        await Task.Delay(10000);
        var snapshot = station.Snapshot(); result.Details["station"] = snapshot;
        Check(result, snapshot.Outputs[1].EncoderStarts > 1 || snapshot.Outputs[1].DroppedBlocks > 0, "A real non-reading source transport reaches bounded recovery.");
        Check(result, snapshot.Outputs[1].QueuedBlocks <= 25, "The blocked output cannot grow its PCM queue.");
        Check(result, snapshot.Outputs[0].Connections == 1 && snapshot.Outputs[0].EncoderStarts == 1 && snapshot.Outputs[0].DroppedBlocks == 0,
            "Network backpressure does not restart or drop audio on the healthy sibling.");
        ValidateProbes(result, [listener], 6);
    }
    ValidateHistory(result, database);
}

async Task CorruptAsync(CaseResult result)
{
    var root = Path.Combine(runRoot, result.Name);
    await using var origin = new Sandbox(icecastRoot, root); await origin.StartAsync();
    var broken = Path.Combine(root, "invalid.wav"); await File.WriteAllTextAsync(broken, "This is not an audio file.");
    MediaItem[] playlist = [fixtures[0], new MediaItem("broken", broken, "", "Invalid fixture"), .. fixtures.Skip(1)];
    var database = Path.Combine(root, "playback.db");
    await using (var journal = new EventJournal(database))
    {
        await using var station = new StationEngine(ffmpeg, new StationSettings("corrupt", playlist, [origin.Output("corrupt")]), journal);
        await station.StartAsync(); await WaitAsync(() => station.Snapshot().Outputs[0].State == "SourceConnected", "Source starts with an invalid future asset.");
        await using var listener = new Probe(ffmpeg, origin.Port, "/next-corrupt");
        await Task.Delay(8500);
        var snapshot = station.Snapshot(); result.Details["station"] = snapshot;
        Check(result, snapshot.State == "Running" && snapshot.Outputs[0].EncoderStarts == 1 && snapshot.Outputs[0].DroppedBlocks == 0,
            "A corrupt upcoming file is rejected without restarting the source or stalling its successor.");
        ValidateProbes(result, [listener], 5);
    }
    Check(result, EventJournal.ReadEvents(database).Any(x => x.Type == "decode_failed" && x.ItemId == "broken"), "Rejected media has an explicit incident event.");
    ValidateHistory(result, database);
}

async Task DurableAsync(CaseResult result)
{
    var root = Path.Combine(runRoot, result.Name);
    await using var origin = new Sandbox(icecastRoot, root); await origin.StartAsync();
    MediaItem LongFixture(string id, int initialTone, int tailTone)
    {
        var path = Path.Combine(root, id + ".wav"); const int count = 20 * AudioFormat.SampleRate;
        using var file = new BinaryWriter(File.Create(path));
        file.Write("RIFF"u8); file.Write(36 + count * 4); file.Write("WAVEfmt "u8);
        file.Write(16); file.Write((short)1); file.Write((short)2); file.Write(48000); file.Write(192000);
        file.Write((short)4); file.Write((short)16); file.Write("data"u8); file.Write(count * 4);
        for (var i = 0; i < count; i++)
        {
            var tone = i < 4 * AudioFormat.SampleRate ? initialTone : tailTone;
            var sample = (short)(7000 * Math.Sin(2 * Math.PI * tone * i / AudioFormat.SampleRate));
            file.Write(sample); file.Write(sample);
        }
        return new(id, path, "Synthetic recovery fixture", id);
    }
    var songs = new[] { LongFixture("unfinished", 440, 880), LongFixture("next-song", 660, 660) };
    var pipeName = "onair-next-dev-" + Guid.NewGuid().ToString("N");
    var config = Path.Combine(root, "local.host.json");
    var db = Path.Combine(root, "playback.db");
    await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
    {
        Ffmpeg = ffmpeg, DataRoot = root, PipeName = pipeName,
        Stations = new[] { new StationSettings("durable", songs, [origin.Output("durable")], Repeat: false) }
    }));
    var sdk = Path.Combine(localRoot, "tools", "dotnet", "dotnet.exe");
    var assembly = Path.Combine(localRoot, "build", "bin", "OnAirNext.Host", "release", "OnAirNext.Host.dll");
    var hostLogs = new List<Task>();
    var generation = 0;
    async Task SaveHostLogAsync(Task<string> text, string file)
    {
        try { await File.WriteAllTextAsync(file, await text); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        { await File.WriteAllTextAsync(file, "Log ended during process cleanup: " + ex.GetType().Name); }
    }
    Process Launch()
    {
        var process = ChildProcess.Start(sdk, [assembly, "--config", config], output: true, role: "durable-test-host");
        var prefix = Path.Combine(root, "host-" + ++generation);
        hostLogs.Add(SaveHostLogAsync(process.StandardOutput.ReadToEndAsync(), prefix + ".stdout.log"));
        hostLogs.Add(SaveHostLogAsync(ChildProcess.DrainErrorAsync(process), prefix + ".stderr.log"));
        return process;
    }
    async Task<JsonDocument> Command(string action, string? id = null, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await pipe.WriteAsync(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Id = id ?? Guid.NewGuid().ToString("N"), Action = action, StationId = "durable" }) + "\n"), timeout.Token);
        return JsonDocument.Parse(await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("No command acknowledgement."));
    }
    async Task WaitRunning(Process activeHost)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        IOException? lastTransportError = null;
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                if (activeHost.HasExited) throw new InvalidOperationException($"Recovered host exited with {activeHost.ExitCode}; inspect the generation's retained stderr log.");
                try
                {
                    using var status = await Command("status", cancellationToken: deadline.Token);
                    var stations = status.RootElement.GetProperty("stations");
                    if (stations.GetArrayLength() > 0 && stations[0].GetProperty("State").GetString() == "Running") return;
                }
                catch (IOException ex) { lastTransportError = ex; }
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        throw new TimeoutException("Recovered host did not reach Running within 20 seconds. Last status transport error: " + lastTransportError?.Message);
    }
    Process? host = null;
    try
    {
        host = Launch(); await WaitRunning(host); await Task.Delay(6200);
        using (var status = await Command("status"))
            Check(result, status.RootElement.GetProperty("stations")[0].GetProperty("CurrentItemSamples").GetInt64() >= 5 * 48000,
                "Crash occurs beyond the fixture's distinctive four-second opening.");
        var originalHostPid = host.Id;
        await ChildProcess.StopAsync(host);
        await ChildProcess.StopAsync(host);
        Check(result, !ChildProcess.ActiveProcesses.ContainsKey(originalHostPid),
            "Repeated cleanup acknowledges only confirmed process exit and is safe after handle disposal.");
        host = null;
        host = Launch(); await WaitRunning(host);
        await using (var listener = new Probe(ffmpeg, origin.Port, "/next-durable"))
        {
            await WaitAsync(() => listener.Snapshot().DecodedSeconds >= 1.2 || listener.Snapshot().Error is not null,
                "Restart listener must decode a measurable opening window.", 12);
            var heard = listener.Snapshot(); result.Details["restartedListener"] = heard;
            Check(result, heard.DecodedSeconds >= 1 && heard.FirstTones.FirstOrDefault() == 440,
                "Independent listener hears the opening tone after a hard kill, rather than checkpoint-position tail audio.");
        }
        var starts = EventJournal.ReadEvents(db).Where(x => x.Type == "started").ToArray();
        Check(result, starts.Length == 2 && starts[0].OccurrenceId != starts[1].OccurrenceId &&
            starts[1].PreviousOccurrenceId == starts[0].OccurrenceId,
            "Actual repeated audio is recorded as a new attempt linked to the interrupted attempt.");
        var skipId = Guid.NewGuid().ToString("N");
        using (var accepted = await Command("skip", skipId))
            Check(result, accepted.RootElement.GetProperty("accepted").GetBoolean(), "Skip admission is acknowledged before hard kill.");
        await ChildProcess.StopAsync(host); host = null;
        host = Launch(); await WaitRunning(host);
        using (var retry = await Command("skip", skipId))
            Check(result, retry.RootElement.GetProperty("accepted").GetBoolean(), "A lost-response Skip retry is accepted after host restart.");
        await using (var listener = new Probe(ffmpeg, origin.Port, "/next-durable"))
        {
            await WaitAsync(() => listener.Snapshot().DecodedSeconds >= 1.2 || listener.Snapshot().Error is not null,
                "Post-Skip listener must decode a measurable window.", 12);
            var heard = listener.Snapshot(); result.Details["afterSkipListener"] = heard;
            Check(result, heard.DecodedSeconds >= 1 && heard.FirstTones.FirstOrDefault() == 660,
                "The consumed item stays consumed and a duplicate Skip cannot skip the next song.");
        }
        var events = EventJournal.ReadEvents(db);
        Check(result, events.Count(x => x.Type == "skipped") == 1, "One durable Skip produces exactly one terminal Skip despite process death and retry.");
        Check(result, events.Count(x => x.Type == "started" && x.ItemId == "unfinished") == 2,
            "The already skipped song is not started a third time.");
        Check(result, EventJournal.Verify(db), "Recovery and restarted attempts preserve the complete event hash chain.");
        using var stopped = await Command("stop");
        await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await ChildProcess.StopAsync(host); host = null;
        host = Launch();
        using (var status = await Command("status"))
            Check(result, status.RootElement.GetProperty("stations").GetArrayLength() == 1 &&
                status.RootElement.GetProperty("stations")[0].GetProperty("State").GetString() == "Stopped" &&
                !status.RootElement.GetProperty("stations")[0].GetProperty("RequestedRunning").GetBoolean(),
                "An explicitly stopped station stays stopped after host restart, while the command interface remains available.");
        using (var collision = await Command("stop", skipId))
            Check(result, !collision.RootElement.GetProperty("accepted").GetBoolean(),
                "Command IDs retain their original operation after restart; a rejected Stop cannot terminate the host.");
        using (var status = await Command("status"))
            Check(result, status.RootElement.GetProperty("stations").GetArrayLength() == 1 &&
                status.RootElement.GetProperty("stations")[0].GetProperty("State").GetString() == "Stopped" &&
                !status.RootElement.GetProperty("stations")[0].GetProperty("RequestedRunning").GetBoolean(),
                "Control interface remains responsive after a cross-operation receipt collision.");
        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
        using (var response = await client.GetAsync($"http://127.0.0.1:{origin.Port}/next-durable", HttpCompletionOption.ResponseHeadersRead))
            Check(result, response.StatusCode == System.Net.HttpStatusCode.NotFound,
                "Listener endpoint confirms that the stopped station did not silently autostart.");
    }
    finally
    {
        if (host is not null) await ChildProcess.StopAsync(host);
        await Task.WhenAll(hostLogs);
    }
}

async Task LifecycleAsync(CaseResult result)
{
    var root = Path.Combine(runRoot, result.Name);
    await using var origin = new Sandbox(icecastRoot, root); await origin.StartAsync();
    var pipe = "onair-next-dev-" + Guid.NewGuid().ToString("N");
    var configuration = Path.Combine(root, "local.host.json");
    await File.WriteAllTextAsync(configuration, JsonSerializer.Serialize(new
    {
        Ffmpeg = ffmpeg, DataRoot = root, PipeName = pipe,
        Stations = new[] { new StationSettings("a", fixtures, [origin.Output("life-a")], AutoStart: false),
            new StationSettings("b", fixtures, [origin.Output("life-b")]) }
    }));
    var sdk = Path.Combine(localRoot, "tools", "dotnet", "dotnet.exe");
    var assembly = Path.Combine(localRoot, "build", "bin", "OnAirNext.Host", "release", "OnAirNext.Host.dll");
    async Task Accepted(ControlHost host, string action, string station, string id)
    {
        using var response = await host.CommandAsync(action, station, id);
        Check(result, response.RootElement.GetProperty("accepted").GetBoolean(), action + " is durably acknowledged for " + station + ".");
    }
    await using (var host = new ControlHost(sdk, assembly, configuration, pipe, Path.Combine(root, "host-1")))
    {
        await host.WaitForStationAsync("b", "Running");
        using (var status = await host.CommandAsync("status"))
        {
            var a = status.RootElement.GetProperty("stations").EnumerateArray().Single(x => x.GetProperty("Id").GetString() == "a");
            Check(result, a.GetProperty("State").GetString() == "Stopped" && !a.GetProperty("RequestedRunning").GetBoolean(),
                "Stopped stations remain visible and do not acquire an output before a Start request.");
        }
        await using var healthy = new Probe(ffmpeg, origin.Port, "/next-life-b");
        await WaitAsync(() => healthy.Snapshot().DecodedSeconds >= 1.2, "Sibling listener must warm up.", 12);
        long siblingConnections, siblingStarts, siblingSamples;
        using (var status = await host.CommandAsync("status"))
        {
            var b = status.RootElement.GetProperty("stations").EnumerateArray().Single(x => x.GetProperty("Id").GetString() == "b");
            siblingConnections = b.GetProperty("Outputs")[0].GetProperty("Connections").GetInt64();
            siblingStarts = b.GetProperty("Outputs")[0].GetProperty("EncoderStarts").GetInt64();
            siblingSamples = b.GetProperty("Samples").GetInt64();
        }
        await Accepted(host, "start-station", "a", "start-a-1");
        await Accepted(host, "start-station", "a", "start-a-1");
        await host.WaitForStationAsync("a", "Running");
        await using (var listener = new Probe(ffmpeg, origin.Port, "/next-life-a"))
        {
            await WaitAsync(() => listener.Snapshot().DecodedSeconds >= 1.2, "Started station must decode.", 12);
            Check(result, listener.Snapshot().SilentWindows == 0, "A UI-compatible Start command produces real decoded audio.");
        }
        await Accepted(host, "stop-station", "a", "stop-a-1");
        await host.WaitForStationAsync("a", "Stopped");
        await Accepted(host, "start-station", "a", "start-a-1");
        using (var status = await host.CommandAsync("status"))
        {
            var a = status.RootElement.GetProperty("stations").EnumerateArray().Single(x => x.GetProperty("Id").GetString() == "a");
            Check(result, a.GetProperty("State").GetString() == "Stopped" && !a.GetProperty("RequestedRunning").GetBoolean(),
                "A duplicate old Start acknowledgement does not undo a later Stop.");
        }
        using (var collision = await host.CommandAsync("skip", "a", "start-a-1"))
            Check(result, !collision.RootElement.GetProperty("accepted").GetBoolean(), "Cross-operation command ID reuse is rejected while stopped.");
        await Accepted(host, "start-station", "a", "start-a-2");
        await host.WaitForStationAsync("a", "Running");
        await Accepted(host, "stop-station", "a", "stop-a-2");
        await host.WaitForStationAsync("a", "Stopped");
        using (var status = await host.CommandAsync("status"))
        {
            var b = status.RootElement.GetProperty("stations").EnumerateArray().Single(x => x.GetProperty("Id").GetString() == "b");
            Check(result, b.GetProperty("Samples").GetInt64() > siblingSamples &&
                b.GetProperty("Outputs")[0].GetProperty("Connections").GetInt64() == siblingConnections &&
                b.GetProperty("Outputs")[0].GetProperty("EncoderStarts").GetInt64() == siblingStarts,
                "Starting/stopping A never resets B's programme, encoder or source connection.");
        }
        await WaitAsync(() => healthy.Snapshot().DecodedSeconds >= 3, "Sibling listener must provide a measurable lifecycle window.", 12);
        var heard = healthy.Snapshot(); result.Details["healthySibling"] = heard;
        Check(result, heard.DecodedSeconds >= 3 && heard.SilentWindows == 0 && heard.Error is null,
            "Independent sibling listener continues through selected-station lifecycle operations.");
    }
    await using (var restarted = new ControlHost(sdk, assembly, configuration, pipe, Path.Combine(root, "host-2")))
    {
        await restarted.WaitForStationAsync("b", "Running");
        using (var status = await restarted.CommandAsync("status"))
        {
            var a = status.RootElement.GetProperty("stations").EnumerateArray().Single(x => x.GetProperty("Id").GetString() == "a");
            Check(result, a.GetProperty("State").GetString() == "Stopped" && !a.GetProperty("RequestedRunning").GetBoolean(),
                "After process death B restarts while explicitly stopped A remains stopped and visible.");
        }
        using var stop = await restarted.CommandAsync("stop");
        await restarted.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Check(result, restarted.Process.ExitCode == 0, "The lifecycle host shuts down with all owned sessions released.");
    }
    Check(result, EventJournal.Verify(Path.Combine(root, "playback.db")), "Independent lifecycle operations preserve the complete event hash chain.");
}

async Task FiniteAsync(CaseResult result)
{
    var root = Path.Combine(runRoot, result.Name);
    await using var origin = new Sandbox(icecastRoot, root); await origin.StartAsync();
    var pipe = "onair-next-dev-" + Guid.NewGuid().ToString("N");
    var configuration = Path.Combine(root, "local.host.json");
    await File.WriteAllTextAsync(configuration, JsonSerializer.Serialize(new
    {
        Ffmpeg = ffmpeg, DataRoot = root, PipeName = pipe,
        Stations = new[] { new StationSettings("finite", [fixtures[0]], [origin.Output("finite")], Repeat: false) }
    }));
    var sdk = Path.Combine(localRoot, "tools", "dotnet", "dotnet.exe");
    var assembly = Path.Combine(localRoot, "build", "bin", "OnAirNext.Host", "release", "OnAirNext.Host.dll");
    var database = Path.Combine(root, "playback.db");
    await using (var first = new ControlHost(sdk, assembly, configuration, pipe, Path.Combine(root, "host-1")))
    {
        await first.WaitForStationAsync("finite", "Completed");
        var events = EventJournal.ReadEvents(database);
        Check(result, events.Count(x => x.Type == "started") == 1 && events.Count(x => x.Type == "ended") == 1,
            "Finite programme has exactly one real start and one committed completion.");
        using var status = await first.CommandAsync("status");
        Check(result, status.RootElement.GetProperty("stations")[0].GetProperty("Outputs")[0].GetProperty("FramesWritten").GetInt64() > 0,
            "The finite programme reached a real encoder/source before completion.");
    }
    await using (var restarted = new ControlHost(sdk, assembly, configuration, pipe, Path.Combine(root, "host-2")))
    {
        await restarted.WaitForStationAsync("finite", "Completed");
        Check(result, EventJournal.ReadEvents(database).Count(x => x.Type == "started") == 1,
            "Restart at committed EOF cannot replay the completed finite playlist.");
        using (var status = await restarted.CommandAsync("status"))
            Check(result, status.RootElement.GetProperty("stations")[0].GetProperty("Outputs")[0].GetProperty("EncoderStarts").GetInt64() == 0,
                "Exhausted recovery does not open a duplicate encoder/source session.");
        using var stop = await restarted.CommandAsync("stop");
        await restarted.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
    }
    Check(result, EventJournal.Verify(database), "EOF recovery preserves the complete event hash chain.");
}

async Task HostAsync(CaseResult result)
{
    var root = Path.Combine(runRoot, result.Name);
    await using var origin = new Sandbox(icecastRoot, root); await origin.StartAsync();
    var pipeName = "onair-next-dev-" + Guid.NewGuid().ToString("N");
    var config = Path.Combine(root, "local.host.json");
    await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
    {
        Ffmpeg = ffmpeg, DataRoot = root, PipeName = pipeName,
        Stations = new[] { new StationSettings("host", fixtures, [origin.Output("host")]) }
    }));
    var sdk = Path.Combine(localRoot, "tools", "dotnet", "dotnet.exe");
    var assembly = Path.Combine(localRoot, "build", "bin", "OnAirNext.Host", "release", "OnAirNext.Host.dll");
    var host = ChildProcess.Start(sdk, [assembly, "--config", config], output: true, role: "engine-host");
    var stderr = ChildProcess.DrainErrorAsync(host); var stdout = host.StandardOutput.ReadToEndAsync();
    async Task<JsonDocument> CommandAsync(string action, string? id = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { Id = id ?? Guid.NewGuid().ToString("N"), Action = action, StationId = "host" }));
        return JsonDocument.Parse(await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Engine IPC returned no result."));
    }
    try
    {
        await Task.Delay(1800);
        using (var status = await CommandAsync("status"))
            Check(result, status.RootElement.GetProperty("stations")[0].GetProperty("State").GetString() == "Running", "Independent engine host responds through its current-user named pipe.");
        await using (var listener = new Probe(ffmpeg, origin.Port, "/next-host"))
        {
            await Task.Delay(2200);
            var commandId = Guid.NewGuid().ToString("N");
            using var first = await CommandAsync("skip", commandId); using var duplicate = await CommandAsync("skip", commandId);
            using var collision = await CommandAsync("stop", commandId);
            Check(result, !collision.RootElement.GetProperty("accepted").GetBoolean(), "Reusing a command ID for a different operation is rejected.");
            await Task.Delay(1700);
            using var status = await CommandAsync("status");
            Check(result, status.RootElement.GetProperty("stations")[0].GetProperty("SkippedItems").GetInt64() == 1, "A lost-response retry of Skip is executed once within the host command session.");
            Check(result, listener.Snapshot().DecodedSeconds >= 2, "Disconnecting the control client leaves decoded broadcasting active.");
            using var stop = await CommandAsync("stop");
        }
        await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(result, host.ExitCode == 0, "Explicit Stop shuts down the independent host cleanly: " + (host.ExitCode == 0 ? "ok" : await stderr));
    }
    finally { await ChildProcess.StopAsync(host); _ = stdout; }
    ValidateHistory(result, Path.Combine(root, "playback.db"));
}

internal sealed class CaseResult(string name)
{
    public string Name { get; } = name;
    public bool Passed { get; set; }
    public double DurationSeconds { get; set; }
    public List<object> Checks { get; } = new();
    public Dictionary<string, object> Details { get; } = new();
    public string? Error { get; set; }
}
