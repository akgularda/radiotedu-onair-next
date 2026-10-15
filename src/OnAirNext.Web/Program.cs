using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using OnAirNext.Web;

var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadioTEDU", "OnAirNext-Dev", "browser-console");
Directory.CreateDirectory(Path.Combine(dataRoot, "http-temp"));
Environment.SetEnvironmentVariable("ASPNETCORE_TEMP", Path.Combine(dataRoot, "http-temp"));
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.WebHost.UseUrls("http://127.0.0.1:18121");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 256L * 1024 * 1024);
builder.Services.Configure<FormOptions>(options => { options.MultipartBodyLengthLimit = 256L * 1024 * 1024; options.MemoryBufferThreshold = 65536; });
var app = builder.Build();
var csrf = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var cookie = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var uploads = new SemaphoreSlim(1, 1);
var allowedActions = new HashSet<string>(StringComparer.Ordinal) { "start-station", "stop-station", "skip", "library-root", "library-managed", "library-rescan" };
var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".aif", ".aiff", ".wma", ".mp4" };
app.Use(async (context, next) =>
{
    var request = context.Request;
    if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address) ||
        request.Host.Host != "127.0.0.1") { context.Response.StatusCode = 403; return; }
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Cache-Control"] = "no-store";
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
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or BadHttpRequestException)
    {
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = ex is ArgumentException or JsonException or InvalidOperationException ? 400 : 503;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});
app.UseDefaultFiles(); app.UseStaticFiles();
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
app.MapPost("/api/import", async (HttpRequest request, CancellationToken token) =>
{
    if (!await uploads.WaitAsync(0, token)) return Results.Json(new { error = "Another upload is in progress. Wait for it to finish." }, statusCode: 429);
    try
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(5));
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
        var staging = Path.Combine(dataRoot, "uploads", id.ToString("N")); Directory.CreateDirectory(staging);
        var paths = new List<string>();
        for (var i = 0; i < form.Files.Count; i++)
        {
            var file = form.Files[i]; var name = Path.GetFileName(file.FileName);
            if (name.Length > 160 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("An audio filename is too long or invalid.");
            var destination = Path.Combine(staging, i.ToString("D2") + "-" + name);
            var temporary = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".partial");
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                { await file.CopyToAsync(output, deadline.Token); await output.FlushAsync(deadline.Token); output.Flush(flushToDisk: true); }
                if (File.Exists(destination))
                {
                    await using var old = File.OpenRead(destination); await using var added = File.OpenRead(temporary);
                    var oldHash = await SHA256.HashDataAsync(old, deadline.Token);
                    var addedHash = await SHA256.HashDataAsync(added, deadline.Token);
                    if (!CryptographicOperations.FixedTimeEquals(oldHash, addedHash))
                        throw new ArgumentException("This upload ID already belongs to different audio. Start a new import.");
                }
                else File.Move(temporary, destination);
                paths.Add(destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return Results.Json(await EngineRpc.SendAsync(engine, new { Id = command, Action = "library-import", StationId = station, Paths = paths }, deadline.Token));
    }
    finally { uploads.Release(); }
});
await app.RunAsync();

internal sealed record ConsoleCommand(string Engine, JsonElement Request);
internal sealed record FolderChoice(string Name, string Path);
