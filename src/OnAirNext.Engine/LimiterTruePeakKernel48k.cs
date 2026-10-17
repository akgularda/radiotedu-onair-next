namespace OnAirNext.Engine;

// Processing detector: an eight-times windowed-sinc grid plus the independent Annex-2 meter kernels.
// The meter keeps its original standard kernels. A wider detector prevents a short FIR's under-read
// from being treated as sufficient peak protection for arbitrary transient/broadband programme PCM.
internal static class LimiterTruePeakKernel48k
{
    internal const int Taps=32;
    internal sealed record Filter(double[] Coefficients,int Offset,double Variation);
    internal static readonly Filter[] Filters=Create();
    private static Filter[] Create()
    {
        var result=new List<Filter>();
        for(var phase=1;phase<8;phase++)
        {
            var coefficients=new double[Taps];var center=15+phase/8.0;
            for(var tap=0;tap<Taps;tap++)
            {
                var distance=tap-center;
                var sinc=Math.Abs(distance)<1e-12?1:Math.Sin(Math.PI*distance)/(Math.PI*distance);
                var window=Math.Abs(distance)>16?0:.42+.5*Math.Cos(Math.PI*distance/16)+.08*Math.Cos(2*Math.PI*distance/16);
                coefficients[tap]=sinc*window;
            }
            var sum=coefficients.Sum();for(var tap=0;tap<Taps;tap++)coefficients[tap]/=sum;
            result.Add(new(coefficients,0,VariationFor(Taps)));
        }
        // Phase zero is the original sample, already guarded without filtering or interpolation error.
        foreach(var coefficients in TruePeakKernel48k.Interpolator)result.Add(new(coefficients,10,VariationFor(coefficients.Length)));
        return result.ToArray();
    }
    private static double VariationFor(int taps)=>Math.Pow(10,(taps-1)/(48.0*20))-1;
}
