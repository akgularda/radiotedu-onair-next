using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class SourceAccessChecksTests
{
    private static string Root() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RadioTEDU", "OnAirNext-Dev", "unit-tests", Guid.NewGuid().ToString("N"));
    private static SourceAccessResult Readable => new(true, "Test service", "S-1-5-19", "Fixed", "fixture-volume", 2, false, true, "Limited fixture check; no audio decoded.");
    private static async Task<SourceAccessCheck> Finished(SourceAccessChecks checks, string station, string id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SourceAccessCheck result;
        while ((result = checks.Get(station, id)).State == "Checking") await Task.Delay(10, deadline.Token);
        return result;
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Utc = DateTimeOffset.UnixEpoch;
        public long Ticks;
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
    [Fact]
    public async Task CheckingReturnsBeforeIOAndRetriesDoNotCreateParallelWorkers()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SourceAccessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var checks = new SourceAccessChecks(_ => { Interlocked.Increment(ref calls); entered.SetResult(); return release.Task; });
        var path = Root(); var id = Guid.NewGuid().ToString();
        try
        {
            Assert.Equal("Checking", checks.Start("a", path, id).State);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Checking", checks.Start("a", path, id).State);
            Assert.Throws<InvalidOperationException>(() => checks.Start("b", path, Guid.NewGuid().ToString()));
            Assert.Equal(1, calls);
        }
        finally { release.TrySetResult(Readable); }
        Assert.Equal("Accessible", (await Finished(checks, "a", id)).State);
    }
    [Fact]
    public async Task ReadyProofBelongsToOneStationFolderAndRequest()
    {
        await using var checks = new SourceAccessChecks(_ => Task.FromResult(Readable));
        var path = Root(); var id = Guid.NewGuid().ToString(); checks.Start("a", path, id);
        await Finished(checks, "a", id); checks.RequireReady("a", path, id);
        Assert.Throws<InvalidOperationException>(() => checks.Get("b", id));
        Assert.Throws<InvalidOperationException>(() => checks.RequireReady("a", path + "-other", id));
        Assert.Throws<ArgumentException>(() => checks.Start("a", path + "-other", id));
        Assert.Throws<ArgumentException>(() => checks.Start("b", path, id));
    }
    [Fact]
    public async Task ClockCorrectionCannotExtendTheAccessLease()
    {
        var clock = new Clock(); await using var checks = new SourceAccessChecks(_ => Task.FromResult(Readable), clock);
        var path = Root(); var id = Guid.NewGuid().ToString(); checks.Start("a", path, id); await Finished(checks, "a", id);
        clock.Utc = clock.Utc.AddDays(-1); clock.Ticks += TimeSpan.FromMinutes(1).Ticks;
        Assert.Equal("Expired", checks.Get("a", id).State);
        Assert.Throws<InvalidOperationException>(() => checks.RequireReady("a", path, id));
    }
    [Fact]
    public async Task IOFailureIsVisibleWithoutLeakingDetailsAndANewCheckCanRecover()
    {
        var calls = 0; await using var checks = new SourceAccessChecks(_ => ++calls == 1
            ? Task.FromException<SourceAccessResult>(new IOException("private-fixture-secret")) : Task.FromResult(Readable));
        var path = Root(); var first = Guid.NewGuid().ToString(); checks.Start("a", path, first);
        var failed = await Finished(checks, "a", first); Assert.Equal("Failed", failed.State);
        Assert.DoesNotContain("private-fixture-secret", failed.Error!);
        var second = Guid.NewGuid().ToString(); checks.Start("a", path, second);
        Assert.Equal("Accessible", (await Finished(checks, "a", second)).State);
    }
    [Fact]
    public async Task UnavailableFolderNeverAuthorizesAnAssignment()
    {
        await using var checks = new SourceAccessChecks(_ => Task.FromResult(Readable with { Accessible = false }));
        var path = Root(); var id = Guid.NewGuid().ToString(); checks.Start("a", path, id);
        Assert.Equal("Unavailable", (await Finished(checks, "a", id)).State);
        Assert.Throws<InvalidOperationException>(() => checks.RequireReady("a", path, id));
    }
    [Fact]
    public async Task CompletedHistoryIsBoundedWithoutEvictingTheLatestResult()
    {
        var clock = new Clock(); await using var checks = new SourceAccessChecks(_ => Task.FromResult(Readable), clock);
        var path = Root(); var first = ""; var last = "";
        for (var i = 0; i < 65; i++)
        {
            last = Guid.NewGuid().ToString(); if (i == 0) first = last;
            checks.Start("a", path, last); await Finished(checks, "a", last);
            clock.Utc = clock.Utc.AddMilliseconds(1); clock.Ticks += TimeSpan.FromMilliseconds(1).Ticks;
        }
        Assert.Throws<InvalidOperationException>(() => checks.Get("a", first));
        Assert.Equal("Accessible", checks.Get("a", last).State);
    }
    [Fact]
    public async Task ShutdownRetainsOwnershipUntilTheActiveProbeCompletes()
    {
        var release = new TaskCompletionSource<SourceAccessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = new SourceAccessChecks(_ => release.Task); checks.Start("a", Root(), Guid.NewGuid().ToString());
        var dispose = checks.DisposeAsync().AsTask();
        try
        {
            Assert.False(dispose.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => checks.Start("a", Root(), Guid.NewGuid().ToString()));
        }
        finally { release.TrySetResult(Readable); }
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Fact]
    public async Task WholeDrivesAndRelativePathsCannotEnterTheProbeWorker()
    {
        var calls = 0; await using var checks = new SourceAccessChecks(_ => { calls++; return Task.FromResult(Readable); });
        Assert.Throws<ArgumentException>(() => checks.Start("a", Path.GetPathRoot(Root())!, Guid.NewGuid().ToString()));
        Assert.Throws<ArgumentException>(() => checks.Start("a", "relative/music", Guid.NewGuid().ToString()));
        Assert.Equal(0, calls);
    }
    [Fact]
    public async Task NewRootRejectionRollsBackItsReceiptAndCommittedReplayNeedsNoFreshLease()
    {
        var path = Root(); await using var journal = new EventJournal(Path.Combine(path, "catalog.db"));
        await journal.InitializeLibraryAsync(); await journal.InitializeDesiredStateAsync(new("a", [], [], AutoStart: false));
        var a = Path.Combine(path, "music-a"); var b = Path.Combine(path, "music-b");
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SetLibraryRootAsync("a", a, "first", () => throw new InvalidOperationException("Expired proof")));
        Assert.Empty(journal.LibraryRoots());
        Assert.True(await journal.SetLibraryRootAsync("a", a, "first", () => { }));
        Assert.True(await journal.SetLibraryRootAsync("a", b, "second", () => { }));
        Assert.False(await journal.SetLibraryRootAsync("a", a, "first", () => throw new InvalidOperationException("Expired proof")));
        Assert.Equal(b, Assert.Single(journal.LibraryRoots()).Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SetLibraryRootAsync("a", b, "first", () => { }));
    }
}
