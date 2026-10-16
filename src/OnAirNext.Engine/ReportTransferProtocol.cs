namespace OnAirNext.Engine;

public sealed record ReportTransferRequest(string Id);
public sealed record ReportTransferHeader(bool Accepted, ReportExport? Receipt = null, string? Error = null);
