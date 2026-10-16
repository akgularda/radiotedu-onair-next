using System.Buffers.Binary;
using OnAirNext.Engine;
using OnAirNext.MeasurementFixtures;
using Xunit;

namespace OnAirNext.Tests;

public sealed class LoudnessTests
{
    private static StereoLoudnessMeter48k NewMeter() => new(TimeSpan.FromMinutes(3));
    private static LoudnessReading Measure(params ToneSegment[] segments)
    {
        var meter = NewMeter();
        StereoReferenceSignals.Render(b => meter.Feed(b.Span), segments);
        meter.Finish();
        return meter.Read();
    }
    private static void Near(double expected, double? actual, double tolerance = 0.1)
    {
        Assert.NotNull(actual);
        Assert.InRange(actual.Value, expected - tolerance, expected + tolerance);
    }

    [Theory]
    [InlineData(-23)]
    [InlineData(-33)]
    public void Tech3341SteadyStereoCalibration(double level)
    {
        var reading = Measure(new ToneSegment(20, level));
        Near(level, reading.IntegratedLufs); Near(level, reading.MomentaryLufs); Near(level, reading.ShortTermLufs);
        Assert.Equal(20L * 48000, reading.IntegratedFrames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tech3341AbsoluteAndRelativeGatesExcludeQuietSections(bool withAbsoluteGate)
    {
        var sections = new List<ToneSegment>();
        if (withAbsoluteGate) sections.Add(new(10, -72));
        sections.AddRange([new(10, -36), new(60, -23), new(10, -36)]);
        if (withAbsoluteGate) sections.Add(new(10, -72));
        Near(-23, Measure(sections.ToArray()).IntegratedLufs);
    }

    [Fact]
    public void Tech3341RelativeGateUsesEnergyRatherThanAveragingDecibels()
        => Near(-23, Measure(new ToneSegment(20, -26), new(20.1, -20), new(20, -26)).IntegratedLufs);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Tech3341SlidingWindowsTrackAlternatingLevels(bool shortTerm)
    {
        var meter = NewMeter();
        var pair = shortTerm ? new[] { new ToneSegment(1.34, -20), new(1.66, -30) }
            : new[] { new ToneSegment(0.18, -20), new(0.22, -30) };
        var segments = Enumerable.Range(0, shortTerm ? 5 : 25).SelectMany(_ => pair).ToArray();
        StereoReferenceSignals.Render(block =>
        {
            meter.Feed(block.Span); var reading = meter.Read();
            if (reading.SampleFrames >= (shortTerm ? 3 : 1) * 48000)
                Near(-23, shortTerm ? reading.ShortTermLufs : reading.MomentaryLufs);
        }, segments);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Tech3341FileMeterMaximaRemainCorrectAcrossTwentyStartingOffsets(bool shortTerm)
    {
        for (var i = 0; i < 20; i++)
        {
            var offset = i * (shortTerm ? 0.15 : 0.020);
            var duration = shortTerm ? 3 : 0.4;
            var reading = Measure(new ToneSegment(offset, double.NegativeInfinity), new(duration, -23), new(1, double.NegativeInfinity));
            Near(-23, shortTerm ? reading.MaximumShortTermLufs : reading.MaximumMomentaryLufs);
        }
    }

    [Theory]
    [InlineData(12000, 0, 0.5, -6)]
    [InlineData(12000, 45, 0.5, -6)]
    [InlineData(8000, 60, 0.5, -6)]
    [InlineData(6000, 67.5, 0.5, -6)]
    [InlineData(12000, 45, 1.41, 3)]
    public void Tech3341TruePeakCases15Through19(double frequency, double phase, double amplitude, double expected)
    {
        var reading = Measure(new ToneSegment(0.2, 20 * Math.Log10(amplitude), frequency, phase, true));
        Assert.NotNull(reading.TruePeakDbtp);
        Assert.InRange(reading.TruePeakDbtp.Value, expected - 0.4, expected + 0.2);
        if (frequency == 12000 && phase == 45)
            Assert.True(reading.TruePeakDbtp - reading.SamplePeakDbfs > 2.5, "An intersample peak must exceed the sample peak.");
    }

    [Theory]
    [InlineData(-20, -30, 10)]
    [InlineData(-20, -15, 5)]
    [InlineData(-40, -20, 20)]
    public void Tech3342TwoLevelRanges(double first, double second, double expected)
        => Near(expected, Measure(new ToneSegment(20, first), new(20, second)).LoudnessRangeLu, 1);

    [Fact]
    public void Tech3342QuietExtremesAreGatedOutOfRange()
    {
        var reading = Measure(new ToneSegment(20, -50), new(20, -35), new(20, -20), new(20, -35), new(20, -50));
        Near(15, reading.LoudnessRangeLu, 1);
        Assert.True(reading.RangeSettled);
    }

    [Fact]
    public void StereoEnergyDoesNotCancelOppositePhase()
    {
        var normal = Measure(new ToneSegment(3, -23)); var inverted = Measure(new ToneSegment(3, -23, InvertRight: true));
        Assert.Equal(normal.IntegratedLufs, inverted.IntegratedLufs);
        Near(-23, inverted.IntegratedLufs);
    }

    [Fact]
    public void SilenceAndIncompleteWindowsAreExplicitAndJsonSafe()
    {
        var meter = NewMeter(); meter.Feed(new float[2 * 19199]);
        var before = meter.Read();
        Assert.Null(before.IntegratedLufs); Assert.Null(before.MomentaryLufs); Assert.Null(before.TruePeakDbtp);
        Assert.Equal(0, before.IntegratedFrames);
        meter.Feed(new float[2 * 4322]); meter.Finish();
        var after = meter.Read();
        Assert.Equal(23521, after.SampleFrames); Assert.Equal(19200, after.IntegratedFrames);
        Assert.Null(after.IntegratedLufs); Assert.False(after.RangeSettled); Assert.True(after.Finished);
        Assert.DoesNotContain("NaN", System.Text.Json.JsonSerializer.Serialize(after));
        meter.Finish(); Assert.Equal(after, meter.Read());
        Assert.Throws<InvalidOperationException>(() => meter.Feed(new float[2]));
    }

    [Fact]
    public void FrameAlignmentNonFiniteSamplesAndWindowOverflowRejectTheWholeBlock()
    {
        var meter = new StereoLoudnessMeter48k(TimeSpan.FromSeconds(3));
        Assert.Throws<ArgumentException>(() => meter.Feed(new float[3]));
        Assert.Throws<ArgumentException>(() => meter.Feed([0, 0, float.NaN, 0]));
        Assert.Throws<ArgumentException>(() => meter.Feed([0, float.PositiveInfinity]));
        Assert.Throws<ArgumentException>(() => meter.FeedPcm16LittleEndian(new byte[3]));
        Assert.Throws<InvalidOperationException>(() => meter.Feed(new float[2 * 144001]));
        Assert.Equal(0, meter.Read().SampleFrames);
        meter.Feed(new float[2 * 144000]);
        Assert.Throws<InvalidOperationException>(() => meter.Feed([0, 0]));
        Assert.Equal(144000, meter.Read().SampleFrames);
        Assert.Throws<ArgumentOutOfRangeException>(() => new StereoLoudnessMeter48k(TimeSpan.FromHours(25)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StereoLoudnessMeter48k(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void ArbitraryChunkingPreservesFiltersPeaksWindowsAndInput()
    {
        var a = NewMeter(); var b = NewMeter();
        StereoReferenceSignals.Render(block =>
        {
            var copy = block.ToArray(); a.Feed(block.Span);
            for (var offset = 0; offset < block.Length;)
            {
                var length = Math.Min(block.Length - offset, 2 * (1 + offset % 127));
                b.Feed(block.Span.Slice(offset, length)); offset += length;
            }
            Assert.True(copy.AsSpan().SequenceEqual(block.Span));
        }, new(3.123, -17, 1337, 37), new(1.321, -31, 6000, 67.5));
        a.Finish(); b.Finish(); Assert.Equal(a.Read(), b.Read());
    }

    [Fact]
    public void LittleEndianPcm16UsesTheSameCalibrationAsExactlyRepresentableFloat()
    {
        var a = NewMeter(); var b = NewMeter();
        var bytes = new byte[1920 * 2]; var floats = new float[1920];
        StereoReferenceSignals.Render(block =>
        {
            for (var i = 0; i < block.Length; i++)
            {
                var value = (short)(block.Span[i] * 32768);
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), value);
                floats[i] = value / 32768f;
            }
            a.FeedPcm16LittleEndian(bytes); b.Feed(floats);
        }, new ToneSegment(3, -23));
        a.Finish(); b.Finish(); Assert.Equal(a.Read(), b.Read());
        Near(-23, a.Read().IntegratedLufs);
    }

    [Fact]
    public void FeedAllocatesNoPerBlockManagedObjectsAfterConstruction()
    {
        var meter = NewMeter(); var block = new float[1920]; meter.Feed(block);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) meter.Feed(block);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
