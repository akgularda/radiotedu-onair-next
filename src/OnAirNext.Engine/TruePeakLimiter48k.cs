namespace OnAirNext.Engine;

public sealed record LimiterSnapshot(string State,double CeilingDbtp,int LookaheadFrames,long InputFrames,long OutputFrames,
    long LimitedFrames,double GainReductionDb,double MaximumGainReductionDb);

/// <summary>
/// Linked-stereo float PCM, fixed memory and no I/O. The detector uses the eight-times 32-tap interpolation and the four-times Annex-2 FIR,
/// plus a conservative bound for gain variation over each kernel support. Original samples are multiplied,
/// never resampled or clipped. A 60 ms read-ahead is a processing delay, not invented programme silence.
/// </summary>
public sealed class TruePeakLimiter48k
{
    public const int LookaheadFrames=2880;
    public const int MaximumBlockFrames=960;
    public const double DefaultCeilingDbtp=-1.5;
    public const float MaximumMixedSample=128;
    private const int Taps=LimiterTruePeakKernel48k.Taps,Future=Taps-1,RingSize=4096,EnvelopeLookahead=LookaheadFrames-Future;
    private const double AttackDbPerFrame=1.0/48,ReleaseDbPerFrame=0.05/48;
    private readonly float[] _raw=new float[RingSize*2];
    private readonly double[] _left=new double[Taps*2],_right=new double[Taps*2];
    private readonly MaximumWindow _support=new(64,Future),_envelope=new(4096,EnvelopeLookahead);
    private readonly double _ceiling;
    private readonly double _ceilingDbtp;
    private long _inputFrames,_outputFrames,_detectorFrame,_limited;
    private int _historyIndex,_flush;
    private double _attenuation,_maximum;
    private bool _finished;
    private LimiterSnapshot _snapshot;
    public TruePeakLimiter48k(double ceilingDbtp=DefaultCeilingDbtp)
    {
        if(!double.IsFinite(ceilingDbtp)||ceilingDbtp is < -8 or > -1.4)throw new ArgumentOutOfRangeException(nameof(ceilingDbtp));
        _ceilingDbtp=ceilingDbtp;_ceiling=Math.Pow(10,(ceilingDbtp-0.001)/20);
        // The finite input domain bounds the attack needed before a newly arriving peak enters the horizon.
        var maximumNorm=LimiterTruePeakKernel48k.Filters.Max(row=>row.Coefficients.Sum(x=>Math.Abs(x))*(1+row.Variation));
        if(20*Math.Log10(MaximumMixedSample*maximumNorm/_ceiling)>=AttackDbPerFrame*EnvelopeLookahead)
            throw new ArgumentException("The ceiling exceeds this limiter's bounded attack horizon.",nameof(ceilingDbtp));
        _snapshot=CreateSnapshot();
    }
    public LimiterSnapshot Snapshot()=>Volatile.Read(ref _snapshot);
    public int Process(ReadOnlySpan<float> input,Span<float> output)
    {
        if(_finished)throw new InvalidOperationException("The limiter has finished; create a new programme session.");
        if(input.Length%2!=0||input.Length>MaximumBlockFrames*2||output.Length<input.Length)
            throw new ArgumentException("Use a complete stereo block and an equally sized output buffer.");
        foreach(var sample in input)if(!float.IsFinite(sample)||Math.Abs(sample)>MaximumMixedSample)
            throw new ArgumentException("Mixed PCM is nonfinite or exceeds the supported input domain.",nameof(input));
        var written=0;
        for(var i=0;i<input.Length;i+=2)
        {
            _inputFrames++;
            if(Feed(input[i],input[i+1],output[(written*2)..]))written++;
        }
        Volatile.Write(ref _snapshot,CreateSnapshot());return written;
    }
    public void Finish()
    {
        if(_finished)return;_finished=true;_flush=LookaheadFrames;
        Volatile.Write(ref _snapshot,CreateSnapshot());
    }
    public int Flush(Span<float> output)
    {
        if(!_finished)throw new InvalidOperationException("Finish the programme input before draining its real tail.");
        if(output.Length%2!=0||output.Length<2)throw new ArgumentException("A stereo output buffer is required.");
        var written=0;
        while(_flush>0&&written<output.Length/2){_flush--;if(Feed(0,0,output[(written*2)..]))written++;}
        Volatile.Write(ref _snapshot,CreateSnapshot());return written;
    }
    private bool Feed(float left,float right,Span<float> destination)
    {
        var n=_detectorFrame++;
        var slot=(int)(n%RingSize)*2;_raw[slot]=left;_raw[slot+1]=right;
        _left[_historyIndex]=_left[_historyIndex+Taps]=left;_right[_historyIndex]=_right[_historyIndex+Taps]=right;
        double bound=Math.Max(Math.Abs(left),Math.Abs(right));
        foreach(var phase in LimiterTruePeakKernel48k.Filters)
        {
            double l=0,r=0,absoluteLeft=0,absoluteRight=0;
            for(var tap=0;tap<phase.Coefficients.Length;tap++)
            {
                var at=_historyIndex+Taps-phase.Offset-tap;
                var a=phase.Coefficients[tap]*_left[at];var b=phase.Coefficients[tap]*_right[at];
                l+=a;r+=b;absoluteLeft+=Math.Abs(a);absoluteRight+=Math.Abs(b);
            }
            bound=Math.Max(bound,Math.Max(Math.Abs(l)+phase.Variation*absoluteLeft,Math.Abs(r)+phase.Variation*absoluteRight));
        }
        _historyIndex=(_historyIndex+1)%Taps;
        var required=bound>_ceiling?20*Math.Log10(bound/_ceiling):0;
        _support.Push(n,required);
        var k=n-Future;if(k<0)return false;
        var supportRequired=_finished&&k>=_inputFrames?0:_support.Maximum;
        _envelope.Push(k,supportRequired-AttackDbPerFrame*k);
        var i=k-EnvelopeLookahead;if(i<0||i>=_inputFrames)return false;
        var anticipated=Math.Max(0,_envelope.Maximum+AttackDbPerFrame*i);
        _attenuation=Math.Max(anticipated,Math.Max(0,_attenuation-ReleaseDbPerFrame));
        var gain=_attenuation==0?1:Math.Pow(10,-_attenuation/20);
        slot=(int)(i%RingSize)*2;
        destination[0]=(float)(_raw[slot]*gain);destination[1]=(float)(_raw[slot+1]*gain);
        _maximum=Math.Max(_maximum,_attenuation);if(_attenuation>0.000001)_limited++;
        _outputFrames++;return true;
    }
    private LimiterSnapshot CreateSnapshot()=>new(_finished&&_flush==0?"Finished":_outputFrames==0?"Preparing":"Processing",
        _ceilingDbtp,LookaheadFrames,_inputFrames,_outputFrames,_limited,_attenuation,_maximum);
    private sealed class MaximumWindow(int capacity,int distance)
    {
        private readonly long[] _positions=new long[capacity];private readonly double[] _values=new double[capacity];
        private int _head,_count;
        public double Maximum=>_count==0?0:_values[_head];
        public void Push(long position,double value)
        {
            while(_count>0&&_positions[_head]<position-distance){_head=(_head+1)%capacity;_count--;}
            while(_count>0&&_values[(_head+_count-1)%capacity]<=value)_count--;
            if(_count==capacity)throw new InvalidOperationException("Limiter window exceeded its fixed capacity.");
            var tail=(_head+_count)%capacity;_positions[tail]=position;_values[tail]=value;_count++;
        }
    }
}
