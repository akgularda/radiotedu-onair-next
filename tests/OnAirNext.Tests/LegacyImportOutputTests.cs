using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed partial class LegacyImportTests
{
    private static void ChangeFixtureOutput(string source,string assignment,object value)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(source,"cleanroom.db"), Pooling = false }.ToString());
        connection.Open();using var command = connection.CreateCommand();command.CommandText = "UPDATE station_outputs SET " + assignment + " WHERE station_id=4";
        command.Parameters.AddWithValue("$value",value);command.ExecuteNonQuery();
    }
    [Fact]
    public async Task DisabledIncompleteDestinationDoesNotBlockOtherStationsAndCannotBeSelected()
    {
        var fixture=Fixture();var database=Path.Combine(fixture.Source,"cleanroom.db");
        using(var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=database,Pooling=false}.ToString()))
        {
            connection.Open();using var command=connection.CreateCommand();command.CommandText="""
                INSERT INTO stations VALUES(7,'Unconfigured external station');
                INSERT INTO station_outputs VALUES(7,'127.0.0.1',18980,'','aac_low_192',192,0,'synthetic-disabled-secret');
                """;command.ExecuteNonQuery();
        }
        var before=SHA256.HashData(File.ReadAllBytes(database));var id=Guid.NewGuid().ToString("N");
        await using(var previews=new LegacyImportPreviews(fixture.Target,["4","9","spare"],()=>true))
        {
            previews.Initialize();previews.Start(id,fixture.Source);var ready=await Finished(previews);
            Assert.Equal("Ready for mapping",ready.State);Assert.NotNull(ready.Backup);
            var disabled=Assert.Single(ready.Stations!,s=>s.Id==7);Assert.NotNull(disabled.PrimaryOutput);
            Assert.False(disabled.PrimaryOutput.Enabled);Assert.Empty(disabled.PrimaryOutput.Mount);
            Assert.Contains(disabled.Warnings,w=>w.Contains("Disabled primary output",StringComparison.Ordinal)&&w.Contains("mount",StringComparison.Ordinal));
            Assert.Throws<ArgumentException>(()=>previews.SaveMappings(id,0,[new(4,"4"),new(9,"9"),new(7,"spare")]));
            var mapped=previews.SaveMappings(id,0,[new(4,"4"),new(9,"9"),new(7,null)]);Assert.Equal("Mapping saved",mapped.State);
            var saved=File.ReadAllText(Path.Combine(fixture.Target,"migration-previews",id+".json"));Assert.DoesNotContain("synthetic-disabled-secret",saved);
        }
        await using var recovered=new LegacyImportPreviews(fixture.Target,["4","9","spare"],()=>true);recovered.Initialize();
        var readback=recovered.Snapshot()!;Assert.Equal("Mapping saved",readback.State);Assert.Null(Assert.Single(readback.Mappings!,m=>m.LegacyStationId==7).TargetStationId);
        Assert.Equal(before,SHA256.HashData(File.ReadAllBytes(database)));
    }
    [Theory]
    [InlineData("host")][InlineData("port")][InlineData("mount")]
    public async Task MalformedEnabledDestinationStillFailsWithStationAndFieldDiagnostic(string field)
    {
        var fixture=Fixture();
        ChangeFixtureOutput(fixture.Source,field switch{"host"=>"icecast_host=$value","port"=>"icecast_port=$value",_=>"icecast_mount=$value"},field=="port"?(object)0:"");
        var before=SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture.Source,"cleanroom.db")));
        await using var previews=new LegacyImportPreviews(fixture.Target,["4","9"],()=>true);previews.Initialize();previews.Start(Guid.NewGuid().ToString("N"),fixture.Source);
        var failed=await Finished(previews);Assert.Equal("Failed",failed.State);Assert.Null(failed.Backup);
        Assert.Contains("Station 4",failed.Error);Assert.Contains("enabled primary output",failed.Error);Assert.Contains(field,failed.Error);
        Assert.Equal(before,SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture.Source,"cleanroom.db"))));
    }
    [Theory]
    [InlineData("host",254)][InlineData("mount",1025)][InlineData("profile",65)]
    public async Task DisabledDestinationStillHonorsPreviewSizeBounds(string field,int length)
    {
        var fixture=Fixture();var column=field switch{"host"=>"icecast_host","mount"=>"icecast_mount",_=>"stream_codec_profile"};
        ChangeFixtureOutput(fixture.Source,"icecast_enabled=0,"+column+"=$value",new string('a',length));
        await using var previews=new LegacyImportPreviews(fixture.Target,["4","9"],()=>true);previews.Initialize();previews.Start(Guid.NewGuid().ToString("N"),fixture.Source);
        var failed=await Finished(previews);Assert.Equal("Failed",failed.State);Assert.Null(failed.Backup);Assert.Contains("size limit",failed.Error);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void MappingCannotAdoptAnInvalidAddressEvenWhenPreviewWasConstructedSeparately(bool enabled)
    {
        var station=new LegacyStationPreview(4,"Fixture",null,null,null,false,0,new("127.0.0.1",0,"/fixture","aac_low_192",192,enabled,false),[]);
        var preview=new LegacyImportPreview(Guid.NewGuid().ToString("N"),"fixture","Ready for mapping",DateTimeOffset.UtcNow,Stations:[station]);
        Assert.Throws<ArgumentException>(()=>LegacyImportPreview.ValidateMappings(preview,[new(4,"4")],new HashSet<string>{"4"}));
    }
}
