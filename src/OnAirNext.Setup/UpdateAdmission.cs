using System.Text.Json;
using OnAirNext.ServiceControl;

namespace OnAirNext.Installation;

// Narrow admission for pre-commissioning application maintenance. This is deliberately
// unsuitable for rolling updates: even a stopped channel with configured outputs denies it.
public static class UpdateAdmission
{
    public static void RequireUnarmedStatus(JsonElement status)
    {
        try
        {
            var controller=status.GetProperty("controller");
            if(controller.GetProperty("mode").GetString()!="windows-service"||controller.GetProperty("service").GetString()!=ServiceEndpoint.Name)
                throw Denied();
            var children=status.GetProperty("children");
            if(children.ValueKind!=JsonValueKind.Object||children.EnumerateObject().Any())throw Denied();
            var audit=status.GetProperty("audit");
            if(audit.GetProperty("Error").ValueKind!=JsonValueKind.Null||audit.GetProperty("PendingEvents").GetInt32()!=0)throw Denied();
            var stations=status.GetProperty("stations");var seen=new HashSet<string>(StringComparer.Ordinal);
            if(stations.ValueKind!=JsonValueKind.Array||stations.GetArrayLength()!=SetupPaths.StationIds.Length)throw Denied();
            foreach(var station in stations.EnumerateArray())
            {
                var id=station.GetProperty("Id").GetString();var outputs=station.GetProperty("Outputs");
                if(id is null||!SetupPaths.StationIds.Contains(id,StringComparer.Ordinal)||!seen.Add(id)||
                    station.GetProperty("State").GetString()!="Stopped"||station.GetProperty("RequestedRunning").ValueKind!=JsonValueKind.False||
                    outputs.ValueKind!=JsonValueKind.Array||outputs.GetArrayLength()!=0)throw Denied();
            }
        }
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {throw Denied();}
    }
    public static void RequireUnarmedConfiguration(JsonElement config,string operatorSid)
    {
        try
        {
            ServiceEndpoint.RequirePath(config.GetProperty("Ffmpeg").GetString()??"",ServiceEndpoint.Ffmpeg);
            ServiceEndpoint.RequirePath(config.GetProperty("DataRoot").GetString()??"",ServiceEndpoint.EngineData);
            if(config.GetProperty("PipeName").GetString()!=ServiceEndpoint.Pipe||config.GetProperty("OperatorSid").GetString()!=operatorSid)throw Denied();
            var stations=config.GetProperty("Stations");var seen=new HashSet<string>(StringComparer.Ordinal);
            if(stations.ValueKind!=JsonValueKind.Array||stations.GetArrayLength()!=SetupPaths.StationIds.Length)throw Denied();
            foreach(var station in stations.EnumerateArray())
            {
                var id=station.GetProperty("Id").GetString();var outputs=station.GetProperty("Outputs");
                if(id is null||!SetupPaths.StationIds.Contains(id,StringComparer.Ordinal)||!seen.Add(id)||
                    station.GetProperty("AutoStart").ValueKind!=JsonValueKind.False||outputs.ValueKind!=JsonValueKind.Array||outputs.GetArrayLength()!=0)throw Denied();
            }
        }
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or ArgumentException)
        {throw Denied();}
    }
    private static InvalidOperationException Denied()=>new("Application maintenance requires the owned service, all eight channels explicitly stopped, no running intent, no child processes, a healthy drained journal, and zero configured outputs. Existing broadcasts were not changed.");
}
