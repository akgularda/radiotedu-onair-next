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
        if (!config.PipeName.StartsWith("onair-next-dev-", StringComparison.Ordinal) || config.PipeName.Length > 100 ||
            config.PipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("Use an isolated development pipe name.");
        if (serviceMode)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows service mode requires Windows.");
            if (config.PipeName != ServiceEndpoint.Pipe) throw new ArgumentException("Use the reserved development service pipe.");
            ServiceFileProtection.ValidateSettings(config.DataRoot, config.Ffmpeg);
        }
        else if (config.PipeName == ServiceEndpoint.Pipe) throw new ArgumentException("The reserved pipe belongs to the Windows service only.");
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
        await using var journal = new EventJournal(Path.Combine(config.DataRoot, "playback.db"));
        if (serviceMode) WindowsServiceDiagnostics.Stage("library and coordinator construction");
        await using var library = new StationLibrary(journal, config.Ffmpeg, config.DataRoot, config.Stations.Where(x => x.Repeat).Select(x => x.Id));
        await using var coordinator = new StationCoordinator(config.Ffmpeg, config.Stations, journal,
            settings => new StationEngine(config.Ffmpeg, settings, journal, planner: library.Planner(settings)));
        library.Changed += coordinator.RefreshLibrary;
        try
        {
            if (serviceMode) WindowsServiceDiagnostics.Stage("library initialization");
            await library.InitializeAsync();
            if (serviceMode) WindowsServiceDiagnostics.Stage("coordinator initialization");
            await coordinator.InitializeAsync();
            if (serviceMode) WindowsServiceDiagnostics.Stage("controller ready");
            ready();
            Console.WriteLine(JsonSerializer.Serialize(new { state = "controller-ready", config.PipeName, stations = config.Stations.Count, loopbackOnly = true, mode = serviceMode ? "windows-service" : "developer" }));
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
                                result = JsonSerializer.Serialize(new { stations = coordinator.Snapshot(),
                                    audit = new { journal.PendingEvents, journal.RejectedEvents, journal.Error },
                                    children = ChildProcess.ActiveProcesses, controller = new { mode = serviceMode ? "windows-service" : "developer", service = serviceMode ? ServiceEndpoint.Name : null } });
                                break;
                            case "start-station" or "stop-station" when request.StationId is { } targetStation:
                                await coordinator.RequestRunningAsync(targetStation, request.Action == "start-station", request.Id);
                                result = "{\"accepted\":true}"; break;
                            case "skip" when request.StationId is { } skipStation:
                                await coordinator.SkipAsync(skipStation, request.Id);
                                result = "{\"accepted\":true}"; break;
                            case "library-status" when request.StationId is { } libraryStation:
                                var libraryPage = library.Page(libraryStation, request.Query ?? "", request.Offset);
                                result = JsonSerializer.Serialize(new { page = libraryPage, worker = library.Status(libraryStation), canImportFiles = library.CanImportFiles(libraryPage.Root) });
                                break;
                            case "library-managed" when request.StationId is { } managedStation:
                                await library.UseManagedRootAsync(managedStation, request.Id);
                                result = "{\"accepted\":true}"; break;
                            case "library-root" when request.StationId is { } rootStation && request.Path is { } rootPath:
                                await library.SetRootAsync(rootStation, rootPath, request.Id);
                                result = "{\"accepted\":true}"; break;
                            case "library-import" when request.StationId is { } importStation && request.Paths is { } importPaths:
                                await library.ImportAsync(importStation, importPaths, request.Id);
                                result = "{\"accepted\":true}"; break;
                            case "library-rescan" when request.StationId is { } rescanStation:
                                await library.RescanAsync(rescanStation, request.Id);
                                result = "{\"accepted\":true}"; break;
                            case "library-metadata" or "library-reset-metadata" or "library-retire" or "library-restore"
                                when request.StationId is { } editStation && request.AssetId is { } assetId:
                                var kind = request.Action switch
                                {
                                    "library-metadata" => LibraryEditKind.Metadata, "library-reset-metadata" => LibraryEditKind.ResetMetadata,
                                    "library-retire" => LibraryEditKind.Retire, _ => LibraryEditKind.Restore
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
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or Microsoft.Data.Sqlite.SqliteException)
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
        finally { stopping(); library.Changed -= coordinator.RefreshLibrary; await stop.CancelAsync(); }
        return 0;

    }
}

internal sealed record HostSettings(string Ffmpeg, string DataRoot, string PipeName, List<StationSettings> Stations, string? OperatorSid = null);
internal sealed record EngineCommand(string Id, string Action, string? StationId, string? Path = null,
    string[]? Paths = null, string? Query = null, int Offset = 0, string? AssetId = null,
    long Revision = 0, long ExpectedVersion = 0, string? Artist = null, string? Title = null);
