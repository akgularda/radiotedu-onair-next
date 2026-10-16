using System.Runtime.InteropServices;

namespace OnAirNext.Engine;

// An audio-agent boundary. No Windows device or UI dependency is allowed in the engine.
public sealed class LiveInput : IDisposable
{
    private readonly Queue<PcmBlock> _blocks = new();
    private readonly object _gate = new();
    private bool _enabled;
    private float _musicGain = 1;
    public long DroppedBlocks { get; private set; }
    public long RejectedBlocks { get; private set; }
    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            _enabled = enabled;
            if (!enabled) while (_blocks.TryDequeue(out var block)) block.Dispose();
        }
    }

    // The input agent transfers ownership whether accepted or rejected.
    public void Push(PcmBlock block)
    {
        lock (_gate)
        {
            if (!_enabled) { block.Dispose(); return; }
            if (block.Length != AudioFormat.BlockBytes || !ValidSamples(block.Memory.Span))
            { block.Dispose(); RejectedBlocks++; return; }
            if (_blocks.Count >= 5) { _blocks.Dequeue().Dispose(); DroppedBlocks++; }
            _blocks.Enqueue(block);
        }
    }

    public void Mix(Span<byte> program)
    {
        if (program.Length % AudioFormat.BytesPerSampleFrame != 0) throw new ArgumentException("Programme PCM must contain complete stereo frames.", nameof(program));
        PcmBlock? input;
        lock (_gate) input = _enabled && _blocks.TryDequeue(out var available) ? available : null;
        if (input is null && _musicGain >= 1f) return;
        try
        {
            var samples = MemoryMarshal.Cast<byte, float>(program);
            ReadOnlySpan<float> mic = input is null ? [] : MemoryMarshal.Cast<byte, float>(input.Memory.Span);
            var target = input is null ? 1f : 0.25f;
            var step = input is null ? 0.75f / (AudioFormat.SampleRate * 0.20f) : 0.75f / (AudioFormat.SampleRate * 0.04f);
            for (var frame = 0; frame < samples.Length / 2; frame++)
            {
                _musicGain = _musicGain < target ? Math.Min(target, _musicGain + step) : Math.Max(target, _musicGain - step);
                for (var channel = 0; channel < 2; channel++)
                {
                    var index = frame * 2 + channel;
                    var value = samples[index] * _musicGain + (input is null ? 0 : mic[index]);
                    samples[index] = value; // Preserve headroom. True-peak processing belongs after the final mix.
                }
            }
        }
        finally { input?.Dispose(); }
    }

    private static bool ValidSamples(ReadOnlySpan<byte> pcm)
    {
        foreach (var value in MemoryMarshal.Cast<byte, float>(pcm)) if (!float.IsFinite(value)) return false;
        return true;
    }

    public void Dispose() => SetEnabled(false);
}
