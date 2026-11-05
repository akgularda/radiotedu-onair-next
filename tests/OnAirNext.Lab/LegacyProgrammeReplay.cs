using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OnAirNext.Engine;

namespace OnAirNext.Lab;

internal static class LegacyProgrammeReplay
{
    private static void Check(CaseResult result, bool condition, string message)
    {
        result.Checks.Add(new { Passed = condition, Description = message });
        if (!condition) throw new InvalidDataException(message);
    }

    internal static async Task<int> RunAsync(string ffmpeg, string icecast, string fixture, string observer)
    {
        fixture = Path.GetFullPath(fixture);
        observer = Path.GetFullPath(observer);
        if (!Path.GetFileName(fixture).StartsWith("next-main-programme-fixture-", StringComparison.Ordinal) ||
            !string.Equals(Path.GetDirectoryName(fixture), Path.GetDirectoryName(observer), StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(observer) != "recovery-load.jsonl")
            throw new ArgumentException("Use an isolated captured fixture beside the live observer.");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture, "fixture.json")));
        var capture = manifest.RootElement;
        var music = Path.Combine(fixture, "music");
        var sweepers = Path.Combine(fixture, "sweepers");
        var expected = Enumerable.Range(1, 6).Select(i => $"track-{i:00}.wav").ToArray();
        var clip = Directory.GetFiles(sweepers).Single();
        using var sourceSignal = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture, "sweeper-signal-evidence.json")));
        var sourceTail = sourceSignal.RootElement.GetProperty("lastQuietMs").GetDouble() / 1000;
        var shortened = capture.TryGetProperty("sweeperVariant", out var variant) && variant.GetString() == "shorter-quiet-tail";
        var result = new CaseResult("isolated-legacy-main-programme");
        var runId = "legacy-main-replay-" + Guid.NewGuid().ToString("N");
        var run = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RadioTEDU", "OnAirNext-Dev", "runs", runId);
        var guard = new LiveWorkloadGuard(observer);
        var lastCheck = Stopwatch.StartNew();
        async Task Wait(Func<bool> done, string description, int seconds)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            while (!done())
            {
                if (lastCheck.ElapsedMilliseconds >= 1500) { guard.Check(); lastCheck.Restart(); }
                try { await Task.Delay(100, limit.Token); }
                catch (OperationCanceledException) when (limit.IsCancellationRequested) { throw new TimeoutException(description); }
            }
        }
        try
        {
            guard.Check();
            Check(result, capture.GetProperty("stationId").GetInt32() == 4 &&
                capture.GetProperty("sweeperEveryCompletedSongs").GetInt32() == 5 &&
                Directory.GetFiles(music).Select(Path.GetFileName).Order().SequenceEqual(expected), "The private fixture is the six-song /radio sequence and five-song cadence.");
            await using (var file = File.OpenRead(clip))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(file));
                Check(result, hash.Equals(capture.GetProperty("sweeperSha256").GetString(), StringComparison.OrdinalIgnoreCase) &&
                    hash.Equals(sourceSignal.RootElement.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase),
                    shortened ? "The private shortened PowerApp asset matches its measured source signal."
                              : "The sweeper bytes match the active PowerApp asset and the measured source signal.");
            }

            Directory.CreateDirectory(run);
            await using var origin = new Sandbox(icecast, Path.Combine(run, "origin"));
            await origin.StartAsync();
            var outputs = new[] { origin.Output("legacy-main-aac"), origin.Output("legacy-main-he", CodecProfile.HeAacV2_64) };
            var settings = new StationSettings("legacy-main", [], outputs, AutoStart: false);
            await using var journal = new EventJournal(Path.Combine(run, "playback.db"));
            await journal.InitializeDesiredStateAsync(settings);
            var kinds = StationBranding.InventoryKinds([settings.Id]);
            await using var library = new StationLibrary(journal, ffmpeg, run, new[] { settings.Id }.Concat(kinds.Keys), contentKinds: kinds);
            using var branding = new StationBranding(journal, library, [settings.Id]);
            using var queues = new StationQueues(journal, library, [settings.Id]);
            await branding.InitializeAsync(); await library.InitializeAsync(); await queues.InitializeAsync();
            await library.SetRootAsync(settings.Id, music, "music");
            await library.SetRootAsync(StationBranding.InventoryKey(settings.Id, ContentKind.Sweeper), sweepers, "sweepers");
            await Wait(() => { library.Wake(); return library.ReadyInventory(settings.Id).ReadyCount == expected.Length && branding.Source(settings.Id).Snapshot()?.SweeperCount == 1; }, "The private media inventory did not become ready.", 25);
            await branding.SaveAsync(settings.Id, 0, new(new(false, 2), new(true, 5)), "powerapp-five");
            var ids = new List<string>();
            var page = library.Page(settings.Id, "", 0);
            foreach (var name in expected)
            {
                var item = page.Items.Single(a => a.RelativePath == name);
                var id = Guid.NewGuid().ToString("N");
                await queues.EnqueueAsync(settings.Id, new(page.Root!.Revision, item.Id, item.EditVersion, item.SourceStamp), (await queues.SnapshotAsync(settings.Id)).Revision, id);
                ids.Add(id);
            }
            await journal.RequestRunningIntentAsync(settings.Id, true, "private-run");
            await using var engine = new StationEngine(ffmpeg, settings, journal, planner: queues.Planner(settings), branding: branding.Source(settings.Id));
            var probes = new List<Probe>();
            try
            {
                await engine.StartAsync();
                await Wait(() => engine.Completion.IsCompleted || engine.Snapshot().Outputs.All(o => o.State == "SourceConnected"), "The private outputs did not connect.", 12);
                if (engine.Completion.IsCompleted) await engine.Completion;
                foreach (var output in outputs) probes.Add(new Probe(ffmpeg, origin.Port, output.Mount, captureQuietIntervals: true));
                await Wait(() => engine.Completion.IsCompleted || engine.Snapshot().CompletedItems >= 7, "The six songs and sweeper did not finish.", 85);
                if (engine.Completion.IsCompleted) await engine.Completion;
                await Wait(() => EventJournal.ReadEvents(journal.DatabasePath).Count(e => e.Type == "ended") >= 7, "The fixture journal did not finish.", 5);
                var endings = EventJournal.ReadEvents(journal.DatabasePath).Where(e => e.Type == "ended").Take(7).ToArray();
                Check(result, endings.Where(e => e.Kind == ContentKind.Music).Select(e => e.QueueEntryId).SequenceEqual(ids), "Every song ended exactly once in the captured queue order.");
                Check(result, endings.Select(e => e.Kind).SequenceEqual(new[] { ContentKind.Music, ContentKind.Music, ContentKind.Music, ContentKind.Music, ContentKind.Music, ContentKind.Sweeper, ContentKind.Music }), "PowerApp followed song five, then the sixth song continued.");
                Check(result, EventJournal.Verify(journal.DatabasePath), "The exact play order is durably recorded in the fixture journal.");
                var audio = probes.Select(p => p.Snapshot()).ToArray();
                result.Details["listeners"] = audio;
                result.Details["publishers"] = engine.Snapshot().Outputs;
                result.Details["productionChanged"] = false;
                var quiet = probes.Select(p => p.SnapshotQuietIntervals().Where(x => x.Duration > .2001).ToArray()).ToArray();
                result.Details["quietOver200ms"] = quiet;
                result.Details["sourceSweeperTailSeconds"] = sourceTail;
                result.Details["shortenedSweeperFixture"] = shortened;
                for (var i = 0; i < audio.Length; i++)
                {
                    var listener = audio[i];
                    // The only source quiet is immediately before the sixth
                    // seven-second excerpt. A small allowance covers codec
                    // priming and the final frames still in flight at capture.
                    var expectedTailEnd = listener.DecodedSeconds - capture.GetProperty("excerptSeconds").GetDouble();
                    var expectedQuiet = shortened ? sourceTail <= .2 && quiet[i].Length == 0
                        : quiet[i].Length == 1 && Math.Abs(quiet[i][0].EndSeconds - expectedTailEnd) <= .5 &&
                          Math.Abs(quiet[i][0].Duration - sourceTail) <= .2;
                    Check(result, listener.Error is null && listener.DecodedSeconds >= 55 && expectedQuiet,
                        shortened ? $"{listener.Mount} decoded the shortened fixture without a gap over 200 ms."
                                  : $"{listener.Mount} decoded the programme and its only gap over 200 ms matches the original PowerApp tail.");
                }
                Check(result, engine.Snapshot().Outputs.All(o => o.Connections == 1 && o.EncoderStarts == 1), "Both test outputs kept their original source connection and encoder.");
            }
            finally
            {
                await Task.WhenAll(probes.Select(p => p.DisposeAsync().AsTask()));
                await journal.RequestRunningIntentAsync(settings.Id, false, "private-stop");
            }
            result.Passed = true;
        }
        catch (Exception ex) { result.Error = ex.ToString(); }
        var reportPath = Path.Combine(fixture, runId + "-evidence.json");
        await using (var report = new FileStream(reportPath, FileMode.CreateNew))
            await JsonSerializer.SerializeAsync(report, result, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(JsonSerializer.Serialize(new { result.Passed, Checks = result.Checks.Count, result.Error, Fixture = fixture, Report = reportPath }));
        return result.Passed ? 0 : 1;
    }
}
