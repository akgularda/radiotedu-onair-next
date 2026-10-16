using Microsoft.Data.Sqlite;

namespace OnAirNext.Engine;

public sealed record MaintenanceJournalInspection(int SchemaVersion,DesiredStationState[] Stations,string Integrity);

// Read-only inspection after the service has exited and the updater holds writer.lock.
// No EventJournal is opened, no WAL/checkpoint/schema or requested state is rewritten.
public static class JournalMaintenanceInspection
{
    public static MaintenanceJournalInspection Read(string database,IReadOnlyCollection<string> expectedStations)
    {
        if(!File.Exists(database)||new FileInfo(database).Length is <1 or >536870912)
            throw new InvalidDataException("The maintenance journal is missing or exceeds its inspection bound.");
        if(expectedStations.Count!=8||expectedStations.Distinct(StringComparer.Ordinal).Count()!=8||expectedStations.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Specify the exact eight installed channels.");
        using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=database,Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=2}.ToString());
        connection.Open();
        using var version=connection.CreateCommand();version.CommandText="SELECT version FROM schema_info";
        using(var reader=version.ExecuteReader())
            if(!reader.Read()||reader.IsDBNull(0)||reader.GetInt64(0)!=1||reader.Read())
                throw new InvalidDataException("Application maintenance requires the explicit compatible journal schema 1.");
        var stations=new List<DesiredStationState>();
        using var state=connection.CreateCommand();state.CommandText="SELECT station_id,enabled,requested_running FROM station_desired_state ORDER BY station_id";
        using(var reader=state.ExecuteReader())
        {
            while(reader.Read())
            {
                if(stations.Count>=8||reader.IsDBNull(0)||reader.IsDBNull(1)||reader.IsDBNull(2)||
                    !expectedStations.Contains(reader.GetString(0),StringComparer.Ordinal)||reader.GetInt64(1) is not (0 or 1)||reader.GetInt64(2)!=0)
                    throw new InvalidDataException("Maintenance cannot preserve a running intent or an unknown channel state.");
                stations.Add(new(reader.GetString(0),reader.GetInt64(1)==1,false));
            }
        }
        if(stations.Count!=8||stations.Select(s=>s.StationId).Distinct(StringComparer.Ordinal).Count()!=8)
            throw new InvalidDataException("The maintenance journal must retain all eight explicitly stopped channels.");
        using var check=connection.CreateCommand();check.CommandText="PRAGMA quick_check";
        using(var reader=check.ExecuteReader())
            if(!reader.Read()||reader.IsDBNull(0)||reader.GetString(0)!="ok"||reader.Read())
                throw new InvalidDataException("The retained journal did not pass its read-only integrity check.");
        return new(1,stations.ToArray(),"ok");
    }
}
