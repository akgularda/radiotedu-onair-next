using System.Runtime.InteropServices;

namespace OnAirNext.Engine;

// Source-frame boundaries measured on the same stereo 48 kHz decode as playback.
// Only exterior silence is removed; pauses within a song remain intact.
public sealed record SourcePlaybackRange(long StartFrame, long EndFrame, long TotalFrames, int Version = 1, bool HasSignal = true)
{
    [System.Text.Json.Serialization.JsonIgnore] public long Frames => EndFrame - StartFrame;
    public static bool AppliesTo(ContentKind kind) => kind is ContentKind.Music or ContentKind.Jingle or ContentKind.Sweeper or ContentKind.Advertisement;
    public static double Duration(ContentKind kind, double originalSeconds, SourceLoudness? measurement)
        => AppliesTo(kind) && measurement?.PlaybackRange is { } range ? range.Frames / (double)AudioFormat.SampleRate : originalSeconds;
    public void Validate()
    {
        if (Version != 1 || TotalFrames <= 0 || TotalFrames > 604800L * AudioFormat.SampleRate ||
            StartFrame < 0 || EndFrame > TotalFrames || EndFrame <= StartFrame)
            throw new ArgumentException("Invalid measured source playback range.");
    }
}

// Bounded first/last signal positions at one-dB thresholds. Gain is known only
// after the complete loudness scan, so retain positions rather than source PCM.
public sealed class SourceEdgeAnalysis
{
    private const int MinimumDb = -100;
    private readonly long[] _first = Enumerable.Repeat(-1L, 101).ToArray();
    private readonly long[] _last = new long[101];
    private long _frames;
    private double _blockPeak;
    private int _blockFrames;
    public long TotalFrames => _frames;

    public void Observe(double peakDbfs, long frames)
    {
        if (double.IsNaN(peakDbfs) || double.IsPositiveInfinity(peakDbfs) || peakDbfs > 80 ||
            frames <= 0 || frames > AudioFormat.SampleRate || _frames > 604800L * AudioFormat.SampleRate - frames)
            throw new InvalidDataException("Invalid source edge measurement frame.");
        var end = checked(_frames + frames);
        var maximum = double.IsNegativeInfinity(peakDbfs) ? -1 : Math.Min(100, (int)Math.Ceiling(peakDbfs) - MinimumDb - 1);
        for (var i = 0; i <= maximum; i++)
        {
            if (_first[i] < 0) _first[i] = _frames;
            _last[i] = end;
        }
        _frames = end;
    }

    public void ObservePcm(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length % AudioFormat.BytesPerSampleFrame != 0) throw new InvalidDataException("Unaligned source analysis PCM.");
        var samples = MemoryMarshal.Cast<byte, float>(pcm);
        for (var i = 0; i < samples.Length; i += AudioFormat.Channels)
        {
            for (var channel = 0; channel < AudioFormat.Channels; channel++)
            {
                var sample = samples[i + channel];
                if (!float.IsFinite(sample) || Math.Abs(sample) > AudioFormat.MaximumSourceSample)
                    throw new InvalidDataException("Invalid source analysis PCM sample.");
                _blockPeak = Math.Max(_blockPeak, Math.Abs(sample));
            }
            if (++_blockFrames == AudioFormat.SampleRate / 100) FlushBlock();
        }
    }

    private void FlushBlock()
    {
        if (_blockFrames == 0) return;
        Observe(_blockPeak == 0 ? double.NegativeInfinity : 20 * Math.Log10(_blockPeak), _blockFrames);
        _blockFrames = 0; _blockPeak = 0;
    }

    public SourcePlaybackRange Complete(SourceLoudness loudness)
    {
        FlushBlock();
        if (_frames == 0)
            throw new InvalidDataException("The complete source edge measurement is unavailable.");
        var threshold = -65 - SourceNormalization.Decide(loudness).AppliedDb;
        if (threshold < MinimumDb || threshold > 0) return new(0, _frames, _frames);
        // Round down to preserve quieter material at a fractional threshold.
        var index = (int)Math.Floor(threshold) - MinimumDb;
        if (_first[index] < 0) return new(0, _frames, _frames, HasSignal: false);
        var start = Math.Max(0, _first[index] - AudioFormat.SampleRate / 50); // retain a 20 ms attack margin
        var end = Math.Min(_frames, _last[index] + AudioFormat.SampleRate / 10); // retain a 100 ms tail
        if (start < AudioFormat.SampleRate / 5) start = 0;
        if (_frames - end < AudioFormat.SampleRate / 5) end = _frames;
        if (end - start < Math.Min(_frames, AudioFormat.SampleRate / 2)) return new(0, _frames, _frames);
        var range = new SourcePlaybackRange(start, end, _frames); range.Validate(); return range;
    }
}
