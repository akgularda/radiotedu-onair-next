using System.Buffers.Binary;

namespace OnAirNext.Engine;

public sealed record LoudnessReading(long SampleFrames, long IntegratedFrames, double? MomentaryLufs,
    double? ShortTermLufs, double? IntegratedLufs, double? LoudnessRangeLu, double? MaximumMomentaryLufs,
    double? MaximumShortTermLufs, double? SamplePeakDbfs, double? TruePeakDbtp, bool RangeSettled, bool Finished);

/// <summary>
/// Single-owner, fixed 48 kHz stereo measurement. No gain, limiter, I/O, background work or compliance verdict.
/// Feed and Read must run on the same measurement worker, never concurrently. Read sorts bounded history;
/// it belongs outside a real-time callback. A full window requires an explicit Finish/new meter boundary.
/// </summary>
public sealed class StereoLoudnessMeter48k
{
    public const int SampleRate = 48000;
    private const int Quantum = 960; // 20 ms: exact M/S windows and 100 ms integration hops.
    private const double Offset = -0.691;
    private static readonly double AbsoluteGateEnergy = Math.Pow(10, (-70 - Offset) / 10);

    // ITU-R BS.1770-5 Annex 2, English edition, pp.18-19. Each row is one polyphase branch.
    private static readonly double[][] Interpolator =
    [
        [0.001708984375, 0.010986328125, -0.0196533203125, 0.033203125, -0.0594482421875, 0.1373291015625,
            0.97216796875, -0.102294921875, 0.047607421875, -0.026611328125, 0.014892578125, -0.00830078125],
        [-0.0291748046875, 0.029296875, -0.0517578125, 0.089111328125, -0.16650390625, 0.465087890625,
            0.77978515625, -0.2003173828125, 0.1015625, -0.0582275390625, 0.0330810546875, -0.0189208984375],
        [-0.0189208984375, 0.0330810546875, -0.0582275390625, 0.1015625, -0.2003173828125, 0.77978515625,
            0.465087890625, -0.16650390625, 0.089111328125, -0.0517578125, 0.029296875, -0.0291748046875],
        [-0.00830078125, 0.014892578125, -0.026611328125, 0.047607421875, -0.102294921875, 0.97216796875,
            0.1373291015625, -0.0594482421875, 0.033203125, -0.0196533203125, 0.010986328125, 0.001708984375]
    ];

    private readonly Channel _left = new(), _right = new();
    private readonly double[] _quanta = new double[150];
    private readonly double[] _integrated, _shortTerms, _rangeScratch;
    private readonly long _maximumFrames;
    private long _frames, _quantumCount;
    private int _withinQuantum, _integratedCount, _shortTermCount;
    private double _quantumEnergy, _momentarySum, _shortTermSum, _samplePeak, _truePeak;
    private double? _momentary, _shortTerm, _maximumMomentary, _maximumShortTerm;
    private bool _finished;

    public StereoLoudnessMeter48k(TimeSpan maximumWindow)
    {
        if (maximumWindow < TimeSpan.FromSeconds(3) || maximumWindow > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(maximumWindow), "Use an explicit window from 3 seconds to 24 hours.");
        _maximumFrames = checked((long)(maximumWindow.TotalSeconds * SampleRate));
        var capacity = checked((int)(_maximumFrames / 4800 + 1));
        _integrated = new double[capacity];
        _shortTerms = new double[capacity];
        _rangeScratch = new double[capacity];
    }

    public void Feed(ReadOnlySpan<float> interleavedStereo)
    {
        ValidateFeed(interleavedStereo.Length, 2);
        // Reject a malformed block atomically rather than poisoning filters or accepting half the block.
        foreach (var value in interleavedStereo)
            if (!float.IsFinite(value)) throw new ArgumentException("PCM must contain only finite samples.", nameof(interleavedStereo));
        for (var i = 0; i < interleavedStereo.Length; i += 2)
            AddFrame(interleavedStereo[i], interleavedStereo[i + 1]);
    }

    public void FeedPcm16LittleEndian(ReadOnlySpan<byte> interleavedStereo)
    {
        ValidateFeed(interleavedStereo.Length, 4);
        for (var i = 0; i < interleavedStereo.Length; i += 4)
            AddFrame(BinaryPrimitives.ReadInt16LittleEndian(interleavedStereo[i..]) / 32768.0,
                BinaryPrimitives.ReadInt16LittleEndian(interleavedStereo[(i + 2)..]) / 32768.0);
    }

    private void ValidateFeed(int length, int valuesPerFrame)
    {
        if (_finished) throw new InvalidOperationException("This measurement is finished; start an explicit new window.");
        if (length % valuesPerFrame != 0) throw new ArgumentException("PCM must contain complete stereo frames.");
        if (length / valuesPerFrame > _maximumFrames - _frames)
            throw new InvalidOperationException("Measurement window is full; finish it and start an explicit new window.");
    }

    private void AddFrame(double left, double right)
    {
        _samplePeak = Math.Max(_samplePeak, Math.Max(Math.Abs(left), Math.Abs(right)));
        _truePeak = Math.Max(_truePeak, Math.Max(_left.Peak(left), _right.Peak(right)));
        var l = _left.Weighted(left); var r = _right.Weighted(right);
        _quantumEnergy += l * l + r * r; // Stereo channel weights are both 1, not an averaged downmix.
        _frames++;
        if (++_withinQuantum != Quantum) return;
        var index = (int)(_quantumCount % 150);
        if (_quantumCount >= 20) _momentarySum -= _quanta[(index + 130) % 150];
        if (_quantumCount >= 150) _shortTermSum -= _quanta[index];
        var energy = _quantumEnergy / Quantum;
        _quanta[index] = energy;
        _momentarySum += energy; _shortTermSum += energy;
        _quantumCount++; _withinQuantum = 0; _quantumEnergy = 0;
        if (_quantumCount >= 20)
        {
            _momentary = Lufs(_momentarySum / 20);
            _maximumMomentary = Maximum(_maximumMomentary, _momentary);
            if (_quantumCount % 5 == 0) _integrated[_integratedCount++] = Math.Max(0, _momentarySum / 20);
        }
        if (_quantumCount >= 150)
        {
            _shortTerm = Lufs(_shortTermSum / 150);
            _maximumShortTerm = Maximum(_maximumShortTerm, _shortTerm);
            if (_quantumCount % 5 == 0) _shortTerms[_shortTermCount++] = Math.Max(0, _shortTermSum / 150);
        }
    }

    /// <summary>Complete FIR tail only. Does not pad loudness windows or invent programme samples.</summary>
    public void Finish()
    {
        if (_finished) return;
        for (var i = 0; i < 12; i++) _truePeak = Math.Max(_truePeak, Math.Max(_left.Peak(0), _right.Peak(0)));
        _finished = true;
    }

    public LoudnessReading Read()
    {
        var gate = Gate(_integrated.AsSpan(0, _integratedCount), 0.1);
        double sum = 0; var included = 0;
        foreach (var energy in _integrated.AsSpan(0, _integratedCount))
            if (energy > gate) { sum += energy; included++; }
        var integrated = included == 0 ? null : Lufs(sum / included);
        var rangeGate = Gate(_shortTerms.AsSpan(0, _shortTermCount), 0.01);
        var rangeCount = 0;
        foreach (var energy in _shortTerms.AsSpan(0, _shortTermCount))
            if (energy >= rangeGate && energy > 0) _rangeScratch[rangeCount++] = energy;
        double? range = null;
        if (rangeCount > 0)
        {
            Array.Sort(_rangeScratch, 0, rangeCount);
            var low = (int)Math.Round((rangeCount - 1) * 0.10, MidpointRounding.AwayFromZero);
            var high = (int)Math.Round((rangeCount - 1) * 0.95, MidpointRounding.AwayFromZero);
            range = 10 * Math.Log10(_rangeScratch[high] / _rangeScratch[low]);
        }
        var integratedFrames = _integratedCount == 0 ? 0 : 19200L + (_integratedCount - 1L) * 4800;
        return new(_frames, integratedFrames, _momentary, _shortTerm, integrated, range, _maximumMomentary,
            _maximumShortTerm, Db(_samplePeak), Db(Math.Max(_samplePeak, _truePeak)), _frames >= SampleRate * 60L, _finished);
    }

    private static double Gate(ReadOnlySpan<double> energies, double relativeFactor)
    {
        double sum = 0; var count = 0;
        foreach (var energy in energies) if (energy > AbsoluteGateEnergy) { sum += energy; count++; }
        return count == 0 ? AbsoluteGateEnergy : Math.Max(AbsoluteGateEnergy, sum / count * relativeFactor);
    }
    private static double? Lufs(double energy) => energy > 0 ? Offset + 10 * Math.Log10(energy) : null;
    private static double? Db(double amplitude) => amplitude > 0 ? 20 * Math.Log10(amplitude) : null;
    private static double? Maximum(double? first, double? second)
        => first is null ? second : second is null ? first : Math.Max(first.Value, second.Value);

    private sealed class Channel
    {
        private double _s1, _s2, _h1, _h2;
        private readonly double[] _history = new double[12];
        private int _index;
        public double Weighted(double sample)
        {
            // BS.1770-5 Annex 1, tables 1 and 2; transposed direct form II, double state.
            var shelf = 1.53512485958697 * sample + _s1;
            _s1 = -2.69169618940638 * sample + 1.69065929318241 * shelf + _s2;
            _s2 = 1.19839281085285 * sample - 0.73248077421585 * shelf;
            var highpass = shelf + _h1;
            _h1 = -2 * shelf + 1.99004745483398 * highpass + _h2;
            _h2 = shelf - 0.99007225036621 * highpass;
            return highpass;
        }
        public double Peak(double sample)
        {
            _history[_index] = sample;
            double peak = 0;
            foreach (var phase in Interpolator)
            {
                double value = 0; var index = _index;
                for (var tap = 0; tap < 12; tap++)
                {
                    value += phase[tap] * _history[index];
                    if (--index < 0) index = 11;
                }
                peak = Math.Max(peak, Math.Abs(value));
            }
            if (++_index == 12) _index = 0;
            return peak;
        }
    }
}
