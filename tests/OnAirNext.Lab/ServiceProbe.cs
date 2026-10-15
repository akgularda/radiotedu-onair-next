using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OnAirNext.Engine;
using OnAirNext.ServiceControl;

namespace OnAirNext.Lab;

internal static class ServiceProbe
{
    public static async Task<int> CommandAsync(string action)
    {
        if (action == "anonymous") return await RejectedAnonymousAsync();
        if(action is not ("status" or "start-station" or "stop-station" or "stop"))throw new ArgumentException("Unknown isolated service probe.");
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var pipe=ServicePipeClient.Create(ServiceEndpoint.Pipe);
        await pipe.ConnectAsync(deadline.Token); ServicePipeClient.AuthenticateServer(pipe,ServiceEndpoint.Pipe);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new { Id=Guid.NewGuid().ToString("N"),Action=action,StationId="service-fixture" });
        await pipe.WriteAsync(bytes,deadline.Token);await pipe.WriteAsync("\n"u8.ToArray(),deadline.Token);
        var response=await PipeFrames.ReadAsync(pipe,524288,deadline.Token)??throw new EndOfStreamException("Service probe was not acknowledged.");
        Console.WriteLine(Encoding.UTF8.GetString(response));return 0;
    }
    private static async Task<int> RejectedAnonymousAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This probe requires Windows.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", ServiceEndpoint.Pipe,
            System.IO.Pipes.PipeAccessRights.ReadWrite | System.IO.Pipes.PipeAccessRights.Synchronize,
            System.IO.Pipes.PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Anonymous,
            HandleInheritability.None);
        await pipe.ConnectAsync(deadline.Token);
        ServicePipeClient.AuthenticateServer(pipe, ServiceEndpoint.Pipe);
        try
        {
            await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { Id = Guid.NewGuid().ToString("N"), Action = "status" }), deadline.Token);
            await pipe.WriteAsync("\n"u8.ToArray(), deadline.Token);
            var received = await pipe.ReadAsync(new byte[1], deadline.Token);
            if (received != 0) throw new InvalidOperationException("An anonymous client received an unauthorized response.");
        }
        catch (IOException) { /* A verified service may reject the connection without a response. */ }
        Console.WriteLine("{\"rejected\":true}"); return 0;
    }
    public static async Task<int> OwnedChildFixtureAsync(bool leaf)
    {
        // Software-lifecycle fixture only: never a codec implementation, audio output or UI simulation.
        ServiceEndpoint.RequirePath(Environment.ProcessPath!,ServiceEndpoint.Ffmpeg);
        if(leaf) { await Task.Delay(Timeout.Infinite);return 0; }
        using var self=Process.GetCurrentProcess();
        var start=new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=false,CreateNoWindow=true };
        start.ArgumentList.Add("--service-child");start.ArgumentList.Add("leaf");
        using var child=Process.Start(start)??throw new InvalidOperationException("Owned fixture child did not start.");
        var payload=JsonSerializer.Serialize(new { ParentPid=self.Id,ParentStartTicks=self.StartTime.ToUniversalTime().Ticks,
            ChildPid=child.Id,ChildStartTicks=child.StartTime.ToUniversalTime().Ticks });
        var path=Path.Combine(ServiceEndpoint.EngineData,"owned-probe.json");
        var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        await File.WriteAllTextAsync(temporary,payload);File.Move(temporary,path,true);
        await Task.Delay(Timeout.Infinite);return 0;
    }
}
