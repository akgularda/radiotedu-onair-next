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
        return await SendAsync(new { Id=Guid.NewGuid().ToString("N"),Action=action,StationId="service-fixture" });
    }
    private static async Task<int> SendAsync(object command)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var pipe=ServicePipeClient.Create(ServiceEndpoint.Pipe);
        await pipe.ConnectAsync(deadline.Token); ServicePipeClient.AuthenticateServer(pipe,ServiceEndpoint.Pipe);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(command);
        await pipe.WriteAsync(bytes,deadline.Token);await pipe.WriteAsync("\n"u8.ToArray(),deadline.Token);
        var response=await PipeFrames.ReadAsync(pipe,524288,deadline.Token)??throw new EndOfStreamException("Service probe was not acknowledged.");
        Console.WriteLine(Encoding.UTF8.GetString(response));return 0;
    }
    public static Task<int> SourceAsync(string instruction)
    {
        var parts = instruction.Split(':', 2); var action = parts[0]; var proof = parts.Length == 2 ? parts[1] : null;
        if (proof is not null && !Guid.TryParse(proof, out _)) throw new ArgumentException("Invalid fixture source proof.");
        var readable = Path.Combine(ServiceEndpoint.EngineData, "probe-source");
        var denied = Path.Combine(ServiceEndpoint.DataRoot, "operator-only-source");
        const string assignment = "b5b272315ef1475c9306fe9ae787c219";
        var station = action == "assign-wrong-station" ? "service-fixture" : "source-fixture";
        var id = action is "assign" or "replay" ? assignment : Guid.NewGuid().ToString("N");
        var operation = action switch
        {
            "readable" or "denied" => "library-source-check", "status" => "library-source-status",
            "assign" or "assign-unchecked" or "assign-mismatch" or "assign-wrong-station" or "replay" => "library-root",
            "library" => "library-status", _ => throw new ArgumentException("Unknown fixture source instruction.")
        };
        return SendAsync(new { Id = id, Action = operation, StationId = station,
            Path = action is "denied" or "assign-mismatch" ? denied : readable,
            SourceCheckId = action == "replay" ? null : proof });
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
