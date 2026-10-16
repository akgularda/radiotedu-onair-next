using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using OnAirNext.Web;
using OnAirNext.UploadProtocol;
using OnAirNext.Installation;

var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadioTEDU", "OnAirNext-Dev", "browser-console");
Directory.CreateDirectory(Path.Combine(dataRoot, "http-temp"));
Environment.SetEnvironmentVariable("ASPNETCORE_TEMP", Path.Combine(dataRoot, "http-temp"));
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.WebHost.UseUrls("http://127.0.0.1:18121");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 256L * 1024 * 1024);
builder.Services.Configure<FormOptions>(options => { options.MultipartBodyLengthLimit = 256L * 1024 * 1024; options.MemoryBufferThreshold = 65536; });
builder.Services.AddSingleton(_ => new SetupCoordinator(dataRoot));
using var access = new ConsoleAccess();
var app = builder.Build();
var reportDownloads = new ReportDownloadGrants();
var csrf = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var cookie = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var uploads = new SemaphoreSlim(1, 1);
var meterRequests = new SemaphoreSlim(1, 1);
var allowedActions = new HashSet<string>(StringComparer.Ordinal) { "start-station", "stop-station", "skip", "library-root", "library-source-check", "library-managed", "library-rescan", "library-metadata", "library-reset-metadata", "library-retire", "library-restore", "library-analyze-loudness" };
var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".aif", ".aiff", ".wma", ".mp4" };
app.Use(async (context, next) =>
{
    var request = context.Request;
    if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address) ||
        request.Host.Host != "127.0.0.1") { context.Response.StatusCode = 403; return; }
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (request.Path == "/api/session" && !access.Allows(request.Headers["X-Studio-Bootstrap"].ToString()))
    { context.Response.StatusCode=403; await context.Response.WriteAsJsonAsync(new { error="Open the installed OnAir Next shortcut to sign into this Windows operator's console." }); return; }
    if (request.Path.StartsWithSegments("/api") && request.Path != "/api/session" && request.Path != "/api/health")
    {
        var supplied=request.Headers["X-Studio-Token"].ToString();
        if(request.Cookies["studio-session"]!=cookie||supplied.Length!=csrf.Length||!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied),Encoding.UTF8.GetBytes(csrf)))
        {context.Response.StatusCode=403;await context.Response.WriteAsJsonAsync(new { error="Open the installed shortcut or reload the authorized local console." });return;}
    }
    if (request.Method != "GET" && request.Method != "HEAD")
    {
        var origin = request.Headers["Origin"].ToString();
        var supplied = request.Headers["X-Studio-Token"].ToString();
        if (origin.Length > 0 && origin != "http://" + request.Host || request.Cookies["studio-session"] != cookie ||
            supplied.Length != csrf.Length || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(csrf)))
        { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "Reload this local console before making changes." }); return; }
    }
    try { await next(); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or BadHttpRequestException or System.ComponentModel.Win32Exception)
    {
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = ex is ArgumentException or JsonException or InvalidOperationException ? 400 : 503;
        await context.Response.WriteAsJsonAsync(new { error = ex is System.ComponentModel.Win32Exception ? "Windows service connection is unavailable. Check the service and operator permissions." : ex.Message });
    }
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapPost("/api/console/close", (HttpResponse response) => { response.OnCompleted(() => { app.Lifetime.StopApplication(); return Task.CompletedTask; }); return Results.Json(new { accepted=true }); });
app.MapGet("/api/health", () => Results.Json(new { product="RadioTEDU-OnAirNext", version=access.Version, processId=Environment.ProcessId }));
app.MapPost("/api/setup/shortcuts", () => { ConsoleShortcuts.Create(); return Results.Json(new { accepted=true }); });
app.MapGet("/api/setup", (SetupCoordinator setup) => Results.Json(setup.Status()));
app.MapPost("/api/setup/prepare", (SetupRequest request, SetupCoordinator setup) => { setup.Prepare(request); return Results.Json(new { accepted=true }); });
app.MapPost("/api/setup/install", (SetupInstall request, SetupCoordinator setup) => { setup.Install(request.Id); return Results.Json(new { accepted=true }); });
app.MapGet("/api/session", (HttpResponse response) =>
{
    response.Cookies.Append("studio-session", cookie, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, IsEssential = true, Path = "/" });
    return Results.Json(new { token = csrf, preview = false });
});
app.MapGet("/api/engines", () => Results.Json(new { engines = EngineRpc.Discover(), preview = false }));
app.MapGet("/api/status", async (string engine, CancellationToken token) =>
    Results.Json(await EngineRpc.SendAsync(engine, new { Id = Guid.NewGuid().ToString("N"), Action = "status" }, token)));
app.MapGet("/api/library", async (string engine, string station, string? query, int? offset, CancellationToken token) =>
    Results.Json(await EngineRpc.SendAsync(engine, new { Id = Guid.NewGuid().ToString("N"), Action = "library-status", StationId = station, Query = query ?? "", Offset = offset ?? 0 }, token)));
app.MapGet("/api/migration-preview", async (string engine, string? id, CancellationToken token) =>
    Results.Json(await EngineRpc.SendAsync(engine, new { Id = Guid.NewGuid().ToString("N"), Action = "legacy-preview", PreviewId = id }, token)));
app.MapPost("/api/migration-preview", async (MigrationPreviewRequest request, CancellationToken token) =>
    Results.Json(await EngineRpc.SendAsync(request.Engine, new { request.Id, Action = "legacy-preview-create", Path = request.LegacyRoot }, token)));
app.MapPost("/api/migration-mapping", async (MigrationMappingRequest request, CancellationToken token) =>
    Results.Json(await EngineRpc.SendAsync(request.Engine, new { Id = Guid.NewGuid().ToString("N"), Action = "legacy-mapping-save", PreviewId = request.Id, request.Revision, request.Mappings }, token)));
app.MapGet("/api/reports", async (string engine, int? offset, CancellationToken token) =>
    Results.Json(await EngineRpc.SendAsync(engine, new { Id = Guid.NewGuid().ToString("N"), Action = "reports", Offset = offset ?? 0 }, token)));
app.MapPost("/api/reports/export", async (ReportExportRequest request, CancellationToken token) =>
    Results.Json(await EngineRpc.SendAsync(request.Engine, new { request.Id, Action = "report-export", request.StationId,
        ReportPeriod = request.Period, ReportSelection = request.Selection }, token)));
app.MapPost("/api/reports/cancel", async (ReportCancelRequest request, CancellationToken token) =>
    Results.Json(await EngineRpc.SendAsync(request.Engine, new { Id = Guid.NewGuid().ToString("N"), Action = "report-cancel", ReportId = request.Id }, token)));
app.MapPost("/api/reports/download", (ReportCancelRequest request) =>
{
    if (!EngineRpc.IsIsolated(request.Engine)) throw new ArgumentException("Choose an isolated engine from System.");
    var ticket = reportDownloads.Create(request.Engine, request.Id, DateTimeOffset.UtcNow);
    return Results.Json(new { url = "/downloads/report/" + ticket });
});
app.MapGet("/downloads/report/{ticket}", async (string ticket, HttpContext context) =>
{
    if (context.Request.Cookies["studio-session"] != cookie) { context.Response.StatusCode = 403; return; }
    var grant = reportDownloads.Claim(ticket, DateTimeOffset.UtcNow);
    if (grant is null) { context.Response.StatusCode = 404; return; }
    await using var download = await ReportDownload.OpenAsync(grant.Engine, grant.ReportId, context.RequestAborted);
    context.Response.ContentType = "text/csv; charset=utf-8";
    context.Response.ContentLength = download.Receipt.Bytes;
    context.Response.Headers["Content-Disposition"] = "attachment; filename=\"" + download.Receipt.FileName + "\"";
    context.Response.Headers["X-Content-SHA256"] = download.Receipt.Sha256;
    try { await download.CopyToAsync(context.Response.Body, context.RequestAborted); }
    catch { context.Abort(); }
});
app.MapGet("/api/loudness", async (string engine, string station, CancellationToken token) =>
{
    if (!await meterRequests.WaitAsync(0, token)) return Results.Json(new { error = "A meter request is already in progress." }, statusCode: 429);
    try
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMilliseconds(900));
        return Results.Json(await EngineRpc.SendAsync(engine, new { Id = Guid.NewGuid().ToString("N"), Action = "loudness", StationId = station }, deadline.Token));
    }
    finally { meterRequests.Release(); }
});
app.MapGet("/api/loudness-history", async (string engine, string station, int? offset, CancellationToken token) =>
    Results.Json(await EngineRpc.SendAsync(engine, new { Id = Guid.NewGuid().ToString("N"), Action = "loudness-history", StationId = station, Offset = offset ?? 0 }, token)));
app.MapGet("/api/source-check", async (string engine, string station, string id, CancellationToken token) =>
    Results.Json(await EngineRpc.SendAsync(engine, new { Id = Guid.NewGuid().ToString("N"), Action = "library-source-status", StationId = station, SourceCheckId = id }, token)));
app.MapPost("/api/command", async (ConsoleCommand command, CancellationToken token) =>
{
    if (command.Request.ValueKind != JsonValueKind.Object || !command.Request.TryGetProperty("Action", out var action) ||
        !allowedActions.Contains(action.GetString() ?? "") || !command.Request.TryGetProperty("Id", out var id) ||
        !Guid.TryParse(id.GetString(), out _)) throw new ArgumentException("This station command is not available.");
    return Results.Json(await EngineRpc.SendAsync(command.Engine, command.Request, token));
});
app.MapGet("/api/folders", (string? path) =>
{
    if (string.IsNullOrEmpty(path))
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var known = new[] { ("Downloads", Path.Combine(home, "Downloads")), ("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)) };
        var folders = known.Where(x => Directory.Exists(x.Item2)).Select(x => new FolderChoice(x.Item1, x.Item2))
            .Concat(DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new FolderChoice(d.Name + " drive", d.RootDirectory.FullName))).ToArray();
        return Results.Json(new { path = (string?)null, parent = (string?)null, folders });
    }
    var root = OnAirNext.Engine.LocalMediaPath.Normalize(path);
    var directory = new DirectoryInfo(root);
    if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Choose an accessible folder without folder links.");
    var children = directory.EnumerateDirectories().Where(d => (d.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden)) == 0)
        .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Take(2001).ToArray();
    if (children.Length > 2000) throw new ArgumentException("This folder has more than 2,000 subfolders. Choose a smaller music location.");
    return Results.Json(new { path = directory.FullName, parent = directory.Parent?.FullName, folders = children.Select(d => new FolderChoice(d.Name, d.FullName)) });
});
app.MapPost("/api/upload-cancel", async (UploadCancellation request, CancellationToken token) =>
{
    if (!await uploads.WaitAsync(0, token)) return Results.Json(new { error = "A transfer is in progress. Wait for it to finish before cancelling." }, statusCode: 429);
    try { await UploadRpc.SendAsync(request.Engine, new("cancel", request.Id, request.Station), null, token); return Results.Json(new { accepted=true }); }
    finally { uploads.Release(); }
});
app.MapPost("/api/import", async (HttpRequest request, CancellationToken token) =>
{
    if (!await uploads.WaitAsync(0, token)) return Results.Json(new { error = "Another upload is in progress. Wait for it to finish." }, statusCode: 429);
    try
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var form = await request.ReadFormAsync(deadline.Token);
        var engine = form["engine"].ToString(); var station = form["station"].ToString(); var command = form["commandId"].ToString();
        if (!EngineRpc.IsIsolated(engine) || !Guid.TryParse(command, out var id) || station.Length is < 1 or > 100 || form.Files.Count is < 1 or > 64)
            throw new ArgumentException("Choose an engine, a station and between 1 and 64 audio files.");
        long total = 0;
        foreach (var file in form.Files)
        {
            if (file.Length <= 0 || !extensions.Contains(Path.GetExtension(file.FileName))) throw new ArgumentException("Choose non-empty audio files.");
            total = checked(total + file.Length);
        }
        if (total > 240L * 1024 * 1024) throw new ArgumentException("Use batches smaller than 240 MiB.");
        if (new DriveInfo(Path.GetPathRoot(dataRoot)!).AvailableFreeSpace < total + 2L * 1024 * 1024 * 1024)
            throw new IOException("The upload drive needs at least 2 GiB of free space after this batch.");
        var batch=id.ToString("N");
        var manifest=new List<UploadFileSpec>();
        foreach(var file in form.Files)
        {
            await using var input=file.OpenReadStream();
            manifest.Add(new(file.FileName,file.Length,Convert.ToHexString(await SHA256.HashDataAsync(input,deadline.Token))));
        }
        var registered=await UploadRpc.SendAsync(engine,new("begin",batch,station,manifest.ToArray()),null,deadline.Token);
        if(registered.State=="Receiving")
            for(var i=0;i<form.Files.Count;i++)
            {
                await using var input=form.Files[i].OpenReadStream();
                await UploadRpc.SendAsync(engine,new("put",batch,station,Index:i),input,deadline.Token);
            }
        await UploadRpc.SendAsync(engine,new("commit",batch,station),null,deadline.Token);
        return Results.Json(new { accepted=true });
    }
    finally { uploads.Release(); }
});
await app.RunAsync();

internal sealed record ConsoleCommand(string Engine, JsonElement Request);
internal sealed record FolderChoice(string Name, string Path);

internal sealed record UploadCancellation(string Engine,string Station,string Id);

internal sealed record SetupInstall(string Id);

internal sealed record ReportExportRequest(string Engine, string Id, string Period, string Selection, string? StationId = null);
internal sealed record ReportCancelRequest(string Engine, string Id);

internal sealed record MigrationPreviewRequest(string Engine, string Id, string LegacyRoot);
internal sealed record MigrationMappingRequest(string Engine, string Id, int Revision, OnAirNext.Engine.LegacyStationMapping[] Mappings);
