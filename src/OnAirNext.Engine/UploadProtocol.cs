namespace OnAirNext.UploadProtocol;

public sealed record UploadFileSpec(string Name, long Bytes, string Sha256);
public sealed record UploadRequest(string Action, string BatchId, string StationId, UploadFileSpec[]? Files = null, int Index = 0);
public sealed record UploadReply(bool Accepted, bool Send = false, string? Error = null, string? State = null);
