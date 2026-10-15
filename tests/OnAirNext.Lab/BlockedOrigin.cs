using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OnAirNext.Lab;

// Accepts source headers, then deliberately stops reading audio. It owns only a loopback socket.
internal sealed class BlockedOrigin : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _clients = new();
    private readonly Task _run;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public BlockedOrigin()
    {
        _listener.Server.ReceiveBufferSize = 1024;
        _listener.Start(); _run = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _clients.Add(BlockAsync(client));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private async Task BlockAsync(TcpClient client)
    {
        using (client)
        {
            client.ReceiveBufferSize = 1024;
            try
            {
                var one = new byte[1]; var header = new List<byte>(); var stream = client.GetStream();
                while (header.Count < 8192)
                {
                    if (await stream.ReadAsync(one, _stop.Token) == 0) return;
                    header.Add(one[0]);
                    if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
                }
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), _stop.Token);
                await Task.Delay(Timeout.Infinite, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (IOException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync(); _listener.Stop(); await _run; await Task.WhenAll(_clients); _stop.Dispose();
    }
}
