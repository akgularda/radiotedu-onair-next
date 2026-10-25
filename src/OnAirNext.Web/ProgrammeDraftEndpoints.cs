using OnAirNext.Engine;

namespace OnAirNext.Web;

internal static class ProgrammeDraftEndpoints
{
    public static void MapProgrammeDrafts(this WebApplication app)
    {
        app.MapGet("/api/programme-drafts", async (string engine, string? station, bool? archived, int? offset, CancellationToken token) =>
            Results.Json(await EngineRpc.SendAsync(engine, new { Id = Guid.NewGuid().ToString("N"), Action = "programme-drafts", StationId = station,
                IncludeArchived = archived ?? false, Offset = offset ?? 0 }, token)));
        app.MapGet("/api/programme-drafts/history", async (string engine, string id, long version, int? offset, CancellationToken token) =>
            Results.Json(await EngineRpc.SendAsync(engine, new { Id = Guid.NewGuid().ToString("N"), Action = "programme-draft-history", DraftId = id,
                ExpectedVersion = version, Offset = offset ?? 0 }, token)));
        app.MapPost("/api/programme-drafts/save", async (SaveProgrammeRequest request, CancellationToken token) =>
            Results.Json(await EngineRpc.SendAsync(request.Engine, new { request.Id, Action = "programme-draft-save", request.ExpectedVersion,
                ProgrammeDraft = request.Draft }, token)));
        app.MapPost("/api/programme-drafts/archive", async (ChangeProgrammeRequest request, CancellationToken token) =>
            Results.Json(await EngineRpc.SendAsync(request.Engine, new { request.Id, Action = "programme-draft-archive", request.DraftId,
                request.ExpectedVersion }, token)));
        app.MapPost("/api/programme-drafts/restore", async (ChangeProgrammeRequest request, CancellationToken token) =>
            Results.Json(await EngineRpc.SendAsync(request.Engine, new { request.Id, Action = "programme-draft-restore", request.DraftId,
                request.ExpectedVersion, request.SourceVersion }, token)));
        app.MapPost("/api/programme-drafts/preview", async (PreviewProgrammeRequest request, CancellationToken token) =>
            Results.Json(await EngineRpc.SendAsync(request.Engine, new { Id = Guid.NewGuid().ToString("N"), Action = "programme-draft-preview",
                StationId = request.Station, PreviewFrom = request.LocalFrom, ProgrammeDraft = request.Draft, request.Offset }, token)));
    }
    private sealed record SaveProgrammeRequest(string Engine, string Id, long ExpectedVersion, ProgrammeDraft Draft);
    private sealed record ChangeProgrammeRequest(string Engine, string Id, string DraftId, long ExpectedVersion, long SourceVersion = 0);
    private sealed record PreviewProgrammeRequest(string Engine, string? Station, string LocalFrom, ProgrammeDraft? Draft = null, int Offset = 0);
}
