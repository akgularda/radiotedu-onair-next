using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OnAirNext.Engine;

public sealed partial class EventJournal
{
    public Task<ReportExport> RequestReportAsync(ReportExport proposed) => TransactionAsync((connection, transaction) =>
    {
        ReportExport.Validate(proposed.Id, proposed.Period, proposed.Selection, proposed.StationId);
        var existing = ReadReport(connection, transaction, proposed.Id);
        if (existing is not null)
        {
            if (existing.Period != proposed.Period || existing.Selection != proposed.Selection || existing.StationId != proposed.StationId)
                throw new InvalidOperationException("This export ID belongs to a different selection. Retry its original request.");
            if (existing.State is "Completed" or "Queued" or "Running") return existing;
            proposed = existing with { State = "Queued", Error = null, FinishedAtUtc = null, CancelRequested = false };
        }
        using var active = connection.CreateCommand(); active.Transaction = transaction;
        active.CommandText = "SELECT COUNT(*) FROM report_exports WHERE state IN ('Queued','Running')";
        if ((long)active.ExecuteScalar()! != 0) throw new InvalidOperationException("Another export is in progress. Wait for it to finish or cancel it in Reports.");
        WriteReport(connection, transaction, proposed); return proposed;
    });

    public Task<ReportExport> BeginReportAsync(string id) => TransactionAsync((connection, transaction) =>
    {
        var current = ReadReport(connection, transaction, id) ?? throw new ArgumentException("Report export not found.");
        if (current.State != "Queued") throw new InvalidOperationException("This export is no longer queued.");
        var next = current with { State = "Running", Attempt = checked(current.Attempt + 1), Records = 0,
            IncompleteRecords = 0, Bytes = 0, Sha256 = null, FinishedAtUtc = null, Error = null };
        WriteReport(connection, transaction, next); return next;
    });

    public Task<bool> SaveReportProgressAsync(ReportExport report) => TransactionAsync((connection, transaction) =>
    {
        var current = ReadReport(connection, transaction, report.Id);
        if (current is null || current.State != "Running" || current.Attempt != report.Attempt) return false;
        if (report.State is not ("Running" or "Completed" or "Failed" or "Cancelled"))
            throw new ArgumentException("Invalid export transition.");
        if (current.CancelRequested && report.State == "Completed") throw new OperationCanceledException("Export cancellation was committed before completion.");
        if (report.Period != current.Period || report.Selection != current.Selection || report.StationId != current.StationId ||
            report.RequestedAtUtc != current.RequestedAtUtc || report.Records < current.Records || report.IncompleteRecords < current.IncompleteRecords)
            throw new InvalidOperationException("The export identity or measured progress cannot change.");
        if (report.State == "Completed" && (report.Bytes <= 0 || report.Sha256 is not { Length: 64 } ||
                report.Sha256.Any(c => !char.IsAsciiHexDigit(c)) || report.SnapshotSequence is null or < 0 || report.SnapshotAtUtc is null))
            throw new InvalidOperationException("A completed export requires a confirmed file and journal snapshot receipt.");
        WriteReport(connection, transaction, report with { CancelRequested = current.CancelRequested }); return true;
    });

    public Task<ReportExport> CancelReportAsync(string id) => TransactionAsync((connection, transaction) =>
    {
        var current = ReadReport(connection, transaction, id) ?? throw new ArgumentException("Report export not found.");
        if (current.State is not ("Queued" or "Running")) return current;
        var next = current with { CancelRequested = true };
        WriteReport(connection, transaction, next); return next;
    });

    public Task InterruptReportsAsync() => TransactionAsync((connection, transaction) =>
    {
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT payload FROM report_exports WHERE state IN ('Queued','Running')";
        var pending = new List<ReportExport>();
        using (var reader = query.ExecuteReader())
            while (reader.Read()) pending.Add(JsonSerializer.Deserialize<ReportExport>(reader.GetString(0))!);
        foreach (var entry in pending) WriteReport(connection, transaction, entry with
        { State = "Interrupted", Error = "The engine restarted before this export was confirmed. Retry to create a fresh snapshot.", FinishedAtUtc = DateTimeOffset.UtcNow });
        return true;
    });

    public ReportExport? Report(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Choose an export from Reports.");
        using var connection = OpenReader(DatabasePath); return ReadReport(connection, null, id);
    }
    public IReadOnlyList<ReportExport> Reports(int offset = 0)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        using var connection = OpenReader(DatabasePath); using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM report_exports ORDER BY requested_utc DESC,id LIMIT 32 OFFSET $offset";
        command.Parameters.AddWithValue("$offset", offset); using var reader = command.ExecuteReader();
        var result = new List<ReportExport>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<ReportExport>(reader.GetString(0))!);
        return result;
    }
    private static ReportExport? ReadReport(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT payload FROM report_exports WHERE id=$id"; command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<ReportExport>(payload) : null;
    }
    private static void WriteReport(SqliteConnection connection, SqliteTransaction transaction, ReportExport report)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO report_exports(id,state,requested_utc,payload) VALUES($id,$state,$at,$payload) ON CONFLICT(id) DO UPDATE SET state=$state,payload=$payload";
        command.Parameters.AddWithValue("$id", report.Id); command.Parameters.AddWithValue("$state", report.State);
        command.Parameters.AddWithValue("$at", report.RequestedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(report)); command.ExecuteNonQuery();
    }
}
