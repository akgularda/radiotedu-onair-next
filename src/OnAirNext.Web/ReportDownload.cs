using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using OnAirNext.Engine;
using OnAirNext.ServiceControl;

namespace OnAirNext.Web;

internal sealed class ReportDownload : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly CancellationTokenSource _deadline;
    public ReportExport Receipt { get; }
    private ReportDownload(NamedPipeClientStream pipe, CancellationTokenSource deadline, ReportExport receipt)
    { _pipe = pipe; _deadline = deadline; Receipt = receipt; }
    public static async Task<ReportDownload> OpenAsync(string engine, string id, CancellationToken token)
    {
        if (!EngineRpc.IsIsolated(engine)) throw new ArgumentException("Choose an isolated engine from System.");
        var name = ServiceEndpoint.Reports(engine); var pipe = ServicePipeClient.Create(name);
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await pipe.ConnectAsync(deadline.Token); ServicePipeClient.AuthenticateServer(pipe, name);
            await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new ReportTransferRequest(id)), deadline.Token);
            await pipe.WriteAsync("\n"u8.ToArray(), deadline.Token);
            var frame = await PipeFrames.ReadAsync(pipe, 16384, deadline.Token) ?? throw new IOException("The report connection closed before confirmation.");
            var header = JsonSerializer.Deserialize<ReportTransferHeader>(frame) ?? throw new IOException("Missing report receipt.");
            if (!header.Accepted || header.Receipt is not { State: "Completed" } receipt)
                throw new InvalidOperationException(header.Error ?? "This report has no confirmed file.");
            ReportExport.Validate(receipt.Id, receipt.Period, receipt.Selection, receipt.StationId);
            if (receipt.Id != id || receipt.Bytes <= 0 || receipt.Sha256 is not { Length: 64 } || receipt.Sha256.Any(c => !char.IsAsciiHexDigit(c)))
                throw new IOException("The report receipt is invalid.");
            return new(pipe, deadline, receipt);
        }
        catch { await pipe.DisposeAsync(); deadline.Dispose(); throw; }
    }
    public async Task CopyToAsync(Stream destination, CancellationToken cancellation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffers = new[] { new byte[65536], new byte[65536] };
        long received = 0; int previous = 0, previousLength = 0, current = 1;
        while (received < Receipt.Bytes)
        {
            _deadline.CancelAfter(TimeSpan.FromSeconds(5)); cancellation.ThrowIfCancellationRequested();
            var read = await _pipe.ReadAsync(buffers[current].AsMemory(0, (int)Math.Min(65536, Receipt.Bytes - received)), _deadline.Token);
            if (read == 0) throw new EndOfStreamException("Report download ended before its confirmed size.");
            hash.AppendData(buffers[current].AsSpan(0, read)); received += read;
            if (previousLength > 0)
            {
                _deadline.CancelAfter(TimeSpan.FromSeconds(5));
                await destination.WriteAsync(buffers[previous].AsMemory(0, previousLength), _deadline.Token);
            }
            (previous, current) = (current, previous); previousLength = read;
        }
        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(Receipt.Sha256!)))
            throw new IOException("Report checksum mismatch. Download a fresh export.");
        // Withhold the final chunk until SHA-256 verifies, so a bad transfer cannot look like a complete HTTP download.
        _deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await destination.WriteAsync(buffers[previous].AsMemory(0, previousLength), _deadline.Token);
    }
    public async ValueTask DisposeAsync() { await _pipe.DisposeAsync(); _deadline.Dispose(); }
}
