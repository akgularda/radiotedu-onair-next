using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class LoudnessMonitorTests
{
    private static readonly DateTimeOffset Anchor = DateTimeOffset.Parse("2026-10-15T21:00:00Z");
    private static PcmBlock Tone(long position, int frames = 960)
    {
        var block = PcmBlock.Rent(); block.SamplePosition = position; block.Length = frames * AudioFormat.BytesPerSampleFrame;
        var pcm = MemoryMarshal.Cast<byte, float>(block.Memory.Span);
        for (var i = 0; i < frames; i++)
        {
            var value = (float)(Math.Pow(10, -23 / 20d) * Math.Sin(2 * Math.PI * 1000 * (position + i) / 48000));
            pcm[i * 2] = value; pcm[i * 2 + 1] = value;
        }
        return block;
    }
    private static bool Send(ProgrammeLoudnessMonitor monitor, long position, int frames = 960)
    { using var block = Tone(position, frames); return monitor.Publish(block); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    [Fact]
    public async Task DefaultMeasurementQueueRetainsTwoSecondClockCatchupWithoutPadding()
    {
        var history = new ConcurrentQueue<ProgrammeLoudnessWindow>();
        await using var monitor = new ProgrammeLoudnessMonitor(
            "9", "clock-catchup", Anchor, history: w => { history.Enqueue(w); return Task.CompletedTask; });
        for (var i = 0; i < 100; i++) Assert.True(Send(monitor, i * 960));
        monitor.Complete(); await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var view = monitor.Snapshot();
        Assert.True(view.Continuous); Assert.Equal(0, view.DroppedFrames);
        Assert.Equal(0, view.QueuedBlocks); Assert.Equal(0, view.PendingHistoryWrites);
        Assert.Equal(0, view.HistoryFailures); Assert.Equal("Stopped", view.State);
        var finished = Assert.Single(history.Where(w => w.Phase == "Finished"));
        Assert.Equal(96000, finished.Reading!.SampleFrames);
        Assert.Equal(0, finished.StartSample); Assert.Equal(96000, finished.EndSample);
        Assert.False(finished.HasMeasurementGap); Assert.False(finished.MissingTail);
    }

    [Fact]
    public async Task ExactWindowRolloverAndPartialEofNeverPadOrLoseSamples()
    {
        var history = new List<ProgrammeLoudnessWindow>();
        await using var monitor = new ProgrammeLoudnessMonitor("9", "session", Anchor, 12017,
            TimeSpan.FromSeconds(3), 500, w => { history.Add(w); return Task.CompletedTask; });
        for (var i = 0; i < 150; i++) Assert.True(Send(monitor, 12017 + i * 960));
        Assert.True(Send(monitor, 12017 + 144000, 17));
        monitor.Complete(); await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var view = monitor.Snapshot();
        Assert.True(view.Continuous); Assert.Equal("Stopped", view.State); Assert.Equal(0, view.QueuedBlocks);
        var closed = history.Where(w => w.Phase == "Finished").ToArray();
        Assert.Equal(2, closed.Length); Assert.Equal(144017, closed.Sum(w => w.Reading!.SampleFrames));
        Assert.Equal(144000, closed[0].Reading!.SampleFrames); Assert.Equal("Duration", closed[0].Reason);
        Assert.Equal(17, closed[1].Reading!.SampleFrames); Assert.Equal(12017 + 144017, closed[1].EndSample);
        Assert.Equal(closed[1], view.LastClosed); Assert.False(closed[0].HasMeasurementGap);
        Assert.InRange(closed[0].Reading!.IntegratedLufs!.Value, -23.1, -22.9);
    }

    [Fact]
    public async Task SlowHistoryDoesNotBlockMeasurementOrProducer()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history=new ConcurrentQueue<ProgrammeLoudnessWindow>();var calls=0;
        await using var monitor=new ProgrammeLoudnessMonitor("9","session",Anchor,queueBlocks:2,history:w=>
        {history.Enqueue(w);if(Interlocked.Increment(ref calls)==1){entered.TrySetResult();return release.Task;}return Task.CompletedTask;});
        try
        {
            Assert.True(Send(monitor,0));await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for(var i=1;i<50;i++)
            {
                await Until(()=>monitor.Snapshot().QueuedBlocks==0);
                Assert.True(Send(monitor,i*960));
            }
            await Until(()=>monitor.Snapshot().Current?.Reading?.SampleFrames>=48000);
            Assert.False(release.Task.IsCompleted);
            Assert.True(monitor.Snapshot().Continuous);
            Assert.Equal(1,monitor.Snapshot().PendingHistoryWrites);
        }
        finally{release.TrySetResult();}
        monitor.Complete();await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0,monitor.Snapshot().PendingHistoryWrites);
        Assert.Equal(0,monitor.Snapshot().HistoryFailures);
        Assert.Equal(48000,history.Single(w=>w.Phase=="Finished").Reading!.SampleFrames);
    }

    [Fact]
    public async Task DelayedHistoryCanRecoverInOrderWithoutRestartingMeasurement()
    {
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history=new ConcurrentQueue<ProgrammeLoudnessWindow>();var calls=0;
        await using var monitor=new ProgrammeLoudnessMonitor("9","session",Anchor,maximumWindow:TimeSpan.FromSeconds(3),queueBlocks:500,history:w=>
        {history.Enqueue(w);return Interlocked.Increment(ref calls)==1?release.Task:Task.CompletedTask;});
        try
        {
            for(var i=0;i<151;i++)Assert.True(Send(monitor,i*960));
            await Until(()=>monitor.Snapshot().HistoryFailures>=1);
            Assert.Equal(1,calls);Assert.True(monitor.Snapshot().Continuous);
            release.TrySetResult();
            await Until(()=>monitor.Snapshot().PendingHistoryWrites==0);
            Assert.True(calls>=3);
            monitor.Complete();await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[]{"Started","Finished","Started","Finished"},history.Select(w=>w.Phase).ToArray());
            Assert.Equal(144960,history.Where(w=>w.Phase=="Finished").Sum(w=>w.Reading!.SampleFrames));
            Assert.True(monitor.Snapshot().Continuous);Assert.Equal(0,monitor.Snapshot().PendingHistoryWrites);
        }
        finally{release.TrySetResult();}
    }

    [Fact]
    public async Task MissingSourcePositionsStillCreateAnExplicitSeparateMeasurementSegment()
    {
        var history=new ConcurrentQueue<ProgrammeLoudnessWindow>();
        await using var monitor=new ProgrammeLoudnessMonitor("9","session",Anchor,queueBlocks:100,history:w=>{history.Enqueue(w);return Task.CompletedTask;});
        Assert.True(Send(monitor,0));Assert.True(Send(monitor,960));Assert.True(Send(monitor,4800));
        monitor.Complete();await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var closed=history.Where(w=>w.Phase=="Finished").ToArray();
        Assert.Equal(2,closed.Length);Assert.Equal("SourceGap",closed[0].Reason);Assert.True(closed[0].HasMeasurementGap);
        Assert.Equal(1920,closed[0].EndSample);Assert.Equal(4800,closed[1].StartSample);Assert.Equal(2880,closed[1].GapBeforeFrames);
        Assert.False(monitor.Snapshot().Continuous);Assert.Equal(1,monitor.Snapshot().Gaps);
    }

    [Fact]
    public async Task UnconfirmedHistoryIsBoundedAndDoesNotLoseMeasuredPcm()
    {
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
        await using var monitor=new ProgrammeLoudnessMonitor("9","session",Anchor,queueBlocks:100,history:_=>{Interlocked.Increment(ref calls);return release.Task;});
        try
        {
            for(var i=0;i<50;i++)Assert.True(Send(monitor,i*960));
            monitor.Complete();await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(monitor.Snapshot().Continuous);Assert.Equal(48000,monitor.Snapshot().LastClosed!.Reading!.SampleFrames);
            Assert.True(monitor.Snapshot().HistoryFailures>=1);Assert.NotNull(monitor.Snapshot().HistoryError);
            Assert.Equal(1,calls);Assert.Equal(1,monitor.Snapshot().PendingHistoryWrites);
        }
        finally{release.TrySetResult();}
        await Until(()=>monitor.Snapshot().PendingHistoryWrites==0);
    }

    [Fact]
    public async Task NonfiniteInputDoesNotPoisonSubsequentMeasurementOrClaimContinuousCoverage()
    {
        var history = new List<ProgrammeLoudnessWindow>();
        await using var monitor = new ProgrammeLoudnessMonitor("9", "session", Anchor, queueBlocks: 100,
            history: w => { history.Add(w); return Task.CompletedTask; });
        Assert.True(Send(monitor, 0));
        using (var bad = Tone(960)) { MemoryMarshal.Cast<byte, float>(bad.Memory.Span)[20] = float.NaN; Assert.True(monitor.Publish(bad)); }
        for (var i = 2; i < 52; i++) Assert.True(Send(monitor, i * 960));
        monitor.Complete(); await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var view = monitor.Snapshot(); Assert.Equal(1, view.InvalidBlocks); Assert.False(view.Continuous);
        Assert.Equal("InvalidPcm", history.First(w => w.Phase == "Finished").Reason);
        Assert.Equal(1920, view.LastClosed!.StartSample); Assert.Equal(960, view.LastClosed.GapBeforeFrames);
        Assert.InRange(view.LastClosed.Reading!.IntegratedLufs!.Value, -23.1, -22.9);
    }

    [Fact]
    public async Task HistoryFailureIsVisibleWhileAudioMeasurementContinues()
    {
        await using var monitor = new ProgrammeLoudnessMonitor("9", "session", Anchor, queueBlocks: 100,
            history: _ => Task.FromException(new IOException("Synthetic unavailable disk")));
        for (var i = 0; i < 50; i++) Assert.True(Send(monitor, i * 960));
        monitor.Complete(); await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var view = monitor.Snapshot(); Assert.True(view.HistoryFailures >= 2); Assert.NotNull(view.HistoryError);
        Assert.True(view.Continuous); Assert.Equal(48000, view.LastClosed!.Reading!.SampleFrames);
        Assert.InRange(view.LastClosed.Reading.IntegratedLufs!.Value, -23.1, -22.9);
    }

    [Fact]
    public async Task RejectedMetadataAndShutdownReleaseOnlyRetainedReferences()
    {
        var monitor = new ProgrammeLoudnessMonitor("9", "session", Anchor, queueBlocks: 2);
        using (var malformed = PcmBlock.Rent()) { malformed.Length = 7; Assert.False(monitor.Publish(malformed)); }
        var retained = Tone(0); Assert.True(monitor.Publish(retained)); retained.Dispose();
        await monitor.DisposeAsync(); await monitor.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => retained.Memory);
        using var after = Tone(960); Assert.False(monitor.Publish(after));
        Assert.Equal(AudioFormat.BlockBytes, after.Memory.Length);
        Assert.Equal(1, monitor.Snapshot().InvalidBlocks);
    }
}
