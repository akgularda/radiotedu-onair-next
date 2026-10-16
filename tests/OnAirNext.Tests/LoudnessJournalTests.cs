using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class LoudnessJournalTests
{
    private static string Database() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RadioTEDU", "OnAirNext-Dev", "unit-tests", Guid.NewGuid().ToString("N"), "measurements.db");
    private static ProgrammeLoudnessWindow Window() => new(Guid.NewGuid().ToString("N"), "9", "session", 123, 123,
        DateTimeOffset.Parse("2026-10-15T21:00:00Z"), "Started", "ContinuousWindow", 144000, false, 0, false, null);

    [Fact]
    public async Task InterruptedWindowKeepsKnownPrefixAndDoesNotInventItsMissingTail()
    {
        var path = Database(); var started = Window(); var meter = new StereoLoudnessMeter48k(TimeSpan.FromSeconds(3));
        meter.Feed(new float[9600]);
        var checkpoint = started with { Phase = "Checkpoint", EndSample = started.StartSample + 4800, Reading = meter.Read() };
        await using (var journal = new EventJournal(path))
        { Assert.True(await journal.SaveMeasurementAsync(started)); Assert.True(await journal.SaveMeasurementAsync(checkpoint)); }
        await using (var journal = new EventJournal(path))
        {
            await journal.RecoverLoudnessAsync("9"); await journal.RecoverLoudnessAsync("9");
            var rows = journal.MeasurementHistory("9"); Assert.Equal(3, rows.Count);
            var interrupted = rows[0]; Assert.Equal("Interrupted", interrupted.Phase); Assert.Equal("ProcessRestart", interrupted.Reason);
            Assert.True(interrupted.MissingTail); Assert.True(interrupted.HasMeasurementGap);
            Assert.Equal(checkpoint.EndSample, interrupted.EndSample); Assert.Equal(checkpoint.Reading, interrupted.Reading);
            Assert.Empty(journal.MeasurementHistory("8"));
        }
    }

    [Fact]
    public async Task FinishedSnapshotsAreImmutableIdempotentAndCannotMoveBetweenStations()
    {
        await using var journal = new EventJournal(Database()); var started = Window();
        Assert.True(await journal.SaveMeasurementAsync(started)); Assert.False(await journal.SaveMeasurementAsync(started));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SaveMeasurementAsync(started with { Reason = "Changed" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SaveMeasurementAsync(started with { StationId = "8", Phase = "Finished" }));
        var ended = started with { Phase = "Finished", Reason = "SessionEnded" };
        Assert.True(await journal.SaveMeasurementAsync(ended)); await journal.RecoverLoudnessAsync("9");
        Assert.Equal(2, journal.MeasurementHistory("9").Count);
        Assert.Throws<ArgumentException>(() => journal.MeasurementHistory("9", count: 101));
        await Assert.ThrowsAsync<ArgumentException>(() => journal.SaveMeasurementAsync(started with { EndSample = 999 }));
    }
}
