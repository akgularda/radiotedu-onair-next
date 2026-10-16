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
        if (action is "anonymous" or "anonymous-files" or "anonymous-reports") return await RejectedAnonymousAsync(action == "anonymous-files" ? ServiceEndpoint.FilePipe : action == "anonymous-reports" ? ServiceEndpoint.ReportPipe : ServiceEndpoint.Pipe);
        if(action is not ("status" or "start-station" or "stop-station" or "stop"))throw new ArgumentException("Unknown isolated service probe.");
        return await SendAsync(new { Id=Guid.NewGuid().ToString("N"),Action=action,StationId="service-fixture" });
    }
    private static async Task<int> SendAsync(object command)
    {
        var response = await RequestAsync(command); Console.WriteLine(response.GetRawText()); return 0;
    }
    private static async Task<JsonElement> RequestAsync(object command)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var pipe=ServicePipeClient.Create(ServiceEndpoint.Pipe);
        await pipe.ConnectAsync(deadline.Token); ServicePipeClient.AuthenticateServer(pipe,ServiceEndpoint.Pipe);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(command);
        await pipe.WriteAsync(bytes,deadline.Token);await pipe.WriteAsync("\n"u8.ToArray(),deadline.Token);
        var response=await PipeFrames.ReadAsync(pipe,524288,deadline.Token)??throw new EndOfStreamException("Service probe was not acknowledged.");
        using var document = JsonDocument.Parse(response); return document.RootElement.Clone();
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
    private static async Task<int> RejectedAnonymousAsync(string name)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This probe requires Windows.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", name,
            System.IO.Pipes.PipeAccessRights.ReadWrite | System.IO.Pipes.PipeAccessRights.Synchronize,
            System.IO.Pipes.PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Anonymous,
            HandleInheritability.None);
        await pipe.ConnectAsync(deadline.Token);
        ServicePipeClient.AuthenticateServer(pipe, name);
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
    public static async Task<int> ReportAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        var accepted = await RequestAsync(new { Id = id, Action = "report-export", ReportPeriod = "year", ReportSelection = "2026" });
        if (!accepted.GetProperty("accepted").GetBoolean()) throw new InvalidOperationException("Report fixture export was rejected.");
        ReportExport? receipt = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (receipt?.State != "Completed")
        {
            timeout.Token.ThrowIfCancellationRequested();
            var listing = await RequestAsync(new { Id = Guid.NewGuid().ToString("N"), Action = "reports" });
            receipt = listing.GetProperty("items").EnumerateArray().Select(item => item.Deserialize<ReportExport>()).SingleOrDefault(item => item?.Id == id);
            if (receipt?.State is "Failed" or "Cancelled" or "Interrupted") throw new InvalidOperationException("Report fixture did not complete.");
            if (receipt?.State != "Completed") await Task.Delay(25, timeout.Token);
        }
        if (receipt.Bytes > 1048576) throw new InvalidOperationException("Disposable report fixture unexpectedly exceeded its size bound.");
        await using var pipe = ServicePipeClient.Create(ServiceEndpoint.ReportPipe);
        await pipe.ConnectAsync(timeout.Token); ServicePipeClient.AuthenticateServer(pipe, ServiceEndpoint.ReportPipe);
        await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new ReportTransferRequest(id)), timeout.Token);
        await pipe.WriteAsync("\n"u8.ToArray(), timeout.Token);
        var frame = await PipeFrames.ReadAsync(pipe, 16384, timeout.Token) ?? throw new IOException("Report header missing.");
        var header = JsonSerializer.Deserialize<ReportTransferHeader>(frame)!;
        if (!header.Accepted || header.Receipt?.Id != id) throw new InvalidOperationException("Report fixture receipt mismatch.");
        // Hold the report lane while independently exercising both other lanes.
        var clock = Stopwatch.StartNew(); var status = await RequestAsync(new { Id = Guid.NewGuid().ToString("N"), Action = "status" });
        var controlsAvailable = status.TryGetProperty("stations", out _) && clock.Elapsed < TimeSpan.FromSeconds(3);
        var libraryAvailable = false;
        await using (var files = ServicePipeClient.Create(ServiceEndpoint.FilePipe))
        {
            await files.ConnectAsync(timeout.Token); ServicePipeClient.AuthenticateServer(files, ServiceEndpoint.FilePipe);
            await files.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { Action = "unknown", StationId = "service-fixture", BatchId = Guid.NewGuid().ToString("N") }), timeout.Token);
            await files.WriteAsync("\n"u8.ToArray(), timeout.Token);
            var reply = await PipeFrames.ReadAsync(files, 16384, timeout.Token) ?? throw new IOException("Library lane did not respond.");
            using var document = JsonDocument.Parse(reply);
            libraryAvailable = !document.RootElement.GetProperty("Accepted").GetBoolean() &&
                document.RootElement.GetProperty("Error").GetString() == "Unknown upload command.";
        }
        using var data = new MemoryStream(); var buffer = new byte[65536]; long total = 0;
        while (total < receipt.Bytes)
        {
            var read = await pipe.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, receipt.Bytes - total)), timeout.Token);
            if (read == 0) throw new EndOfStreamException("Report fixture transfer ended early.");
            data.Write(buffer, 0, read); total += read;
        }
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data.GetBuffer().AsSpan(0, (int)data.Length)));
        var verified = hash == receipt.Sha256 && total == receipt.Bytes &&
            Encoding.UTF8.GetString(data.GetBuffer(), 0, (int)data.Length).Contains("occurrence_id,station_id", StringComparison.Ordinal);
        Console.WriteLine(JsonSerializer.Serialize(new { verified, controlsAvailable, libraryAvailable, receipt, actualBytes = total, sha256 = hash }));
        return verified && controlsAvailable && libraryAvailable ? 0 : 1;
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
