using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class BrandingConfigurationTests
{
    private static string Database()
    {
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RadioTEDU","OnAirNext-Dev","unit-tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);return Path.Combine(root,"branding.db");
    }
    private static BrandingRules Enabled=>new(new(true,2),new(true,3));
    [Fact]
    public async Task SettingsAreDisabledByDefaultAndDurablePerChannelWithoutResettingCadence()
    {
        var path=Database();
        await using(var journal=new EventJournal(path))
        {
            await journal.InitializeBrandingAsync();var original=await journal.BrandingConfigurationAsync("a");
            Assert.Equal(0,original.Version);Assert.Equal(BrandingRules.Disabled,original.Rules);
            var ended=new PlaybackEvent("event","occurrence","a","ended","m","Fixture","Song",ContentKind.Music,DateTimeOffset.UtcNow,48000);
            await journal.AppendAsync(ended);var progress=await journal.BrandingProgressAsync("a");
            var saved=await journal.SaveBrandingConfigurationAsync("a",0,Enabled,"save-a");
            Assert.Equal(1,saved.Version);Assert.Equal(progress,await journal.BrandingProgressAsync("a"));
            Assert.Equal(BrandingRules.Disabled,(await journal.BrandingConfigurationAsync("b")).Rules);
        }
        await using(var reopened=new EventJournal(path))
        {
            await reopened.InitializeBrandingAsync();Assert.Equal(Enabled,(await reopened.BrandingConfigurationAsync("a")).Rules);
            Assert.Equal(1,(await reopened.BrandingProgressAsync("a")).CompletedMusic);
        }
    }
    [Fact]
    public async Task RetriesReturnTheirOriginalReceiptWithoutRewindingLaterSettings()
    {
        await using var journal=new EventJournal(Database());await journal.InitializeBrandingAsync();
        var first=await journal.SaveBrandingConfigurationAsync("a",0,Enabled,"first");
        var disabled=await journal.SaveBrandingConfigurationAsync("a",1,BrandingRules.Disabled,"second");
        Assert.Equal(first,await journal.SaveBrandingConfigurationAsync("a",0,Enabled,"first"));
        Assert.Equal(disabled,await journal.BrandingConfigurationAsync("a"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>journal.SaveBrandingConfigurationAsync("b",0,Enabled,"first"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>journal.SaveBrandingConfigurationAsync("a",1,Enabled,"stale"));
        Assert.Equal(disabled,await journal.BrandingConfigurationAsync("a"));
    }
    [Fact]
    public async Task ConcurrentEditsCannotOverwriteAnUnreviewedVersion()
    {
        await using var journal=new EventJournal(Database());await journal.InitializeBrandingAsync();
        var results=await Task.WhenAll(Enumerable.Range(0,2).Select(async i=>
        {
            try{await journal.SaveBrandingConfigurationAsync("a",0,i==0?Enabled:BrandingRules.Disabled,"request-"+i);return true;}
            catch(InvalidOperationException){return false;}
        }));
        Assert.Single(results.Where(x=>x));Assert.Equal(1,(await journal.BrandingConfigurationAsync("a")).Version);
    }
}
