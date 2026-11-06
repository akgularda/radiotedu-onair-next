using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class DecoderPreparationTests
{
    private static readonly MediaItem Item = new("test","unused.wav","Artist","Song");
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Until(Func<bool> condition)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(!condition())await Task.Delay(10,timeout.Token);
    }
    private sealed class Decoder(int blocks=1000, Func<ValueTask>? release=null) : IFrameDecoder
    {
        private int _remaining=blocks;
        public Task Ready=>Task.CompletedTask;
        public int ReleaseAttempts;
        public ValueTask<PcmBlock?> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(_remaining--<=0)return ValueTask.FromResult<PcmBlock?>(null);
            var block=PcmBlock.Rent();MemoryMarshal.Cast<byte,float>(block.Memory.Span).Fill(.1f);
            return ValueTask.FromResult<PcmBlock?>(block);
        }
        public ValueTask DisposeAsync(){Interlocked.Increment(ref ReleaseAttempts);return release?.Invoke()??ValueTask.CompletedTask;}
    }
    [Fact]
    public async Task SlowOpenAndRetirementDoNotBlockTheOwnerAndDisposalWaitsForOwnership()
    {
        using var open=new ManualResetEventSlim();var entered=Signal();var close=Signal();var closing=Signal();
        var decoder=new Decoder(release:async()=>{closing.TrySetResult();await close.Task;});
        await using var pool=new DecoderPreparation(_=>{entered.TrySetResult();open.Wait();return decoder;});
        try
        {
            var source=pool.Start(Item);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(source.Ready.IsCompleted);
            pool.Retire(source);Assert.Equal(1,pool.Snapshot().Retiring);
            var stop=pool.DisposeAsync().AsTask();Assert.False(stop.IsCompleted);
            open.Set();await closing.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(stop.IsCompleted);
            close.TrySetResult();await stop.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0,pool.Snapshot().Owned);Assert.Equal(0,pool.Snapshot().Active);Assert.Equal(1,decoder.ReleaseAttempts);
        }
        finally{open.Set();close.TrySetResult();}
    }
    [Fact]
    public async Task RetiringARequestWaitingForCapacityNeverLaunchesItsCodec()
    {
        var created=new ConcurrentBag<string>();
        await using var pool=new DecoderPreparation(item=>{created.Add(item.Id);return new Decoder();});
        var active=Enumerable.Range(0,4).Select(i=>pool.Start(Item with{Id=i.ToString()})).ToArray();
        await Task.WhenAll(active.Select(s=>s.Ready)).WaitAsync(TimeSpan.FromSeconds(10));
        var waiting=pool.Start(Item with{Id="withdrawn"});Assert.False(waiting.Ready.IsCompleted);
        pool.Retire(waiting);await Until(()=>pool.Snapshot().Owned==4);
        Assert.DoesNotContain("withdrawn",created);Assert.Equal(4,pool.Snapshot().Active);
        foreach(var source in active)pool.Retire(source);
        await Until(()=>pool.Snapshot().Owned==0);
    }
    [Fact]
    public async Task SlowOperatingSystemCallsKeepBothPhysicalWorkersAndPendingRequestsBounded()
    {
        using var open=new ManualResetEventSlim();var entered=0;
        await using var pool=new DecoderPreparation(_=>{Interlocked.Increment(ref entered);open.Wait();return new Decoder();});
        try
        {
            var sources=Enumerable.Range(0,16).Select(i=>pool.Start(Item with{Id=i.ToString()})).ToArray();
            await Until(()=>Volatile.Read(ref entered)==4);
            Assert.Equal(4,pool.Snapshot().Active);Assert.Equal(16,pool.Snapshot().Owned);
            Assert.Throws<IOException>(()=>pool.Start(Item));
            foreach(var source in sources)pool.Retire(source);
            await Until(()=>pool.Snapshot().Owned==4);
            Assert.Equal(4,Volatile.Read(ref entered));
            open.Set();await pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0,pool.Snapshot().Active);Assert.Equal(0,pool.Snapshot().Owned);
        }
        finally{open.Set();}
    }
    [Fact]
    public async Task FailedOpenReturnsItsSlotAndRemainsAnExplicitReadFailure()
    {
        await using var pool=new DecoderPreparation(item=>item.Id=="bad"?throw new IOException("source unavailable"):new Decoder());
        var bad=pool.Start(Item with{Id="bad"});
        await Assert.ThrowsAsync<IOException>(()=>bad.Ready);
        await Assert.ThrowsAsync<IOException>(()=>bad.ReadAsync(CancellationToken.None).AsTask());
        pool.Retire(bad);
        var good=pool.Start(Item);await good.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1,pool.Snapshot().Active);pool.Retire(good);await Until(()=>pool.Snapshot().Owned==0);
    }
    [Fact]
    public async Task FailedRetirementKeepsOwnershipAndCanBeRetried()
    {
        var attempts=0;var decoder=new Decoder(release:()=>Interlocked.Increment(ref attempts)==1
            ?ValueTask.FromException(new IOException("injected release failure")):ValueTask.CompletedTask);
        await using var pool=new DecoderPreparation(_=>decoder);
        var source=pool.Start(Item);await source.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<IOException>(()=>source.DisposeAsync().AsTask());
        Assert.Equal(1,pool.Snapshot().Active);Assert.Equal(1,pool.Snapshot().Owned);Assert.Equal("IOException",pool.Snapshot().CleanupError);
        await pool.DisposeAsync();Assert.Equal(2,decoder.ReleaseAttempts);Assert.Equal(0,pool.Snapshot().Active);Assert.Equal(0,pool.Snapshot().Owned);
    }
    [Fact]
    public async Task SlowFutureOpenAndOldSourceCleanupDoNotPauseCurrentProgrammeSamples()
    {
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RadioTEDU","OnAirNext-Dev","unit-tests",Guid.NewGuid().ToString("N"));
        await using var journal=new EventJournal(Path.Combine(root,"decoder-continuity.db"));
        using var open=new ManualResetEventSlim();var futureEntered=Signal();var close=Signal();var oldClosing=Signal();
        var output=new Output();var items=new[]{Item with{Id="first"},Item with{Id="second"},Item with{Id="future"}};
        var settings=new StationSettings("test",items,[],Repeat:true);
        await using var station=new StationEngine("unused",settings,journal,[output],null,null,item=>
        {
            if(item.Id=="future"){futureEntered.TrySetResult();open.Wait();}
            return item.Id=="first"?new Decoder(20,async()=>{oldClosing.TrySetResult();await close.Task;}):new Decoder(200);
        });
        try
        {
            await station.StartAsync();await futureEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));await oldClosing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var before=station.Snapshot().Samples;
            await Task.Delay(500);
            var after=station.Snapshot();Assert.Equal("Running",after.State);Assert.Equal("second",after.CurrentAssetId);
            Assert.True(after.Samples-before>=48000*.35,$"Only {after.Samples-before} programme frames advanced while future open and retirement were blocked.");
            Assert.True(output.Blocks>=35);Assert.Equal(0,after.AuditRejectedEvents);
            Assert.InRange(after.DecoderPreparation!.Active,1,4);
        }
        finally{open.Set();close.TrySetResult();}
        await station.DisposeAsync();Assert.Equal(0,station.Snapshot().DecoderPreparation!.Owned);
        Assert.True(EventJournal.Verify(Path.Combine(root,"decoder-continuity.db")));
    }
    private sealed class Output : IProgramOutput
    {
        private int _blocks;public int Blocks=>Volatile.Read(ref _blocks);
        public void Start(){}
        public bool Publish(PcmBlock block){Interlocked.Increment(ref _blocks);return true;}
        public OutputSnapshot Snapshot()=>new("test","SourceConnected",1,Blocks,0,0,0,null,null);
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
