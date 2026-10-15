using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class LibraryCatalogTests
{
    private static string Root() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RadioTEDU", "OnAirNext-Dev", "unit-tests", Guid.NewGuid().ToString("N"));
    private static async Task Initialize(EventJournal journal)
    {
        await journal.InitializeLibraryAsync();
        await journal.InitializeDesiredStateAsync(new("a", [], [], AutoStart: false));
        await journal.InitializeDesiredStateAsync(new("b", [], [], AutoStart: false));
    }
    private static LibraryAsset Asset(int index) => new("asset-" + index, "song-" + index + ".wav", "Fixture", "Song " + index.ToString("D4"), 10, 100, 123, "Ready");

    [Fact]
    public async Task ReplayedFolderReceiptCannotReplaceALaterFolder()
    {
        var root = Root(); await using var journal = new EventJournal(Path.Combine(root, "catalog.db"));
        await Initialize(journal);
        Assert.True(await journal.SetLibraryRootAsync("a", Path.Combine(root, "first"), "root-1"));
        Assert.True(await journal.SetLibraryRootAsync("a", Path.Combine(root, "second"), "root-2"));
        Assert.False(await journal.SetLibraryRootAsync("a", Path.Combine(root, "first"), "root-1"));
        Assert.EndsWith("second", journal.LibraryPage("a").Root!.Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SetLibraryRootAsync("b", Path.Combine(root, "first"), "root-1"));
        Assert.Null(journal.LibraryPage("b").Root);
    }

    [Fact]
    public async Task SearchAndPaginationRetainAllAssetsAndTreatWildcardsLiterally()
    {
        var root = Root(); await using var journal = new EventJournal(Path.Combine(root, "catalog.db"));
        await Initialize(journal); await journal.SetLibraryRootAsync("a", Path.Combine(root, "music"), "root");
        var configured = journal.LibraryRoots().Single();
        foreach (var batch in Enumerable.Range(0, 137).Select(Asset).Chunk(100))
            Assert.True(await journal.ApplyLibraryChangesAsync(configured, batch, []));
        var first = journal.LibraryPage("a"); var second = journal.LibraryPage("a", offset: 50); var third = journal.LibraryPage("a", offset: 100);
        Assert.Equal(137, first.Total); Assert.Equal(137, first.Ready);
        Assert.Equal(50, first.Items.Count); Assert.Equal(50, second.Items.Count); Assert.Equal(37, third.Items.Count);
        Assert.Equal(137, first.Items.Concat(second.Items).Concat(third.Items).Select(x => x.Id).Distinct().Count());
        Assert.Empty(journal.LibraryPage("a", "%").Items);
        Assert.Equal("asset-136", Assert.Single(journal.LibraryPage("a", "Song 0136").Items).Id);
    }

    [Fact]
    public async Task AStaleScanCannotPublishIntoAReplacementRoot()
    {
        var root = Root(); await using var journal = new EventJournal(Path.Combine(root, "catalog.db"));
        await Initialize(journal); await journal.SetLibraryRootAsync("a", Path.Combine(root, "old"), "root-1");
        var old = journal.LibraryRoots().Single();
        await journal.ApplyLibraryChangesAsync(old, [Asset(1)], []);
        await journal.SetLibraryRootAsync("a", Path.Combine(root, "new"), "root-2");
        Assert.False(await journal.ApplyLibraryChangesAsync(old, [Asset(2)], ["asset-1"]));
        Assert.Empty(journal.LibraryPage("a").Items);
        Assert.Equal("Ready", Assert.Single(journal.LibraryAssets(old)).State);
    }

    [Fact]
    public async Task PendingImportAndRootSurviveRestartWithoutDuplicatingRetriedRequests()
    {
        var root = Root(); var db = Path.Combine(root, "catalog.db"); var files = new[] { Path.Combine(root, "source.wav") };
        await using (var journal = new EventJournal(db))
        {
            await Initialize(journal);
            Assert.True(await journal.QueueLibraryImportAsync("a", files, Path.Combine(root, "managed"), "import"));
        }
        await using (var journal = new EventJournal(db))
        {
            await Initialize(journal);
            Assert.False(await journal.QueueLibraryImportAsync("a", files, Path.Combine(root, "managed"), "import"));
            Assert.Equal(files, journal.PendingLibraryImport()!.Paths);
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.QueueLibraryImportAsync("a", [Path.Combine(root, "different.wav")], Path.Combine(root, "managed"), "import"));
            await journal.FinishLibraryImportAsync("import", null);
            Assert.Null(journal.PendingLibraryImport());
            Assert.Equal("Completed", journal.LibraryPage("a").Import!.State);
        }
    }

    [Fact]
    public async Task InvalidAssetRollsBackAnEntireBatchAndMissingFilesStayInHistory()
    {
        var root = Root(); await using var journal = new EventJournal(Path.Combine(root, "catalog.db"));
        await Initialize(journal); await journal.SetLibraryRootAsync("a", Path.Combine(root, "music"), "root");
        var configured = journal.LibraryRoots().Single();
        await Assert.ThrowsAsync<ArgumentException>(() => journal.ApplyLibraryChangesAsync(configured,
            [Asset(1), Asset(2) with { RelativePath = Path.Combine("..", "escape.wav") }], []));
        Assert.Empty(journal.LibraryPage("a").Items);
        await journal.ApplyLibraryChangesAsync(configured, [Asset(1)], []);
        await journal.ApplyLibraryChangesAsync(configured, [], ["asset-1"]);
        var page = journal.LibraryPage("a");
        Assert.Equal(1, page.Total); Assert.Equal(0, page.Ready); Assert.Equal(1, page.Missing);
        Assert.Equal("Missing", Assert.Single(page.Items).State);
    }

    [Fact]
    public async Task InvalidImportsDoNotCreateAReceiptOrRoot()
    {
        var root = Root(); await using var journal = new EventJournal(Path.Combine(root, "catalog.db"));
        await Initialize(journal);
        await Assert.ThrowsAsync<ArgumentException>(() => journal.QueueLibraryImportAsync("a", [], root, "import"));
        await Assert.ThrowsAsync<ArgumentException>(() => journal.SetLibraryRootAsync("unknown", root, "root"));
        Assert.Empty(journal.LibraryRoots());
        Assert.True(await journal.QueueLibraryImportAsync("a", [Path.Combine(root, "song.wav")], root, "import"));
    }

    [Fact]
    public async Task AFullImportQueueRejectsNewWorkWithoutConsumingItsCommandId()
    {
        var root = Root(); await using var journal = new EventJournal(Path.Combine(root, "catalog.db"));
        await Initialize(journal); var files = new[] { Path.Combine(root, "song.wav") };
        for (var i = 0; i < 16; i++) await journal.QueueLibraryImportAsync("a", files, root, "job-" + i);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.QueueLibraryImportAsync("a", files, root, "overflow"));
        Assert.False(await journal.QueueLibraryImportAsync("a", files, root, "job-0"));
        await journal.FinishLibraryImportAsync("job-0", null);
        Assert.True(await journal.QueueLibraryImportAsync("a", files, root, "overflow"));
    }
}
