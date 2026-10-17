using System.Diagnostics;
using System.Security.Cryptography;

namespace OnAirNext.Engine;

// Used only on the dedicated report-export worker. Publication never writes through this stream.
internal sealed class ReportWriteStream(FileStream file, int bytesPerSecond, CancellationToken cancellation) : Stream
{
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _bytes, _checkedAt;
    public string Sha256 => Convert.ToHexStringLower(_hash.GetCurrentHash());
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => file.Length;
    public override long Position { get => file.Position; set => throw new NotSupportedException(); }
    public override void Flush() { cancellation.ThrowIfCancellationRequested(); file.Flush(); }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        cancellation.ThrowIfCancellationRequested();
        if (bytesPerSecond > 0 && (_bytes == 0 || _bytes - _checkedAt >= 65536))
        {
            if (new DriveInfo(Path.GetPathRoot(file.Name)!).AvailableFreeSpace < 2L * 1024 * 1024 * 1024)
                throw new ReportStorageException();
            _checkedAt = _bytes;
        }
        file.Write(buffer); _hash.AppendData(buffer); _bytes += buffer.Length;
        if (bytesPerSecond <= 0 || _bytes - _checkedAt < 65536) return;
        while (_bytes / (double)bytesPerSecond - _elapsed.Elapsed.TotalSeconds > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var delay = (int)Math.Clamp((_bytes / (double)bytesPerSecond - _elapsed.Elapsed.TotalSeconds) * 1000, 1, 100);
            if (cancellation.WaitHandle.WaitOne(delay)) cancellation.ThrowIfCancellationRequested();
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

public sealed class ReportStorageException() : IOException("Report export needs at least 2 GiB free space. Free space and retry; existing reports are retained.") { }
