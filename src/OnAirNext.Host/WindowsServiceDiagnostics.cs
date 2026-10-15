using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace OnAirNext.ServiceControl;

internal static class WindowsServiceDiagnostics
{
    private static string _stage = "SCM dispatcher";
    public static void Stage(string stage) => Volatile.Write(ref _stage, stage);
    public static void RecordFailure(Exception error)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            // Fixed, protected service storage only; never use paths or values from a failed configuration.
            ServiceEndpoint.RequirePath(ServiceEndpoint.EngineData, ServiceEndpoint.EngineData);
            ServiceFileProtection.AssertProtected(new DirectoryInfo(ServiceEndpoint.DataRoot)
                .GetAccessControl(System.Security.AccessControl.AccessControlSections.Access | System.Security.AccessControl.AccessControlSections.Owner));
            if (!Directory.Exists(ServiceEndpoint.EngineData)) return;
            var destination = Path.Combine(ServiceEndpoint.EngineData, "boot-failure.json");
            ServiceEndpoint.RequirePath(destination, destination);
            var errors = new List<object>();
            for (Exception? item = error; item is not null && errors.Count < 4; item = item.InnerException)
                errors.Add(new { Type = item.GetType().FullName, item.HResult,
                    NativeError = item is Win32Exception native ? (int?)native.NativeErrorCode : null,
                    Frames = new StackTrace(item, false).GetFrames()?.Take(8)
                        .Select(frame => { var method = frame.GetMethod(); return method?.DeclaringType?.FullName + "." + method?.Name; }).ToArray() });
            var payload = JsonSerializer.Serialize(new { At = DateTimeOffset.UtcNow, Stage = Volatile.Read(ref _stage), Errors = errors });
            var temporary = Path.Combine(ServiceEndpoint.EngineData, "boot-failure-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temporary, payload); File.Move(temporary, destination, true);
        }
        catch { /* Diagnostics must never change failure reporting or service ownership. */ }
    }
}
