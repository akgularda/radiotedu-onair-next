using System.Text.Json;
using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class StationQueueIntegrationTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required EventJournal Journal { get; init; }
        public required StationLibrary Library { get; init; }
        public required StationQueues Queues { get; init; }
        public StationSettings Settings(string station="a") => new(station,[],[],AutoStart:false);
        public QueueLibraryReference Source(string name="alpha",string station="a")
        {
            var page=Library.Page(station,"",0);var asset=page.Items.Single(x=>x.RelativePath==name+".wav");
            return new(page.Root!.Revision,asset.Id,asset.EditVersion,asset.SourceStamp);
        }
        public async Task<string> Add(string name="alpha",string station="a")
        {
            var id=Guid.NewGuid().ToString("N");
            await Queues.EnqueueAsync(station,Source(name,station),(await Queues.SnapshotAsync(station)).Revision,id);return id;
        }
        public async Task Wait(Func<bool> condition)
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while(!condition()){Library.Wake();await Task.Delay(60,deadline.Token);}
        }
        public async ValueTask DisposeAsync(){Queues.Dispose();await Library.DisposeAsync();await Journal.DisposeAsync();}
        public static async Task<Fixture> Create()
        {
            var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RadioTEDU","OnAirNext-Dev","unit-tests",Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);var journal=new EventJournal(Path.Combine(root,"playback.db"));
            foreach(var station in new[]{"a","b"})await journal.InitializeDesiredStateAsync(new(station,[],[],AutoStart:false));
            var library=new StationLibrary(journal,"unused",root,["a","b"],(path,_)=>Task.FromResult(new AudioMetadata("Fixture",Path.GetFileNameWithoutExtension(path),30)));
            var queues=new StationQueues(journal,library,["a","b"]);
            await library.InitializeAsync();await queues.InitializeAsync();
            foreach(var station in new[]{"a","b"})
            {
                var folder=Path.Combine(root,station);Directory.CreateDirectory(folder);
                foreach(var name in new[]{"alpha","beta","gamma","delta"})await File.WriteAllTextAsync(Path.Combine(folder,name+".wav"),"synthetic fixture");
                await library.SetRootAsync(station,folder,Guid.NewGuid().ToString("N"));
            }
            var result=new Fixture{Root=root,Journal=journal,Library=library,Queues=queues};
            await result.Wait(()=>library.ReadyInventory("a").ReadyCount==4&&library.ReadyInventory("b").ReadyCount==4);return result;
        }
    }

    [Fact]
    public async Task LibraryAppendIsBoundToChannelAndDoesNotArmAnOutput()
    {
        await using var f=await Fixture.Create();var a=await f.Add();var b=await f.Add("beta","b");
        var view=await f.Queues.SnapshotAsync("a");Assert.Equal(a,Assert.Single(view.Items).Id);Assert.True(view.Items[0].Available);
        Assert.Equal(b,Assert.Single((await f.Queues.SnapshotAsync("b")).Items).Id);
        Assert.False((await f.Journal.InitializeDesiredStateAsync(f.Settings())).RequestedRunning);
        Assert.DoesNotContain(f.Root,JsonSerializer.Serialize(view),StringComparison.OrdinalIgnoreCase);
        var planner=f.Queues.Planner(f.Settings())!;Assert.Equal(a,planner.SelectPrepared(0,new HashSet<string>())!.QueueEntryId);
        Assert.False(planner.Select(0).AdvancesRotation);planner.ReleaseQueueReservations();
    }

    [Fact]
    public async Task LostAcknowledgementReplaysAfterTheSourceIsRetiredWithoutAppendingAgain()
    {
        await using var f=await Fixture.Create();var source=f.Source();var id=Guid.NewGuid().ToString("N");
        await f.Queues.EnqueueAsync("a",source,0,id);
        await f.Library.EditAssetAsync("a",source.Revision,source.AssetId,source.ExpectedVersion,LibraryEditKind.Retire,"retire");
        await f.Queues.EnqueueAsync("a",source,0,id);
        Assert.Single((await f.Queues.SnapshotAsync("a")).Items);Assert.False((await f.Queues.SnapshotAsync("a")).Items[0].Available);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Queues.EnqueueAsync("b",source,0,id));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Queues.EnqueueAsync("a",source with{ExpectedVersion=1},0,id));
    }

    [Fact]
    public async Task StaleLibrarySelectionCannotCreateAQueueEntryOrKeepACommandReceipt()
    {
        await using var f=await Fixture.Create();var source=f.Source();var id=Guid.NewGuid().ToString("N");
        await f.Library.EditAssetAsync("a",source.Revision,source.AssetId,source.ExpectedVersion,LibraryEditKind.Metadata,"edit",artist:"Fixture",title:"Corrected title");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Queues.EnqueueAsync("a",source,0,id));
        await f.Queues.EnqueueAsync("a",f.Source(),0,id);
        Assert.Equal("Corrected title",Assert.Single((await f.Queues.SnapshotAsync("a")).Items).Title);
    }

    [Fact]
    public async Task LiveEditsProtectCurrentAndReadyItemsWhileOtherItemsCanMove()
    {
        await using var f=await Fixture.Create();var first=await f.Add();var second=await f.Add("beta");var third=await f.Add("gamma");var fourth=await f.Add("delta");
        var planner=f.Queues.Planner(f.Settings())!;
        Assert.Equal(first,planner.SelectPrepared(0,new HashSet<string>())!.QueueEntryId);
        Assert.Equal(second,planner.SelectPrepared(0,new HashSet<string>{first})!.QueueEntryId);
        planner.RetainQueueReservations(new HashSet<string>{first,second});
        await f.Journal.RequestRunningIntentAsync("a",true,"run");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Queues.EditAsync("a",first,4,QueueEdit.Remove,"remove-current"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Queues.EditAsync("a",third,4,QueueEdit.MoveUp,"cross-ready"));
        await f.Queues.EditAsync("a",fourth,4,QueueEdit.MoveUp,"move-free");
        Assert.Equal(new[]{first,second,fourth,third},(await f.Queues.SnapshotAsync("a")).Items.Select(x=>x.Id));
        Assert.Equal(fourth,planner.SelectPrepared(0,new HashSet<string>{first,second,third})!.QueueEntryId);
        // The original edit is acknowledged even if its item is now locked.
        await f.Queues.EditAsync("a",fourth,4,QueueEdit.MoveUp,"move-free");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Queues.EditAsync("a",fourth,5,QueueEdit.Remove,"remove-ready"));
        planner.ReleaseQueueReservations();
        await f.Queues.EditAsync("a",fourth,5,QueueEdit.Remove,"remove-free");
        Assert.Equal(new[]{first,second,third},(await f.Queues.SnapshotAsync("a")).Items.Select(x=>x.Id));
    }

    [Fact]
    public async Task PendingSourceWithdrawalFallsBackToMusicWithoutConsumingItsEntry()
    {
        await using var f=await Fixture.Create();var source=f.Source();var id=await f.Add();
        await f.Library.EditAssetAsync("a",source.Revision,source.AssetId,source.ExpectedVersion,LibraryEditKind.Retire,"retire");
        var planner=f.Queues.Planner(f.Settings())!;var selection=planner.SelectPrepared(0,new HashSet<string>());
        Assert.NotNull(selection);Assert.Null(selection.QueueEntryId);Assert.NotEqual(source.AssetId,selection.Item.Id);
        var view=await f.Queues.SnapshotAsync("a");Assert.Equal(id,Assert.Single(view.Items).Id);Assert.False(view.Items[0].Available);
        planner.ReleaseQueueReservations();
    }

    [Fact]
    public async Task ACommittedEditCannotBeSelectedUntilItsReservationLeaseIsReleased()
    {
        await using var f=await Fixture.Create();await f.Add();var second=await f.Add("beta");
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release=new ManualResetEventSlim();
        void Observe(string station)
        {
            if(station!="a")return;entered.TrySetResult();
            if(!release.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException("Queue publication observer was not released.");
        }
        f.Journal.QueueChanged+=Observe;
        var mutation=Task.Run(()=>f.Queues.EditAsync("a",second,2,QueueEdit.MoveUp,"move-with-held-ack"));
        var planner=f.Queues.Planner(f.Settings())!;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(planner.SelectPrepared(0,new HashSet<string>())!.QueueEntryId);
        }
        finally{release.Set();f.Journal.QueueChanged-=Observe;await mutation;}
        Assert.Equal(second,planner.SelectPrepared(0,new HashSet<string>())!.QueueEntryId);
        planner.ReleaseQueueReservations();
    }

    [Fact]
    public async Task ChangingBytesWithoutChangingTagsInvalidatesTheQueuedSourceStamp()
    {
        await using var f=await Fixture.Create();var source=f.Source();await f.Add();
        var path=Path.Combine(f.Root,"a","alpha.wav");await File.AppendAllTextAsync(path," changed");
        await f.Wait(()=>f.Source().SourceStamp!=source.SourceStamp);
        await f.Wait(()=>f.Library.ResolveQueuedItem("a",f.Journal.PublishedQueue("a")!.Entries[0]) is null);
        Assert.Equal("Pending",Assert.Single(f.Journal.PublishedQueue("a")!.Entries).State);
    }

    [Fact]
    public async Task ReplacingTheLiveFolderCannotAdoptASameNamedFileForAnOldQueueEntry()
    {
        await using var f=await Fixture.Create();await f.Add();
        var other=Path.Combine(f.Root,"replacement");Directory.CreateDirectory(other);await File.WriteAllTextAsync(Path.Combine(other,"alpha.wav"),"different fixture");
        await f.Library.SetRootAsync("a",other,"replacement");await f.Wait(()=>f.Library.ReadyInventory("a").ReadyCount==1);
        Assert.False(Assert.Single((await f.Queues.SnapshotAsync("a")).Items).Available);
        var planner=f.Queues.Planner(f.Settings())!;Assert.Null(planner.Select(0).QueueEntryId);planner.ReleaseQueueReservations();
    }

    [Fact]
    public async Task UnlinkedPrototypeEntriesAreVisibleButNeverSelectedAsAuthorizedMedia()
    {
        await using var f=await Fixture.Create();
        await f.Journal.AppendStationQueueAsync("a",new("unlinked","untrusted.wav","Fixture","Unlinked",DurationSeconds:5),0,"raw");
        var planner=f.Queues.Planner(f.Settings())!;Assert.Null(planner.Select(0).QueueEntryId);
        Assert.False(Assert.Single((await f.Queues.SnapshotAsync("a")).Items).Available);
        await f.Queues.EditAsync("a","raw",1,QueueEdit.Remove,"remove-raw");Assert.Empty((await f.Queues.SnapshotAsync("a")).Items);
        planner.ReleaseQueueReservations();
    }

    [Fact]
    public async Task UnavailableRecoveryIsDeferredAndTheReturningFileKeepsItsAttemptLink()
    {
        await using var f=await Fixture.Create();var id=await f.Add();var entry=Assert.Single(f.Journal.PublishedQueue("a")!.Entries);var item=entry.Item;
        var cp=new PlayoutCheckpoint("a","Playing",3,item,"original",960,1920,true,DateTimeOffset.UtcNow,id);
        await f.Journal.AppendAsync(new("start","original","a","started",item.Id,item.Artist,item.Title,item.Kind,DateTimeOffset.UtcNow,0,QueueEntryId:id),cp);
        var path=item.Path;var content=await File.ReadAllTextAsync(path);var modified=File.GetLastWriteTimeUtc(path);File.Delete(path);
        await f.Wait(()=>f.Library.Page("a","",0).Items.Single(x=>x.Id==item.Id).State=="Missing");
        await f.Wait(()=>f.Library.ResolveQueuedItem("a",f.Journal.PublishedQueue("a")!.Entries[0]) is null);
        await f.Journal.PrepareRecoveryAsync("a");var planner=f.Queues.Planner(f.Settings())!;
        Assert.Null(planner.ResolveQueueRecovery(new(item,id)));
        var deferred=await f.Journal.DeferUnavailableQueueRecoveryAsync("a","original");Assert.Null(deferred.QueueEntryId);Assert.Equal(3,deferred.RotationIndex);
        await File.WriteAllTextAsync(path,content);File.SetLastWriteTimeUtc(path,modified);
        await f.Wait(()=>f.Library.ResolveQueuedItem("a",f.Journal.PublishedQueue("a")!.Entries[0]) is not null);
        var restarted=planner.SelectPrepared(3,new HashSet<string>())!;Assert.Equal(id,restarted.QueueEntryId);Assert.Equal("original",restarted.PreviousOccurrenceId);
        Assert.True(EventJournal.Verify(f.Journal.DatabasePath));planner.ReleaseQueueReservations();
    }

    [Fact]
    public async Task FiniteStationsKeepTheirExistingPlannerAndUnknownChannelsAreRejected()
    {
        await using var f=await Fixture.Create();Assert.Null(f.Queues.Planner(f.Settings() with{Repeat=false}));
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Queues.SnapshotAsync("unknown"));
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Queues.EnqueueAsync("unknown",f.Source(),0,Guid.NewGuid().ToString("N")));
    }
}
