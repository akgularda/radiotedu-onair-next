namespace OnAirNext.Engine;

public static class PipeFrames
{
    public static async Task<byte[]?> ReadAsync(Stream stream, int maximumBytes, CancellationToken token)
    {
        if (maximumBytes is < 1 or > 1048576) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var frame = new MemoryStream();
        var buffer = new byte[Math.Min(1024, maximumBytes + 1)];
        while (frame.Length <= maximumBytes)
        {
            var count = await stream.ReadAsync(buffer, token);
            if (count == 0)
            {
                if (frame.Length == 0) return null;
                throw new EndOfStreamException("Local request ended before its frame delimiter.");
            }
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
            var length = newline < 0 ? count : newline;
            if (frame.Length + length > maximumBytes) throw new InvalidDataException("Local request exceeds its frame limit.");
            frame.Write(buffer, 0, length);
            if (newline >= 0) return frame.ToArray();
        }
        throw new InvalidDataException("Local request exceeds its frame limit.");
    }
}
