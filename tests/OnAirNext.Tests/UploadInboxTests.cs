using System.Security.Cryptography;
using System.Text;
using OnAirNext.Engine;
using OnAirNext.UploadProtocol;
using Xunit;

namespace OnAirNext.Tests;

public sealed class UploadInboxTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public string Root {get;}=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RadioTEDU","OnAirNext-Dev","unit-tests",Guid.NewGuid().ToString("N"));
        public EventJournal Journal {get;}
        public StationLibrary Library {get;}
        public UploadInbox Inbox {get;}
        public Fixture(){Journal=new(Path.Combine(Root,"catalog.db"));Library=new(Journal,"unused",Root,["a","b"],(_,_)=>Task.FromResult(new AudioMetadata("Artist","Song",10)));Inbox=new(Journal,Library);}
        public async Task InitializeAsync(){await Journal.InitializeLibraryAsync();foreach(var id in new[]{"a","b"})await Journal.InitializeDesiredStateAsync(new(id,[],[],AutoStart:false));await Inbox.InitializeAsync();}
        public async ValueTask DisposeAsync(){await Library.DisposeAsync();await Journal.DisposeAsync();}
    }
    private static readonly byte[] Bytes=Encoding.UTF8.GetBytes("original fixture bytes");
    private static UploadFileSpec Spec(string name="song.wav")=>new(name,Bytes.Length,Convert.ToHexString(SHA256.HashData(Bytes)));
    private static string Id()=>Guid.NewGuid().ToString("N");
    [Fact]
    public async Task RetainedBytesSurviveRestartAndCanBeDiscardedWithoutChangingOriginals()
    {
        await using var f=new Fixture();await f.InitializeAsync();var id=Id();
        var original=Path.Combine(f.Root,"original.wav");await File.WriteAllBytesAsync(original,Bytes);
        await f.Inbox.BeginAsync("a",id,[Spec()]);await f.Inbox.ReceiveAsync("a",id,0,new MemoryStream(Bytes),CancellationToken.None);
        var resumed=new UploadInbox(f.Journal,f.Library);await resumed.InitializeAsync();
        Assert.True(await resumed.HasFileAsync("a",id,0,CancellationToken.None));Assert.Equal(1,Assert.Single(resumed.Snapshot("a")).ReceivedFiles);
        await resumed.CancelAsync("a",id);Assert.Empty(resumed.Snapshot("a"));Assert.Equal(Bytes,await File.ReadAllBytesAsync(original));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>resumed.BeginAsync("a",id,[Spec()]));
    }
    [Fact]
    public async Task BatchIdentityIsBoundToStationOrderAndContent()
    {
        await using var f=new Fixture();await f.InitializeAsync();var id=Id();
        await f.Inbox.BeginAsync("a",id,[Spec()]);await f.Inbox.BeginAsync("a",id,[Spec()]);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Inbox.BeginAsync("b",id,[Spec()]));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Inbox.BeginAsync("a",id,[Spec("other.wav")]));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Inbox.CancelAsync("b",id));Assert.Single(f.Inbox.Snapshot("a"));
    }
    [Fact]
    public async Task TruncationAndHashMismatchNeverPublishAFileAndSameBatchCanRetry()
    {
        await using var f=new Fixture();await f.InitializeAsync();var id=Id();await f.Inbox.BeginAsync("a",id,[Spec()]);
        await Assert.ThrowsAsync<EndOfStreamException>(()=>f.Inbox.ReceiveAsync("a",id,0,new MemoryStream(Bytes[..^1]),CancellationToken.None));
        Assert.False(await f.Inbox.HasFileAsync("a",id,0,CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(()=>f.Inbox.ReceiveAsync("a",id,0,new MemoryStream(new byte[Bytes.Length]),CancellationToken.None));
        Assert.Empty(Directory.GetFiles(Path.Combine(f.Root,"upload-inbox"),"*",SearchOption.AllDirectories));
        await f.Inbox.ReceiveAsync("a",id,0,new MemoryStream(Bytes),CancellationToken.None);Assert.True(await f.Inbox.HasFileAsync("a",id,0,CancellationToken.None));
    }
    [Fact]
    public async Task FourRetainedBatchesBoundStorageAndCancellationReleasesAdmission()
    {
        await using var f=new Fixture();await f.InitializeAsync();var ids=Enumerable.Range(0,4).Select(_=>Id()).ToArray();
        foreach(var id in ids)await f.Inbox.BeginAsync("a",id,[Spec()]);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Inbox.BeginAsync("a",Id(),[Spec()]));
        await f.Inbox.CancelAsync("a",ids[0]);await f.Inbox.BeginAsync("a",Id(),[Spec()]);Assert.Equal(4,f.Inbox.Snapshot("a").Length);
    }
    [Theory]
    [InlineData("../song.wav")][InlineData("folder/song.wav")][InlineData("CON.wav")][InlineData("LPT1.wav")][InlineData("song.wav ")][InlineData("song.txt")][InlineData("a:b.wav")]
    public async Task UnsafeNamesCannotEnterPrivateStorage(string name)
    {
        await using var f=new Fixture();await f.InitializeAsync();await Assert.ThrowsAsync<ArgumentException>(()=>f.Inbox.BeginAsync("a",Id(),[Spec(name)]));Assert.Empty(f.Inbox.Snapshot("a"));
    }
    [Fact]
    public async Task SizeCountAndMissingIdentitiesFailBeforeRegistration()
    {
        await using var f=new Fixture();await f.InitializeAsync();
        foreach(var files in new[]{Array.Empty<UploadFileSpec>(),Enumerable.Repeat(Spec(),65).ToArray(),new[]{Spec() with{Bytes=UploadInbox.MaximumBytes+1}},new[]{Spec() with{Sha256=null!}},new[]{Spec() with{Name=null!}},new UploadFileSpec[]{null!}})
            await Assert.ThrowsAsync<ArgumentException>(()=>f.Inbox.BeginAsync("a",Id(),files));
        Assert.Empty(f.Inbox.Snapshot("a"));
    }
    [Fact]
    public async Task RootChangeFencesNewCommitButNotAlreadyCommittedAcknowledgementReplay()
    {
        await using var f=new Fixture();await f.InitializeAsync();var id=Id();await f.Inbox.BeginAsync("a",id,[Spec()]);
        var root=Path.Combine(f.Root,"music","managed");
        await f.Journal.SetLibraryRootAsync("a",root,"change");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Journal.QueueLibraryImportAsync("a",[Path.Combine(f.Root,"staged.wav")],root,id,uploaded:true));
        await f.Inbox.CancelAsync("a",id);var second=Id();await f.Inbox.BeginAsync("a",second,[Spec()]);
        var paths=new[]{Path.Combine(f.Root,"staged.wav")};Assert.True(await f.Journal.QueueLibraryImportAsync("a",paths,root,second,uploaded:true));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Inbox.CancelAsync("a",second));
        await f.Journal.SetLibraryRootAsync("a",Path.Combine(f.Root,"other"),"later");
        Assert.False(await f.Journal.QueueLibraryImportAsync("a",paths,root,second,uploaded:true));
        await f.Journal.FinishLibraryImportAsync(second,null);
        await new UploadInbox(f.Journal,f.Library).InitializeAsync();Assert.Empty(f.Inbox.Snapshot("a"));
        Assert.False(await f.Journal.QueueLibraryImportAsync("a",paths,root,second,uploaded:true));
    }
    [Fact]
    public async Task AdmissionCannotCaptureARootChangedAfterManagedStorageWasChecked()
    {
        await using var f=new Fixture();await f.InitializeAsync();var id=Id();var payload=System.Text.Json.JsonSerializer.Serialize(new[]{Spec()});
        await f.Journal.SetLibraryRootAsync("a",Path.Combine(f.Root,"music"),"changed");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Journal.RegisterLibraryUploadAsync("a",id,payload,expectedRevision:0));
        Assert.Null(f.Journal.LibraryUpload(id));
        await f.Journal.RegisterLibraryUploadAsync("a",id,payload,expectedRevision:1);
        Assert.Equal(1,f.Journal.LibraryUpload(id)!.Revision);
    }
    [Fact]
    public async Task ExternalLiveFoldersRejectUploadAdmission()
    {
        await using var f=new Fixture();await f.InitializeAsync();
        await f.Journal.SetLibraryRootAsync("a",f.Root+"-external","folder");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Inbox.BeginAsync("a",Id(),[Spec()]));Assert.Empty(f.Inbox.Snapshot("a"));
    }
}
