using System.Security.Cryptography;
using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class ReportExportTests
{
    private static string Root() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RadioTEDU", "OnAirNext-Dev", "unit-tests", Guid.NewGuid().ToString("N"));
    private static PlaybackEvent Entry(string id, string station, DateTimeOffset at) =>
        new("start-" + id, id, station, "started", "source", "Synthetic artist", id, ContentKind.Music, at, 0);
    private static async Task<ReportExport> Finished(EventJournal journal, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var item = journal.Report(id)!;
            if (item.State is not ("Queued" or "Running")) return item;
            await Task.Delay(10, timeout.Token);
        }
    }
    [Fact]
    public void CalendarYearUsesIstanbulMidnightAtBothEnds()
    {
        var (start, end) = PlayReports.PeriodBounds("year", "2026");
        Assert.Equal(DateTimeOffset.Parse("2025-12-31T21:00:00Z"), start);
        Assert.Equal(DateTimeOffset.Parse("2026-12-31T21:00:00Z"), end);
        Assert.Throws<ArgumentException>(() => PlayReports.PeriodBounds("year", "26"));
        Assert.Throws<ArgumentException>(() => PlayReports.PeriodBounds("day", "2026-02-30"));
    }
    [Fact]
    public async Task ConfirmedAnnualDownloadRetainsSelectedStationAndExactFileDigest()
    {
        var root = Root(); await using var journal = new EventJournal(Path.Combine(root, "events.db"));
        var start = DateTimeOffset.Parse("2025-12-31T21:00:00Z");
        await journal.AppendAsync(Entry("before", "9", start.AddTicks(-1)));
        await journal.AppendAsync(Entry("included", "9", start));
        await journal.AppendAsync(Entry("other", "1", start.AddHours(1)));
        await journal.AppendAsync(Entry("after", "9", start.AddYears(1)));
        await using var exports = new ReportExports(journal, ["1", "9"]); await exports.InitializeAsync();
        var id = Guid.NewGuid().ToString("N"); await exports.StartAsync(id, "year", "2026", "9");
        var receipt = await Finished(journal, id); Assert.Equal("Completed", receipt.State);
        Assert.Equal(1, receipt.Records); Assert.Equal(1, receipt.IncompleteRecords); Assert.Equal(1, receipt.Attempt);
        Assert.Equal(4, receipt.SnapshotSequence); Assert.NotNull(receipt.SnapshotAtUtc);
        var download = exports.OpenDownload(id); await using var file = download.File;
        Assert.Equal(receipt.Bytes, file.Length);
        Assert.Equal(receipt.Sha256, Convert.ToHexStringLower(await SHA256.HashDataAsync(file)));
        file.Position = 0; using var reader = new StreamReader(file); var csv = await reader.ReadToEndAsync();
        Assert.Contains("included", csv); Assert.DoesNotContain("before", csv); Assert.DoesNotContain("other", csv); Assert.DoesNotContain("after", csv);
        Assert.Equal(receipt, await exports.StartAsync(id, "year", "2026", "9"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => exports.StartAsync(id, "day", "2026-01-01", "9"));
    }
    [Fact]
    public async Task RestartRetainsUnconfirmedArtifactAndRetriesWithANewAttempt()
    {
        var root = Root(); await using var journal = new EventJournal(Path.Combine(root, "events.db"));
        var id = Guid.NewGuid().ToString("N");
        await journal.RequestReportAsync(new(id, "day", "2026-10-16", null, DateTimeOffset.UtcNow));
        var running = await journal.BeginReportAsync(id);
        var reportsRoot = Path.Combine(root, "reports"); Directory.CreateDirectory(reportsRoot);
        var oldPath = Path.Combine(reportsRoot, running.FileName); await File.WriteAllTextAsync(oldPath, "unconfirmed artifact");
        await using var exports = new ReportExports(journal, ["9"]); await exports.InitializeAsync();
        Assert.Equal("Interrupted", journal.Report(id)!.State);
        Assert.Throws<InvalidOperationException>(() => exports.OpenDownload(id));
        await exports.StartAsync(id, "day", "2026-10-16"); var retried = await Finished(journal, id);
        Assert.Equal("Completed", retried.State); Assert.Equal(2, retried.Attempt); Assert.Equal(0, retried.Records);
        Assert.Equal("unconfirmed artifact", await File.ReadAllTextAsync(oldPath));
        Assert.NotEqual(running.FileName, retried.FileName);
    }
    [Fact]
    public async Task DurableCancellationCannotBeOverwrittenByLateCompletion()
    {
        await using var journal = new EventJournal(Path.Combine(Root(), "events.db"));
        var id = Guid.NewGuid().ToString("N");
        await journal.RequestReportAsync(new(id, "year", "2026", null, DateTimeOffset.UtcNow));
        var running = await journal.BeginReportAsync(id); await journal.CancelReportAsync(id);
        await Assert.ThrowsAsync<OperationCanceledException>(() => journal.SaveReportProgressAsync(running with { State = "Completed" }));
        Assert.True(journal.Report(id)!.CancelRequested); Assert.Equal("Running", journal.Report(id)!.State);
        await journal.SaveReportProgressAsync(running with { State = "Cancelled" });
        var retry = await journal.RequestReportAsync(new(id, "year", "2026", null, DateTimeOffset.UtcNow));
        Assert.False(retry.CancelRequested); Assert.Equal("Queued", retry.State);
        Assert.Equal(2, (await journal.BeginReportAsync(id)).Attempt);
    }
    [Fact]
    public async Task CancelledStreamingExportLeavesNoPublishedOrPartialCsv()
    {
        var root = Root(); await using var journal = new EventJournal(Path.Combine(root, "events.db"));
        var at = DateTimeOffset.Parse("2026-10-16T08:00:00Z");
        for (var i = 0; i < 260; i++) await journal.AppendAsync(Entry("cancel-" + i, "9", at.AddMilliseconds(i)));
        using var cancel = new CancellationTokenSource(); var path = Path.Combine(root, "cancelled.csv");
        Assert.Throws<OperationCanceledException>(() => PlayReports.ExportRange(journal.DatabasePath, at.AddDays(-1), at.AddDays(1), path,
            cancellation: cancel.Token, progress: (_, _) => cancel.Cancel()));
        Assert.False(File.Exists(path)); Assert.Empty(Directory.EnumerateFiles(root, "cancelled.csv.*.tmp"));
        Assert.True(EventJournal.Verify(journal.DatabasePath));
    }
}
