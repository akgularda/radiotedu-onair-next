using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class DesiredStateTests
{
    private static string Database()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RadioTEDU", "OnAirNext-Dev", "unit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return Path.Combine(root, "desired-state.db");
    }
    private static StationSettings Settings(string id = "station", bool enabled = true)
        => new(id, [new("asset", "fixture.wav", "Fixture", "Song")], [], Enabled: enabled);

    [Fact]
    public async Task ExplicitStopSurvivesReopenDespiteAutostartConfiguration()
    {
        var db = Database();
        await using (var journal = new EventJournal(db))
        {
            Assert.True((await journal.InitializeDesiredStateAsync(Settings())).RequestedRunning);
            Assert.True(await journal.SetDesiredStateAsync("station", true, false, "stop-1"));
        }
        await using (var journal = new EventJournal(db))
            Assert.Equal(new DesiredStationState("station", true, false), await journal.InitializeDesiredStateAsync(Settings()));
    }

    [Fact]
    public async Task DisabledStationsNeverAcquireRunningIntentFromAnAutostartDefault()
    {
        await using var journal = new EventJournal(Database());
        Assert.Equal(new DesiredStationState("station", false, false), await journal.InitializeDesiredStateAsync(Settings(enabled: false)));
        await Assert.ThrowsAsync<ArgumentException>(() => journal.SetDesiredStateAsync("station", false, true, "invalid"));
        Assert.Equal(new DesiredStationState("station", false, false), await journal.DesiredStateAsync("station"));
    }

    [Fact]
    public async Task LostStopAcknowledgementCannotUndoALaterStartDecision()
    {
        await using var journal = new EventJournal(Database());
        await journal.InitializeDesiredStateAsync(Settings());
        await journal.SetDesiredStateAsync("station", true, false, "stop-a");
        await journal.SetDesiredStateAsync("station", true, true, "start-b");
        Assert.False(await journal.SetDesiredStateAsync("station", true, false, "stop-a"));
        Assert.True((await journal.DesiredStateAsync("station"))!.RequestedRunning);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SetDesiredStateAsync("station", false, false, "stop-a"));
    }

    [Fact]
    public async Task SkipAndStartupCommandsShareTheSameIdNamespace()
    {
        await using var journal = new EventJournal(Database());
        await journal.InitializeDesiredStateAsync(Settings());
        await journal.RequestSkipAsync("station", "same-id", "attempt");
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SetDesiredStateAsync("station", true, false, "same-id"));
        Assert.True((await journal.DesiredStateAsync("station"))!.RequestedRunning);
    }

    [Fact]
    public async Task AllStationStopRollsBackItsReceiptAndEarlierChangesIfAnyStationIsUnknown()
    {
        await using var journal = new EventJournal(Database());
        await journal.InitializeDesiredStateAsync(Settings("a"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.RequestStopAllAsync(["a", "b"], "stop-all"));
        Assert.True((await journal.DesiredStateAsync("a"))!.RequestedRunning);
        await journal.InitializeDesiredStateAsync(Settings("b", enabled: false));
        Assert.True(await journal.RequestStopAllAsync(["a", "b"], "stop-all"));
        Assert.False(await journal.RequestStopAllAsync(["b", "a"], "stop-all"));
        Assert.Equal(new DesiredStationState("a", true, false), await journal.DesiredStateAsync("a"));
        Assert.Equal(new DesiredStationState("b", false, false), await journal.DesiredStateAsync("b"));
    }
}
