using Microsoft.Data.Sqlite;
using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class RecoveryTests
{
    private static string Database()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RadioTEDU", "OnAirNext-Dev", "unit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return Path.Combine(root, "recovery.db");
    }
    private static readonly MediaItem Song = new("song", "fixture.wav", "Artist", "Title");
    private static PlayoutCheckpoint Checkpoint(string state = "Playing", string? queue = null) =>
        new("station", state, 4, state == "BetweenItems" ? null : Song, "attempt-a", state == "BetweenItems" ? 0 : 96000,
            480000, state != "BetweenItems", DateTimeOffset.UtcNow, queue, "old-session");
    private static PlaybackEvent Start() => new("start-event", "attempt-a", "station", "started", Song.Id,
        Song.Artist, Song.Title, Song.Kind, DateTimeOffset.UtcNow.AddSeconds(-2), 384000);

    [Fact]
    public async Task RepeatedRecoveryClosesTheOldAttemptOnceAndKeepsTheUnfinishedItem()
    {
        var db = Database();
        await using (var journal = new EventJournal(db))
        {
            await journal.AppendAsync(Start(), Checkpoint());
            var first = await journal.PrepareRecoveryAsync("station");
            var second = await journal.PrepareRecoveryAsync("station");
            Assert.Equal("RestartPending", first!.State);
            Assert.Equal(first, second);
            Assert.Equal(Song, first.Item);
            Assert.Equal(4, first.RotationIndex);
            Assert.Equal("attempt-a", first.OccurrenceId);
        }
        var events = EventJournal.ReadEvents(db);
        var interrupted = Assert.Single(events, x => x.Type == "interrupted");
        Assert.Equal("last_committed_checkpoint", interrupted.TimingQuality);
        Assert.Contains("unknown", interrupted.Detail);
        Assert.True(EventJournal.Verify(db));
    }

    [Fact]
    public async Task CompletedItemRemainsConsumedAfterRepeatedReopens()
    {
        var db = Database();
        await using (var journal = new EventJournal(db)) await journal.SaveCheckpointAsync(Checkpoint("BetweenItems"));
        for (var i = 0; i < 3; i++)
        {
            await using var journal = new EventJournal(db);
            var recovered = await journal.PrepareRecoveryAsync("station");
            Assert.Equal("BetweenItems", recovered!.State);
            Assert.Null(recovered.Item); Assert.Equal(4, recovered.RotationIndex);
        }
        Assert.Empty(EventJournal.ReadEvents(db));
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData("queue-a", 4)]
    public async Task AcceptedSkipSurvivesRestartAndConsumesOnlyItsOwnQueueEntry(string? queue, int nextRotation)
    {
        var db = Database();
        await using var journal = new EventJournal(db);
        await journal.TransactionAsync((c, t) =>
        {
            using var sql = c.CreateCommand(); sql.Transaction = t;
            sql.CommandText = "CREATE TABLE studio_queue(id TEXT PRIMARY KEY,state TEXT); INSERT INTO studio_queue VALUES('queue-a','playing'),('queue-b','pending')";
            sql.ExecuteNonQuery(); return true;
        });
        await journal.AppendAsync(Start() with { QueueEntryId = queue }, Checkpoint(queue: queue));
        Assert.True(await journal.RequestSkipAsync("station", "skip-a", "attempt-a"));
        var recovered = await journal.PrepareRecoveryAsync("station");
        Assert.Equal("BetweenItems", recovered!.State);
        Assert.Equal(nextRotation, recovered.RotationIndex);
        Assert.Null(recovered.Item);
        Assert.False(await journal.HasPendingSkipAsync("station", "attempt-a"));
        await journal.PrepareRecoveryAsync("station");
        Assert.Single(EventJournal.ReadEvents(db), x => x.Type == "skipped");
        var states = await journal.TransactionAsync((c, t) =>
        {
            using var query = c.CreateCommand(); query.Transaction = t;
            query.CommandText = "SELECT state FROM studio_queue ORDER BY id";
            using var r = query.ExecuteReader(); var result = new List<string>();
            while (r.Read()) result.Add(r.GetString(0)); return result;
        });
        Assert.Equal(queue is null ? "playing" : "completed", states[0]);
        Assert.Equal("pending", states[1]);
    }

    [Fact]
    public async Task SkipReceiptRejectsDifferentStationOrAttemptAndNeverTargetsTheNextTrack()
    {
        await using var journal = new EventJournal(Database());
        Assert.True(await journal.RequestSkipAsync("station", "command-a", "attempt-a"));
        Assert.False(await journal.RequestSkipAsync("station", "command-a", "attempt-a"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.RequestSkipAsync("other", "command-a", "attempt-a"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.RequestSkipAsync("station", "command-a", "attempt-b"));
        Assert.False(await journal.HasPendingSkipAsync("station", "attempt-b"));
    }

    [Fact]
    public async Task DuplicateEventAcknowledgementCannotRewindCheckpoint()
    {
        var db = Database();
        await using var journal = new EventJournal(db);
        var start = Start(); var first = Checkpoint();
        await journal.AppendAsync(start, first);
        var advanced = first with { ProgramSamples = first.ProgramSamples + 48000, ItemSamples = first.ItemSamples + 48000 };
        await journal.SaveCheckpointAsync(advanced);
        await journal.AppendAsync(start, first);
        Assert.Equal(advanced, await journal.CheckpointAsync("station"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.AppendAsync(start with { Title = "Different payload" }, first));
        Assert.Equal(advanced, await journal.CheckpointAsync("station"));
        Assert.True(EventJournal.Verify(db));
    }

    [Fact]
    public async Task CorruptCheckpointIsRejectedWithoutInventingPlayback()
    {
        var db = Database();
        await using var journal = new EventJournal(db);
        await journal.SaveCheckpointAsync(Checkpoint() with { ItemSamples = -1 });
        await Assert.ThrowsAsync<InvalidDataException>(() => journal.PrepareRecoveryAsync("station"));
        Assert.Empty(EventJournal.ReadEvents(db));
    }

    [Fact]
    public async Task ExplicitlyStoppedItemDoesNotAcquireAnotherInterruptionOnReadback()
    {
        var db = Database();
        await using var journal = new EventJournal(db);
        var stopped = Checkpoint("Stopped");
        await journal.SaveCheckpointAsync(stopped);
        Assert.Equal(stopped, await journal.PrepareRecoveryAsync("station"));
        Assert.Empty(EventJournal.ReadEvents(db));
    }
}
