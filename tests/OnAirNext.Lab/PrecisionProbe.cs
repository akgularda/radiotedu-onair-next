using System.Diagnostics;
using System.Runtime.InteropServices;
using OnAirNext.Engine;

namespace OnAirNext.Lab;

internal sealed record PrecisionResult(long ComparedFrames, long MismatchedFrames, long SubSixteenBitFrames,
    long StereoDifferentFrames, double MaximumErrorLsb24, int? FirstFixtureFrame);

// Synthetic 24-bit input and in-memory listener comparison. No listener/programme recording is written.
internal sealed class PrecisionProbe : IAsyncDisposable
{
    private readonly Process _process;
    private readonly CodecPipe _pcm = new(System.IO.Pipes.PipeDirection.In);
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(12));
    private readonly Task<PrecisionResult> _reading;
    private readonly Task<string> _errors;
    private readonly int[] _left, _right;
    public PrecisionProbe(string ffmpeg, int port, string mount, int[] left, int[] right)
    {
        _left = left; _right = right;
        _process = ChildProcess.Start(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-rw_timeout", "5000000",
            "-analyzeduration", "200000", "-probesize", "32768", "-i", $"http://127.0.0.1:{port}{mount}", "-t", "3",
            "-map", "0:a:0", "-ar", "48000", "-ac", "2", "-f", "f32le", "-c:a", "pcm_f32le", _pcm.Path], role: "precision-listener");
        _errors = ChildProcess.DrainErrorAsync(_process); _reading = ReadAsync();
    }
    public Task<PrecisionResult> Result => _reading;

    private async Task<PrecisionResult> ReadAsync()
    {
        await _pcm.ConnectAsync(_stop.Token);
        var lookup = new Dictionary<(int, int), int>();
        for (var i = 0; i < _left.Length; i++) lookup.TryAdd((_left[i], _right[i]), i);
        var buffer = new byte[AudioFormat.BlockBytes];
        long frames = 0, mismatches = 0, detail = 0, stereo = 0; double maxError = 0; int? first = null; var expected = 0;
        while (true)
        {
            var length = 0;
            while (length < buffer.Length)
            {
                var count = await _pcm.Stream.ReadAsync(buffer.AsMemory(length), _stop.Token);
                if (count == 0) break; length += count;
            }
            if (length == 0) break;
            if (length % 8 != 0) throw new InvalidDataException("Listener returned incomplete stereo float frames.");
            Compare(buffer.AsSpan(0, length), lookup, ref first, ref expected, ref frames, ref mismatches, ref detail, ref stereo, ref maxError);
        }
        await _process.WaitForExitAsync(_stop.Token);
        if (_process.ExitCode != 0) throw new InvalidDataException("Precision listener failed: " + (await _errors)[..Math.Min(300, (await _errors).Length)]);
        return new(frames, mismatches, detail, stereo, maxError, first);
    }
    private void Compare(ReadOnlySpan<byte> bytes, Dictionary<(int, int), int> lookup, ref int? first, ref int expected,
        ref long frames, ref long mismatches, ref long detail, ref long stereo, ref double maxError)
    {
        var pcm = MemoryMarshal.Cast<byte, float>(bytes);
        for (var i = 0; i < pcm.Length; i += 2)
        {
            if (!float.IsFinite(pcm[i]) || !float.IsFinite(pcm[i + 1])) throw new InvalidDataException("Nonfinite listener audio.");
            var left = (int)Math.Round(pcm[i] * 8388608d); var right = (int)Math.Round(pcm[i + 1] * 8388608d);
            if (first is null)
            {
                if (!lookup.TryGetValue((left, right), out expected)) { mismatches++; continue; }
                first = expected;
            }
            var error = Math.Max(Math.Abs(left - _left[expected]), Math.Abs(right - _right[expected]));
            if (error > 1) mismatches++; maxError = Math.Max(maxError, error);
            if (left % 256 != 0 || right % 256 != 0) detail++;
            if (left != right) stereo++;
            frames++; expected = (expected + 1) % _left.Length;
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync(); await ChildProcess.StopAsync(_process);
        try { await _reading; } catch (Exception) { }
        _pcm.Dispose(); _stop.Dispose();
    }

    public static (MediaItem Item, int[] Left, int[] Right) Fixture(string directory)
    {
        Directory.CreateDirectory(directory); var file = Path.Combine(directory, "stereo-detail-24.wav");
        var left = new int[48000]; var right = new int[48000];
        using var writer = new BinaryWriter(File.Create(file));
        writer.Write("RIFF"u8); writer.Write(36 + 48000 * 6); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)2); writer.Write(48000); writer.Write(288000);
        writer.Write((short)6); writer.Write((short)24); writer.Write("data"u8); writer.Write(48000 * 6);
        for (var i = 0; i < 48000; i++)
        {
            left[i] = (int)(524288 * Math.Sin(2 * Math.PI * 440 * i / 48000)) + i % 257 - 128;
            right[i] = (int)(393216 * Math.Sin(2 * Math.PI * 880 * i / 48000)) + (i * 37) % 251 - 125;
            foreach (var value in new[] { left[i], right[i] })
            { writer.Write((byte)value); writer.Write((byte)(value >> 8)); writer.Write((byte)(value >> 16)); }
        }
        return (new("stereo-detail", file, "Synthetic engineering signal", "24-bit stereo precision", DurationSeconds: 1), left, right);
    }
}
