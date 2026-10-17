using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using OnAirNext.Engine;

namespace OnAirNext.Lab;

internal sealed class ControlHost : IAsyncDisposable
{
    private readonly string _pipe, _logPrefix;
    private readonly Task<string> _stdout, _stderr;
    internal Process Process { get; }

    internal ControlHost(string sdk, string assembly, string configuration, string pipe, string logPrefix)
    {
        _pipe = pipe; _logPrefix = logPrefix;
        Process = ChildProcess.Start(sdk, [assembly, "--config", configuration], output: true, role: "lifecycle-test-host");
        _stdout = Process.StandardOutput.ReadToEndAsync(); _stderr = ChildProcess.DrainErrorAsync(Process);
    }

    internal Task<JsonDocument> CommandAsync(string action, string? station = null, string? id = null, CancellationToken token = default)
        => RequestAsync(new { Id=id??Guid.NewGuid().ToString("N"), Action=action, StationId=station,
            JournalFeatures=action=="status"?new[]{RecoveryCompatibility.HeldRotationFeature,RecoveryCompatibility.StationQueueFeature}:null },token);

    internal async Task<JsonDocument> RequestAsync(object request, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        var frame = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n");
        if(frame.Length>65536)throw new ArgumentException("The test command exceeds the host frame limit.");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(deadline.Token); await pipe.WriteAsync(frame, deadline.Token);
                return JsonDocument.Parse(await PipeFrames.ReadAsync(pipe, 131072, deadline.Token) ?? throw new EndOfStreamException("No control acknowledgement."));
            }
            catch (IOException) when (attempt == 0 && !deadline.IsCancellationRequested)
            { await Task.Delay(50, deadline.Token); }
        }
    }

    internal async Task WaitForStationAsync(string station, string state)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                if (Process.HasExited) throw new InvalidOperationException($"Test host exited with {Process.ExitCode}; inspect {_logPrefix}.stderr.log.");
                try
                {
                    using var status = await CommandAsync("status", token: deadline.Token);
                    if (status.RootElement.GetProperty("stations").EnumerateArray().Any(x => x.GetProperty("Id").GetString() == station && x.GetProperty("State").GetString() == state)) return;
                }
                catch (Exception ex) when ((ex is IOException or OperationCanceledException) && !deadline.IsCancellationRequested) { }
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        throw new TimeoutException($"Station {station} did not reach {state} within 20 seconds.");
    }

    public async ValueTask DisposeAsync()
    {
        try { await ChildProcess.StopAsync(Process); }
        finally
        {
            await SaveAsync(_stdout, _logPrefix + ".stdout.log");
            await SaveAsync(_stderr, _logPrefix + ".stderr.log");
        }
    }
    private static async Task SaveAsync(Task<string> text, string path)
    {
        try { await File.WriteAllTextAsync(path, await text); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        { await File.WriteAllTextAsync(path, "Log ended during cleanup: " + ex.GetType().Name); }
    }
}
