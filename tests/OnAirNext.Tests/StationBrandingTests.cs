using OnAirNext.Engine;
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
    public void RoleKeysCannotCollideWithProgrammeStationsOrOtherRoles()
    {
        Assert.Throws<ArgumentException>(()=>StationBranding.InventoryKinds(["a","a"]));
        Assert.Throws<ArgumentException>(()=>StationBranding.InventoryKinds(["a",StationBranding.InventoryKey("a",ContentKind.Jingle)]));
        Assert.Throws<ArgumentException>(()=>StationBranding.InventoryKey("a",ContentKind.Music));
        Assert.Equal(4,StationBranding.InventoryKinds(["a","b"]).Count);
    }
}
