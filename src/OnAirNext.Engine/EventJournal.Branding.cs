using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OnAirNext.Engine;

public sealed record BrandingConfiguration(string StationId,long Version,BrandingRules Rules);

public sealed partial class EventJournal
{
    internal static bool HasEnabledBrandingConfiguration(SqliteConnection connection,SqliteTransaction? transaction)
    {
        using var exists=connection.CreateCommand();exists.Transaction=transaction;
        exists.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='station_branding'";
        if(Convert.ToInt32(exists.ExecuteScalar(),System.Globalization.CultureInfo.InvariantCulture)==0)return false;
        using var query=connection.CreateCommand();query.Transaction=transaction;query.CommandText="SELECT station_id FROM station_branding";
        var stations=new List<string>();
        using(var reader=query.ExecuteReader())while(reader.Read())
        {if(stations.Count>=256)throw new InvalidDataException("Branding configuration exceeds its channel bound.");stations.Add(reader.GetString(0));}
        foreach(var station in stations)
        {
            var config=ReadBrandingConfiguration(connection,transaction,station);
            if(config.Rules.Jingle.Enabled||config.Rules.Sweeper.Enabled)return true;
        }
        return false;
    }

    public Task<bool> RequiresBrandingConfigurationSupportAsync()=>TransactionAsync(HasEnabledBrandingConfiguration);
    public Task InitializeBrandingAsync()=>TransactionAsync((connection,transaction)=>
    {
        using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="""
            CREATE TABLE IF NOT EXISTS station_branding(station_id TEXT PRIMARY KEY,version INTEGER NOT NULL,payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS branding_commands(id TEXT PRIMARY KEY,request TEXT NOT NULL,result TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();return true;
    });

    public Task<BrandingConfiguration> BrandingConfigurationAsync(string station)=>TransactionAsync((connection,transaction)=>
    {
        RequireBrandingStation(station);return ReadBrandingConfiguration(connection,transaction,station);
    });

    public Task<BrandingConfiguration> SaveBrandingConfigurationAsync(string station,long expectedVersion,BrandingRules rules,string id)
    {
        RequireBrandingStation(station);ArgumentNullException.ThrowIfNull(rules);
        _=new BrandingRotation(rules,[]);
        if(expectedVersion<0||string.IsNullOrWhiteSpace(id)||id.Length>128)
            throw new ArgumentException("Choose a bounded command ID and the reviewed branding version.");
        var request=JsonSerializer.Serialize(new{StationId=station,ExpectedVersion=expectedVersion,Rules=rules});
        return TransactionAsync((connection,transaction)=>
        {
            using var receipt=connection.CreateCommand();receipt.Transaction=transaction;
            receipt.CommandText="SELECT request,result FROM branding_commands WHERE id=$id";receipt.Parameters.AddWithValue("$id",id);
            using(var reader=receipt.ExecuteReader())
                if(reader.Read())
                {
                    if(reader.GetString(0)!=request)throw new InvalidOperationException("This command ID already belongs to a different branding request.");
                    return JsonSerializer.Deserialize<BrandingConfiguration>(reader.GetString(1))
                        ??throw new InvalidDataException("The saved branding receipt is empty.");
                }
            var current=ReadBrandingConfiguration(connection,transaction,station);
            if(current.Version!=expectedVersion)throw new InvalidOperationException("Branding changed after this review. Refresh the channel before saving again.");
            var updated=new BrandingConfiguration(station,checked(current.Version+1),rules);var payload=JsonSerializer.Serialize(updated);
            using var save=connection.CreateCommand();save.Transaction=transaction;
            save.CommandText="INSERT INTO station_branding(station_id,version,payload) VALUES($station,$version,$payload) ON CONFLICT(station_id) DO UPDATE SET version=excluded.version,payload=excluded.payload";
            save.Parameters.AddWithValue("$station",station);save.Parameters.AddWithValue("$version",updated.Version);save.Parameters.AddWithValue("$payload",payload);save.ExecuteNonQuery();
            using var complete=connection.CreateCommand();complete.Transaction=transaction;
            complete.CommandText="INSERT INTO branding_commands(id,request,result) VALUES($id,$request,$result)";
            complete.Parameters.AddWithValue("$id",id);complete.Parameters.AddWithValue("$request",request);complete.Parameters.AddWithValue("$result",payload);complete.ExecuteNonQuery();
            return updated;
        });
    }

    private static void RequireBrandingStation(string station)
    {
        if(string.IsNullOrWhiteSpace(station)||station.Length>128)throw new ArgumentException("Choose a valid branding channel.");
    }
    private static BrandingConfiguration ReadBrandingConfiguration(SqliteConnection connection,SqliteTransaction? transaction,string station)
    {
        using var query=connection.CreateCommand();query.Transaction=transaction;
        query.CommandText="SELECT version,payload FROM station_branding WHERE station_id=$station";query.Parameters.AddWithValue("$station",station);
        using var reader=query.ExecuteReader();if(!reader.Read())return new(station,0,BrandingRules.Disabled);
        var value=JsonSerializer.Deserialize<BrandingConfiguration>(reader.GetString(1))??throw new InvalidDataException("The saved branding configuration is empty.");
        if(value.StationId!=station||value.Version!=reader.GetInt64(0)||value.Version<1)throw new InvalidDataException("The saved branding configuration does not match its channel/version.");
        _=new BrandingRotation(value.Rules,[]);return value;
    }
}
