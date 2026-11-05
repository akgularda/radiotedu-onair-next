using System.Runtime.InteropServices;
using System.Text.Json;
using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class SourcePlaybackRangeTests
{
    private static SourceLoudness Measurement(double lufs=-18) => new(new string('A',64),100,123,20,lufs,-4,5,DateTimeOffset.UtcNow);
    [Fact]
    public void OnlyExteriorSilenceIsExcludedAndTheInternalPauseRemains()
    {
        var edges=new SourceEdgeAnalysis();
        for(var i=0;i<200;i++)edges.Observe(double.NegativeInfinity,480);
        for(var i=0;i<100;i++)edges.Observe(-20,480);
        for(var i=0;i<300;i++)edges.Observe(double.NegativeInfinity,480);
        for(var i=0;i<100;i++)edges.Observe(-20,480);
        for(var i=0;i<1300;i++)edges.Observe(double.NegativeInfinity,480);
        var result=edges.Complete(Measurement());
        Assert.Equal(95040,result.StartFrame);Assert.Equal(340800,result.EndFrame);Assert.Equal(960000,result.TotalFrames);
        Assert.Equal(5.12,result.Frames/48000d,8);
    }
    [Fact]
    public void SilenceThresholdUsesTheActualNormalizationGain()
    {
        var edges=new SourceEdgeAnalysis();edges.Observe(-62,48000);edges.Observe(-20,48000);edges.Observe(-62,48000);
        var reduced=edges.Complete(Measurement(-18)); // -5 dB: outside sections become -67 dBFS
        var unchanged=edges.Complete(Measurement(-23));
        Assert.True(reduced.StartFrame>0);Assert.True(reduced.EndFrame<reduced.TotalFrames);
        Assert.Equal(0,unchanged.StartFrame);Assert.Equal(unchanged.TotalFrames,unchanged.EndFrame);
    }
    [Fact]
    public void PartialPcmFramesAndStereoSignalsRemainAccountedFor()
    {
        var samples=new float[2*481];for(var i=1;i<samples.Length;i+=2)samples[i]=.1f;
        var edges=new SourceEdgeAnalysis();edges.ObservePcm(MemoryMarshal.AsBytes(samples.AsSpan()));
        var result=edges.Complete(Measurement());Assert.Equal(481,result.TotalFrames);Assert.Equal(481,result.Frames);
        Assert.Throws<InvalidDataException>(()=>edges.ObservePcm(new byte[7]));
        Assert.Throws<InvalidDataException>(()=>edges.Observe(double.NaN,480));
    }
    [Fact]
    public void AnEntirelySilentFileIsExplicitWithoutInventingAudibleBounds()
    {
        var edges=new SourceEdgeAnalysis();edges.Observe(double.NegativeInfinity,48000);
        var result=edges.Complete(Measurement());Assert.False(result.HasSignal);Assert.Equal(0,result.StartFrame);Assert.Equal(48000,result.EndFrame);
    }
    [Fact]
    public void ShortTransientsRetainTheOriginalClip()
    {
        var edges=new SourceEdgeAnalysis();edges.Observe(double.NegativeInfinity,48000);edges.Observe(-10,480);edges.Observe(double.NegativeInfinity,48000);
        var result=edges.Complete(Measurement());Assert.Equal(0,result.StartFrame);Assert.Equal(result.TotalFrames,result.EndFrame);
    }
    [Fact]
    public void OldMeasurementJsonRemainsReadableAndProgramsKeepTheirTiming()
    {
        var original=Measurement();var json=JsonSerializer.Serialize(original);Assert.DoesNotContain("PlaybackRange",json);
        Assert.Null(JsonSerializer.Deserialize<SourceLoudness>(json)!.PlaybackRange);
        var clipped=original with {PlaybackRange=new(48000,96000,960000)};
        Assert.Equal(1,SourcePlaybackRange.Duration(ContentKind.Music,20,clipped));
        Assert.Equal(1,SourcePlaybackRange.Duration(ContentKind.Jingle,20,clipped));
        Assert.Equal(20,SourcePlaybackRange.Duration(ContentKind.Program,20,clipped));
        Assert.Throws<ArgumentException>(()=>(clipped with{PlaybackRange=new(10,9,20)}).Validate());
    }
}
