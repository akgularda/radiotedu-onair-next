using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class LibraryEditingTests
{
    private static string Root() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RadioTEDU", "OnAirNext-Dev", "unit-tests", Guid.NewGuid().ToString("N"));
    private static LibraryAsset Asset => new("song", "song.wav", "File artist", "File title", 10, 100, 123, "Ready");
    private static async Task<LibraryRoot> Initialize(EventJournal journal, string path)
    {
        await journal.InitializeLibraryAsync();
        await journal.InitializeDesiredStateAsync(new("a", [], [], AutoStart: false));
        await journal.InitializeDesiredStateAsync(new("b", [], [], AutoStart: false));
        await journal.SetLibraryRootAsync("a", path, "root");
        var root = journal.LibraryRoots().Single(); await journal.ApplyLibraryChangesAsync(root, [Asset], []); return root;
    }

    [Fact]
    public async Task ManualTagsSurviveRescanAndRestartAndResetUsesLatestFileTags()
    {
        var directory = Root(); var db = Path.Combine(directory, "catalog.db"); long revision;
        await using (var journal = new EventJournal(db))
        {
            var root = await Initialize(journal, Path.Combine(directory, "music")); revision = root.Revision;
            await journal.ChangeLibraryAssetAsync("a", revision, "song", 0, LibraryEditKind.Metadata, "edit", "Operator artist", "Operator title");
            await journal.ApplyLibraryChangesAsync(root, [Asset with { Artist = "New file artist", Title = "New file title", Seconds = 12, ModifiedTicks = 456 }], []);
            var item = journal.LibraryItem("a", revision, "song")!;
            Assert.Equal("Operator title", item.Title); Assert.Equal(12, item.Seconds); Assert.True(item.HasMetadataOverride);
            Assert.Single(journal.LibraryPage("a", "Operator title").Items); Assert.Empty(journal.LibraryPage("a", "New file title").Items);
            Assert.Equal("New file title", Assert.Single(journal.LibraryAssets(root, raw: true)).Title);
        }
        await using (var journal = new EventJournal(db))
        {
            await journal.InitializeLibraryAsync();
            Assert.Equal("Operator artist", journal.LibraryItem("a", revision, "song")!.Artist);
            await journal.ChangeLibraryAssetAsync("a", revision, "song", 1, LibraryEditKind.ResetMetadata, "reset");
            var reset = journal.LibraryItem("a", revision, "song")!;
            Assert.Equal("New file title", reset.Title); Assert.Equal("New file artist", reset.Artist);
            Assert.False(reset.HasMetadataOverride); Assert.Equal(2L, reset.EditVersion);
        }
    }

    [Fact]
    public async Task RemovedItemsKeepScanStateAndRestoreRequiresFreshValidation()
    {
        var directory = Root(); await using var journal = new EventJournal(Path.Combine(directory, "catalog.db"));
        var root = await Initialize(journal, Path.Combine(directory, "music"));
        await journal.ChangeLibraryAssetAsync("a", root.Revision, "song", 0, LibraryEditKind.Retire, "remove");
        await journal.ApplyLibraryChangesAsync(root, [], ["song"]);
        var retired = journal.LibraryItem("a", root.Revision, "song")!;
        Assert.Equal("Retired", retired.State); Assert.Equal("Missing", retired.ValidationState);
        Assert.Equal(1, journal.LibraryPage("a").Retired); Assert.Equal(0, journal.LibraryPage("a").Ready);
        await journal.ChangeLibraryAssetAsync("a", root.Revision, "song", 1, LibraryEditKind.Restore, "restore");
        var restored = journal.LibraryItem("a", root.Revision, "song")!;
        Assert.Equal("Invalid", restored.State); Assert.Equal(-1, restored.ModifiedTicks); Assert.Equal(0, journal.LibraryPage("a").Ready);
        await journal.ApplyLibraryChangesAsync(root, [Asset], []);
        Assert.Equal("Ready", journal.LibraryItem("a", root.Revision, "song")!.State);
        Assert.False(await journal.ChangeLibraryAssetAsync("a", root.Revision, "song", 0, LibraryEditKind.Retire, "remove"));
        Assert.Equal(1, journal.LibraryPage("a").Ready); Assert.Equal(0, journal.LibraryPage("a").Retired);
    }

    [Fact]
    public async Task RetryCannotUndoANewerEditAndStaleRejectionDoesNotConsumeItsId()
    {
        var directory = Root(); await using var journal = new EventJournal(Path.Combine(directory, "catalog.db"));
        var root = await Initialize(journal, Path.Combine(directory, "music"));
        await journal.ChangeLibraryAssetAsync("a", root.Revision, "song", 0, LibraryEditKind.Metadata, "first", "A", "One");
        await journal.ChangeLibraryAssetAsync("a", root.Revision, "song", 1, LibraryEditKind.Metadata, "second", "B", "Two");
        Assert.False(await journal.ChangeLibraryAssetAsync("a", root.Revision, "song", 0, LibraryEditKind.Metadata, "first", "A", "One"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.ChangeLibraryAssetAsync("a", root.Revision, "song", 0, LibraryEditKind.Metadata, "first", "A", "Different"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.ChangeLibraryAssetAsync("a", root.Revision, "song", 0, LibraryEditKind.Metadata, "stale", "C", "Three"));
        Assert.Equal("Two", journal.LibraryItem("a", root.Revision, "song")!.Title);
        Assert.True(await journal.ChangeLibraryAssetAsync("a", root.Revision, "song", 2, LibraryEditKind.Metadata, "stale", "C", "Three"));
        Assert.Equal(3L, journal.LibraryItem("a", root.Revision, "song")!.EditVersion);
    }

    [Fact]
    public async Task FolderAndStationIdentityFenceEditsEvenWhenAssetIdsMatch()
    {
        var directory = Root(); await using var journal = new EventJournal(Path.Combine(directory, "catalog.db"));
        var old = await Initialize(journal, Path.Combine(directory, "old"));
        await journal.ChangeLibraryAssetAsync("a", old.Revision, "song", 0, LibraryEditKind.Metadata, "edit-a", "A", "A title");
        await journal.SetLibraryRootAsync("b", Path.Combine(directory, "b"), "root-b");
        var b = journal.LibraryRoots().Single(r => r.StationId == "b"); await journal.ApplyLibraryChangesAsync(b, [Asset], []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.ChangeLibraryAssetAsync("b", b.Revision, "song", 0, LibraryEditKind.Metadata, "edit-a", "A", "A title"));
        Assert.Equal("File title", journal.LibraryItem("b", b.Revision, "song")!.Title);
        await journal.SetLibraryRootAsync("a", Path.Combine(directory, "new"), "root-new");
        var newer = journal.LibraryRoots().Single(r => r.StationId == "a"); await journal.ApplyLibraryChangesAsync(newer, [Asset], []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.ChangeLibraryAssetAsync("a", old.Revision, "song", 1, LibraryEditKind.Retire, "old-dialog"));
        Assert.True(await journal.ChangeLibraryAssetAsync("a", newer.Revision, "song", 0, LibraryEditKind.Retire, "old-dialog"));
        Assert.Equal("A title", Assert.Single(journal.LibraryAssets(old)).Title);
        Assert.Equal("Ready", journal.LibraryItem("b", b.Revision, "song")!.State);
    }

    [Fact]
    public async Task InvalidMetadataAndVersionsLeaveNoReceiptOrPartialChange()
    {
        var directory = Root(); await using var journal = new EventJournal(Path.Combine(directory, "catalog.db"));
        var root = await Initialize(journal, Path.Combine(directory, "music"));
        foreach (var title in new[] { " ", "bad\ntitle", new string('x',257) })
            await Assert.ThrowsAsync<ArgumentException>(() => journal.ChangeLibraryAssetAsync("a", root.Revision, "song", 0, LibraryEditKind.Metadata, "edit", "Artist", title));
        await Assert.ThrowsAsync<ArgumentException>(() => journal.ChangeLibraryAssetAsync("a", root.Revision, "song", -1, LibraryEditKind.Retire, "edit"));
        Assert.True(await journal.ChangeLibraryAssetAsync("a", root.Revision, "song", 0, LibraryEditKind.Metadata, "edit", "", "Valid title"));
        Assert.Equal(1L, journal.LibraryItem("a", root.Revision, "song")!.EditVersion);
    }

    [Fact]
    public async Task CatalogEditsNeverRewriteActualAttemptMetadata()
    {
        var directory = Root(); var db = Path.Combine(directory, "catalog.db"); await using var journal = new EventJournal(db);
        var root = await Initialize(journal, Path.Combine(directory, "music"));
        var started = new PlaybackEvent("event", "occurrence", "a", "started", "song", "Heard artist", "Heard title", ContentKind.Music, DateTimeOffset.UtcNow, 0);
        await journal.AppendAsync(started);
        await journal.ChangeLibraryAssetAsync("a", root.Revision, "song", 0, LibraryEditKind.Metadata, "edit", "New artist", "New title");
        await journal.ChangeLibraryAssetAsync("a", root.Revision, "song", 1, LibraryEditKind.Retire, "remove");
        Assert.Equal(started, Assert.Single(EventJournal.ReadEvents(db)));
    }
}
