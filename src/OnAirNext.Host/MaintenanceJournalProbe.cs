using System.Text.Json;
using OnAirNext.Engine;
using OnAirNext.ServiceControl;

namespace OnAirNext.Host;

internal static class MaintenanceJournalProbe
{
    public static int Run(string database)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Windows maintenance inspection requires Windows.");
        // Only the new installation's current journal or a fixed operation's snapshot.
        // No arbitrary SQL, media, old database or credential file can be requested.
        database=Path.GetFullPath(database);var current=Path.Combine(ServiceEndpoint.EngineData,"playback.db");
        var updates=Path.Combine(ServiceEndpoint.DataRoot,"updates");
        var relative=Path.GetRelativePath(updates,database).Split(Path.DirectorySeparatorChar);
        var snapshot=relative.Length==5&&Guid.TryParseExact(relative[0],"N",out _)&&(relative[1]=="snapshot"||relative[1].StartsWith("snapshot-",StringComparison.Ordinal)&&Guid.TryParseExact(relative[1][9..],"N",out _))&&relative[2]=="data"&&relative[3]=="journal"&&relative[4]=="playback.db";
        if(!string.Equals(database,current,StringComparison.OrdinalIgnoreCase)&&!snapshot)
            throw new InvalidOperationException("Inspect only the retained new playback journal or its reviewed maintenance snapshot.");
        ServiceEndpoint.RequirePath(database,database);
        var result=JournalMaintenanceInspection.Read(database,["1","2","4","5","8","9","10","11"]);
        Console.WriteLine(JsonSerializer.Serialize(result));return 0;
    }
}
