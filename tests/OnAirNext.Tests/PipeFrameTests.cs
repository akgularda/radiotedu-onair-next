using System.Text;
using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class PipeFrameTests
{
    [Fact]
    public async Task FramePreservesUtf8AndAcceptsAnExactLimit()
    {
        var bytes = Encoding.UTF8.GetBytes("İstanbul");
        using var stream = new MemoryStream([.. bytes, (byte)'\n']);
        Assert.Equal(bytes, await PipeFrames.ReadAsync(stream, bytes.Length, CancellationToken.None));
    }
    [Fact]
    public async Task OverlongFrameIsRejectedBeforeItCanGrowWithoutBound()
    {
        using var stream = new MemoryStream("12345\n"u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeFrames.ReadAsync(stream, 4, CancellationToken.None));
    }
    [Fact]
    public async Task APartialFrameIsNeverAcceptedAsACompleteCommand()
    {
        using var stream = new MemoryStream("partial"u8.ToArray());
        await Assert.ThrowsAsync<EndOfStreamException>(() => PipeFrames.ReadAsync(stream, 100, CancellationToken.None));
    }
    [Fact]
    public async Task ADisconnectedClientWithoutARequestReturnsNoFrame()
    {
        using var stream = new MemoryStream();
        Assert.Null(await PipeFrames.ReadAsync(stream, 100, CancellationToken.None));
    }
}
