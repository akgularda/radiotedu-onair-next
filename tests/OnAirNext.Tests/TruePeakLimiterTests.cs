using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class TruePeakLimiterTests
{
    private static float[] Render(float[] input,int chunk=960,TruePeakLimiter48k? limiter=null)
    {
        limiter??=new();var result=new List<float>();var output=new float[1920];
        for(var offset=0;offset<input.Length;offset+=chunk*2)
        {
            var count=Math.Min(chunk*2,input.Length-offset);var frames=limiter.Process(input.AsSpan(offset,count),output);
            result.AddRange(output.AsSpan(0,frames*2).ToArray());
        }
        limiter.Finish();int n;while((n=limiter.Flush(output))>0)result.AddRange(output.AsSpan(0,n*2).ToArray());
        return result.ToArray();
    }
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(37)][InlineData(959)][InlineData(960)][InlineData(2880)][InlineData(3001)][InlineData(9000)]
    public void FiniteTailContainsEveryOriginalFrameAndNoPadding(int frames)
    {
        var source=Enumerable.Range(0,frames*2).Select(i=>(i%97-48)/1048576f).ToArray();var limiter=new TruePeakLimiter48k();
        var output=Render(source,limiter:limiter);Assert.Equal(source,output);
        Assert.Equal(frames,limiter.Snapshot().InputFrames);Assert.Equal(frames,limiter.Snapshot().OutputFrames);Assert.Equal("Finished",limiter.Snapshot().State);
        Assert.Equal(0,limiter.Snapshot().LimitedFrames);Assert.Equal(0,limiter.Flush(new float[1920]));
    }
    [Fact]
    public void StereoImageAndOversampledCeilingSurviveSustainedOverload()
    {
        var input=new float[48000*2];for(var i=0;i<input.Length;i+=2){input[i]=4;input[i+1]=2;}
        var limiter=new TruePeakLimiter48k();var output=Render(input,limiter:limiter);
        for(var i=0;i<output.Length;i+=2)Assert.Equal(output[i]/2,output[i+1]);
        var meter=new StereoLoudnessMeter48k(TimeSpan.FromSeconds(3));meter.Feed(output);meter.Finish();
        Assert.True(meter.Read().TruePeakDbtp<=TruePeakLimiter48k.DefaultCeilingDbtp+0.001);
        Assert.True(limiter.Snapshot().MaximumGainReductionDb>12);Assert.True(limiter.Snapshot().LimitedFrames>0);
    }
    [Fact]
    public void AttackAndReleaseRemainSmoothAcrossAHighTransient()
    {
        var input=new float[16000*2];
        for(var i=0;i<input.Length;i+=2){input[i]=i==16000?32f:0.2f;input[i+1]=input[i]/2;}
        var output=Render(input);double? previous=null;
        for(var i=0;i<input.Length;i+=2)
        {
            var gain=20*Math.Log10(output[i]/input[i]);
            if(previous is double p)Assert.InRange(gain-p,-1.0/48-0.0001,0.05/48+0.0001);
            previous=gain;
        }
        var meter=new StereoLoudnessMeter48k(TimeSpan.FromSeconds(3));meter.Feed(output);meter.Finish();Assert.True(meter.Read().TruePeakDbtp<=-1.499);
    }
    [Fact]
    public void ChunkSizesDoNotChangeTheProcessedProgramme()
    {
        var input=new float[20000*2];var random=new Random(17705);
        for(var i=0;i<input.Length;i++)input[i]=(float)((random.NextDouble()*2-1)*4);
        Assert.Equal(Render(input,960),Render(input,37));
    }
    [Fact]
    public void MalformedBlocksAreRejectedBeforeStateChanges()
    {
        foreach(var invalid in new[]{float.NaN,float.PositiveInfinity,129f})
        {
            var limiter=new TruePeakLimiter48k();var input=new float[1920];input[31]=invalid;
            Assert.Throws<ArgumentException>(()=>limiter.Process(input,new float[1920]));Assert.Equal(0,limiter.Snapshot().InputFrames);
            var valid=new float[6000];valid[2]=0.000003f;Assert.Equal(valid,Render(valid,limiter:limiter));
        }
        Assert.Throws<ArgumentOutOfRangeException>(()=>new TruePeakLimiter48k(-1));
        Assert.Equal(AudioFormat.SamplesPerBlock,TruePeakLimiter48k.MaximumBlockFrames);
    }
    [Theory]
    [InlineData(-8)][InlineData(-4)][InlineData(-1.4)]
    public void EverySupportedCeilingFitsTheFixedAttackHorizon(double ceiling)
    {Assert.Equal(ceiling,new TruePeakLimiter48k(ceiling).Snapshot().CeilingDbtp);}

}
