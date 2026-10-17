using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class StationQueueTests
{
    private static string Database()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RadioTEDU", "OnAirNext-Dev", "unit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return Path.Combine(root, "playback.db");
    }
    private static MediaItem Item(string id = "song") => new(id, "synthetic-" + id + ".wav", "Artist", "Title " + id, DurationSeconds: 30);
    private static Task Init(EventJournal journal, string station = "a")
        => journal.InitializeDesiredStateAsync(new(station, [], [], AutoStart: false));
    private static PlaybackEvent Event(string type, string queue, string station = "a", string occurrence = "attempt", MediaItem? item = null)
    {
        item ??= Item();
        return new(Guid.NewGuid().ToString("N"), occurrence, station, type, item.Id, item.Artist, item.Title,
            item.Kind, DateTimeOffset.UtcNow, type == "started" ? 0 : 960, QueueEntryId: queue);
    }
    private static PlayoutCheckpoint Checkpoint(string queue, string state = "Playing")
        => new("a", state, 7, Item(), "attempt", 960, 1920, true, DateTimeOffset.UtcNow, QueueEntryId: queue);

    [Fact]
    public async Task DistinctChannelsKeepTheirOwnOrderAndIdempotentAppendReceipts()
    {
        await using var journal = new EventJournal(Database()); await Init(journal); await Init(journal, "b");
        await journal.AppendStationQueueAsync("a", Item(), 0, "append-a");
        await journal.AppendStationQueueAsync("b", Item("other"), 0, "append-b");
        var retry = await journal.AppendStationQueueAsync("a", Item(), 0, "append-a");
        Assert.Equal(1, retry.Revision); Assert.Equal("append-a", Assert.Single(retry.Entries).Id);
        Assert.Equal("append-b", Assert.Single((await journal.ReadStationQueueAsync("b")).Entries).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.AppendStationQueueAsync("b", Item(), 1, "append-a"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.AppendStationQueueAsync("a", Item("changed"), 0, "append-a"));
        Assert.Equal(retry, journal.PublishedQueue("a"));
    }

    [Fact]
    public async Task ConcurrentStaleEditsCannotOverwriteOrderOrLeaveACommandReceipt()
    {
        await using var journal = new EventJournal(Database()); await Init(journal);
        async Task<bool> Add(string id)
        { try { await journal.AppendStationQueueAsync("a", Item(id), 0, id); return true; } catch (InvalidOperationException) { return false; } }
        var results = await Task.WhenAll(Add("one"), Add("two")); Assert.Single(results, x => x);
        var view = await journal.ReadStationQueueAsync("a"); var loser = view.Entries[0].Id == "one" ? "two" : "one";
        view = await journal.AppendStationQueueAsync("a", Item(loser), view.Revision, loser);
        Assert.Equal(2, view.Entries.Count); Assert.Equal(2, view.Revision);
    }

    [Fact]
    public async Task OrderingRemovalAndMetadataSnapshotsSurviveReopen()
    {
        var database = Database(); StationQueueSnapshot final;
        await using (var journal = new EventJournal(database))
        {
            await Init(journal);
            await journal.AppendStationQueueAsync("a", Item("one"), 0, "one");
            await journal.AppendStationQueueAsync("a", Item("two"), 1, "two");
            var moved = await journal.EditStationQueueAsync("a", "two", 2, QueueEdit.MoveUp, "move");
            Assert.Equal(new[] { "two", "one" }, moved.Entries.Select(x => x.Id));
            final = await journal.EditStationQueueAsync("a", "one", 3, QueueEdit.Remove, "remove");
            Assert.Equal("two", Assert.Single(final.Entries).Id);
            Assert.Equal(final.Revision, (await journal.EditStationQueueAsync("a", "one", 3, QueueEdit.Remove, "remove")).Revision);
        }
        await using var reopened = new EventJournal(database);
        Assert.Null(reopened.PublishedQueue("a"));
        var restored = await reopened.ReadStationQueueAsync("a");
        Assert.Equal(final.Revision, restored.Revision); Assert.Equal(final.Entries, restored.Entries);
    }

    [Fact]
    public async Task StartedAndTerminalEventsShareTheQueueAndCheckpointCommit()
    {
        await using var journal = new EventJournal(Database()); await Init(journal);
        await journal.AppendStationQueueAsync("a", Item(), 0, "q");
        await journal.AppendAsync(Event("started", "q"), Checkpoint("q"));
        Assert.Equal("Playing", Assert.Single(journal.PublishedQueue("a")!.Entries).State);
        var terminal = Event("ended", "q");
        var between = Checkpoint("q") with { State = "BetweenItems", Item = null, QueueEntryId = null, ItemSamples = 0, Announced = false };
        await journal.AppendAsync(terminal, between);
        Assert.Empty(journal.PublishedQueue("a")!.Entries); // visible before the returned event task acknowledges
        Assert.Equal(between, await journal.CheckpointAsync("a"));
        var version = journal.PublishedQueue("a")!.Revision;
        await journal.AppendAsync(terminal, between);
        Assert.Equal(version, journal.PublishedQueue("a")!.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.AppendAsync(Event("started", "q", occurrence: "repeat")));
        Assert.Equal(2, EventJournal.ReadEvents(journal.DatabasePath).Count);
        Assert.True(EventJournal.Verify(journal.DatabasePath));
    }

    [Theory]
    [InlineData("interrupted", "Pending", false)]
    [InlineData("skipped", "Completed", true)]
    [InlineData("decode_failed", "Failed", true)]
    [InlineData("failed", "Failed", true)]
    public async Task ActualAttemptStatusKeepsUnfinishedItemsAndConsumesTerminalItems(string type, string state, bool consumed)
    {
        await using var journal = new EventJournal(Database()); await Init(journal);
        await journal.AppendStationQueueAsync("a", Item(), 0, "q"); await journal.AppendAsync(Event("started", "q"));
        await journal.AppendAsync(Event(type, "q"));
        var view = journal.PublishedQueue("a")!;
        if (consumed) Assert.Empty(view.Entries); else Assert.Equal(state, Assert.Single(view.Entries).State);
        if (type == "failed") await journal.AppendAsync(Event("decode_failed", "q"));
        Assert.True(EventJournal.Verify(journal.DatabasePath));
    }

    [Fact]
    public async Task CrashRecoveryKeepsTheUnfinishedQueueItemAndLinksAnActualNewAttempt()
    {
        var database = Database();
        await using (var journal = new EventJournal(database))
        {
            await Init(journal); await journal.AppendStationQueueAsync("a", Item(), 0, "q");
            await journal.AppendAsync(Event("started", "q"), Checkpoint("q"));
        }
        await using var recovered = new EventJournal(database);
        var saved = await recovered.PrepareRecoveryAsync("a");
        Assert.Equal("RestartPending", saved!.State); Assert.Equal("q", saved.QueueEntryId);
        Assert.Equal("Pending", Assert.Single(recovered.PublishedQueue("a")!.Entries).State);
        await recovered.PrepareRecoveryAsync("a");
        Assert.Single(EventJournal.ReadEvents(database), e => e.Type == "interrupted");
        // The engine creates a fresh occurrence at zero; the old checkpoint is evidence only.
        var restarted = Event("started", "q", occurrence: "new-attempt") with { PreviousOccurrenceId = "attempt", SamplePosition = 1920 };
        await recovered.AppendAsync(restarted, saved with { State = "Playing", OccurrenceId = "new-attempt", ItemSamples = 0 });
        await recovered.AppendAsync(Event("ended", "q", occurrence: "new-attempt"));
        Assert.Empty(recovered.PublishedQueue("a")!.Entries);
        Assert.Equal(4, EventJournal.ReadEvents(database).Count);
    }

    [Fact]
    public async Task RecoveredSkipConsumesOnlyItsTargetAndDoesNotAdvanceMusicRotation()
    {
        await using var journal = new EventJournal(Database()); await Init(journal);
        await journal.AppendStationQueueAsync("a", Item(), 0, "q");
        await journal.AppendStationQueueAsync("a", Item("later"), 1, "later");
        await journal.AppendAsync(Event("started", "q"), Checkpoint("q"));
        await journal.RequestSkipAsync("a", "skip", "attempt");
        var saved = await journal.PrepareRecoveryAsync("a");
        Assert.Equal("BetweenItems", saved!.State); Assert.Null(saved.QueueEntryId); Assert.Equal(7, saved.RotationIndex);
        Assert.Equal("later", Assert.Single(journal.PublishedQueue("a")!.Entries).Id);
        await journal.PrepareRecoveryAsync("a");
        Assert.Single(EventJournal.ReadEvents(journal.DatabasePath), e => e.Type == "skipped");
    }

    [Fact]
    public async Task DelayedTerminalFromTheInterruptedAttemptCannotConsumeTheRestart()
    {
        await using var journal = new EventJournal(Database()); await Init(journal);
        await journal.AppendStationQueueAsync("a", Item(), 0, "q");
        await journal.AppendAsync(Event("started", "q"), Checkpoint("q"));
        await journal.PrepareRecoveryAsync("a");
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.AppendAsync(Event("ended", "q")));
        await journal.AppendAsync(Event("started", "q", occurrence: "restart"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.AppendAsync(Event("ended", "q")));
        Assert.Equal("Playing", Assert.Single(journal.PublishedQueue("a")!.Entries).State);
        await journal.AppendAsync(Event("ended", "q", occurrence: "restart"));
        Assert.Empty(journal.PublishedQueue("a")!.Entries);
        Assert.Equal(4, EventJournal.ReadEvents(journal.DatabasePath).Count);
    }

    [Fact]
    public async Task CommittedEofStaysConsumedAfterReopenAndCannotBecomeAnotherInterruption()
    {
        var database = Database();
        await using (var journal = new EventJournal(database))
        {
            await Init(journal); await journal.AppendStationQueueAsync("a", Item(), 0, "q");
            await journal.AppendAsync(Event("started", "q"), Checkpoint("q"));
            await journal.AppendAsync(Event("ended", "q"), Checkpoint("q") with
                { State = "BetweenItems", Item = null, QueueEntryId = null, Announced = false, ItemSamples = 0 });
        }
        await using var reopened = new EventJournal(database);
        Assert.Equal("BetweenItems", (await reopened.PrepareRecoveryAsync("a"))!.State);
        Assert.Empty(reopened.PublishedQueue("a")!.Entries);
        Assert.Equal(new[] { "started", "ended" }, EventJournal.ReadEvents(database).Select(e => e.Type));
    }

    [Fact]
    public async Task WrongChannelOrMetadataCannotCommitAnEventOrCheckpoint()
    {
        await using var journal = new EventJournal(Database()); await Init(journal); await Init(journal, "b");
        await journal.AppendStationQueueAsync("a", Item(), 0, "q");
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.AppendAsync(Event("started", "q", "b"), Checkpoint("q")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.AppendAsync(Event("started", "q") with { Title = "Changed" }, Checkpoint("q")));
        Assert.Empty(EventJournal.ReadEvents(journal.DatabasePath)); Assert.Null(await journal.CheckpointAsync("a"));
        Assert.Equal("Pending", Assert.Single((await journal.ReadStationQueueAsync("a")).Entries).State);
    }

    [Fact]
    public async Task RunningOrHeldRecoveryItemsCannotBeReorderedOrRemoved()
    {
        await using var journal = new EventJournal(Database()); await Init(journal);
        await journal.AppendStationQueueAsync("a", Item(), 0, "q"); await journal.AppendStationQueueAsync("a", Item("next"), 1, "next");
        await journal.RequestRunningIntentAsync("a", true, "start");
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.EditStationQueueAsync("a", "q", 2, QueueEdit.Remove, "edit"));
        await journal.RequestRunningIntentAsync("a", false, "stop"); await journal.SaveCheckpointAsync(Checkpoint("q", "Stopped"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.EditStationQueueAsync("a", "q", 2, QueueEdit.Remove, "edit"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.EditStationQueueAsync("a", "next", 2, QueueEdit.MoveUp, "move"));
        Assert.Equal(2, (await journal.ReadStationQueueAsync("a")).Entries.Count);
    }

    [Fact]
    public async Task OlderReadersAndMaintenanceRejectPendingQueuesWithoutChangingThem()
    {
        var database = Database(); var stations = new[] { "1", "2", "4", "5", "8", "9", "10", "11" };
        await using (var journal = new EventJournal(database))
        {
            foreach (var station in stations) await Init(journal, station);
            await journal.AppendStationQueueAsync("1", Item(), 0, "q");
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.RequireRecoveryReaderAsync([RecoveryCompatibility.HeldRotationFeature]));
            await journal.RequireRecoveryReaderAsync([RecoveryCompatibility.HeldRotationFeature, RecoveryCompatibility.StationQueueFeature]);
        }
        var bytes = await File.ReadAllBytesAsync(database);
        Assert.Throws<InvalidOperationException>(() => JournalMaintenanceInspection.Read(database, stations));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(database));
    }

    [Fact]
    public async Task CapacityIsBoundedPerChannelAndRejectedAppendCanBeRetriedAfterConsumption()
    {
        await using var journal = new EventJournal(Database()); await Init(journal); await Init(journal, "b");
        for (var i = 0; i < EventJournal.MaximumQueuedItems; i++) await journal.AppendStationQueueAsync("a", Item(), i, "q" + i);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.AppendStationQueueAsync("a", Item(), 128, "overflow"));
        await journal.AppendStationQueueAsync("b", Item(), 0, "other-channel");
        await journal.AppendAsync(Event("started", "q0")); await journal.AppendAsync(Event("ended", "q0"));
        var view = await journal.ReadStationQueueAsync("a");
        view = await journal.AppendStationQueueAsync("a", Item(), view.Revision, "overflow");
        Assert.Equal(EventJournal.MaximumQueuedItems, view.Entries.Count); Assert.Equal("overflow", view.Entries[^1].Id);
        await Assert.ThrowsAsync<ArgumentException>(() => journal.AppendStationQueueAsync("unknown", Item(), 0, "unknown"));
        await Assert.ThrowsAsync<ArgumentException>(() => journal.AppendStationQueueAsync("a", Item() with { DurationSeconds = double.NaN }, view.Revision, "invalid"));
    }
}
