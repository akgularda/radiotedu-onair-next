using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class JournalMaintenanceInspectionTests
{
    private static readonly string[] Ids=["1","2","4","5","8","9","10","11"];
    private static async Task<string> CreateAsync()
    {
        var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RadioTEDU","OnAirNext-Dev","unit-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,"playback.db");
        await using(var journal=new EventJournal(path))
            foreach(var id in Ids)await journal.InitializeDesiredStateAsync(new StationSettings(id,[],[],Enabled:id!="4",AutoStart:false));
        return path;
    }
    private static SqliteConnection Open(string path)
    {
        var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());connection.Open();return connection;
    }
    private static void Execute(SqliteConnection connection,string sql)
    {using var command=connection.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
    private static async Task<byte[]> ReadSharedFixtureAsync(string path)
    {
        await using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,65536,true);
        using var bytes=new MemoryStream();await input.CopyToAsync(bytes);return bytes.ToArray();
    }
    [Fact]
    public async Task ReadOnlyInspectionPreservesDisabledAndStoppedStatesAndDatabaseBytes()
    {
        var path=await CreateAsync();var before=SHA256.HashData(await File.ReadAllBytesAsync(path));
        var result=JournalMaintenanceInspection.Read(path,Ids);var after=await File.ReadAllBytesAsync(path);
        Assert.Equal(1,result.SchemaVersion);Assert.Equal("ok",result.Integrity);Assert.Equal(8,result.Stations.Length);
        Assert.All(result.Stations,s=>Assert.False(s.RequestedRunning));Assert.False(result.Stations.Single(s=>s.StationId=="4").Enabled);
        Assert.Equal(before,SHA256.HashData(after));
    }
    [Theory]
    [InlineData("UPDATE schema_info SET version=2")]
    [InlineData("DELETE FROM station_desired_state WHERE station_id='1'")]
    [InlineData("UPDATE station_desired_state SET station_id='external' WHERE station_id='1'")]
    [InlineData("UPDATE station_desired_state SET enabled=3 WHERE station_id='1'")]
    [InlineData("UPDATE station_desired_state SET requested_running=1 WHERE station_id='1'")]
    public async Task UnsupportedSchemaOrNonStoppedPersistedStateIsNeverAdopted(string mutation)
    {
        var path=await CreateAsync();using(var connection=Open(path))Execute(connection,mutation);
        var bytes=await File.ReadAllBytesAsync(path);
        Assert.Throws<InvalidDataException>(()=>JournalMaintenanceInspection.Read(path,Ids));
        var after=await File.ReadAllBytesAsync(path);Assert.Equal(bytes,after);
    }
    [Fact]
    public async Task CommittedRunningIntentInWalIsObservedWithoutCheckpointingOrRewritingIt()
    {
        var path=await CreateAsync();using var connection=Open(path);
        Execute(connection,"PRAGMA journal_mode=WAL;PRAGMA wal_autocheckpoint=0;UPDATE station_desired_state SET requested_running=1 WHERE station_id='2'");
        var database=await ReadSharedFixtureAsync(path);var wal=await ReadSharedFixtureAsync(path+"-wal");Assert.NotEmpty(wal);
        Assert.Throws<InvalidDataException>(()=>JournalMaintenanceInspection.Read(path,Ids));
        var afterDatabase=await ReadSharedFixtureAsync(path);var afterWal=await ReadSharedFixtureAsync(path+"-wal");
        Assert.Equal(database,afterDatabase);Assert.Equal(wal,afterWal);
    }
    [Fact]
    public async Task UnfinishedAutomaticBrandingRejectsMaintenanceWithoutRewritingRecovery()
    {
        var path=await CreateAsync();
        await using(var journal=new EventJournal(path))
        {
            var item=new MediaItem("j","synthetic.wav","Fixture","Identifier",ContentKind.Jingle);
            await journal.SaveCheckpointAsync(new("1","Stopped",3,item,"unfinished",960,960,true,DateTimeOffset.UtcNow,HoldsRotation:true));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>journal.RequireRecoveryReaderAsync(null));
            await journal.RequireRecoveryReaderAsync([RecoveryCompatibility.HeldRotationFeature]);
        }
        var before=await File.ReadAllBytesAsync(path);
        Assert.Throws<InvalidOperationException>(()=>JournalMaintenanceInspection.Read(path,Ids));
        Assert.Equal(before,await File.ReadAllBytesAsync(path));
    }
    [Fact]
    public async Task EnabledBrandingSettingsCannotBeSilentlyLostByAnOlderApplicationImage()
    {
        var path=await CreateAsync();
        await using(var journal=new EventJournal(path))
        {
            await journal.InitializeBrandingAsync();
            await journal.SaveBrandingConfigurationAsync("1",0,new(new(true,2),new(false,3)),"enable");
        }
        var before=await File.ReadAllBytesAsync(path);
        Assert.Throws<InvalidOperationException>(()=>JournalMaintenanceInspection.Read(path,Ids));
        Assert.Equal(before,await File.ReadAllBytesAsync(path));
    }
    [Fact]
    public async Task ArbitraryOrRepeatedRosterIsRejectedBeforeOpeningTheJournal()
    {
        var path=await CreateAsync();Assert.Throws<ArgumentException>(()=>JournalMaintenanceInspection.Read(path,["1"]));
        Assert.Throws<ArgumentException>(()=>JournalMaintenanceInspection.Read(path,Enumerable.Repeat("1",8).ToArray()));
    }
}
