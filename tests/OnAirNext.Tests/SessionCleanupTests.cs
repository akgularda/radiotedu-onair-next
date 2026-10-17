using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class SessionCleanupTests
{
    private sealed class Output(bool failFirst) : IProgramOutput
    {
        public int Attempts;
        public void Start() { }
        public bool Publish(PcmBlock block) => true;
        public OutputSnapshot Snapshot() => new("test", "Stopped", 0, 0, 0, 0, 0, null, null);
        public ValueTask DisposeAsync()
        {
            Attempts++;
            return failFirst && Attempts == 1 ? ValueTask.FromException(new IOException("Injected cleanup failure")) : ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task OneFailedCleanupDoesNotPreventHealthyOutputReleaseAndCanBeRetried()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadioTEDU", "OnAirNext-Dev", "unit-tests", Guid.NewGuid().ToString("N"));
        await using var journal = new EventJournal(Path.Combine(root, "cleanup.db"));
        var failed = new Output(true); var healthy = new Output(false);
        var settings = new StationSettings("test", [new("asset", "fixture.wav", "Artist", "Song")], []);
        var session = new StationEngine("unused", settings, journal, [failed, healthy]);
        await Assert.ThrowsAsync<IOException>(() => session.DisposeAsync().AsTask());
        Assert.Equal(1, healthy.Attempts);
        await session.DisposeAsync(); await session.DisposeAsync();
        Assert.Equal(2, failed.Attempts);
        Assert.Equal(1, healthy.Attempts);
    }
}
