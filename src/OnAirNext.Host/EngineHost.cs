using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using OnAirNext.Engine;
using OnAirNext.ServiceControl;

namespace OnAirNext.Host;

internal static class EngineHost
{
    public static async Task<int> RunAsync(string configPath, bool serviceMode, CancellationToken externalStop, Action ready, Action stopping)
    {
        if (serviceMode)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows service mode requires Windows.");
            WindowsServiceDiagnostics.Stage("configuration protection");
            ServiceFileProtection.Bootstrap(configPath);
        }
        if (serviceMode) WindowsServiceDiagnostics.Stage("configuration read");
        if (new FileInfo(configPath).Length > 1048576) throw new ArgumentException("The engine configuration exceeds its size limit.");
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var config = JsonSerializer.Deserialize<HostSettings>(await File.ReadAllTextAsync(configPath, externalStop), json)
            ?? throw new InvalidDataException("Missing engine configuration.");
        if (config.Commissioning && !serviceMode)
            throw new ArgumentException("Production destinations are available only through the protected installed Windows service.");
        if (!config.Commissioning && config.Stations.SelectMany(station => station.Outputs).Any(output => output.Commissioned))
            throw new ArgumentException("Production destinations require an explicit commissioning configuration.");
        if (config.Commissioning && config.Stations.SelectMany(station => station.Outputs).Any(output => !output.Commissioned))
            throw new ArgumentException("Keep commissioned and development output configurations separate.");
        if (!config.PipeName.StartsWith("onair-next-dev-", StringComparison.Ordinal) || config.PipeName.Length > 100 ||
            config.PipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("Use an isolated development pipe name.");
        if (serviceMode)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows service mode requires Windows.");
            if (config.PipeName != ServiceEndpoint.Pipe) throw new ArgumentException("Use the reserved development service pipe.");
            ServiceFileProtection.ValidateSettings(config.DataRoot, config.Ffmpeg);
        }
        else if (ServiceEndpoint.IsReserved(config.PipeName) || (config.PipeName.EndsWith("-files", StringComparison.Ordinal) || config.PipeName.EndsWith("-reports", StringComparison.Ordinal))) throw new ArgumentException("The reserved pipe belongs to the Windows service only.");
        _ = ServiceEndpoint.Reports(config.PipeName);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(externalStop);
        if (serviceMode) WindowsServiceDiagnostics.Stage("pipe policy");
        ServicePipePolicy? servicePolicy = null;
        if (serviceMode && OperatingSystem.IsWindows())
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            servicePolicy = new ServicePipePolicy(identity.User?.Value ?? throw new InvalidOperationException("Service identity is unavailable."),
                config.OperatorSid ?? throw new ArgumentException("Select a Windows operator during setup."));
        }
        if (serviceMode) WindowsServiceDiagnostics.Stage("pipe create");
        await using var pipe = serviceMode && OperatingSystem.IsWindows()
            ? servicePolicy!.CreateServer()
            : new NamedPipeServerStream(config.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        if (serviceMode) WindowsServiceDiagnostics.Stage("journal open");
        await using var journal = serviceMode ? EventJournal.OpenIsolatedWindowsService()
            : new EventJournal(Path.Combine(config.DataRoot, "playback.db"));
        if (serviceMode) WindowsServiceDiagnostics.Stage("library and coordinator construction");
        var brandingStations=config.Stations.Where(x=>x.Repeat).Select(x=>x.Id).ToArray();
        var brandingKinds=StationBranding.InventoryKinds(brandingStations);
        var libraryInventories=brandingStations.Concat(brandingKinds.Keys).ToArray();
        await using var library = new StationLibrary(journal, config.Ffmpeg, config.DataRoot, libraryInventories,contentKinds:brandingKinds);
        using var branding = new StationBranding(journal,library,brandingStations);
        using var queues = new StationQueues(journal,library,brandingStations);
        await using var coordinator = new StationCoordinator(config.Ffmpeg, config.Stations, journal,
            settings => new StationEngine(config.Ffmpeg, settings, journal,
                outputs: config.Commissioning ? settings.Outputs.Select(output => (IProgramOutput)new IcecastOutput(config.Ffmpeg, output, LegacyServiceFence.RequireReleased)).ToArray() : null,
                planner: queues.Planner(settings), branding:settings.Repeat?branding.Source(settings.Id):null),
            commissionedService: config.Commissioning);
        await using var sourceChecks = new SourceAccessChecks(async path =>
        {
            if (ChildProcess.ActiveProcesses.Values.Any(role => role.Contains("cleanup pending", StringComparison.Ordinal)))
                throw new InvalidOperationException("An owned worker still needs confirmed cleanup.");
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Engine executable is unavailable.");
            var arguments = new List<string>();
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                arguments.Add(typeof(EngineHost).Assembly.Location);
            arguments.AddRange(["--probe-media-source", path]);
            var output = await ChildProcess.CaptureAsync(executable, arguments.ToArray());
            var result = JsonSerializer.Deserialize<SourceAccessResult>(output) ?? throw new InvalidDataException("Source check returned no result.");
            if (serviceMode && result.IdentitySid != "S-1-5-19") throw new UnauthorizedAccessException("The source check did not run under the service identity.");
            return result;
        });
        library.Changed += coordinator.RefreshLibrary;
        queues.Changed += coordinator.RefreshLibrary;
        try
        {
            if (serviceMode) WindowsServiceDiagnostics.Stage("library initialization");
            await branding.InitializeAsync();
            await library.InitializeAsync();
            if (serviceMode) WindowsServiceDiagnostics.Stage("coordinator initialization");
            await coordinator.InitializeAsync(queues.InitializeAsync);
            var inbox = new UploadInbox(journal, library);
            await inbox.InitializeAsync();
            await using var fileServer = new UploadPipeServer(config.PipeName, serviceMode, servicePolicy, inbox, stop.Token);
            await using var reports = new ReportExports(journal, config.Stations.Select(station => station.Id));
            await reports.InitializeAsync();
            await using var reportServer = new ReportPipeServer(config.PipeName, serviceMode, servicePolicy, reports, stop.Token);
            await using var privateCue=new PrivateCuePipeServer(config.PipeName,serviceMode,servicePolicy,library,config.Ffmpeg,stop.Token);
            await using var legacyPreviews = new LegacyImportPreviews(config.DataRoot, config.Stations.Select(station => station.Id),
                () => coordinator.Snapshot().All(station => station.State == "Stopped" && station.Outputs.Count == 0));
            legacyPreviews.Initialize();
            void RequireMusicTargetUnarmed(string stationId)
            {
                var target = coordinator.Snapshot().SingleOrDefault(station => station.Id == stationId);
                if (target is null || target.State != "Stopped" || target.RequestedRunning || target.Outputs.Count != 0)
                    throw new InvalidOperationException("Music migration requires this target channel stopped with no configured outputs. Other channels keep running.");
            }
            if (serviceMode) WindowsServiceDiagnostics.Stage("controller ready");
            ready();
            Console.WriteLine(JsonSerializer.Serialize(new { state = "controller-ready", config.PipeName, stations = config.Stations.Count, loopbackOnly = !config.Commissioning, mode = config.Commissioning ? "commissioned-service" : serviceMode ? "windows-service" : "developer" }));
            while (!stop.IsCancellationRequested)
            {
                var connected = false;
                try
                {
                    await pipe.WaitForConnectionAsync(stop.Token);
                    connected = true;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    var frame = await PipeFrames.ReadAsync(pipe, 65536, deadline.Token);
                    if (frame is null) continue;
                    if (serviceMode && OperatingSystem.IsWindows()) servicePolicy!.Authorize(pipe);
                    var request = JsonSerializer.Deserialize<EngineCommand>(frame, json);
                    if (request is null || string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 128) continue;
                    string result;
                    var shutdown = false;
                    try
                    {
                        switch (request.Action)
                        {
                            case "status":
                                await journal.RequireRecoveryReaderAsync(request.JournalFeatures);
                                result = JsonSerializer.Serialize(new { stations = coordinator.Snapshot(),
                                    audit = new { journal.PendingEvents, journal.RejectedEvents, journal.Error },
                                    children = ChildProcess.ActiveProcesses, privateCue=privateCue.Snapshot(), controller = new { mode = serviceMode ? "windows-service" : "developer", service = serviceMode ? ServiceEndpoint.Name : null, capabilities = new[] { "legacy-music-assignment-v1", RecoveryCompatibility.HeldRotationFeature, "branding-library-v1","private-cue-v1",RecoveryCompatibility.StationQueueFeature } } });
                                break;
                            case "queue-status" when request.StationId is { } queueStation:
                                result=JsonSerializer.Serialize(new{queue=await queues.SnapshotAsync(queueStation)});break;
                            case "queue-append" when request.StationId is { } appendStation && request.QueueSource is { } queueSource:
                                await queues.EnqueueAsync(appendStation,queueSource,request.QueueRevision,request.Id);
                                result=JsonSerializer.Serialize(new{accepted=true,queue=await queues.SnapshotAsync(appendStation)});break;
                            case "queue-up" or "queue-down" or "queue-remove" when request.StationId is { } editQueueStation && request.QueueEntryId is { } queueEntry:
                                var queueEdit=request.Action=="queue-up"?QueueEdit.MoveUp:request.Action=="queue-down"?QueueEdit.MoveDown:QueueEdit.Remove;
                                await queues.EditAsync(editQueueStation,queueEntry,request.QueueRevision,queueEdit,request.Id);
                                result=JsonSerializer.Serialize(new{accepted=true,queue=await queues.SnapshotAsync(editQueueStation)});break;
                            case "private-cue-status":result=JsonSerializer.Serialize(new{cue=privateCue.Snapshot()});break;
                            case "private-cue-stop" when request.SessionId is { } cueSession:
                                privateCue.RequestStop(cueSession);result=JsonSerializer.Serialize(new{accepted=true,cue=privateCue.Snapshot()});break;
                            case "branding-status" when request.StationId is { } brandingStation:
                                result=JsonSerializer.Serialize(new{branding=await branding.SnapshotAsync(brandingStation)});break;
                            case "branding-save" when request.StationId is { } saveBrandingStation && request.BrandingRules is { } brandingRules:
                                result=JsonSerializer.Serialize(new{accepted=true,configuration=await branding.SaveAsync(saveBrandingStation,request.Revision,brandingRules,request.Id)});break;
                            case "start-station" or "stop-station" when request.StationId is { } targetStation:
                                await coordinator.RequestRunningAsync(targetStation, request.Action == "start-station", request.Id);
                                result = "{\"accepted\":true}"; break;
                            case "loudness" when request.StationId is { } meterStation:
                                result = JsonSerializer.Serialize(new { station = meterStation, meter = coordinator.Loudness(meterStation) });
                                break;
                            case "loudness-history" when request.StationId is { } historyStation:
                                if (!config.Stations.Any(station => station.Id == historyStation)) throw new ArgumentException("Unknown station.");
                                result = JsonSerializer.Serialize(new { station = historyStation, items = journal.MeasurementHistory(historyStation, request.Offset) });
                                break;
                            case "legacy-preview":
                                result = JsonSerializer.Serialize(new { preview = legacyPreviews.Snapshot(request.PreviewId), scope = LegacyImportPreview.Scope }); break;
                            case "legacy-preview-create" when request.Path is { } legacyRoot:
                                result = JsonSerializer.Serialize(new { accepted = true, preview = legacyPreviews.Start(request.Id, legacyRoot), scope = LegacyImportPreview.Scope }); break;
                            case "legacy-mapping-save" when request.PreviewId is { } previewId && request.Mappings is { } mappings && request.Revision is >= 0 and <= int.MaxValue:
                                result = JsonSerializer.Serialize(new { accepted = true, preview = legacyPreviews.SaveMappings(previewId, checked((int)request.Revision), mappings), scope = LegacyImportPreview.Scope }); break;
                            case "reports":
                                result = JsonSerializer.Serialize(new { items = reports.Snapshot(request.Offset), error = reports.Error ?? reportServer.Error }); break;
                            case "report-export" when request.ReportPeriod is { } reportPeriod && request.ReportSelection is { } selection:
                                result = JsonSerializer.Serialize(new { accepted = true, report = await reports.StartAsync(request.Id, reportPeriod, selection, request.StationId) }); break;
                            case "report-cancel" when request.ReportId is { } reportId:
                                result = JsonSerializer.Serialize(new { accepted = true, report = await reports.CancelAsync(reportId) }); break;
                            case "skip" when request.StationId is { } skipStation:
                                await coordinator.SkipAsync(skipStation, request.Id);
                                result = "{\"accepted\":true}"; break;
                            case "library-status" when request.StationId is { } libraryStation:
                                var libraryPage = library.Page(libraryStation, request.Query ?? "", request.Offset);
                                result = JsonSerializer.Serialize(new { page = libraryPage, worker = library.Status(libraryStation), canImportFiles = library.CanImportFiles(libraryPage.Root), uploads = inbox.Snapshot(libraryStation), uploadError = fileServer.Error });
                                break;
                            case "library-managed" when request.StationId is { } managedStation:
                                await library.UseManagedRootAsync(managedStation, request.Id);
                                result = "{\"accepted\":true}"; break;
                            case "library-source-check" when request.StationId is { } sourceStation && request.Path is { } sourcePath:
                                if (!libraryInventories.Contains(sourceStation,StringComparer.Ordinal)) throw new ArgumentException("Unknown library inventory.");
                                result = JsonSerializer.Serialize(new { accepted = true, check = sourceChecks.Start(sourceStation, sourcePath, request.Id) }); break;
                            case "library-source-status" when request.StationId is { } checkedStation && request.SourceCheckId is { } sourceId:
                                result = JsonSerializer.Serialize(new { check = sourceChecks.Get(checkedStation, sourceId) }); break;
                            case "library-legacy-root" when request.StationId is { } musicStation && request.Path is { } musicPath && request.PreviewId is { } musicPreview && request.MappingRevision is >= 1:
                                var normalizedMusicPath = EventJournal.NormalizeLibraryPath(musicPath);
                                var previousAssignment = journal.LegacyMusicAssignment(request.Id);
                                LegacyMusicProof musicProof;
                                if (previousAssignment is not null)
                                {
                                    musicProof = previousAssignment.Proof;
                                    if (musicProof.PreviewId != musicPreview || musicProof.MappingRevision != request.MappingRevision ||
                                        musicProof.TargetStationId != musicStation || musicProof.ExpectedLibraryRevision != request.Revision ||
                                        musicProof.SelectedMusicFolder != normalizedMusicPath)
                                        throw new InvalidOperationException("This request ID belongs to another saved music assignment.");
                                }
                                else
                                {
                                    RequireMusicTargetUnarmed(musicStation);
                                    musicProof = await Task.Factory.StartNew(() =>
                                    {
                                        if (OperatingSystem.IsWindows()) Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                                        return legacyPreviews.PrepareMusicAssignment(musicPreview, request.MappingRevision.GetValueOrDefault(),
                                            musicStation, normalizedMusicPath, request.Revision);
                                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                                }
                                await library.SetLegacyMusicRootAsync(musicStation, normalizedMusicPath, request.Id, musicProof, () =>
                                {
                                    RequireMusicTargetUnarmed(musicStation);
                                    sourceChecks.RequireReady(musicStation, normalizedMusicPath, request.SourceCheckId);
                                    legacyPreviews.RequireCurrentMusicMapping(musicProof);
                                });
                                result = JsonSerializer.Serialize(new { accepted = true, assignment = journal.LegacyMusicAssignment(request.Id) }); break;
                            case "library-root" when request.StationId is { } rootStation && request.Path is { } rootPath:
                                await library.SetRootAsync(rootStation, rootPath, request.Id, serviceMode
                                    ? () => sourceChecks.RequireReady(rootStation, rootPath, request.SourceCheckId) : null);
                                result = "{\"accepted\":true}"; break;
                            case "library-import" when request.StationId is { } importStation && request.Paths is { } importPaths:
                                if (serviceMode) throw new InvalidOperationException("Use the Library file transfer controls to import into the service.");
                                await library.ImportAsync(importStation, importPaths, request.Id);
                                result = "{\"accepted\":true}"; break;
                            case "library-rescan" when request.StationId is { } rescanStation:
                                await library.RescanAsync(rescanStation, request.Id);
                                result = "{\"accepted\":true}"; break;
                            case "library-metadata" or "library-reset-metadata" or "library-retire" or "library-restore" or "library-analyze-loudness"
                                when request.StationId is { } editStation && request.AssetId is { } assetId:
                                var kind = request.Action switch
                                {
                                    "library-metadata" => LibraryEditKind.Metadata, "library-reset-metadata" => LibraryEditKind.ResetMetadata,
                                    "library-retire" => LibraryEditKind.Retire, "library-analyze-loudness" => LibraryEditKind.AnalyzeLoudness, _ => LibraryEditKind.Restore
                                };
                                var edited = await library.EditAssetAsync(editStation, request.Revision, assetId, request.ExpectedVersion, kind,
                                    request.Id, request.Artist, request.Title);
                                result = JsonSerializer.Serialize(new { accepted = true, asset = edited }); break;
                            case "stop" when !serviceMode:
                                shutdown = await coordinator.StopAllAsync(request.Id);
                                result = "{\"accepted\":true}"; break;
                            default:
                                result = "{\"accepted\":false,\"error\":\"Unknown command or station.\"}"; break;
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                    {
                        result = JsonSerializer.Serialize(new { accepted = false, error = ex.Message });
                    }
                    try
                    {
                        await pipe.WriteAsync(Encoding.UTF8.GetBytes(result + "\n"), deadline.Token);
                        // Keep the sole pipe instance alive; wait for the client to consume the response and close.
                        var trailing = await pipe.ReadAsync(new byte[1], deadline.Token);
                        if (trailing != 0) throw new IOException("Unexpected trailing command data.");
                    }
                    finally { if (shutdown) stop.Cancel(); }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { /* one stalled client cannot retain the command pipe */ }
                catch (IOException) { /* a disconnected UI does not own playout */ }
                catch (JsonException) { /* reject malformed local commands */ }
                catch (UnauthorizedAccessException) { /* reject unidentifiable/unauthorized clients without executing commands */ }
                finally
                {
                    if (connected)
                    {
                        try { pipe.Disconnect(); }
                        catch (IOException) when (stop.IsCancellationRequested) { }
                    }
                }
            }
        }
        finally { stopping(); library.Changed -= coordinator.RefreshLibrary; queues.Changed -= coordinator.RefreshLibrary; await stop.CancelAsync(); }
        return 0;

    }
}

internal sealed record HostSettings(string Ffmpeg, string DataRoot, string PipeName, List<StationSettings> Stations, string? OperatorSid = null, bool Commissioning = false);
internal sealed record EngineCommand(string Id, string Action, string? StationId, string? Path = null,
    string[]? Paths = null, string? Query = null, int Offset = 0, string? AssetId = null,string? SessionId=null,
    long Revision = 0, long ExpectedVersion = 0, string? Artist = null, string? Title = null, string? SourceCheckId = null, string? ReportPeriod = null, string? ReportSelection = null, string? ReportId = null, string? PreviewId = null, LegacyStationMapping[]? Mappings = null, int? MappingRevision = null, string[]? JournalFeatures = null, BrandingRules? BrandingRules = null,
    QueueLibraryReference? QueueSource = null,long QueueRevision = 0,string? QueueEntryId = null);
