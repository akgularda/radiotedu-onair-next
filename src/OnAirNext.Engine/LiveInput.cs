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
            if (!_enabled || block.Length != AudioFormat.BlockBytes) { block.Dispose(); return; }
            if (_blocks.Count >= 5) { _blocks.Dequeue().Dispose(); DroppedBlocks++; }
            _blocks.Enqueue(block);
        }
    }

    public void Mix(Span<byte> program)
    {
        PcmBlock? input;
        lock (_gate) input = _enabled && _blocks.TryDequeue(out var available) ? available : null;
        if (input is null && _musicGain >= 1f) return;
        try
        {
            var samples = MemoryMarshal.Cast<byte, short>(program);
            ReadOnlySpan<short> mic = input is null ? [] : MemoryMarshal.Cast<byte, short>(input.Memory.Span);
            var target = input is null ? 1f : 0.25f;
            var step = input is null ? 0.75f / (AudioFormat.SampleRate * 0.20f) : 0.75f / (AudioFormat.SampleRate * 0.04f);
            for (var frame = 0; frame < samples.Length / 2; frame++)
            {
                _musicGain = _musicGain < target ? Math.Min(target, _musicGain + step) : Math.Max(target, _musicGain - step);
                for (var channel = 0; channel < 2; channel++)
                {
                    var index = frame * 2 + channel;
                    var value = samples[index] * _musicGain + (input is null ? 0 : mic[index]);
                    samples[index] = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
                }
            }
        }
        finally { input?.Dispose(); }
    }

    public void Dispose() => SetEnabled(false);
}
