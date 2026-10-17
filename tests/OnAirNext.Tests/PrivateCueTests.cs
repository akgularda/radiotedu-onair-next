using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class PrivateCueTests
{
    private static PrivateCueRequest Request()=>new(Guid.NewGuid().ToString("N"),"station",1,new string('A',64),0,"1:1");
    [Fact]
    public void Only_one_cue_owns_the_decoder_until_confirmed_cleanup()
    {
        var gate=new PrivateCueGate();var first=gate.Acquire(Request());
        Assert.Throws<InvalidOperationException>(()=>gate.Acquire(Request()));
        gate.RequestStop(first.Request.SessionId);Assert.True(first.Token.IsCancellationRequested);
        gate.CleanupFailed(first);Assert.Equal("Cleanup pending",gate.Snapshot().State);
        Assert.Throws<InvalidOperationException>(()=>gate.Acquire(Request()));
        gate.Complete(first);Assert.Equal("Idle",gate.Snapshot().State);
        var next=gate.Acquire(Request());gate.Complete(first);Assert.Equal(next.Request.SessionId,gate.Snapshot().SessionId);gate.Complete(next);
    }
    [Fact]
    public void Stop_before_delayed_open_cannot_start_an_unwanted_preview()
    {
        var gate=new PrivateCueGate();var request=Request();gate.RequestStop(request.SessionId);
        Assert.Throws<InvalidOperationException>(()=>gate.Acquire(request));Assert.Equal("Idle",gate.Snapshot().State);
    }
    [Fact]
    public void Stale_stop_does_not_stop_a_new_selection_or_replay_a_finished_session()
    {
        var gate=new PrivateCueGate();var old=gate.Acquire(Request());gate.Complete(old);
        var next=gate.Acquire(Request());gate.RequestStop(old.Request.SessionId);
        Assert.False(next.Token.IsCancellationRequested);Assert.Throws<InvalidOperationException>(()=>gate.Acquire(old.Request));gate.Complete(next);
    }
    [Theory]
    [InlineData(-1,0)]
    [InlineData(1,-1)]
    public void Invalid_catalog_versions_are_rejected(long revision,long version)
    {Assert.Throws<ArgumentException>(()=>(Request() with{Revision=revision,ExpectedVersion=version}).Validate());}
    [Fact]
    public void Raw_paths_and_missing_asset_ids_cannot_be_cued()
    {
        Assert.Throws<ArgumentException>(()=>(Request() with{AssetId="C:/private/file.wav"}).Validate());
        Assert.Throws<ArgumentException>(()=>(Request() with{AssetId=null!}).Validate());
        Assert.Throws<ArgumentException>(()=>(Request() with{StartSample=-1}).Validate());
        Assert.Throws<ArgumentException>(()=>(Request() with{SessionId="reuse-a-command"}).Validate());
    }
    [Fact]
    public async Task Source_lease_rejects_retirement_changed_content_and_stale_roots()
    {
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RadioTEDU","OnAirNext-Dev","unit-tests",Guid.NewGuid().ToString("N"));
        var folder=Path.Combine(root,"songs");Directory.CreateDirectory(folder);var file=Path.Combine(folder,"source.wav");await File.WriteAllTextAsync(file,"synthetic catalog fixture");
        await using var journal=new EventJournal(Path.Combine(root,"catalog.db"));await journal.InitializeDesiredStateAsync(new("station",[],[],AutoStart:false));
        await using var library=new StationLibrary(journal,"unused",root,["station"],(path,token)=>Task.FromResult(new AudioMetadata("Artist","Cue source",2)));
        await library.InitializeAsync();await library.SetRootAsync("station",folder,"root");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while(library.ReadyInventory("station").ReadyCount==0){library.Wake();await Task.Delay(100,timeout.Token);}
        var page=library.Page("station","",0);var asset=Assert.Single(page.Items);
        var request=Request() with{Revision=page.Root!.Revision,AssetId=asset.Id,ExpectedVersion=asset.EditVersion,ExpectedSourceStamp=asset.SourceStamp};
        using(var source=library.OpenPrivateCue(request)){Assert.Equal(file,source.Item.Path);Assert.Equal(96000,source.TotalSamples);}
        await library.EditAssetAsync("station",request.Revision,asset.Id,asset.EditVersion,LibraryEditKind.Retire,"retire");
        Assert.Throws<InvalidOperationException>(()=>library.OpenPrivateCue(request));
        var retired=library.Page("station","",0).Items.Single();
        await library.EditAssetAsync("station",request.Revision,asset.Id,retired.EditVersion,LibraryEditKind.Restore,"restore");
        var restored=library.Page("station","",0).Items.Single();request=request with{ExpectedVersion=restored.EditVersion};
        await File.AppendAllTextAsync(file," changed");var changed=Record.Exception(()=>library.OpenPrivateCue(request));Assert.True(changed is IOException or InvalidOperationException);
        var other=Path.Combine(root,"other");Directory.CreateDirectory(other);await library.SetRootAsync("station",other,"replace");
        Assert.Throws<InvalidOperationException>(()=>library.OpenPrivateCue(request));
        Assert.Empty(EventJournal.ReadEvents(journal.DatabasePath));
    }
}
