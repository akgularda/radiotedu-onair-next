using System.Collections.Concurrent;
using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class CoordinatorTests
{
    private static string Database()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RadioTEDU", "OnAirNext-Dev", "unit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return Path.Combine(root, "coordinator.db");
    }
    private static StationSettings Station(string id, bool autoStart = false, bool enabled = true) =>
        new(id, [new("asset", "fixture.wav", "Artist", "Song")], [], AutoStart: autoStart, Enabled: enabled);
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    private sealed class Session(string id, EventJournal journal, Task? release = null) : IStationSession
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile string _state = "Stopped";
        public int Starts, Stops, Skips;
        public string Id => id;
        public Task Completion => _completion.Task;
        public Task StartAsync() { Interlocked.Increment(ref Starts); _state = "Running"; return Task.CompletedTask; }
        public StationSnapshot Snapshot() => new(id, _state, 0, 0, Skips, "Song", 0, 0, []);
        public async Task SkipAsync(string commandId)
        { if (await journal.RequestSkipAsync(id, commandId)) Interlocked.Increment(ref Skips); }
        public void Fail() { _state = "Faulted"; _completion.TrySetException(new IOException("Injected session failure")); }
        public void Finish() { _state = "Completed"; _completion.TrySetResult(); }
        public async ValueTask DisposeAsync()
        {
            if (release is not null) await release;
            if (Interlocked.Exchange(ref Stops, 1) == 0) { _state = "Stopped"; _completion.TrySetResult(); }
        }
    }

    [Fact]
    public async Task StartingAndStoppingOneStationLeavesItsSiblingAndLaterIntentUntouched()
    {
        var database = Database();
        var created = new ConcurrentQueue<Session>();
        await using (var journal = new EventJournal(database))
        await using (var coordinator = new StationCoordinator("unused", [Station("a"), Station("b", autoStart: true)], journal,
            settings => { var session = new Session(settings.Id, journal); created.Enqueue(session); return session; }))
        {
            await coordinator.InitializeAsync();
            await Until(() => coordinator.Snapshot().Single(x => x.Id == "b").State == "Running");
            Assert.True(await coordinator.RequestRunningAsync("a", true, "start-a"));
            await Until(() => coordinator.Snapshot().Single(x => x.Id == "a").State == "Running");
            Assert.True(await coordinator.RequestRunningAsync("a", false, "stop-a"));
            await Until(() => coordinator.Snapshot().Single(x => x.Id == "a").State == "Stopped");
            Assert.False(await coordinator.RequestRunningAsync("a", true, "start-a"));
            Assert.False(coordinator.Snapshot().Single(x => x.Id == "a").RequestedRunning);
            Assert.Equal("Running", coordinator.Snapshot().Single(x => x.Id == "b").State);
            Assert.Equal(2, created.Count);
            Assert.Equal(0, created.Single(x => x.Id == "b").Stops);
            Assert.Equal(1, created.Single(x => x.Id == "a").Stops);
        }
        await using var reopened = new EventJournal(database);
        Assert.False((await reopened.DesiredStateAsync("a"))!.RequestedRunning);
        Assert.True((await reopened.DesiredStateAsync("b"))!.RequestedRunning);
    }

    [Fact]
    public async Task FailedSessionCannotBeReplacedUntilCleanupCompletesAndSiblingCommandsStillWork()
    {
        await using var journal = new EventJournal(Database());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = new ConcurrentQueue<Session>();
        await using var coordinator = new StationCoordinator("unused", [Station("a", autoStart: true), Station("b")], journal,
            settings =>
            {
                var session = new Session(settings.Id, journal, settings.Id == "a" && a.IsEmpty ? release.Task : null);
                if (settings.Id == "a") a.Enqueue(session);
                return session;
            });
        try
        {
            await coordinator.InitializeAsync();
            await Until(() => a.TryPeek(out var first) && first.Starts == 1);
            Assert.True(a.TryPeek(out var original)); original!.Fail();
            await Until(() => coordinator.Snapshot().Single(x => x.Id == "a").State == "Releasing");
            Assert.True(await coordinator.RequestRunningAsync("b", true, "start-b"));
            await Until(() => coordinator.Snapshot().Single(x => x.Id == "b").State == "Running");
            Assert.Single(a);
            release.TrySetResult();
            await Until(() => a.Count == 2 && coordinator.Snapshot().Single(x => x.Id == "a").State == "Running");
            Assert.Equal(1, original.Stops);
            Assert.Equal("Running", coordinator.Snapshot().Single(x => x.Id == "b").State);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task NaturalCompletionDoesNotAutomaticallyReplayTheFinishedProgramme()
    {
        await using var journal = new EventJournal(Database());
        var sessions = new ConcurrentQueue<Session>();
        await using var coordinator = new StationCoordinator("unused", [Station("a", autoStart: true)], journal,
            settings => { var session = new Session(settings.Id, journal); sessions.Enqueue(session); return session; });
        await coordinator.InitializeAsync();
        await Until(() => sessions.TryPeek(out var current) && current.Starts == 1);
        Assert.True(sessions.TryPeek(out var original)); original!.Finish();
        await Until(() => coordinator.Snapshot().Single().State == "Completed");
        await Task.Delay(100);
        Assert.Single(sessions);
        Assert.True(coordinator.Snapshot().Single().RequestedRunning);
    }

    [Fact]
    public async Task FolderReturnSignalSurvivesEofAndCannotUndoAnExplicitStop()
    {
        await using var journal = new EventJournal(Database());
        var sessions = new ConcurrentQueue<Session>();
        await using var coordinator = new StationCoordinator("unused", [Station("a", autoStart: true)], journal,
            settings => { var session = new Session(settings.Id, journal); sessions.Enqueue(session); return session; });
        await coordinator.InitializeAsync();
        await Until(() => sessions.TryPeek(out var current) && current.Starts == 1);
        coordinator.RefreshLibrary("a", true);
        await Task.Delay(100); // worker sees the signal before the old session reaches EOF
        coordinator.RefreshLibrary("a", false); // an empty queue cannot erase the folder-return wakeup
        Assert.True(sessions.TryPeek(out var original)); original!.Finish();
        await Until(() => sessions.Count == 2 && coordinator.Snapshot().Single().State == "Running");
        await coordinator.RequestRunningAsync("a", false, "stop-a");
        await Until(() => coordinator.Snapshot().Single().State == "Stopped");
        coordinator.RefreshLibrary("a", true);
        await Task.Delay(100);
        Assert.Equal(2, sessions.Count);
        Assert.False(coordinator.Snapshot().Single().RequestedRunning);
    }

    [Fact]
    public async Task DisabledUnknownAndConflictingRequestsNeverAcquireAStationSession()
    {
        await using var journal = new EventJournal(Database());
        var created = new ConcurrentQueue<string>();
        await using var coordinator = new StationCoordinator("unused", [Station("a"), Station("b", enabled: false)], journal,
            settings => { created.Enqueue(settings.Id); return new Session(settings.Id, journal); });
        await coordinator.InitializeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RequestRunningAsync("b", true, "disabled"));
        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.RequestRunningAsync("unknown", true, "unknown"));
        await coordinator.RequestRunningAsync("a", true, "same-id");
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RequestRunningAsync("b", false, "same-id"));
        Assert.False(coordinator.Snapshot().Single(x => x.Id == "b").RequestedRunning);
        Assert.DoesNotContain("b", created);
    }

    [Fact]
    public async Task AcknowledgementOfAnEarlierSkipCanBeRetriedAfterTheStationHasStopped()
    {
        await using var journal = new EventJournal(Database());
        Session? session = null;
        await using var coordinator = new StationCoordinator("unused", [Station("a", autoStart: true)], journal,
            settings => session = new Session(settings.Id, journal));
        await coordinator.InitializeAsync();
        await Until(() => coordinator.Snapshot().Single().State == "Running");
        await coordinator.SkipAsync("a", "skip-a");
        await coordinator.RequestRunningAsync("a", false, "stop-a");
        await Until(() => coordinator.Snapshot().Single().State == "Stopped");
        await coordinator.SkipAsync("a", "skip-a");
        Assert.Equal(1, session!.Skips);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SkipAsync("a", "new-skip"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RequestRunningAsync("a", false, "skip-a"));
    }

    [Fact]
    public async Task CoordinatorDisposalIsIdempotentAndRejectsLaterMutations()
    {
        await using var journal = new EventJournal(Database());
        var coordinator = new StationCoordinator("unused", [Station("a")], journal, settings => new Session(settings.Id, journal));
        await coordinator.InitializeAsync();
        await coordinator.DisposeAsync(); await coordinator.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.RequestRunningAsync("a", true, "late"));
    }
}
