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
        var database=await File.ReadAllBytesAsync(path);var wal=await File.ReadAllBytesAsync(path+"-wal");Assert.NotEmpty(wal);
        Assert.Throws<InvalidDataException>(()=>JournalMaintenanceInspection.Read(path,Ids));
        var afterDatabase=await File.ReadAllBytesAsync(path);var afterWal=await File.ReadAllBytesAsync(path+"-wal");
        Assert.Equal(database,afterDatabase);Assert.Equal(wal,afterWal);
    }
    [Fact]
    public async Task ArbitraryOrRepeatedRosterIsRejectedBeforeOpeningTheJournal()
    {
        var path=await CreateAsync();Assert.Throws<ArgumentException>(()=>JournalMaintenanceInspection.Read(path,["1"]));
        Assert.Throws<ArgumentException>(()=>JournalMaintenanceInspection.Read(path,Enumerable.Repeat("1",8).ToArray()));
    }
}
