using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using OnAirNext.Engine;
using OnAirNext.ServiceControl;

namespace OnAirNext.Host;

// Report bytes have their own authenticated lane: neither station controls nor Library uploads wait for a download.
internal sealed class ReportPipeServer : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly ServicePipePolicy? _policy;
    private readonly ReportExports _reports;
    private readonly CancellationTokenSource _stop;
    private readonly Task _worker;
    private string? _error;
    public string? Error => Volatile.Read(ref _error);
    public ReportPipeServer(string control, bool serviceMode, ServicePipePolicy? policy, ReportExports reports, CancellationToken token)
    {
        var name = ServiceEndpoint.Reports(control); _policy = policy; _reports = reports;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _pipe = serviceMode && OperatingSystem.IsWindows() ? policy!.CreateServer(name)
            : new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance, 65536, 65536);
        _worker = RunAsync();
    }
    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var connected = false; var started = false; var authorized = false;
            try
            {
                await _pipe.WaitForConnectionAsync(_stop.Token); connected = true;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                var frame = await PipeFrames.ReadAsync(_pipe, 4096, deadline.Token);
                if (frame is null) continue;
                if (_policy is not null && OperatingSystem.IsWindows()) _policy.Authorize(_pipe);
                authorized = true;
                var request = JsonSerializer.Deserialize<ReportTransferRequest>(frame) ?? throw new ArgumentException("Choose a completed report.");
                var download = _reports.OpenDownload(request.Id);
                await using var file = download.File;
                await ReplyAsync(new(true, download.Receipt), deadline.Token); started = true;
                var buffer = new byte[65536]; long sent = 0; var clock = Stopwatch.StartNew();
                while (sent < download.Receipt.Bytes)
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, download.Receipt.Bytes - sent)), deadline.Token);
                    if (read == 0) throw new EndOfStreamException("The report file ended before its confirmed size.");
                    deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    await _pipe.WriteAsync(buffer.AsMemory(0, read), deadline.Token); sent += read;
                    var delay = sent / (2d * 1024 * 1024) - clock.Elapsed.TotalSeconds;
                    if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Min(delay, 1)), deadline.Token);
                }
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                if (await _pipe.ReadAsync(new byte[1], deadline.Token) != 0) throw new IOException("Unexpected trailing report request.");
                Volatile.Write(ref _error, null);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception failure) when (failure is IOException or OperationCanceledException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
            {
                Volatile.Write(ref _error, "The report download did not finish. Retry the completed report; playback and Library transfers are independent.");
                if (connected && authorized && !started)
                {
                    using var replyDeadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); replyDeadline.CancelAfter(TimeSpan.FromSeconds(1));
                    try { await ReplyAsync(new(false, Error: "The confirmed report is unavailable. Refresh Reports or create a fresh export."), replyDeadline.Token); }
                    catch (Exception error) when (error is IOException or OperationCanceledException) { }
                }
            }
            catch (Exception)
            {
                Volatile.Write(ref _error, "Report transfer is unavailable. Station playback is independent.");
                try { await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token); } catch (OperationCanceledException) { break; }
            }
            finally { if (connected) try { _pipe.Disconnect(); } catch (IOException) { } }
        }
    }
    private Task ReplyAsync(ReportTransferHeader reply, CancellationToken token) =>
        _pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply) + "\n"), token).AsTask();
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await _worker; } finally { await _pipe.DisposeAsync(); _stop.Dispose(); }
    }
}
