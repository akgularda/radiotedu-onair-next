using OnAirNext.Engine;
using OnAirNext.UploadProtocol;
using System.Security.Cryptography;
using Xunit;

namespace OnAirNext.Tests;

public sealed class StationBrandingTests
{
    private static string Root()
    {
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RadioTEDU","OnAirNext-Dev","unit-tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);return root;
    }
    [Fact]
    public async Task RoleFoldersPublishValidatedPoolsWithoutArmingRulesOrReplayingRetriedActivation()
    {
        var root=Root();var jingles=Path.Combine(root,"jingles");var sweepers=Path.Combine(root,"sweepers");
        Directory.CreateDirectory(jingles);Directory.CreateDirectory(sweepers);
        await File.WriteAllTextAsync(Path.Combine(jingles,"identifier.wav"),"fixture");
        await File.WriteAllTextAsync(Path.Combine(sweepers,"sweeper.wav"),"fixture");
        await using var journal=new EventJournal(Path.Combine(root,"catalog.db"));
        foreach(var id in new[]{"a","b"})await journal.InitializeDesiredStateAsync(new(id,[],[],AutoStart:false));
        var kinds=StationBranding.InventoryKinds(["a","b"]);
        await using var library=new StationLibrary(journal,"unused",root,new[]{"a","b"}.Concat(kinds.Keys),
            (path,_)=>Task.FromResult(new AudioMetadata("Fixture",Path.GetFileNameWithoutExtension(path),.2)),kinds);
        using var branding=new StationBranding(journal,library,["a","b"]);
        await branding.InitializeAsync();await library.InitializeAsync();
        var first=await branding.SnapshotAsync("a");Assert.Equal(BrandingRules.Disabled,first.Configuration.Rules);
        await library.SetRootAsync(first.JingleInventory,jingles,"jingle-folder");
        await library.SetRootAsync(first.SweeperInventory,sweepers,"sweeper-folder");
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while(branding.Source("a").Snapshot() is not{JingleCount:1,SweeperCount:1})
        {library.Wake();await Task.Delay(100,deadline.Token);}
        var due=new BrandingCadence("a",2,3);
        Assert.Null(branding.Source("a").Snapshot()!.Decide(due).Item);
        var enabled=new BrandingRules(new(true,2),new(true,3));
        var saved=await branding.SaveAsync("a",0,enabled,"enable");
        var pool=branding.Source("a").Snapshot();Assert.Equal(ContentKind.Jingle,pool!.Decide(due).Item!.Kind);
        Assert.True(await journal.RequiresBrandingConfigurationSupportAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>journal.RequireRecoveryReaderAsync(null));
        await journal.RequireRecoveryReaderAsync([RecoveryCompatibility.HeldRotationFeature]);
        Assert.Equal(saved,await branding.SaveAsync("a",0,enabled,"enable"));Assert.Same(pool,branding.Source("a").Snapshot());
        Assert.Equal(BrandingRules.Disabled,(await branding.SnapshotAsync("b")).Configuration.Rules);
        var page=library.Page(first.JingleInventory,"",0);var asset=Assert.Single(page.Items);
        await library.EditAssetAsync(first.JingleInventory,page.Root!.Revision,asset.Id,asset.EditVersion,LibraryEditKind.Retire,"retire");
        Assert.Equal(0,branding.Source("a").Snapshot()!.JingleCount);
        Assert.NotNull((await branding.SnapshotAsync("a")).Warning);
        var disabled=await branding.SaveAsync("a",1,BrandingRules.Disabled,"disable");
        var disabledPool=branding.Source("a").Snapshot();
        await branding.SaveAsync("a",0,enabled,"enable");
        Assert.Same(disabledPool,branding.Source("a").Snapshot());
        Assert.Equal(disabled,(await branding.SnapshotAsync("a")).Configuration);
        Assert.False(await journal.RequiresBrandingConfigurationSupportAsync());await journal.RequireRecoveryReaderAsync(null);
    }
    [Fact]
    public async Task ManagedRoleUploadUsesItsParentWithoutCreatingAnotherProgrammeStation()
    {
        var root=Root();await using var journal=new EventJournal(Path.Combine(root,"catalog.db"));
        await journal.InitializeDesiredStateAsync(new("a",[],[],AutoStart:false));
        var kinds=StationBranding.InventoryKinds(["a"]);
        await using var library=new StationLibrary(journal,"unused",root,new[]{"a"}.Concat(kinds.Keys),
            (_,_)=>Task.FromResult(new AudioMetadata("Fixture","Identifier",.2)),kinds);
        using var branding=new StationBranding(journal,library,["a"]);await branding.InitializeAsync();await library.InitializeAsync();
        var key=StationBranding.InventoryKey("a",ContentKind.Jingle);var inbox=new UploadInbox(journal,library);await inbox.InitializeAsync();
        var bytes="synthetic retained upload bytes"u8.ToArray();var id=Guid.NewGuid().ToString("N");
        await inbox.BeginAsync(key,id,[new UploadFileSpec("identifier.wav",bytes.Length,Convert.ToHexString(SHA256.HashData(bytes)))]);
        await inbox.ReceiveAsync(key,id,0,new MemoryStream(bytes),CancellationToken.None);
        await inbox.CommitAsync(key,id,CancellationToken.None);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while(library.ReadyInventory(key).ReadyCount!=1){library.Wake();await Task.Delay(100,deadline.Token);}
        Assert.Equal(ContentKind.Jingle,Assert.Single(library.ReadyInventory(key).Items).Kind);
        Assert.Null(await journal.DesiredStateAsync(key));Assert.False((await journal.DesiredStateAsync("a"))!.RequestedRunning);
        Assert.Equal(BrandingRules.Disabled,(await branding.SnapshotAsync("a")).Configuration.Rules);
    }
    [Fact]
    public async Task ARegisteredRoleCannotAcceptRootsOrUploadsWithoutAKnownParent()
    {
        var root=Root();await using var journal=new EventJournal(Path.Combine(root,"catalog.db"));
        var kinds=StationBranding.InventoryKinds(["a"]);
        await using var library=new StationLibrary(journal,"unused",root,new[]{"a"}.Concat(kinds.Keys),
            (_,_)=>Task.FromResult(new AudioMetadata("Fixture","Identifier",.2)),kinds);
        using var branding=new StationBranding(journal,library,["a"]);await branding.InitializeAsync();await library.InitializeAsync();
        var key=StationBranding.InventoryKey("a",ContentKind.Jingle);var inbox=new UploadInbox(journal,library);await inbox.InitializeAsync();
        await Assert.ThrowsAsync<ArgumentException>(()=>library.SetRootAsync(key,Path.Combine(root,"jingles"),"folder"));
        await Assert.ThrowsAsync<ArgumentException>(()=>library.ImportAsync(key,[Path.Combine(root,"identifier.wav")],"import"));
        var bytes="synthetic upload"u8.ToArray();var id=Guid.NewGuid().ToString("N");var files=new[]{new UploadFileSpec("identifier.wav",bytes.Length,Convert.ToHexString(SHA256.HashData(bytes)))};
        await Assert.ThrowsAsync<ArgumentException>(()=>inbox.BeginAsync(key,id,files));
        Assert.Empty(journal.LibraryRoots());
        await journal.InitializeDesiredStateAsync(new("a",[],[],AutoStart:false));
        Assert.Equal(key,(await inbox.BeginAsync(key,id,files)).StationId);
        Assert.Null(await journal.DesiredStateAsync(key));
    }
    [Fact]
    public void RoleKeysCannotCollideWithProgrammeStationsOrOtherRoles()
    {
        Assert.Throws<ArgumentException>(()=>StationBranding.InventoryKinds(["a","a"]));
        Assert.Throws<ArgumentException>(()=>StationBranding.InventoryKinds(["a",StationBranding.InventoryKey("a",ContentKind.Jingle)]));
        Assert.Throws<ArgumentException>(()=>StationBranding.InventoryKey("a",ContentKind.Music));
        Assert.Equal(4,StationBranding.InventoryKinds(["a","b"]).Count);
    }
}
