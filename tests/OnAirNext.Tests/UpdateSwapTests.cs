using System.Text.Json;
using OnAirNext.Installation;
using Xunit;

namespace OnAirNext.Tests;

public sealed class UpdateSwapTests
{
    private sealed class Fixture
    {
        public string Root { get; }=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RadioTEDU","OnAirNext-Dev","unit-tests",Guid.NewGuid().ToString("N"));
        public UpdateSwap Swap { get; private set; }=null!;
        public UpdateImage Old { get; private set; }=null!;
        public UpdateImage Next { get; private set; }=null!;
        public string DataPath=>Path.Combine(Root,"data","playback.db");
        public async Task CreateAsync()
        {
            Directory.CreateDirectory(Root);Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);await File.WriteAllTextAsync(DataPath,"current queue and history");
            var installed=Path.Combine(Root,"Application");Old=await ImageAsync(installed,'a');
            var id=Guid.NewGuid().ToString("N");Next=await ImageAsync(installed+".candidate-"+id,'b');
            Swap=new(installed,id,Old,Next);
        }
        private static async Task<UpdateImage> ImageAsync(string root,char version)
        {
            var files=new List<SetupFile>();
            foreach(var path in new[]{"bin/OnAirNext.Host.exe","console/OnAirNext.Web.exe","setup/OnAirNext.Setup.exe","console/wwwroot/index.html"})
            {
                var full=SetupPaths.Resolve(root,path);Directory.CreateDirectory(Path.GetDirectoryName(full)!);await File.WriteAllTextAsync(full,version+" "+path);
                files.Add(new(path,new FileInfo(full).Length,await SetupPaths.HashAsync(full)));
            }
            var runtime=new List<SetupFile>();Directory.CreateDirectory(Path.Combine(root,"tools"));
            foreach(var name in new[]{"ffmpeg.exe","fdk.dll"})
            {
                var full=Path.Combine(root,"tools",name);await File.WriteAllTextAsync(full,"unchanged encoder "+name);runtime.Add(new(name,new FileInfo(full).Length,await SetupPaths.HashAsync(full)));
            }
            var manifest=Path.Combine(root,"bundle.json");await File.WriteAllTextAsync(manifest,JsonSerializer.Serialize(new SetupBundle(new string(version,40),files.ToArray())));
            return new(await SetupPaths.HashAsync(manifest),runtime.ToArray());
        }
    }
    private static Task Stopped()=>Task.CompletedTask;
    private static Task Checkpoint(string phase)=>Task.CompletedTask;
    [Fact]
    public async Task ApplicationRoundTripPreservesEncoderAndCurrentDataAndBothImages()
    {
        var f=new Fixture();await f.CreateAsync();Assert.Equal(UpdateDiskState.Prepared,await f.Swap.InspectAsync());
        await f.Swap.ApplyAsync(Stopped,Checkpoint);await f.Swap.ApplyAsync(Stopped,Checkpoint);
        Assert.Equal(UpdateDiskState.Applied,await f.Swap.InspectAsync());
        await File.WriteAllTextAsync(f.DataPath,"later queue and immutable attempts");
        await f.Swap.RollbackAsync(Stopped,Checkpoint);await f.Swap.RollbackAsync(Stopped,Checkpoint);
        Assert.Equal(UpdateDiskState.Prepared,await f.Swap.InspectAsync());
        Assert.Equal("later queue and immutable attempts",await File.ReadAllTextAsync(f.DataPath));
        Assert.True(Directory.Exists(f.Swap.CandidateRoot));Assert.False(Directory.Exists(f.Swap.PreviousRoot));
        Assert.Equal(f.Old.BundleSha256,await SetupPaths.HashAsync(Path.Combine(f.Swap.InstallRoot,"bundle.json")));
    }
    [Theory]
    [InlineData("Original application retained")][InlineData("Application activated")]
    public async Task ApplyCanResumeAfterEitherCompletedRename(string crashAt)
    {
        var f=new Fixture();await f.CreateAsync();
        Task Crash(string phase)=>phase==crashAt?Task.FromException(new IOException("simulated power interruption")):Task.CompletedTask;
        await Assert.ThrowsAsync<IOException>(()=>f.Swap.ApplyAsync(Stopped,Crash));
        var reopened=new UpdateSwap(f.Swap.InstallRoot,Path.GetFileName(f.Swap.CandidateRoot).Split(".candidate-")[1],f.Old,f.Next);
        await reopened.ApplyAsync(Stopped,Checkpoint);Assert.Equal(UpdateDiskState.Applied,await reopened.InspectAsync());
        Assert.Equal("current queue and history",await File.ReadAllTextAsync(f.DataPath));
    }
    [Theory]
    [InlineData("Replacement application retained")][InlineData("Original application restored")]
    public async Task RollbackCanResumeAfterEitherCompletedRename(string crashAt)
    {
        var f=new Fixture();await f.CreateAsync();await f.Swap.ApplyAsync(Stopped,Checkpoint);
        Task Crash(string phase)=>phase==crashAt?Task.FromException(new IOException("simulated power interruption")):Task.CompletedTask;
        await Assert.ThrowsAsync<IOException>(()=>f.Swap.RollbackAsync(Stopped,Crash));
        await f.Swap.RollbackAsync(Stopped,Checkpoint);Assert.Equal(UpdateDiskState.Prepared,await f.Swap.InspectAsync());
    }
    [Fact]
    public async Task FenceIsRecheckedBeforeEachRenameAndFailureRetainsRecoverableBytes()
    {
        var f=new Fixture();await f.CreateAsync();var calls=0;
        Task Fence()=>++calls==2?Task.FromException(new InvalidOperationException("service reclaimed")):Task.CompletedTask;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Swap.ApplyAsync(Fence,Checkpoint));
        Assert.Equal(UpdateDiskState.Released,await f.Swap.InspectAsync());
        await f.Swap.RollbackAsync(Stopped,Checkpoint);Assert.Equal(UpdateDiskState.Prepared,await f.Swap.InspectAsync());Assert.Equal(2,calls);
    }
    [Fact]
    public async Task TamperedCandidateOrRetainedOriginalCannotReplaceApplication()
    {
        var f=new Fixture();await f.CreateAsync();await File.WriteAllTextAsync(Path.Combine(f.Swap.CandidateRoot,"bin","OnAirNext.Host.exe"),"unreviewed");
        await Assert.ThrowsAsync<IOException>(()=>f.Swap.ApplyAsync(Stopped,Checkpoint));Assert.True(Directory.Exists(f.Swap.InstallRoot));Assert.False(Directory.Exists(f.Swap.PreviousRoot));
        var g=new Fixture();await g.CreateAsync();await g.Swap.ApplyAsync(Stopped,Checkpoint);
        await File.WriteAllTextAsync(Path.Combine(g.Swap.PreviousRoot,"bin","OnAirNext.Host.exe"),"tampered backup");
        await Assert.ThrowsAsync<IOException>(()=>g.Swap.RollbackAsync(Stopped,Checkpoint));Assert.True(Directory.Exists(g.Swap.InstallRoot));
    }
    [Fact]
    public async Task UnknownFilesAndConflictingLayoutsFailWithoutDeletingAnything()
    {
        var f=new Fixture();await f.CreateAsync();var unknown=Path.Combine(f.Swap.InstallRoot,"operator-notes.txt");await File.WriteAllTextAsync(unknown,"retain me");
        await Assert.ThrowsAsync<IOException>(()=>f.Swap.ApplyAsync(Stopped,Checkpoint));Assert.Equal("retain me",await File.ReadAllTextAsync(unknown));
        var g=new Fixture();await g.CreateAsync();Directory.CreateDirectory(g.Swap.PreviousRoot);
        await Assert.ThrowsAsync<IOException>(()=>g.Swap.InspectAsync());Assert.True(Directory.Exists(g.Swap.CandidateRoot));Assert.True(Directory.Exists(g.Swap.InstallRoot));
    }
    [Fact]
    public async Task ReviewCannotChangeRuntimeOrUseTraversalOperationId()
    {
        var f=new Fixture();await f.CreateAsync();
        Assert.Throws<ArgumentException>(()=>new UpdateSwap(f.Swap.InstallRoot,"../other",f.Old,f.Next));
        Assert.Throws<ArgumentException>(()=>new UpdateSwap(f.Swap.InstallRoot,Guid.NewGuid().ToString("N"),f.Old,f.Next with{RuntimeFiles=[f.Next.RuntimeFiles[0] with{Sha256=new string('c',64)},f.Next.RuntimeFiles[1]]}));
        Assert.Throws<ArgumentException>(()=>new UpdateSwap(f.Swap.InstallRoot,Guid.NewGuid().ToString("N"),f.Old,f.Old));
    }
    [Fact]
    public async Task CancellationBeforeActivationRetainsOriginalAndDoesNotConsumeCandidate()
    {
        var f=new Fixture();await f.CreateAsync();using var stop=new CancellationTokenSource();
        Task Cancel(string phase){if(phase=="Original application retained")stop.Cancel();return Task.CompletedTask;}
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Swap.ApplyAsync(Stopped,Cancel,stop.Token));
        Assert.Equal(UpdateDiskState.Released,await f.Swap.InspectAsync());await f.Swap.RollbackAsync(Stopped,Checkpoint);Assert.Equal(UpdateDiskState.Prepared,await f.Swap.InspectAsync());
    }
    private static async Task<(UpdateOperationStore Store,string Id,string Hash,string Folder)> OperationAsync(Fixture f)
    {
        var folder=Path.Combine(f.Root,"protected-operations");Directory.CreateDirectory(folder);
        var store=new UpdateOperationStore(f.Swap.InstallRoot,folder);var id=Path.GetFileName(f.Swap.CandidateRoot).Split(".candidate-")[1];
        var review=new UpdateSwapReview(id,f.Old,f.Next,DateTimeOffset.UtcNow);var hash=await store.SaveReviewAsync(review);
        Assert.Equal(hash,await store.SaveReviewAsync(review));return(store,id,hash,folder);
    }
    [Fact]
    public async Task DurableRollbackDirectionSurvivesCrashAndRejectsStaleApplyRetries()
    {
        var f=new Fixture();await f.CreateAsync();var operation=await OperationAsync(f);
        await operation.Store.RunAsync(operation.Id,operation.Hash,UpdateDirection.Apply,Stopped);
        Task Crash(string phase)=>phase=="Replacement application retained"?Task.FromException(new IOException("simulated crash")):Task.CompletedTask;
        await Assert.ThrowsAsync<IOException>(()=>operation.Store.RunAsync(operation.Id,operation.Hash,UpdateDirection.Rollback,Stopped,Crash));
        var reopened=new UpdateOperationStore(f.Swap.InstallRoot,operation.Folder);
        var intent=await reopened.ReadIntentAsync(operation.Id,operation.Hash);Assert.NotNull(intent);Assert.Equal(UpdateDirection.Rollback,intent.Direction);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>reopened.RunAsync(operation.Id,operation.Hash,UpdateDirection.Apply,Stopped));
        Assert.Equal(UpdateDiskState.Released,await f.Swap.InspectAsync());
        var result=await reopened.RunAsync(operation.Id,operation.Hash,UpdateDirection.Rollback,Stopped);
        Assert.Equal("Original application files restored",result.Phase);Assert.Equal(UpdateDiskState.Prepared,await f.Swap.InspectAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>reopened.RunAsync(operation.Id,operation.Hash,UpdateDirection.Apply,Stopped));
    }
    [Fact]
    public async Task DirectionIsCommittedBeforeAnyMutationIncludingFirstRollbackCheckpoint()
    {
        var f=new Fixture();await f.CreateAsync();var operation=await OperationAsync(f);
        Task Crash(string phase)=>Task.FromException(new IOException("crash after durable request"));
        await Assert.ThrowsAsync<IOException>(()=>operation.Store.RunAsync(operation.Id,operation.Hash,UpdateDirection.Rollback,Stopped,Crash));
        Assert.Equal(UpdateDiskState.Prepared,await f.Swap.InspectAsync());
        var saved=await operation.Store.ReadIntentAsync(operation.Id,operation.Hash);Assert.NotNull(saved);Assert.Equal(UpdateDirection.Rollback,saved.Direction);Assert.Equal("Rollback requested",saved.Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>operation.Store.RunAsync(operation.Id,operation.Hash,UpdateDirection.Apply,Stopped));
    }
    [Fact]
    public async Task ChangedReviewOrDifferentIntentProofCannotAuthorizeFileChanges()
    {
        var f=new Fixture();await f.CreateAsync();var operation=await OperationAsync(f);
        var path=Path.Combine(operation.Folder,operation.Id+".review.json");await File.AppendAllTextAsync(path," ");
        await Assert.ThrowsAsync<IOException>(()=>operation.Store.RunAsync(operation.Id,operation.Hash,UpdateDirection.Apply,Stopped));
        Assert.Null(await operation.Store.ReadIntentAsync(operation.Id,operation.Hash));Assert.Equal(UpdateDiskState.Prepared,await f.Swap.InspectAsync());
        var g=new Fixture();await g.CreateAsync();var other=await OperationAsync(g);
        await other.Store.RunAsync(other.Id,other.Hash,UpdateDirection.Apply,Stopped);
        await Assert.ThrowsAsync<IOException>(()=>other.Store.ReadIntentAsync(other.Id,new string('e',64)));
        Assert.Equal(UpdateDiskState.Applied,await g.Swap.InspectAsync());
    }
    [Fact]
    public async Task ConcurrentOperationIsDeniedWhileCommittedDirectionIsBeingExecuted()
    {
        var f=new Fixture();await f.CreateAsync();var operation=await OperationAsync(f);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Pause(string phase){if(phase=="Update requested"){entered.SetResult();await release.Task;}}
        var first=operation.Store.RunAsync(operation.Id,operation.Hash,UpdateDirection.Apply,Stopped,Pause);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try{await Assert.ThrowsAsync<IOException>(()=>operation.Store.RunAsync(operation.Id,operation.Hash,UpdateDirection.Rollback,Stopped));}
        finally{release.SetResult();}
        Assert.Equal(UpdateDirection.Apply,(await first).Direction);Assert.Equal(UpdateDiskState.Applied,await f.Swap.InspectAsync());
    }
    [Fact]
    public async Task OrphanPartialRecordNeverOverridesCommittedRollbackDirection()
    {
        var f=new Fixture();await f.CreateAsync();var operation=await OperationAsync(f);
        await operation.Store.RunAsync(operation.Id,operation.Hash,UpdateDirection.Rollback,Stopped);
        await File.WriteAllTextAsync(Path.Combine(operation.Folder,operation.Id+".intent.json.partial-"+Guid.NewGuid().ToString("N")),JsonSerializer.Serialize(new UpdateIntent(operation.Hash,UpdateDirection.Apply,"newer but uncommitted",999,DateTimeOffset.UtcNow)));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>operation.Store.RunAsync(operation.Id,operation.Hash,UpdateDirection.Apply,Stopped));
        var saved=await operation.Store.ReadIntentAsync(operation.Id,operation.Hash);Assert.NotNull(saved);Assert.Equal(UpdateDirection.Rollback,saved.Direction);Assert.Equal(UpdateDiskState.Prepared,await f.Swap.InspectAsync());
    }
    [Fact]
    public async Task PollingObservesCompleteMonotonicIntentWhileAtomicReplacementContinues()
    {
        var f=new Fixture();await f.CreateAsync();var operation=await OperationAsync(f);
        await operation.Store.RunAsync(operation.Id,operation.Hash,UpdateDirection.Rollback,Stopped);
        var snapshots=0;long revision=0;var reader=new UpdateOperationStore(f.Swap.InstallRoot,operation.Folder);
        var writer=Task.Run(async()=>
        {
            for(var i=0;i<40;i++)await operation.Store.RunAsync(operation.Id,operation.Hash,UpdateDirection.Rollback,Stopped);
        });
        do
        {
            var snapshot=await reader.ReadIntentAsync(operation.Id,operation.Hash);Assert.NotNull(snapshot);
            Assert.Equal(UpdateDirection.Rollback,snapshot.Direction);Assert.True(snapshot.Revision>=revision);
            revision=snapshot.Revision;snapshots++;await Task.Yield();
        }while(!writer.IsCompleted);
        await writer;Assert.True(snapshots>0);
        var latest=await operation.Store.ReadIntentAsync(operation.Id,operation.Hash);Assert.NotNull(latest);
        Assert.True(latest.Revision>=revision);Assert.Equal(82,latest.Revision);
        Assert.Equal(UpdateDiskState.Prepared,await f.Swap.InspectAsync());
    }

    [Fact]
    public async Task SnapshotWaitHonorsCancellationAndMissingGuardCannotHideAnExistingIntent()
    {
        var f=new Fixture();await f.CreateAsync();var operation=await OperationAsync(f);
        await operation.Store.RunAsync(operation.Id,operation.Hash,UpdateDirection.Rollback,Stopped);
        var guard=Path.Combine(operation.Folder,operation.Id+".snapshot.lock");
        await using(var held=new FileStream(guard,FileMode.Open,FileAccess.Read,FileShare.None))
        {
            using var cancelled=new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>operation.Store.ReadIntentAsync(operation.Id,operation.Hash,cancelled.Token));
        }
        Assert.NotNull(await operation.Store.ReadIntentAsync(operation.Id,operation.Hash));
        File.Delete(guard);
        await Assert.ThrowsAsync<IOException>(()=>operation.Store.ReadIntentAsync(operation.Id,operation.Hash));
        Assert.Equal(UpdateDiskState.Prepared,await f.Swap.InspectAsync());
    }

}
