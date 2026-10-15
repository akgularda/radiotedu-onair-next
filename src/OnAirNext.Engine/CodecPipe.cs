using System.IO.Pipes;

namespace OnAirNext.Engine;

// Windows Process redirected stdio uses synchronous anonymous-pipe handles.
// Use overlapped named pipes so many codec readers cannot starve station clocks.
public sealed class CodecPipe : IDisposable
{
    public string Path { get; }
    public NamedPipeServerStream Stream { get; }
    public CodecPipe(PipeDirection direction)
    {
        var name = "onair-next-codec-" + Guid.NewGuid().ToString("N");
        Path = @"\\.\pipe\" + name;
        Stream = new NamedPipeServerStream(name, direction, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
    }
    public Task ConnectAsync(CancellationToken token) => Stream.WaitForConnectionAsync(token);
    public void Dispose() => Stream.Dispose();
}
